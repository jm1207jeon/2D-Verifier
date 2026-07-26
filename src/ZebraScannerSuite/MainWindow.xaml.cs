using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ZebraScannerSuite.Models;
using ZebraScannerSuite.Services;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingRect = System.Drawing.Rectangle;

namespace ZebraScannerSuite;

public partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private CoreScannerService? _scanner;
    private OcrService? _ocr;

    // 처리 파이프라인 (저장/OCR 버퍼링 - 상태바에 진행 표시)
    private readonly Channel<Func<Task>> _jobs = Channel.CreateUnbounded<Func<Task>>();
    private int _pendingJobs;

    // Tab1 상태
    private readonly ObservableCollection<FieldValue> _fields = new();
    private readonly ObservableCollection<ScanRecord> _history = new();
    private byte[]? _lastImageBytes;
    private int _lastPixelW, _lastPixelH;
    private BarcodeData? _pendingScan;          // 이미지 대기 중인 바코드
    private bool _awaitingScanImage;
    private readonly DispatcherTimer _imageTimeout = new() { Interval = TimeSpan.FromSeconds(4) };

    // OCR 영역 선택
    private Point _selStart;
    private bool _selecting;
    private Rect _selectionUi = Rect.Empty;

    // Tab2 상태
    private readonly ObservableCollection<ParamGrade> _verifyParams = new();
    private readonly ObservableCollection<VerificationResult> _verifySession = new();
    private bool _awaitingVerifyImage;

    // Tab3 상태
    private readonly ObservableCollection<MultiScanRow> _multiRows = new();
    private readonly Dictionary<string, MultiScanRow> _multiSeen = new();
    private int _multiTotal;
    private bool _multiRunning;
    private readonly DispatcherTimer _retriggerTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        FieldsGrid.ItemsSource = _fields;
        HistoryList.ItemsSource = _history;
        VerifyGrid.ItemsSource = _verifyParams;
        VerifySessionList.ItemsSource = _verifySession;
        MultiGrid.ItemsSource = _multiRows;
        _imageTimeout.Tick += ImageTimeout_Tick;
        _retriggerTimer.Tick += RetriggerTimer_Tick;
    }

    // ==================== 초기화 / 종료 ====================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();
        ApplySettingsToUi();

        _ocr = new OcrService();
        OcrEngineText.Text = _ocr.EngineDescription;

        _ = Task.Run(WorkerLoop);

        await Task.Run(InitScanner);
        UpdateScannerStatus();

        // 강제 스캔 모드가 저장되어 있으면 스캐너 연결 후 재무장
        if (ForceOcrCheck.IsChecked == true)
            ForceOcr_Toggled(this, new RoutedEventArgs());
    }

    private void InitScanner()
    {
        try
        {
            _scanner = new CoreScannerService();
            _scanner.BarcodeScanned += b => Dispatcher.BeginInvoke(() => OnBarcode(b));
            _scanner.ImageCaptured += img => Dispatcher.BeginInvoke(() => OnImage(img));
            _scanner.DevicesChanged += () => Dispatcher.BeginInvoke(UpdateScannerStatus);
            _scanner.StatusMessage += m => Dispatcher.BeginInvoke(() => SetStatus(m));
            _scanner.Open();
        }
        catch (Exception ex)
        {
            _scanner = null;
            Dispatcher.BeginInvoke(() =>
                SetStatus("스캐너 연동 불가: " + ex.Message + " (HID 키보드 입력창은 사용 가능)"));
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            CollectSettingsFromUi();
            SettingsService.Save(_settings);
        }
        catch { }
        try
        {
            if (_multiRunning && _scanner?.ActiveScanner is { } s) _scanner.ReleaseTrigger(s.Id);
            _scanner?.Dispose();
        }
        catch { }
    }

    private void ApplySettingsToUi()
    {
        SaveDirText.Text = _settings.ImageSaveDirectory;
        FileRuleText.Text = _settings.FileNameRule;
        OcrPatternsText.Text = string.Join(Environment.NewLine, _settings.OcrPatterns);
        CopyClipboardCheck.IsChecked = _settings.CopyOcrToClipboard;
        MultiRetriggerCheck.IsChecked = _settings.MultiAutoRetrigger;
        RulesGrid.ItemsSource = _settings.ExtractionRules;
        ForceRulesGrid.ItemsSource = _settings.ForceOcrRules;
        ForceOcrCheck.IsChecked = _settings.ForceOcrEnabled;

        ModeBarcodeOnly.IsChecked = _settings.ScanMode == 0;
        ModeBarcodeImage.IsChecked = _settings.ScanMode == 1;
        ModeBarcodeImageOcr.IsChecked = _settings.ScanMode == 2;

        HostModeCombo.Items.Clear();
        foreach (var (name, code) in CoreScannerService.HostModes)
            HostModeCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        int idx = Array.FindIndex(CoreScannerService.HostModes, m => m.Code == _settings.PreferredHostMode);
        HostModeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        UpdateRuleExample();
    }

    private void CollectSettingsFromUi()
    {
        _settings.ImageSaveDirectory = SaveDirText.Text.Trim();
        _settings.FileNameRule = FileRuleText.Text.Trim();
        _settings.OcrPatterns = OcrPatternsText.Text
            .Split('\n').Select(s => s.Trim('\r').Trim()).Where(s => s.Length > 0).ToList();
        _settings.CopyOcrToClipboard = CopyClipboardCheck.IsChecked == true;
        _settings.MultiAutoRetrigger = MultiRetriggerCheck.IsChecked == true;
        _settings.ForceOcrEnabled = ForceOcrCheck.IsChecked == true;
        _settings.ScanMode = CurrentMode;
        if (HostModeCombo.SelectedItem is ComboBoxItem { Tag: string code })
            _settings.PreferredHostMode = code;
    }

    private int CurrentMode =>
        ModeBarcodeImageOcr.IsChecked == true ? 2 : ModeBarcodeImage.IsChecked == true ? 1 : 0;

    // ==================== 상태 표시 ====================

    private void SetStatus(string msg) => StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}";

    private void UpdateScannerStatus()
    {
        var s = _scanner?.ActiveScanner;
        if (s == null)
        {
            ScannerStatusText.Text = "스캐너: 미연결";
            return;
        }
        ScannerStatusText.Text = $"스캐너: {s.Model} (S/N {s.Serial}) [{s.Type}]";
        if (!s.Type.Contains("SNAPI", StringComparison.OrdinalIgnoreCase))
            SetStatus($"현재 {s.Type} 모드입니다. 이미지 캡처/검증 기능은 'USB SNAPI (이미징 지원)' 전환 후 사용하세요.");
    }

    private void UpdateBusy()
    {
        bool busy = _pendingJobs > 0;
        ProgressBarMain.IsIndeterminate = busy;
        BufferText.Text = busy ? $"버퍼 {_pendingJobs}건 처리 중" : "대기";
    }

    private void Enqueue(Func<Task> job)
    {
        Interlocked.Increment(ref _pendingJobs);
        Dispatcher.BeginInvoke(UpdateBusy);
        _jobs.Writer.TryWrite(job);
    }

    private async Task WorkerLoop()
    {
        await foreach (var job in _jobs.Reader.ReadAllAsync())
        {
            try { await job(); }
            catch (Exception ex) { await Dispatcher.BeginInvoke(() => SetStatus("처리 오류: " + ex.Message)); }
            Interlocked.Decrement(ref _pendingJobs);
            await Dispatcher.BeginInvoke(UpdateBusy);
        }
    }

    // ==================== 바코드 / 이미지 이벤트 라우팅 ====================

    private void OnBarcode(BarcodeData b)
    {
        if (MainTabs.SelectedIndex == 2)
        {
            if (_multiRunning) AddMultiScan(b);
            return;
        }
        if (MainTabs.SelectedIndex == 1) return; // Verify 탭은 캡처 버튼 기반
        HandleScanTab(b);
    }

    private void HandleScanTab(BarcodeData b)
    {
        BarcodeText.Text = b.Text;
        SymbologyText.Text = b.Symbology;
        OcrResultText.Text = "";
        _fields.Clear();
        foreach (var f in DataExtractionService.Apply(b.Text, _settings.ExtractionRules))
            _fields.Add(f);

        var record = new ScanRecord { Barcode = b.Text, Symbology = b.Symbology, Time = b.Time };
        _history.Insert(0, record);
        while (_history.Count > 200) _history.RemoveAt(_history.Count - 1);

        int mode = CurrentMode;
        if (mode == 0 || _scanner?.ActiveScanner is not { } dev)
        {
            SetStatus($"바코드 리딩 완료: {b.Symbology}");
            return;
        }

        // 모드 ②/③: 디코드 직후 같은 위치의 이미지를 자동 캡처
        _pendingScan = b;
        _awaitingScanImage = true;
        _imageTimeout.Stop();
        _imageTimeout.Start();
        SetStatus("이미지 캡처 중... (자동 트리거)");
        Interlocked.Increment(ref _pendingJobs);
        UpdateBusy();
        Task.Run(() =>
        {
            Thread.Sleep(120); // 디코드 세션 종료 대기
            bool ok = _scanner!.CaptureImage(dev.Id);
            Interlocked.Decrement(ref _pendingJobs);
            Dispatcher.BeginInvoke(() =>
            {
                UpdateBusy();
                if (!ok)
                {
                    _awaitingScanImage = false;
                    _imageTimeout.Stop();
                    SetStatus("이미지 캡처 명령 실패 - 호스트 모드가 'USB SNAPI (이미징 지원)'인지 확인하세요.");
                }
            });
        });
    }

    private void ImageTimeout_Tick(object? sender, EventArgs e)
    {
        _imageTimeout.Stop();
        if (_awaitingScanImage || _awaitingVerifyImage)
        {
            _awaitingScanImage = false;
            _awaitingVerifyImage = false;
            if (_scanner?.ActiveScanner is { } d) _scanner.ReleaseTrigger(d.Id);
            SetStatus("이미지 수신 시간 초과 - SNAPI(이미징) 모드 및 연결 상태를 확인하세요.");
        }
    }

    private void OnImage(byte[] imageBytes)
    {
        if (_scanner?.ActiveScanner is { } d) _scanner.ReleaseTrigger(d.Id);
        _imageTimeout.Stop();

        if (_awaitingVerifyImage)
        {
            _awaitingVerifyImage = false;
            RunVerification(imageBytes);
            return;
        }

        ShowPreview(imageBytes);

        // 강제 스캔(OCR) 모드: 촬영 이미지에서 바코드 → 텍스트 순으로 인식
        if (!_awaitingScanImage && ForceOcrCheck.IsChecked == true && MainTabs.SelectedIndex == 0)
        {
            ProcessForceImage(imageBytes);
            return;
        }

        if (!_awaitingScanImage || _pendingScan == null)
        {
            SetStatus("이미지 수신");
            return;
        }
        _awaitingScanImage = false;
        var scan = _pendingScan;
        _pendingScan = null;
        var record = _history.FirstOrDefault(r => r.Time == scan.Time && r.Barcode == scan.Text);
        int mode = CurrentMode;
        CollectSettingsFromUi();
        var settingsSnapshot = _settings;

        Enqueue(async () =>
        {
            // 1) 이미지 저장
            string path = ImageSaveService.Save(imageBytes, scan.Text, scan.Symbology, "", settingsSnapshot);
            await Dispatcher.BeginInvoke(() =>
            {
                if (record != null) { record.ImagePath = Path.GetFileName(path); HistoryList.Items.Refresh(); }
                SetStatus("이미지 저장 완료: " + path);
            });

            // 2) 모드 ③: OCR 후 값 입력
            if (mode == 2 && _ocr is { IsAvailable: true })
            {
                await Dispatcher.BeginInvoke(() => SetStatus("OCR 진행 중..."));
                string text = await _ocr.RecognizeAsync(imageBytes);
                var matches = OcrService.FilterByPatterns(text, settingsSnapshot.OcrPatterns);
                string value = matches.Count > 0 ? matches[0] : "";
                await Dispatcher.BeginInvoke(() =>
                {
                    OcrResultText.Text = string.Join(" | ", matches);
                    if (record != null) { record.OcrValue = value; HistoryList.Items.Refresh(); }
                    if (value.Length > 0 && settingsSnapshot.CopyOcrToClipboard)
                        try { Clipboard.SetText(value); } catch { }
                    SetStatus(matches.Count > 0
                        ? $"OCR 완료: {value} (패턴 일치 {matches.Count}건)"
                        : "OCR 완료: 패턴에 일치하는 문자 없음");
                });
            }
        });
    }

    // ==================== 강제 스캔(OCR) 모드 ====================
    // SNAPI에서는 '디코드 실패' 이벤트가 호스트로 전달되지 않아 "트리거 2회"를 감지할 수 없다.
    // 대신 모드를 켜면(F9) 트리거 1회 = 촬영이 되고, 촬영 이미지에서
    // ① 소프트웨어 바코드 디코드(ZXing) 시도 → ② 실패 시 강제 OCR 규칙(유형1/2)으로 값 추출.

    private void ForceOcr_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool on = ForceOcrCheck.IsChecked == true;
        if (_scanner?.ActiveScanner is not { } dev)
        {
            if (on) SetStatus("스캐너 미연결 - 강제 스캔 모드는 스캐너 연결 후 동작합니다.");
            return;
        }
        Task.Run(() =>
        {
            bool ok = on ? _scanner.SetCaptureImageMode(dev.Id) : _scanner.SetCaptureBarcodeMode(dev.Id);
            Dispatcher.BeginInvoke(() => SetStatus(on
                ? (ok ? "강제 스캔 모드 ON - 트리거를 당기면 촬영 후 바코드/텍스트를 인식합니다."
                      : "강제 스캔 모드 전환 실패 - 'USB SNAPI (이미징 지원)' 모드인지 확인하세요.")
                : "강제 스캔 모드 OFF - 일반 바코드 디코드 모드로 복귀"));
        });
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            ForceOcrCheck.IsChecked = ForceOcrCheck.IsChecked != true;
            e.Handled = true;
        }
    }

    private void ProcessForceImage(byte[] bytes)
    {
        CollectSettingsFromUi();
        var settingsSnapshot = _settings;
        SetStatus("강제 스캔: 분석 중 (바코드 → 텍스트 순)...");

        Enqueue(async () =>
        {
            // ① 소프트웨어 바코드 디코드 시도
            BarcodeData? sw = TrySoftwareDecode(bytes);

            // ② OCR (강제 규칙 → 일반 허용 패턴 순)
            string ocrText = "";
            if (_ocr is { IsAvailable: true })
                ocrText = await _ocr.RecognizeAsync(bytes);
            string force = OcrService.ApplyForceRules(ocrText, settingsSnapshot.ForceOcrRules) ?? "";
            if (force.Length == 0 && sw == null)
                force = OcrService.FilterByPatterns(ocrText, settingsSnapshot.OcrPatterns).FirstOrDefault() ?? "";

            // ③ 이미지 저장 ({BARCODE} 토큰 = 바코드값 또는 OCR값)
            string baseName = sw?.Text ?? (force.Length > 0 ? force : "NOCODE");
            string path = ImageSaveService.Save(bytes, baseName, sw?.Symbology ?? "TEXT", force, settingsSnapshot);

            var record = new ScanRecord
            {
                Barcode = sw?.Text ?? (force.Length > 0 ? force : "(텍스트 인식 실패)"),
                Symbology = sw != null ? sw.Symbology + " (SW)" : "OCR",
                OcrValue = force,
                ImagePath = Path.GetFileName(path),
            };

            await Dispatcher.BeginInvoke(() =>
            {
                if (sw != null)
                {
                    BarcodeText.Text = sw.Text;
                    SymbologyText.Text = sw.Symbology + " (SW)";
                    _fields.Clear();
                    foreach (var f in DataExtractionService.Apply(sw.Text, settingsSnapshot.ExtractionRules))
                        _fields.Add(f);
                }
                else
                {
                    BarcodeText.Text = force;
                    SymbologyText.Text = "OCR";
                    _fields.Clear();
                }
                OcrResultText.Text = force;
                _history.Insert(0, record);
                while (_history.Count > 200) _history.RemoveAt(_history.Count - 1);
                if (force.Length > 0 && settingsSnapshot.CopyOcrToClipboard)
                    try { Clipboard.SetText(force); } catch { }
                SetStatus(sw != null
                    ? $"강제 스캔: 바코드 인식 ({sw.Symbology}) + 이미지 저장 완료"
                    : force.Length > 0
                        ? $"강제 스캔 OCR: {force} (이미지 저장 완료)"
                        : "강제 스캔: 바코드/패턴 인식 실패 (이미지는 저장됨)");
            });

            RearmForceCapture();
        });
    }

    /// <summary>촬영 이미지에서 ZXing으로 바코드 디코드 시도 (강제 스캔 모드용 폴백)</summary>
    private static BarcodeData? TrySoftwareDecode(byte[] bytes)
    {
        try
        {
            using var bmp = LoadBitmap(bytes);
            var reader = new ZXing.Windows.Compatibility.BarcodeReader
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true },
            };
            var res = reader.Decode(bmp);
            if (res == null || string.IsNullOrEmpty(res.Text)) return null;
            return new BarcodeData { Text = res.Text, Symbology = res.BarcodeFormat.ToString(), Time = DateTime.Now };
        }
        catch { return null; }
    }

    /// <summary>강제 스캔 모드 유지: 이미지 수신 후 스캐너가 디코드 모드로 복귀하므로 다시 촬영 모드로 무장</summary>
    private void RearmForceCapture()
    {
        if (_scanner?.ActiveScanner is not { } dev) return;
        bool on = Dispatcher.Invoke(() => ForceOcrCheck.IsChecked == true && MainTabs.SelectedIndex == 0);
        if (!on) return;
        Task.Run(() =>
        {
            Thread.Sleep(150);
            _scanner.SetCaptureImageMode(dev.Id);
        });
    }

    private void AddForceRule_Click(object sender, RoutedEventArgs e) =>
        _settings.ForceOcrRules.Add(new ForceOcrRule { Name = "새 규칙", Pattern = @"(\d+)", Output = "$1" });

    private void DelForceRule_Click(object sender, RoutedEventArgs e)
    {
        if (ForceRulesGrid.SelectedItem is ForceOcrRule r) _settings.ForceOcrRules.Remove(r);
    }

    private void ShowPreview(byte[] bytes)
    {
        _lastImageBytes = bytes;
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(bytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        _lastPixelW = bmp.PixelWidth;
        _lastPixelH = bmp.PixelHeight;
        PreviewImage.Source = bmp;
        NoImageText.Visibility = Visibility.Collapsed;
        SelectionRect.Visibility = Visibility.Collapsed;
        _selectionUi = Rect.Empty;
    }

    // ==================== Tab1 UI 핸들러 ====================

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SetStatus(CurrentMode switch
        {
            0 => "모드: 바코드만 리딩",
            1 => "모드: 바코드 + 이미지 캡처",
            _ => "모드: 바코드 + 이미지 + OCR 입력",
        });
    }

    private void ClearScan_Click(object sender, RoutedEventArgs e)
    {
        BarcodeText.Text = "";
        SymbologyText.Text = "-";
        OcrResultText.Text = "";
        _fields.Clear();
        PreviewImage.Source = null;
        _lastImageBytes = null;
        NoImageText.Visibility = Visibility.Visible;
        SelectionRect.Visibility = Visibility.Collapsed;
    }

    private void HidInputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        string text = HidInputBox.Text.Trim();
        HidInputBox.Clear();
        if (text.Length == 0) return;
        OnBarcode(new BarcodeData { Text = text, Symbology = "HID-KBD", Time = DateTime.Now });
        e.Handled = true;
    }

    private void RefreshScanners_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner == null) { Task.Run(InitScanner); SetStatus("스캐너 재검색..."); return; }
        _scanner.RefreshScanners();
        UpdateScannerStatus();
        SetStatus($"스캐너 {_scanner.Scanners.Count}대 검색됨");
    }

    private void SwitchHostMode_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner?.ActiveScanner is not { } dev)
        {
            SetStatus("연결된 스캐너가 없습니다.");
            return;
        }
        if (HostModeCombo.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        bool permanent = HostModePermanent.IsChecked == true;
        SetStatus("호스트 모드 전환 중... 스캐너가 재부팅됩니다 (수 초 소요)");
        Task.Run(() =>
        {
            bool ok = _scanner.SwitchHostMode(dev.Id, code, permanent);
            Dispatcher.BeginInvoke(() => SetStatus(ok
                ? "호스트 모드 전환 명령 전송 완료 - 재연결 대기 중"
                : "호스트 모드 전환 실패"));
        });
    }

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "이미지 저장 폴더 선택" };
        if (Directory.Exists(SaveDirText.Text)) dlg.InitialDirectory = SaveDirText.Text;
        if (dlg.ShowDialog(this) == true)
        {
            SaveDirText.Text = dlg.FolderName;
            UpdateRuleExample();
        }
    }

    private void FileRuleText_TextChanged(object sender, TextChangedEventArgs e) => UpdateRuleExample();

    private void UpdateRuleExample()
    {
        try
        {
            string path = ImageSaveService.BuildPath(
                SaveDirText.Text.Length > 0 ? SaveDirText.Text : ".",
                FileRuleText.Text, "0123456789012", "EAN-13", "SAMPLE", ".jpg");
            FileRuleExample.Text = "예시: " + Path.GetFileName(path);
        }
        catch (Exception ex) { FileRuleExample.Text = "규칙 오류: " + ex.Message; }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        CollectSettingsFromUi();
        SettingsService.Save(_settings);
        SetStatus("설정이 저장되었습니다. (재실행 시에도 유지)");
    }

    private void AddRule_Click(object sender, RoutedEventArgs e) =>
        _settings.ExtractionRules.Add(new ExtractionRule { Name = "새 규칙", Type = "REGEX", Param1 = "" });

    private void DelRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is ExtractionRule r) _settings.ExtractionRules.Remove(r);
    }

    // ---------- OCR 영역 선택 ----------

    private void ImageHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_lastImageBytes == null) return;
        _selecting = true;
        _selStart = e.GetPosition(ImageHost);
        ImageHost.CaptureMouse();
        SelectionRect.Visibility = Visibility.Visible;
        UpdateSelection(_selStart, _selStart);
    }

    private void ImageHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (_selecting) UpdateSelection(_selStart, e.GetPosition(ImageHost));
    }

    private void ImageHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        ImageHost.ReleaseMouseCapture();
        UpdateSelection(_selStart, e.GetPosition(ImageHost));
    }

    private void UpdateSelection(Point a, Point b)
    {
        _selectionUi = new Rect(a, b);
        System.Windows.Controls.Canvas.SetLeft(SelectionRect, _selectionUi.X);
        System.Windows.Controls.Canvas.SetTop(SelectionRect, _selectionUi.Y);
        SelectionRect.Width = _selectionUi.Width;
        SelectionRect.Height = _selectionUi.Height;
    }

    /// <summary>UI 선택영역 → 이미지 픽셀 좌표 (Stretch=Uniform 매핑)</summary>
    private DrawingRect? SelectionToPixelRect()
    {
        if (_lastImageBytes == null || _selectionUi.IsEmpty || _selectionUi.Width < 4 || _selectionUi.Height < 4)
            return null;
        double cw = ImageHost.ActualWidth, ch = ImageHost.ActualHeight;
        if (cw <= 0 || ch <= 0 || _lastPixelW <= 0) return null;
        double scale = Math.Min(cw / _lastPixelW, ch / _lastPixelH);
        double dispW = _lastPixelW * scale, dispH = _lastPixelH * scale;
        double offX = (cw - dispW) / 2, offY = (ch - dispH) / 2;

        int x = (int)((_selectionUi.X - offX) / scale);
        int y = (int)((_selectionUi.Y - offY) / scale);
        int w = (int)(_selectionUi.Width / scale);
        int h = (int)(_selectionUi.Height / scale);
        x = Math.Clamp(x, 0, _lastPixelW - 1);
        y = Math.Clamp(y, 0, _lastPixelH - 1);
        w = Math.Clamp(w, 1, _lastPixelW - x);
        h = Math.Clamp(h, 1, _lastPixelH - y);
        return new DrawingRect(x, y, w, h);
    }

    private void OcrRegion_Click(object sender, RoutedEventArgs e)
    {
        var rect = SelectionToPixelRect();
        if (rect == null)
        {
            SetStatus("이미지 위에서 마우스로 인식할 영역을 드래그한 뒤 실행하세요.");
            return;
        }
        RunManualOcr(rect);
    }

    private void OcrFull_Click(object sender, RoutedEventArgs e) => RunManualOcr(null);

    private void RunManualOcr(DrawingRect? region)
    {
        if (_lastImageBytes == null) { SetStatus("OCR 대상 이미지가 없습니다."); return; }
        if (_ocr is not { IsAvailable: true }) { SetStatus("OCR 엔진을 사용할 수 없습니다."); return; }
        CollectSettingsFromUi();
        var bytes = _lastImageBytes;
        var settingsSnapshot = _settings;
        SetStatus(region == null ? "전체 이미지 OCR 진행 중..." : "선택 영역 OCR 진행 중...");

        Enqueue(async () =>
        {
            byte[] target = bytes;
            if (region is { } r)
            {
                using var src = LoadBitmap(bytes);
                using var crop = src.Clone(r, src.PixelFormat);
                using var ms = new MemoryStream();
                crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                target = ms.ToArray();
            }
            string text = await _ocr.RecognizeAsync(target);
            var matches = OcrService.FilterByPatterns(text, settingsSnapshot.OcrPatterns);
            await Dispatcher.BeginInvoke(() =>
            {
                OcrResultText.Text = matches.Count > 0 ? string.Join(" | ", matches) : "";
                if (matches.Count > 0 && settingsSnapshot.CopyOcrToClipboard)
                    try { Clipboard.SetText(matches[0]); } catch { }
                SetStatus(matches.Count > 0
                    ? $"OCR 완료: {matches[0]} (일치 {matches.Count}건)"
                    : $"OCR 완료: 패턴 일치 없음 (원문 {text.Length}자)");
            });
        });
    }

    private static DrawingBitmap LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var tmp = new DrawingBitmap(ms);
        return new DrawingBitmap(tmp); // 스트림 분리 복사본
    }

    // ==================== Tab2 : BARCODE VERIFY ====================

    private void VerifyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner?.ActiveScanner is not { } dev)
        {
            SetStatus("연결된 스캐너가 없습니다. '이미지 파일 검증'을 사용할 수 있습니다.");
            return;
        }
        _awaitingVerifyImage = true;
        _imageTimeout.Stop();
        _imageTimeout.Start();
        SetStatus("검증용 이미지 캡처 중... 심볼을 스캐너 정면 중앙에 위치시키세요.");
        Task.Run(() =>
        {
            bool ok = _scanner.CaptureImage(dev.Id);
            if (!ok) Dispatcher.BeginInvoke(() =>
            {
                _awaitingVerifyImage = false;
                SetStatus("캡처 실패 - 'USB SNAPI (이미징 지원)' 모드인지 확인하세요.");
            });
        });
    }

    private void VerifyFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "검증할 바코드 이미지 선택",
            Filter = "이미지 파일|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff",
        };
        if (dlg.ShowDialog(this) == true)
            RunVerification(File.ReadAllBytes(dlg.FileName));
    }

    private void RunVerification(byte[] imageBytes)
    {
        SetStatus("ISO/IEC 15415 시뮬레이션 분석 중...");
        Enqueue(async () =>
        {
            VerificationResult result;
            try
            {
                using var bmp = LoadBitmap(imageBytes);
                result = Iso15415Verifier.Verify(bmp);
            }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(() => SetStatus("검증 실패: " + ex.Message));
                return;
            }
            await Dispatcher.BeginInvoke(() =>
            {
                _verifySession.Add(result);
                VerifySessionList.SelectedItem = result;
                ShowVerifyResult(result);
                SetStatus($"검증 완료: 종합 {result.OverallLetter} ({result.OverallNumeric:0.0})");
            });
            RearmForceCapture(); // 강제 스캔 모드였다면 촬영 모드 복귀
        });
    }

    private void ShowVerifyResult(VerificationResult r)
    {
        _verifyParams.Clear();
        foreach (var p in r.Params) _verifyParams.Add(p);
        OverallGradeText.Text = $"{r.OverallLetter} ({r.OverallNumeric:0.0})";
        OverallBorder.Background = r.OverallLetter switch
        {
            "A" => System.Windows.Media.Brushes.LightGreen,
            "B" => System.Windows.Media.Brushes.PaleGreen,
            "C" => System.Windows.Media.Brushes.Khaki,
            "D" => System.Windows.Media.Brushes.Orange,
            _ => System.Windows.Media.Brushes.LightCoral,
        };
        VerifyFormatText.Text = r.Format;
        VerifyDecodedText.Text = r.DecodedText;

        var lines = new List<string>();
        if (r.Recommendations.Count > 0)
        {
            lines.Add("▶ 개선 권장사항");
            lines.AddRange(r.Recommendations.Select(x => "  • " + x));
            lines.Add("");
        }
        lines.AddRange(r.Notes.Select(n => "• " + n));
        VerifyNotesText.Text = string.Join(Environment.NewLine, lines);

        byte[] imgBytes = OverlayCheck.IsChecked == true && r.AnnotatedPng.Length > 0
            ? r.AnnotatedPng : r.ImagePng;
        if (imgBytes.Length > 0)
        {
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(imgBytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            VerifyImage.Source = bmp;
        }
    }

    private void OverlayCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (VerifySessionList.SelectedItem is VerificationResult r) ShowVerifyResult(r);
    }

    private void VerifySessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VerifySessionList.SelectedItem is VerificationResult r) ShowVerifyResult(r);
    }

    private void VerifyClear_Click(object sender, RoutedEventArgs e)
    {
        _verifySession.Clear();
        _verifyParams.Clear();
        OverallGradeText.Text = "-";
        VerifyImage.Source = null;
        VerifyDecodedText.Text = "";
        VerifyNotesText.Text = "";
    }

    private void VerifyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_verifySession.Count == 0) { SetStatus("저장할 측정 결과가 없습니다."); return; }
        try
        {
            string scannerInfo = _scanner?.ActiveScanner?.ToString() ?? "(스캐너 미연결 - 파일 검증)";
            string path = ReportService.SaveReport(_verifySession.ToList(), _settings.ReportDirectory, scannerInfo);
            SetStatus("리포트 저장: " + path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { SetStatus("리포트 저장 실패: " + ex.Message); }
    }

    // ==================== Tab3 : Multi / Continuous ====================

    private void MultiStart_Click(object sender, RoutedEventArgs e)
    {
        if (_scanner?.ActiveScanner is not { } dev)
        {
            SetStatus("연결된 스캐너가 없습니다.");
            return;
        }
        _multiRunning = true;
        MultiStartBtn.IsEnabled = false;
        MultiStopBtn.IsEnabled = true;
        _scanner.PullTrigger(dev.Id);
        if (MultiRetriggerCheck.IsChecked == true) _retriggerTimer.Start();
        SetStatus("연속 스캔 시작 - 시야의 바코드를 빠르게 훑으세요 (중복 자동 제거)");
    }

    private void MultiStop_Click(object sender, RoutedEventArgs e)
    {
        _multiRunning = false;
        _retriggerTimer.Stop();
        MultiStartBtn.IsEnabled = true;
        MultiStopBtn.IsEnabled = false;
        if (_scanner?.ActiveScanner is { } dev) _scanner.ReleaseTrigger(dev.Id);
        SetStatus($"연속 스캔 정지 - 총 {_multiTotal}회 / 고유 {_multiSeen.Count}건");
    }

    private void RetriggerTimer_Tick(object? sender, EventArgs e)
    {
        // 핸드헬드형 디코드 세션 타임아웃 대비 재트리거 (DS9908 프레젠테이션 모드에서는 상시 감지)
        if (_multiRunning && _scanner?.ActiveScanner is { } dev)
            _scanner.PullTrigger(dev.Id);
    }

    private void AddMultiScan(BarcodeData b)
    {
        _multiTotal++;
        string key = b.Symbology + "|" + b.Text;
        if (_multiSeen.TryGetValue(key, out var row))
        {
            row.Count++;
            MultiGrid.Items.Refresh();
        }
        else
        {
            row = new MultiScanRow
            {
                No = _multiRows.Count + 1,
                TimeText = b.Time.ToString("HH:mm:ss.fff"),
                Symbology = b.Symbology,
                Data = b.Text,
            };
            _multiSeen[key] = row;
            _multiRows.Add(row);
            MultiGrid.ScrollIntoView(row);
        }
        MultiTotalText.Text = _multiTotal.ToString();
        MultiUniqueText.Text = _multiSeen.Count.ToString();
    }

    private void MultiClear_Click(object sender, RoutedEventArgs e)
    {
        _multiRows.Clear();
        _multiSeen.Clear();
        _multiTotal = 0;
        MultiTotalText.Text = "0";
        MultiUniqueText.Text = "0";
    }

    private void MultiExport_Click(object sender, RoutedEventArgs e)
    {
        if (_multiRows.Count == 0) { SetStatus("내보낼 데이터가 없습니다."); return; }
        var dlg = new SaveFileDialog
        {
            Title = "CSV 내보내기",
            Filter = "CSV 파일|*.csv",
            FileName = $"multiscan_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("No,Time,Symbology,Data,Count");
        foreach (var r in _multiRows)
            sb.AppendLine($"{r.No},{r.TimeText},{Csv(r.Symbology)},{Csv(r.Data)},{r.Count}");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        SetStatus("CSV 저장 완료: " + dlg.FileName);
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ==================== 탭 전환 ====================

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.OriginalSource != MainTabs) return;
        if (MainTabs.SelectedIndex != 2 && _multiRunning) MultiStop_Click(sender, new RoutedEventArgs());
    }
}
