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

    // 처리 파이프라인 (저장/OCR 버퍼링 - 상태바에 진행 표시)
    private readonly Channel<Func<Task>> _jobs = Channel.CreateUnbounded<Func<Task>>();
    private int _pendingJobs;

    // Tab1 상태
    private readonly ObservableCollection<FieldValue> _fields = new();
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
    private bool _multiActive; // 멀티 스캔 수신 중
    private BarcodeData? _multiImageScan; // 멀티 탭 배경 사진 저장 대기 중인 판독 건
    private DateTime _multiImageAt;       // 위 촬영 요청 시각 (미수신 3초 후 자동 해제)

    public MainWindow()
    {
        InitializeComponent();
        FieldsGrid.ItemsSource = _fields;
        MultiGrid.ItemsSource = _multiRows;
        _imageTimeout.Tick += ImageTimeout_Tick;
    }

    // ==================== 초기화 / 종료 ====================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();
        ApplySettingsToUi();

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
                if (_multiActive) _scanner.SetBeepAfterGoodDecode(s.Id, true);
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
        WedgeCheck.IsChecked = _settings.WedgeOutput;
        ForceScanCheck.IsChecked = _settings.ForceScanEnabled;
        MultiSaveImageCheck.IsChecked = _settings.MultiSaveImage;

        ModeBarcodeOnly.IsChecked = _settings.ScanMode == 0;
        ModeBarcodeImage.IsChecked = _settings.ScanMode >= 1;

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
        _settings.WedgeOutput = WedgeCheck.IsChecked == true;
        _settings.ForceScanEnabled = ForceScanCheck.IsChecked == true;
        _settings.MultiSaveImage = MultiSaveImageCheck.IsChecked == true;
        _settings.ScanMode = CurrentMode;
        if (HostModeCombo.SelectedItem is ComboBoxItem { Tag: string code })
            _settings.PreferredHostMode = code;
    }

    private int CurrentMode => ModeBarcodeImage.IsChecked == true ? 1 : 0;

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
            if (_multiActive)
            {
                AddMultiScan(b);
                // 사진 저장 옵션: 판독 직후 같은 위치를 배경에서 촬영·저장 (화면 표시 없음)
                if (MultiSaveImageCheck.IsChecked == true) MultiCaptureImage(b);
            }
            return;
        }
        HandleScanTab(b);
    }

    private void HandleScanTab(BarcodeData b)
    {
        SetBarcodeDisplay(b.Text);
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

        // 멀티 탭: 배경 사진 저장만 수행하고 화면에는 표시하지 않음
        if (MainTabs.SelectedIndex == 1 && _multiImageScan != null)
        {
            var mScan = _multiImageScan;
            _multiImageScan = null;
            var mSettings = _settings;
            Enqueue(async () =>
            {
                string path = ImageSaveService.Save(imageBytes, mScan.Text, mScan.Symbology, "", mSettings);
                await Dispatcher.BeginInvoke(() => SetStatus("사진 저장 완료: " + path));
            });
            return;
        }

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
        int mode = CurrentMode;
        CollectSettingsFromUi();
        var settingsSnapshot = _settings;

        Enqueue(async () =>
        {
            // 1) 이미지 저장
            string path = ImageSaveService.Save(imageBytes, scan.Text, scan.Symbology, "", settingsSnapshot);
            await Dispatcher.BeginInvoke(() => SetStatus("이미지 저장 완료: " + path));
        });
    }

    // ==================== 강제 스캔 모드 ====================
    // SNAPI에서는 '디코드 실패' 이벤트가 호스트로 전달되지 않아 "트리거 2회"를 감지할 수 없다.
    // 대신 모드를 켜면(F9) 트리거 1회 = 촬영이 되고, 촬영 이미지에서
    // 소프트웨어 → 하드웨어 순으로 바코드를 인식하고 이미지를 항상 저장한다.

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
    /// <summary>현재 탭/설정에 맞는 스캐너 상태를 보장한다 (최대 5회 재시도).
    /// - 멀티 탭: 디코드 모드 + 스캐너 비프 억제(신규만 앱 비프)
    /// - 일반 탭: 강제 스캔 ON → 촬영 모드 / OFF → 디코드 모드, 비프 복원
    /// 탭 전환·시작·재연결 시 호출되어 인식 불능 상태를 복구한다.</summary>
    private void EnsureScannerMode(string reason)
    {
        if (_scanner is not { IsOpen: true }) return;
        bool multiTab = Dispatcher.Invoke(() => MainTabs.SelectedIndex == 1);
        bool forceScan = !multiTab && Dispatcher.Invoke(() => ForceScanCheck.IsChecked == true);
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
                    _scanner.SetBeepAfterGoodDecode(dev.Id, !multiTab); // 멀티 탭: 중복 무음, 신규만 앱 비프
                    bool ok = forceScan
                        ? _scanner.SetCaptureImageMode(dev.Id)
                        : _scanner.SetCaptureBarcodeMode(dev.Id);
                    if (ok)
                    {
                        Dispatcher.BeginInvoke(() => SetStatus(
                            $"{reason}: {(multiTab ? "멀티 스캔(트리거=1회 판독)" : forceScan ? "강제 스캔(트리거=촬영)" : "바코드 디코드")} 모드 준비 완료"));
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
        SetStatus("강제 스캔: 바코드 분석 중...");

        Enqueue(async () =>
        {
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

            // ④ 이미지 저장 (바코드 인식 여부와 무관하게 항상 저장)
            string baseName = sw?.Text ?? "NOCODE";
            string path = ImageSaveService.Save(bytes, baseName, sw?.Symbology ?? "IMAGE", "", settingsSnapshot);

            await Dispatcher.BeginInvoke(() =>
            {
                if (sw != null)
                {
                    SetBarcodeDisplay(sw.Text);
                    SymbologyText.Text = sw.Symbology;
                    _fields.Clear();
                    foreach (var f in DataExtractionService.Apply(sw.Text, settingsSnapshot.ExtractionRules))
                        _fields.Add(f);
                }
                else
                {
                    SetBarcodeDisplay("");
                    SymbologyText.Text = "인식 실패";
                    _fields.Clear();
                }
                // 자체 키보드 웨지
                if (sw != null && WedgeCheck.IsChecked == true && !IsActive)
                {
                    string wedgeText = sw.Text;
                    Task.Run(() => KeyboardWedge.TypeText(wedgeText));
                }
                SetStatus(sw != null
                    ? $"강제 스캔: 바코드 인식 ({sw.Symbology}) + 이미지 저장 완료"
                    : "강제 스캔: 바코드 인식 실패 (이미지는 저장됨)");
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

    /// <summary>바코드 리딩값 표시: GS1 응용식별자(01,10,17,11,240,21,30 등)만 빨간색으로 강조</summary>
    private void SetBarcodeDisplay(string text)
    {
        BarcodeText.Inlines.Clear();
        if (string.IsNullOrEmpty(text)) return;
        foreach (var token in Gs1Parser.Tokenize(text))
        {
            var run = new System.Windows.Documents.Run(token.Text);
            if (token.IsAi)
            {
                run.Foreground = System.Windows.Media.Brushes.Red;
            }
            BarcodeText.Inlines.Add(run);
        }
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
        SetStatus(CurrentMode == 0 ? "모드: 바코드만 리딩" : "모드: 바코드 + 이미지 캡처");
    }

    private void ClearScan_Click(object sender, RoutedEventArgs e)
    {
        SetBarcodeDisplay("");
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
                FileRuleText.Text, "0123456789012", "EAN-13", "", ".jpg");
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

    private static DrawingBitmap LoadBitmap(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var tmp = new DrawingBitmap(ms);
        return new DrawingBitmap(tmp); // 스트림 분리 복사본
    }

    // ==================== Multi / Continuous 탭 ====================
    // 디코드 모드 리스너: 트리거를 당길 때마다 스캐너 하드웨어가 1회 판독.
    // 중복은 행 추가 없이 횟수만 증가(무음), 신규 바코드만 비프.

    /// <summary>멀티 탭 진입 시 자동 준비 (버튼 없이 트리거만으로 동작)</summary>
    private void MultiArm()
    {
        _multiActive = true;
        EnsureScannerMode("멀티 스캔");
    }

    /// <summary>멀티 탭 이탈 시 일반 스캔 설정으로 복귀</summary>
    private void MultiDisarm()
    {
        _multiActive = false;
        EnsureScannerMode("일반 스캔 복귀");
    }

    /// <summary>멀티 탭 사진 저장 옵션: 판독 직후 이미지 모드로 촬영 트리거 (배경 처리, 1건씩)</summary>
    private void MultiCaptureImage(BarcodeData b)
    {
        if (_scanner?.ActiveScanner is not { } dev) return;
        // 이전 촬영이 3초 안에 완료되지 않았으면 미수신으로 보고 새로 진행
        bool inFlight = _multiImageScan != null && (DateTime.Now - _multiImageAt).TotalSeconds < 3;
        if (inFlight) return;
        _multiImageScan = b;
        _multiImageAt = DateTime.Now;
        Task.Run(() =>
        {
            Thread.Sleep(80); // 디코드 세션 종료 대기
            bool ok = _scanner!.CaptureImage(dev.Id);
            if (!ok) Dispatcher.BeginInvoke(() => _multiImageScan = null);
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
            MultiGrid.ScrollIntoView(row);
            FlashRow(row);
        }
        else
        {
            var ai = Gs1Parser.Parse(b.Text);
            row = new MultiScanRow
            {
                No = _multiRows.Count + 1,
                TimeText = b.Time.ToString("HH:mm:ss"),
                Gtin = ai.GetValueOrDefault("01", ""),
                Lot = ai.GetValueOrDefault("10", ""),
                Mfg = Gs1Parser.FormatGs1Date(ai.GetValueOrDefault("11", "")),
                Exp = Gs1Parser.FormatGs1Date(ai.GetValueOrDefault("17", "")),
                Pn = ai.GetValueOrDefault("240", ""),
                Sn = ai.GetValueOrDefault("21", ""),
                Upn = ai.GetValueOrDefault("30", ""),
                Raw = b.Text,
            };
            _multiSeen[key] = row;
            _multiRows.Add(row);
            MultiGrid.ScrollIntoView(row);
            FlashRow(row);
            // 신규 바코드에만 비프 1회 (중복은 무음)
            if (_scanner?.ActiveScanner is { } bdev)
                Task.Run(() => _scanner.Beep(bdev.Id, 0));
        }
        MultiTotalText.Text = _multiTotal.ToString();
        BigCountText.Text = _multiSeen.Count.ToString();
    }

    /// <summary>판독된 행을 연한 앰버색으로 잠깐 하이라이트 후 부드럽게 사라지게 표시</summary>
    private void FlashRow(MultiScanRow row)
    {
        MultiGrid.UpdateLayout();
        if (MultiGrid.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.Controls.DataGridRow dgr)
            return;
        var color = System.Windows.Media.Color.FromArgb(70, 255, 193, 7); // 연한 앰버
        var brush = new System.Windows.Media.SolidColorBrush(color);
        dgr.Background = brush;
        var anim = new System.Windows.Media.Animation.ColorAnimation
        {
            To = System.Windows.Media.Color.FromArgb(0, 255, 193, 7),
            Duration = TimeSpan.FromMilliseconds(650),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        anim.Completed += (_, _) => dgr.Background = System.Windows.Media.Brushes.Transparent;
        brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
    }

    private void MultiClear_Click(object sender, RoutedEventArgs e)
    {
        _multiRows.Clear();
        _multiSeen.Clear();
        _multiTotal = 0;
        MultiTotalText.Text = "0";
        BigCountText.Text = "0";
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
        sb.AppendLine("No,Time,GTIN,LOT,MFG,EXP,PN,SN,UPN,Raw,Count");
        foreach (var r in _multiRows)
            sb.AppendLine($"{r.No},{r.TimeText},{CsvText(r.Gtin)},{CsvText(r.Lot)},{Csv(r.Mfg)},{Csv(r.Exp)},{CsvText(r.Pn)},{CsvText(r.Sn)},{CsvText(r.Upn)},{CsvText(r.Raw)},{r.Count}");
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        SetStatus("CSV 저장 완료: " + dlg.FileName);
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>엑셀에서 숫자(지수 표기)로 변환되지 않도록 ="값" 수식 형태로 감싼다 (GTIN 등)</summary>
    private static string CsvText(string s) =>
        string.IsNullOrEmpty(s) ? "" : "\"=\"\"" + s.Replace("\"", "\"\"") + "\"\"\"";

    // ==================== 탭 전환 ====================

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.OriginalSource != MainTabs) return;
        // 탭 전환마다 스캐너 상태를 재보장해 인식 불능 상태를 복구한다
        if (MainTabs.SelectedIndex == 1) MultiArm();
        else MultiDisarm();
    }
}
