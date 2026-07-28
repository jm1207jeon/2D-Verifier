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

    // 강제 스캔: 하드웨어 디코더 재시도 대기용
    private TaskCompletionSource<BarcodeData>? _forceDecodeTcs;

    // Multi 탭 상태
    private readonly ObservableCollection<MultiScanRow> _multiRows = new();
    private readonly Dictionary<string, MultiScanRow> _multiSeen = new();
    private int _multiTotal;
    private bool _multiRunning;
    private int _repullInFlight;
    // 하트비트: 즉시 재트리거가 놓친 경우를 대비한 보조 (실제 재트리거는 디코드 직후 40ms 내 수행)
    private readonly DispatcherTimer _retriggerTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public MainWindow()
    {
        InitializeComponent();
        FieldsGrid.ItemsSource = _fields;
        HistoryList.ItemsSource = _history;
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

        // 시작 시 스캐너 동작 모드 보장 (재시도 포함):
        // 이전 세션에서 촬영 모드로 남아 있던 상태 복구 + 강제 스캔 설정 재무장
        EnsureScannerMode("초기화");
    }

    private void InitScanner()
    {
        try
        {
            _scanner = new CoreScannerService();
            _scanner.BarcodeScanned += b => Dispatcher.BeginInvoke(() => OnBarcode(b));
            _scanner.ImageCaptured += img => Dispatcher.BeginInvoke(() => OnImage(img));
            _scanner.DevicesChanged += () => Dispatcher.BeginInvoke(() =>
            {
                UpdateScannerStatus();
                // 재연결(모드 전환/재플러그) 시 동작 모드·키보드 에뮬레이터 설정 재적용
                if (_scanner?.Scanners.Count > 0) EnsureScannerMode("스캐너 재연결");
            });
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
            if (_scanner?.ActiveScanner is { } s)
            {
                if (_multiRunning)
                {
                    _scanner.ReleaseTrigger(s.Id);
                    _scanner.SetBeepAfterGoodDecode(s.Id, true);
                }
                // 촬영 모드로 남겨두면 다음 실행/다른 프로그램에서 스캔이 안 되므로 디코드 모드로 복원
                _scanner.SetCaptureBarcodeMode(s.Id);
            }
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
        WedgeCheck.IsChecked = _settings.WedgeOutput;
        ForceScanCheck.IsChecked = _settings.ForceScanEnabled;
        ForceOcrEnableCheck.IsChecked = _settings.ForceOcrEnabled;

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
        _settings.WedgeOutput = WedgeCheck.IsChecked == true;
        _settings.ForceScanEnabled = ForceScanCheck.IsChecked == true;
        _settings.ForceOcrEnabled = ForceOcrEnableCheck.IsChecked == true;
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
        // 강제 스캔의 하드웨어 재판독 대기 중이면 해당 흐름으로 전달
        if (_forceDecodeTcs != null && _forceDecodeTcs.TrySetResult(b)) return;

        if (MainTabs.SelectedIndex == 1) // Multi / Continuous 탭
        {
            if (_multiRunning)
            {
                AddMultiScan(b);
                // 디코드 성공 시 세션이 종료되므로 즉시 재트리거 (최고 속도 연속 스캔)
                RepullTriggerSoon();
            }
            return;
        }
        HandleScanTab(b);
    }

    private void HandleScanTab(BarcodeData b)
    {
        BarcodeText.Text = b.Text;
        SymbologyText.Text = b.Symbology;
        _fields.Clear();
        foreach (var f in DataExtractionService.Apply(b.Text, _settings.ExtractionRules))
            _fields.Add(f);

        // 자체 키보드 웨지: 다른 창(엑셀 등)이 포커스일 때 커서 위치로 값 + Enter 전송
        // (Zebra 에뮬레이터 대체 - Caps Lock 무영향)
        if (WedgeCheck.IsChecked == true && !IsActive)
        {
            string wedgeText = b.Text;
            Task.Run(() => KeyboardWedge.TypeText(wedgeText));
        }

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
            Thread.Sleep(80); // 디코드 세션 종료 대기
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
        if (_awaitingScanImage)
        {
            _awaitingScanImage = false;
            if (_scanner?.ActiveScanner is { } d) _scanner.ReleaseTrigger(d.Id);
            SetStatus("이미지 수신 시간 초과 - SNAPI(이미징) 모드 및 연결 상태를 확인하세요.");
        }
    }

    private void OnImage(byte[] imageBytes)
    {
        if (_scanner?.ActiveScanner is { } d) _scanner.ReleaseTrigger(d.Id);
        _imageTimeout.Stop();

        ShowPreview(imageBytes);

        // 강제 스캔 모드: 촬영 이미지에서 바코드 → (옵션) 텍스트 순으로 인식
        if (!_awaitingScanImage && ForceScanCheck.IsChecked == true && MainTabs.SelectedIndex == 0)
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

    private void ForceScan_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_scanner is not { IsOpen: true })
        {
            if (ForceScanCheck.IsChecked == true)
                SetStatus("스캐너 미연결 - 강제 스캔 모드는 스캐너 연결 후 동작합니다.");
            return;
        }
        EnsureScannerMode(ForceScanCheck.IsChecked == true ? "강제 스캔 ON" : "강제 스캔 OFF");
    }

    /// <summary>스캐너 동작 모드를 현재 UI 설정에 맞게 보장한다 (최대 5회 재시도).
    /// - 강제 스캔 ON → 촬영 모드 / OFF → 디코드 모드로 확정
    /// - 이전 세션이 촬영 모드로 남긴 상태, 시작·재연결 직후 명령 실패를 복구
    /// - 키보드 에뮬레이터(Caps Lock 토글 원인)도 설정에 따라 재적용</summary>
    private void EnsureScannerMode(string reason)
    {
        if (_scanner is not { IsOpen: true }) return;
        bool forceScan = Dispatcher.Invoke(() => ForceScanCheck.IsChecked == true);
        Task.Run(() =>
        {
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                var dev = _scanner.ActiveScanner;
                if (dev == null)
                {
                    Thread.Sleep(700);
                    _scanner.RefreshScanners();
                    continue;
                }
                try
                {
                    // Caps Lock 토글의 원인인 HID 키보드 에뮬레이터는 항상 끈다 (자체 웨지로 대체)
                    _scanner.SetKeyboardEmulator(false);
                    _scanner.ScanEnable(dev.Id);
                    bool ok = forceScan
                        ? _scanner.SetCaptureImageMode(dev.Id)
                        : _scanner.SetCaptureBarcodeMode(dev.Id);
                    if (ok)
                    {
                        Dispatcher.BeginInvoke(() => SetStatus(
                            $"{reason}: {(forceScan ? "강제 스캔(트리거=촬영) 모드" : "바코드 디코드 모드")} 준비 완료"));
                        return;
                    }
                }
                catch { }
                Thread.Sleep(700);
            }
            Dispatcher.BeginInvoke(() => SetStatus(
                $"{reason}: 스캐너 모드 설정 실패 - USB 재연결 후 [스캐너 새로고침]을 눌러주세요."));
        });
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            ForceScanCheck.IsChecked = ForceScanCheck.IsChecked != true;
            e.Handled = true;
        }
    }

    private void ProcessForceImage(byte[] bytes)
    {
        CollectSettingsFromUi();
        var settingsSnapshot = _settings;
        bool doOcr = ForceOcrEnableCheck.IsChecked == true;
        SetStatus(doOcr ? "강제 스캔: 분석 중 (바코드 → 텍스트 순)..." : "강제 스캔: 분석 중 (바코드만)...");

        Enqueue(async () =>
        {
            // OCR·품질 분석은 디코드와 병렬로 미리 시작 (대기시간 단축)
            Task<string> ocrTask = doOcr && _ocr is { IsAvailable: true }
                ? _ocr.RecognizeAsync(bytes)
                : Task.FromResult("");
            // ① 고속 소프트웨어 디코드 (~수십 ms)
            BarcodeData? sw = TrySoftwareDecode(bytes, thorough: false);

            // ② 실패 시 하드웨어 디코더 재판독 (최대 0.6초) - 일반 스캔과 동일 성능
            if (sw == null && _scanner?.ActiveScanner is { } hwDev)
            {
                await Dispatcher.BeginInvoke(() => SetStatus("강제 스캔: 하드웨어 디코더로 재판독 중..."));
                sw = await TryHardwareDecodeAsync(hwDev.Id);
            }

            // ③ 마지막으로 정밀 소프트웨어 디코드 (TryHarder+대비보정)
            sw ??= TrySoftwareDecode(bytes, thorough: true);

            // ④ OCR 결과 취합 (이미 병렬로 돌고 있었음)
            string force = "";
            if (doOcr)
            {
                string ocrText = await ocrTask;
                force = OcrService.ApplyForceRules(ocrText, settingsSnapshot.ForceOcrRules) ?? "";
                if (force.Length == 0 && sw == null)
                    force = OcrService.FilterByPatterns(ocrText, settingsSnapshot.OcrPatterns).FirstOrDefault() ?? "";
            }


            // ③ 이미지 저장 ({BARCODE} 토큰 = 바코드값 또는 OCR값)
            string baseName = sw?.Text ?? (force.Length > 0 ? force : "NOCODE");
            string path = ImageSaveService.Save(bytes, baseName, sw?.Symbology ?? "TEXT", force, settingsSnapshot);

            var record = new ScanRecord
            {
                Barcode = sw?.Text ?? (force.Length > 0 ? force : doOcr ? "(텍스트 인식 실패)" : "(촬영)"),
                Symbology = sw?.Symbology ?? (doOcr ? "OCR" : "IMAGE"),
                OcrValue = force,
                ImagePath = Path.GetFileName(path),
            };

            await Dispatcher.BeginInvoke(() =>
            {
                if (sw != null)
                {
                    BarcodeText.Text = sw.Text;
                    SymbologyText.Text = sw.Symbology;
                    _fields.Clear();
                    foreach (var f in DataExtractionService.Apply(sw.Text, settingsSnapshot.ExtractionRules))
                        _fields.Add(f);
                }
                else
                {
                    BarcodeText.Text = force;
                    SymbologyText.Text = force.Length > 0 ? "OCR" : "인식 실패";
                    _fields.Clear();
                }
                _history.Insert(0, record);
                while (_history.Count > 200) _history.RemoveAt(_history.Count - 1);
                if (force.Length > 0 && settingsSnapshot.CopyOcrToClipboard)
                    try { Clipboard.SetText(force); } catch { }

                // 자체 키보드 웨지 (바코드값 또는 OCR값)
                string finalValue = sw?.Text ?? force;
                if (finalValue.Length > 0 && WedgeCheck.IsChecked == true && !IsActive)
                    Task.Run(() => KeyboardWedge.TypeText(finalValue));
                SetStatus(sw != null
                    ? $"강제 스캔: 바코드 인식 ({sw.Symbology}) + 이미지 저장 완료"
                    : force.Length > 0
                        ? $"강제 스캔 OCR: {force} (이미지 저장 완료)"
                        : doOcr
                            ? "강제 스캔: 바코드/패턴 인식 실패 (이미지는 저장됨)"
                            : "강제 스캔: 촬영/저장 완료 (OCR 꺼짐)");
            });

            RearmForceCapture();
        });
    }

    /// <summary>촬영 이미지에서 ZXing으로 바코드 디코드 시도 (강제 스캔 모드용 폴백).
    /// 다단계 전처리(대비 스트레칭/확대)로 인식률을 높인다.</summary>
    private static BarcodeData? TrySoftwareDecode(byte[] bytes, bool thorough)
    {
        try
        {
            using var bmp = LoadBitmap(bytes);
            var d = thorough ? SoftwareDecoder.DecodeThorough(bmp) : SoftwareDecoder.DecodeFast(bmp);
            if (d == null) return null;
            return new BarcodeData { Text = d.Value.Text, Symbology = d.Value.Format + " (SW)", Time = DateTime.Now };
        }
        catch { return null; }
    }

    /// <summary>강제 스캔 중 하드웨어 디코더 재판독:
    /// 디코드 모드 전환 → SDK 트리거 → 바코드 이벤트 대기(타임아웃) → 트리거 해제.
    /// 촬영 모드 복귀는 이후 RearmForceCapture()가 수행한다.</summary>
    private async Task<BarcodeData?> TryHardwareDecodeAsync(int scannerId, int timeoutMs = 600)
    {
        if (_scanner == null) return null;
        var tcs = new TaskCompletionSource<BarcodeData>(TaskCreationOptions.RunContinuationsAsynchronously);
        _forceDecodeTcs = tcs;
        try
        {
            _scanner.SetCaptureBarcodeMode(scannerId);
            await Task.Delay(30);
            _scanner.PullTrigger(scannerId);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            return done == tcs.Task ? tcs.Task.Result : null;
        }
        catch { return null; }
        finally
        {
            _forceDecodeTcs = null;
            try { _scanner.ReleaseTrigger(scannerId); } catch { }
        }
    }

    /// <summary>강제 스캔 모드 유지: 이미지 수신 후 스캐너가 디코드 모드로 복귀하므로 다시 촬영 모드로 무장</summary>
    private void RearmForceCapture()
    {
        if (_scanner?.ActiveScanner is not { } dev) return;
        bool on = Dispatcher.Invoke(() => ForceScanCheck.IsChecked == true && MainTabs.SelectedIndex == 0);
        if (!on) return;
        Task.Run(() =>
        {
            Thread.Sleep(60);
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
        _fields.Clear();
        PreviewImage.Source = null;
        _lastImageBytes = null;
        NoImageText.Visibility = Visibility.Visible;
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

    private static DrawingBitmap LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var tmp = new DrawingBitmap(ms);
        return new DrawingBitmap(tmp); // 스트림 분리 복사본
    }

    // ==================== Multi / Continuous 탭 ====================

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
        // 스캐너 자체 디코드 비프를 끄고, 신규 바코드일 때만 앱이 비프 (중복=무음)
        _scanner.SetBeepAfterGoodDecode(dev.Id, false);
        _scanner.PullTrigger(dev.Id);
        if (MultiRetriggerCheck.IsChecked == true) _retriggerTimer.Start();
        SetStatus("연속 스캔 시작 - 신규 바코드만 비프, 중복은 무음 (자동 집계)");
    }

    private void MultiStop_Click(object sender, RoutedEventArgs e)
    {
        _multiRunning = false;
        _retriggerTimer.Stop();
        MultiStartBtn.IsEnabled = true;
        MultiStopBtn.IsEnabled = false;
        if (_scanner?.ActiveScanner is { } dev)
        {
            _scanner.ReleaseTrigger(dev.Id);
            _scanner.SetBeepAfterGoodDecode(dev.Id, true); // 디코드 비프 복원
        }
        SetStatus($"연속 스캔 정지 - 총 {_multiTotal}회 / 고유 {_multiSeen.Count}건");
    }

    private void RetriggerTimer_Tick(object? sender, EventArgs e)
    {
        // 하트비트 재트리거(0.5초): 디코드 세션 타임아웃/즉시 재트리거 누락 대비
        // (DS9908 프레젠테이션 모드에서는 상시 감지되므로 보조 역할)
        if (_multiRunning && _scanner?.ActiveScanner is { } dev)
            _scanner.PullTrigger(dev.Id);
    }

    /// <summary>디코드 직후 즉시 트리거 재무장: release → 40ms → pull.
    /// 연속 스캔 속도를 타이머 주기와 무관하게 최대화한다. (중복 호출은 1건으로 병합)</summary>
    private void RepullTriggerSoon()
    {
        if (!_multiRunning || _scanner?.ActiveScanner is not { } dev) return;
        if (Interlocked.Exchange(ref _repullInFlight, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                _scanner.ReleaseTrigger(dev.Id);
                Thread.Sleep(40); // 세션 정리 최소 대기
                if (_multiRunning) _scanner.PullTrigger(dev.Id);
            }
            catch { }
            finally { Interlocked.Exchange(ref _repullInFlight, 0); }
        });
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
            // 신규 바코드에만 비프 1회 (중복은 무음)
            if (_scanner?.ActiveScanner is { } bdev)
                Task.Run(() => _scanner.Beep(bdev.Id, 0));
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
        if (MainTabs.SelectedIndex != 1 && _multiRunning) MultiStop_Click(sender, new RoutedEventArgs());
    }
}
