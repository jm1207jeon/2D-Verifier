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
    private readonly ObservableCollection<ScanListRow> _scanRows = new(); // 실시간 목록
    private readonly Dictionary<string, ScanListRow> _scanSeen = new();   // 중복 판정 (스캔값 기준)
    private string? _scanLastLot;   // 직전 행의 LOT (묶음 판정)
    private bool _scanGroupAlt;     // LOT 묶음 배경 교대 플래그
    private byte[]? _lastImageBytes;
    private int _lastPixelW, _lastPixelH;
    private BarcodeData? _pendingScan;          // 이미지 대기 중인 바코드
    private bool _awaitingScanImage;
    // 이미지 수신 대기 중 들어온 추가 스캔: 사진이 다른 바코드 이름으로 저장되는 일이 없도록
    // 순서대로 예약해 두었다가 앞 촬영이 끝나면 하나씩 촬영한다.
    private readonly Queue<BarcodeData> _captureQueue = new();
    private const int CaptureQueueMax = 20;
    // 블루투스/저속 스캐너(DS2278 등)의 이미지 전송 시간을 고려해 여유 있게 대기
    private readonly DispatcherTimer _imageTimeout = new() { Interval = TimeSpan.FromSeconds(8) };

    // 종료 진행 중 (배경 재시도 루프가 종료 시 복원한 스캐너 설정을 다시 덮어쓰지 않도록)
    private volatile bool _closing;
    // 확인 대화상자/파일 대화상자가 떠 있는 동안 스캔이 대화상자로 타이핑되거나(Enter=확인!)
    // 목록에 섞여 들어가지 않도록 스캔 이벤트를 보류한다
    private int _dialogDepth;
    // 설정 자동 저장 디바운스 (경로 타이핑 중 글자마다 파일을 쓰지 않도록)
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _settingsSaveFailNotified;

    // 일반(비 Zebra) HID 키보드 스캐너 지원: 창이 활성일 때 빠른 연속 입력 + Enter를 스캔으로 인식
    private readonly StringBuilder _kbdScanBuf = new();
    private DateTime _kbdScanLast = DateTime.MinValue;
    private const int KbdScanGapMs = 80; // 스캐너 타이핑 문자 간격 상한 (사람 타이핑과 구분)

    // 키보드 에뮬레이터(Caps Lock 토글 원인) 재비활성화 주기 제한
    private DateTime _lastKbdEmuOff = DateTime.MinValue;

    // 강제 스캔: 하드웨어 디코더 재시도 대기용
    private TaskCompletionSource<BarcodeData>? _forceDecodeTcs;

    // Multi 탭 상태
    private readonly ObservableCollection<MultiScanRow> _multiRows = new();   // 세로: 고유 코드당 1행
    private readonly Dictionary<string, MultiScanRow> _multiSeen = new();
    private readonly ObservableCollection<MultiLotRow> _multiLotRows = new(); // 가로: LOT당 1행 (세로 데이터에서 실시간 파생)
    private readonly Dictionary<string, MultiLotRow> _multiLotSeen = new();
    // 헤더 클릭 정렬 상태 (클릭 순서대로 다중 정렬, 오름→내림→해제 순환)
    private readonly List<(string Member, System.ComponentModel.ListSortDirection Dir)> _multiSortV = new();
    private readonly List<(string Member, System.ComponentModel.ListSortDirection Dir)> _multiSortH = new();
    private int _multiTotal;
    private bool _multiActive; // 멀티 스캔 수신 중
    private bool _multiDirty;  // 마지막 CSV 내보내기 이후 추가된 데이터가 있는지 (종료/지우기 경고용)
    private BarcodeData? _multiImageScan; // 멀티 탭 배경 사진 저장 대기 중인 판독 건
    private readonly Queue<BarcodeData> _multiCaptureQueue = new(); // 촬영 중 들어온 판독 건 예약 (누락 방지)
    // 촬영 요청 후 이미지가 8초 안에 오지 않으면 해제하고 예약 건으로 넘어간다 (블루투스 저속 전송 고려)
    private readonly DispatcherTimer _multiImageTimeout = new() { Interval = TimeSpan.FromSeconds(8) };

    // 스캐너 자동 관리 (수동 새로고침/호스트 모드 버튼 대체)
    private DateTime _lastHostSwitch = DateTime.MinValue; // SNAPI 자동 전환 반복 방지
    private int _hostSwitchAttempts;                      // 세션당 자동 전환 시도 횟수 (무한 재부팅 루프 방지)
    private const int HostSwitchMaxAttempts = 3;
    private readonly DispatcherTimer _scannerWatchdog = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _watchdogBusy;
    private bool _sdkFailNotified; // SDK 미설치 안내는 한 번만 (10초마다 상태바를 덮어쓰지 않도록)
    private int _scannerModeBusy;    // EnsureScannerMode 실행 중 표시 (워치독과의 동시 SDK 호출 방지)
    private int _scannerModePending; // 실행 중에 들어온 재요청 - 유실 방지용 (루프 종료 후 즉시 재실행)

    public MainWindow()
    {
        InitializeComponent();
        FieldsGrid.ItemsSource = _fields;
        ScanListGrid.ItemsSource = _scanRows;
        MultiGrid.ItemsSource = _multiRows;
        MultiLotGrid.ItemsSource = _multiLotRows;
        _imageTimeout.Tick += ImageTimeout_Tick;
        _multiImageTimeout.Tick += MultiImageTimeout_Tick;
        _scannerWatchdog.Tick += ScannerWatchdog_Tick;
        _settingsSaveTimer.Tick += (_, _) => { _settingsSaveTimer.Stop(); SaveSettingsNow(); };

        // 작은 화면(노트북 1366x768 등)에서 창이 화면 밖으로 잘리지 않도록 작업 영역에 맞춘다
        var wa = SystemParameters.WorkArea;
        if (Width > wa.Width - 16) Width = Math.Max(MinWidth, wa.Width - 16);
        if (Height > wa.Height - 16) Height = Math.Max(MinHeight, wa.Height - 16);
    }

    /// <summary>10초마다 연결 상태 점검: 연동 실패 시 재시도, 목록이 비면 재검색.
    /// (수동 [스캐너 새로고침] 버튼 대체 - PnP 이벤트를 놓친 경우의 안전망)</summary>
    private async void ScannerWatchdog_Tick(object? sender, EventArgs e)
    {
        if (_closing || _watchdogBusy || _scannerModeBusy != 0) return; // EnsureScannerMode 진행 중이면 충돌 방지 위해 건너뜀
        _watchdogBusy = true;
        try
        {
            if (_scanner == null)
            {
                await Task.Run(() => InitScanner(quiet: true));
                UpdateScannerStatus();
                if (_scanner != null) EnsureScannerMode("자동 재연결");
            }
            else if (_scanner.ScannerCount == 0)
            {
                await Task.Run(_scanner.RefreshScanners);
                UpdateScannerStatus();
                if (_scanner.ScannerCount > 0) EnsureScannerMode("자동 재연결");
            }
        }
        catch (Exception ex) { AppLog.Error("워치독 오류", ex); }
        finally { _watchdogBusy = false; }
    }

    // ==================== 초기화 / 종료 ====================

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsService.Load();
        ApplySettingsToUi();

        _ = Task.Run(WorkerLoop);

        await Task.Run(() => InitScanner());
        UpdateScannerStatus();

        // 시작 시 스캐너 동작 모드 보장 (재시도 포함):
        // 이전 세션에서 촬영 모드로 남아 있던 상태 복구 + 강제 스캔 설정 재무장
        EnsureScannerMode("초기화");
        _scannerWatchdog.Start();
    }

    private void InitScanner(bool quiet = false)
    {
        CoreScannerService? svc = null;
        try
        {
            svc = new CoreScannerService();
            svc.BarcodeScanned += b => Dispatcher.BeginInvoke(() => OnBarcode(b));
            svc.ImageCaptured += img => Dispatcher.BeginInvoke(() => OnImage(img));
            svc.DevicesChanged += () => Dispatcher.BeginInvoke(() =>
            {
                UpdateScannerStatus();
                // 재연결(모드 전환/재플러그) 시 동작 모드·키보드 에뮬레이터 설정 재적용
                if (_scanner?.ScannerCount > 0) EnsureScannerMode("스캐너 재연결");
            });
            svc.StatusMessage += m => Dispatcher.BeginInvoke(() => SetStatus(m, StatusLevel.Warn));
            svc.Open();
            _scanner = svc; // 완전히 열린 뒤에만 공개 (반쯤 초기화된 객체를 다른 스레드가 쓰지 않도록)
            _sdkFailNotified = false;
        }
        catch (Exception ex)
        {
            try { svc?.Dispose(); } catch { }
            _scanner = null;
            AppLog.Error("스캐너 연동 실패", ex);
            // SDK 미설치 PC(일반 키보드 스캐너 사용)에서는 10초마다 같은 안내로 상태바를 덮어쓰지 않도록 한 번만 표시
            if (!quiet || !_sdkFailNotified)
            {
                _sdkFailNotified = true;
                Dispatcher.BeginInvoke(() =>
                    SetStatus("스캐너 연동 불가: " + ex.Message + " (일반 키보드 스캐너는 계속 사용 가능)", StatusLevel.Warn));
            }
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 내보내지 않은 멀티 스캔 데이터가 있으면 종료 전에 확인 (검사 기록 유실 방지)
        if (_multiDirty && _multiRows.Count > 0)
        {
            var r = ShowConfirm(
                $"멀티 스캔 목록에 CSV로 내보내지 않은 데이터 {_multiRows.Count}건이 있습니다.\n" +
                "종료하면 목록은 사라집니다. (CSV 내보내기는 [멀티 스캔] 탭에서 할 수 있습니다)\n\n그래도 종료하시겠습니까?",
                "종료 확인", MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }

        _closing = true;
        _scannerWatchdog.Stop();
        _imageTimeout.Stop();
        _multiImageTimeout.Stop();
        _settingsSaveTimer.Stop();
        try
        {
            CollectSettingsFromUi();
            SettingsService.Save(_settings);
        }
        catch (Exception ex) { AppLog.Error("종료 시 설정 저장 실패", ex); }
        try
        {
            if (_scanner?.ActiveScanner is { } s)
            {
                if (_multiActive) _scanner.SetBeepAfterGoodDecode(s.Id, true);
                // 촬영 모드로 남겨두면 다음 실행/다른 프로그램에서 스캔이 안 되므로 디코드 모드로 복원
                _scanner.SetCaptureBarcodeMode(s.Id);

                // 종료 시 스캐너를 일반 키보드 스캐너 상태로 완전 복원:
                // - 드라이버 키보드 에뮬레이터 원복 (실행 중에만 껐던 설정)
                // - SNAPI였다면 USB HID 키보드 모드로 영구 전환해 저장 상태 자체를 키보드 스캐너로.
                //   (구버전이 영구 SNAPI로 남긴 스캐너도 이 종료 처리 한 번으로 정상화됨)
                //   다음 실행 시 앱이 다시 임시(비영구)로 SNAPI 전환한다.
                bool emu = _scanner.SetKeyboardEmulator(true);
                bool host = true;
                if (s.Type.Contains("SNAPI", StringComparison.OrdinalIgnoreCase))
                    host = _scanner.SwitchHostMode(s.Id, "XUA-45001-3", permanent: true); // USB HID 키보드
                if (!emu || !host)
                    AppLog.Warn($"종료 시 스캐너 복원 일부 실패 (emulator={emu}, hostMode={host}) - USB 재연결로 복구됨");
            }
            _scanner?.Dispose();
        }
        catch (Exception ex) { AppLog.Error("종료 시 스캐너 복원 실패", ex); }
    }

    /// <summary>확인 대화상자. 기본 버튼은 '아니오'라 대화상자 위에서 스캐너가 보내는 Enter로
    /// 삭제/종료가 실행되지 않는다. 대화상자가 떠 있는 동안 스캔 이벤트는 보류된다.</summary>
    private MessageBoxResult ShowConfirm(string message, string caption, MessageBoxImage icon)
    {
        _dialogDepth++;
        try
        {
            return MessageBox.Show(this, message, caption, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
        }
        finally { _dialogDepth--; }
    }

    private void ApplySettingsToUi()
    {
        SaveDirText.Text = _settings.ImageSaveDirectory;
        ForceScanCheck.IsChecked = _settings.ForceScanEnabled;
        MultiSaveImageCheck.IsChecked = _settings.MultiSaveImage;
        SaveDateFolderCheck.IsChecked = _settings.SaveDateFolder;
        SaveLotFolderCheck.IsChecked = _settings.SaveLotFolder;
        DupIgnoreCheck.IsChecked = _settings.IgnoreDuplicates;

        ModeBarcodeOnly.IsChecked = _settings.ScanMode == 0;
        ModeBarcodeImage.IsChecked = _settings.ScanMode >= 1;

        // 멀티 탭 보기 모드 + UDI 컬럼 표시 + 사용자 컬럼 폭 복원
        MultiViewVertical.IsChecked = _settings.MultiViewMode == 0;
        MultiViewHorizontal.IsChecked = _settings.MultiViewMode == 1;
        MultiUdiCheck.IsChecked = _settings.ShowUdiColumn;
        ApplyMultiViewVisibility();
        ApplyUdiColumnVisibility();
        ApplyColWidths(MultiGrid, _settings.MultiColWidthsV);
        ApplyColWidths(MultiLotGrid, _settings.MultiColWidthsH);
    }

    /// <summary>UDI(원문) 컬럼 표시/숨김 - 기본 숨김, 옵션 체크 시 두 보기 모두에 표시</summary>
    private void ApplyUdiColumnVisibility()
    {
        var vis = MultiUdiCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MultiUdiColumn.Visibility = vis;
        MultiLotUdiColumn.Visibility = vis;
    }

    private void MultiUdi_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyUdiColumnVisibility();
        AutoSaveSettings();
    }

    private void CollectSettingsFromUi()
    {
        _settings.ImageSaveDirectory = SaveDirText.Text.Trim();
        _settings.WedgeOutput = true; // 키보드 입력은 항상 사용 (UI 옵션 제거됨)
        _settings.ForceScanEnabled = ForceScanCheck.IsChecked == true;
        _settings.MultiSaveImage = MultiSaveImageCheck.IsChecked == true;
        _settings.SaveDateFolder = SaveDateFolderCheck.IsChecked == true;
        _settings.SaveLotFolder = SaveLotFolderCheck.IsChecked == true;
        _settings.IgnoreDuplicates = DupIgnoreCheck.IsChecked == true;
        _settings.ScanMode = CurrentMode;
        _settings.MultiViewMode = MultiViewHorizontal.IsChecked == true ? 1 : 0;
        _settings.ShowUdiColumn = MultiUdiCheck.IsChecked == true;

        // 사용자가 조절한 컬럼 폭 저장 - 숨김/미배치 컬럼(폭 0)은 기존 저장값을 유지
        _settings.MultiColWidthsV = MergeColWidths(MultiGrid, _settings.MultiColWidthsV);
        _settings.MultiColWidthsH = MergeColWidths(MultiLotGrid, _settings.MultiColWidthsH);
    }

    private static List<double> MergeColWidths(System.Windows.Controls.DataGrid grid, List<double>? previous)
    {
        var result = new List<double>(grid.Columns.Count);
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            double w = grid.Columns[i].ActualWidth;
            if (w <= 0 && previous != null && previous.Count == grid.Columns.Count) w = previous[i];
            result.Add(double.IsFinite(w) ? w : 0);
        }
        return result;
    }

    /// <summary>저장된 컬럼 폭 복원 (컬럼 수가 바뀐 구버전 설정은 무시, 비정상 값은 건너뜀)</summary>
    private static void ApplyColWidths(System.Windows.Controls.DataGrid grid, List<double>? widths)
    {
        if (widths == null || widths.Count != grid.Columns.Count) return;
        for (int i = 0; i < widths.Count; i++)
            if (double.IsFinite(widths[i]) && widths[i] > 10 && widths[i] < 3000)
                grid.Columns[i].Width = new DataGridLength(widths[i]);
    }

    /// <summary>설정 컨트롤 변경 시 자동 저장 (설정 저장 버튼 대체)</summary>
    private void Setting_Changed(object sender, RoutedEventArgs e) => AutoSaveSettings();

    private void Setting_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // 저장 경로: 키보드 스캐너 오입력·오타로 깨진 값은 시각적으로 경고하고 저장하지 않는다
        bool valid = SettingsService.IsValidDirectory(SaveDirText.Text.Trim());
        SaveDirText.Background = valid ? System.Windows.Media.Brushes.White
                                       : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 228, 225));
        SaveDirText.ToolTip = valid ? null : "올바른 절대 경로가 아닙니다 (예: D:\\UDI\\Images). 이 값은 저장되지 않습니다.";
        if (valid) AutoSaveSettings();
        else SetStatus("저장 경로가 올바르지 않아 적용되지 않았습니다 - [찾아보기]로 폴더를 다시 선택하세요.", StatusLevel.Warn);
    }

    /// <summary>변경 후 0.6초 동안 추가 변경이 없을 때 1회 저장 (연속 변경 시 파일 쓰기 횟수 최소화)</summary>
    private void AutoSaveSettings()
    {
        if (!IsLoaded || _closing) return;
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        if (_closing) return;
        try
        {
            CollectSettingsFromUi();
            SettingsService.Save(_settings);
            _settingsSaveFailNotified = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("설정 자동 저장 실패", ex);
            if (!_settingsSaveFailNotified)
            {
                _settingsSaveFailNotified = true;
                SetStatus("설정 저장 실패 (재실행 시 이전 설정으로 돌아갈 수 있음): " + ex.Message, StatusLevel.Error);
            }
        }
    }

    private int CurrentMode => ModeBarcodeImage.IsChecked == true ? 1 : 0;

    // ==================== 상태 표시 ====================

    private enum StatusLevel { Info, Warn, Error }

    private static readonly System.Windows.Media.Brush StatusInfoBrush = System.Windows.Media.Brushes.Black;
    private static readonly System.Windows.Media.Brush StatusWarnBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0x5B, 0x00));
    private static readonly System.Windows.Media.Brush StatusErrorBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x1E, 0x1E));

    /// <summary>상태바 메시지. 경고/오류는 색과 굵기로 구분해 정상 메시지 사이에서 눈에 띄게 한다.
    /// (상태바가 유일한 알림 채널이므로 실패는 반드시 Warn/Error로 표시할 것)</summary>
    private void SetStatus(string msg, StatusLevel level = StatusLevel.Info)
    {
        StatusText.Text = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        StatusText.Foreground = level switch
        {
            StatusLevel.Error => StatusErrorBrush,
            StatusLevel.Warn => StatusWarnBrush,
            _ => StatusInfoBrush,
        };
        StatusText.FontWeight = level == StatusLevel.Info ? FontWeights.Normal : FontWeights.Bold;
        StatusText.ToolTip = StatusText.Text; // 긴 메시지가 잘려도 전체를 볼 수 있게
        if (level != StatusLevel.Info) AppLog.Warn(msg);
    }

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
            SetStatus($"현재 {s.Type} 모드 - 바코드 스캔은 정상 동작하며, 이미지 캡처(사진 저장/강제 스캔)만 제한됩니다.", StatusLevel.Warn);
    }

    /// <summary>현재 스캐너가 이미지 캡처(SNAPI 이미징)를 지원하는 모드인지.
    /// DS9908 등 SNAPI 기종은 지원, 기타 모드/일반 스캐너는 바코드 스캔만 가능.</summary>
    private bool ImagingSupported =>
        _scanner?.ActiveScanner?.Type.Contains("SNAPI", StringComparison.OrdinalIgnoreCase) == true;

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
            catch (Exception ex)
            {
                AppLog.Error("배경 작업 오류", ex);
                await Dispatcher.BeginInvoke(() => SetStatus("처리 오류: " + ex.Message, StatusLevel.Error));
            }
            Interlocked.Decrement(ref _pendingJobs);
            await Dispatcher.BeginInvoke(UpdateBusy);
        }
    }

    // ==================== 바코드 / 이미지 이벤트 라우팅 ====================

    private void OnBarcode(BarcodeData b)
    {
        try
        {
            // 강제 스캔의 하드웨어 재판독 대기 중이면 해당 흐름으로 전달
            if (_forceDecodeTcs != null && _forceDecodeTcs.TrySetResult(b)) return;
            if (_closing) return;

            // 제어문자만 있는 등 비어 있는 판독값은 목록/키보드 입력에 섞이지 않도록 무시
            if (string.IsNullOrWhiteSpace(b.Text))
            {
                SetStatus("빈 바코드 데이터 수신 - 무시했습니다 (라벨 인쇄 상태를 확인하세요).", StatusLevel.Warn);
                return;
            }
            // 확인/파일 대화상자가 떠 있는 동안의 스캔은 처리하지 않는다 (대화상자에 Enter가 들어가거나 목록에 섞임 방지)
            if (_dialogDepth > 0)
            {
                SetStatus("대화상자가 열려 있어 이 스캔은 처리하지 않았습니다 - 대화상자를 닫고 다시 스캔하세요.", StatusLevel.Warn);
                return;
            }

            // 모드 전환/재연결 후 드라이버의 HID 키보드 에뮬레이터가 되살아나 스캔마다
            // Caps Lock이 토글되는 문제 방지: 스캔 이벤트 시 주기적으로 재차 비활성화
            var sc = _scanner;
            if (sc != null && (DateTime.Now - _lastKbdEmuOff).TotalSeconds > 5)
            {
                _lastKbdEmuOff = DateTime.Now;
                Task.Run(() => { try { sc.SetKeyboardEmulator(false); } catch { } });
            }

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
        catch (Exception ex)
        {
            AppLog.Error("바코드 처리 오류", ex);
            SetStatus("바코드 처리 오류: " + ex.Message, StatusLevel.Error);
        }
    }

    private void HandleScanTab(BarcodeData b)
    {
        SetBarcodeDisplay(b.Text);
        SymbologyText.Text = b.Symbology;
        _fields.Clear();
        foreach (var f in DataExtractionService.Apply(b.Text, _settings.ExtractionRules))
            _fields.Add(f);

        // 실시간 목록 갱신 (신규=행 추가, 중복=기존 행 하이라이트만)
        bool duplicate = RecordScan(b);

        // 자체 키보드 웨지(항상 사용): 다른 창(엑셀 등)이 포커스일 때 커서 위치로 값 + Enter 전송.
        // '중복값 무시'가 켜져 있으면 중복 스캔은 입력을 생략한다.
        if (!IsActive && !(duplicate && DupIgnoreCheck.IsChecked == true))
            SendWedge(b.Text);

        int mode = CurrentMode;
        if (mode == 0 || _scanner?.ActiveScanner == null)
        {
            SetStatus(duplicate ? $"중복 스캔 (이미 목록에 있음): {b.Symbology}" : $"바코드 리딩 완료: {b.Symbology}");
            return;
        }
        if (!ImagingSupported)
        {
            // 이미징 미지원 스캐너(모드): 바코드는 정상 처리하고 촬영만 생략
            SetStatus($"바코드 리딩 완료: {b.Symbology} (이 스캐너는 이미지 캡처 미지원 - 바코드만 처리)", StatusLevel.Warn);
            return;
        }

        // 모드 ②: 디코드 직후 같은 위치의 이미지를 자동 캡처.
        // 앞 촬영의 이미지를 아직 기다리는 중이면 예약해 두고 순서대로 촬영한다
        // (예약 없이 덮어쓰면 앞 사진이 뒤 바코드 이름으로 저장되는 오류가 생긴다).
        if (_awaitingScanImage)
        {
            if (_captureQueue.Count >= CaptureQueueMax)
            {
                SetStatus($"촬영 예약이 가득 참({CaptureQueueMax}건) - 이 스캔의 사진은 생략됩니다. 잠시 후 다시 스캔하세요.", StatusLevel.Warn);
                return;
            }
            _captureQueue.Enqueue(b);
            SetStatus($"이미지 수신 대기 중 - 촬영 예약 {_captureQueue.Count}건 (앞 촬영이 끝나면 순서대로 촬영)");
            return;
        }
        StartScanCapture(b);
    }

    /// <summary>키보드 웨지 전송 (배경). 차단되면(대상 프로그램이 관리자 권한 등) 상태바에 안내.</summary>
    private void SendWedge(string text)
    {
        Task.Run(() =>
        {
            bool ok = KeyboardWedge.TypeText(text);
            if (!ok)
                Dispatcher.BeginInvoke(() => SetStatus(
                    "키보드 입력이 차단되었습니다 - 입력 대상 프로그램(엑셀 등)이 관리자 권한으로 실행 중이면 이 프로그램도 관리자 권한으로 실행하세요.",
                    StatusLevel.Error));
        });
    }

    /// <summary>일반 스캔 모드 ②의 촬영 시작: 대기 상태 설정 → 타임아웃 시작 → 촬영 명령</summary>
    private void StartScanCapture(BarcodeData b)
    {
        if (_scanner?.ActiveScanner is not { } dev) return;
        var sc = _scanner;
        _pendingScan = b;
        _awaitingScanImage = true;
        _imageTimeout.Stop();
        _imageTimeout.Start();
        SetStatus("이미지 캡처 중... (자동 트리거)");
        Interlocked.Increment(ref _pendingJobs);
        UpdateBusy();
        Task.Run(() =>
        {
            bool ok = false;
            try
            {
                Thread.Sleep(35); // 디코드 세션 종료 대기 (최소화 - 더 줄이면 촬영 명령이 무시됨)
                ok = sc.CaptureImage(dev.Id);
            }
            catch (Exception ex) { AppLog.Error("촬영 명령 오류", ex); }
            Interlocked.Decrement(ref _pendingJobs);
            Dispatcher.BeginInvoke(() =>
            {
                UpdateBusy();
                if (!ok && _pendingScan == b)
                {
                    _awaitingScanImage = false;
                    _pendingScan = null;
                    _imageTimeout.Stop();
                    SetStatus("이미지 캡처 명령 실패 - 바코드는 처리됨. 스캐너 연결/SNAPI(이미징) 모드를 확인하세요.", StatusLevel.Error);
                    CaptureNext();
                }
            });
        });
    }

    /// <summary>예약된 다음 촬영 진행 (앞 촬영 완료/실패/시간 초과 후 호출)</summary>
    private void CaptureNext()
    {
        if (_closing || _awaitingScanImage || _captureQueue.Count == 0) return;
        var next = _captureQueue.Dequeue();
        StartScanCapture(next);
    }

    private void ImageTimeout_Tick(object? sender, EventArgs e)
    {
        _imageTimeout.Stop();
        if (_awaitingScanImage)
        {
            _awaitingScanImage = false;
            var missed = _pendingScan;
            _pendingScan = null;
            if (_scanner?.ActiveScanner is { } d) SafeReleaseTrigger(d.Id);
            SetStatus($"이미지 수신 시간 초과 - 이 스캔의 사진은 저장되지 않았습니다{(missed != null ? $" ({missed.Symbology})" : "")}. " +
                      "SNAPI(이미징) 모드 및 연결 상태를 확인하고 다시 스캔하세요.", StatusLevel.Error);
            CaptureNext();
        }
    }

    private void SafeReleaseTrigger(int scannerId)
    {
        var sc = _scanner;
        if (sc == null) return;
        Task.Run(() => { try { sc.ReleaseTrigger(scannerId); } catch (Exception ex) { AppLog.Error("트리거 해제 오류", ex); } });
    }

    private void OnImage(byte[] imageBytes)
    {
        try
        {
            if (_scanner?.ActiveScanner is { } d) SafeReleaseTrigger(d.Id);
            _imageTimeout.Stop();
            if (_closing) return;

            // 멀티 탭 촬영분: 배경 사진 저장만 수행하고 화면에는 표시하지 않음
            // (촬영 직후 탭을 옮겼더라도 그 판독 건의 사진이므로 같은 이름으로 저장)
            if (_multiImageScan != null)
            {
                var mScan = _multiImageScan;
                _multiImageScan = null;
                _multiImageTimeout.Stop();
                var mSettings = _settings;
                Enqueue(async () =>
                {
                    string path = SaveImageWithMessage(imageBytes, mScan.Text, mScan.Symbology, mSettings);
                    await Dispatcher.BeginInvoke(() => SetStatus("사진 저장 완료: " + path));
                });
                MultiCaptureNext();
                return;
            }

            if (!ShowPreview(imageBytes))
            {
                SetStatus("수신한 이미지를 표시할 수 없습니다 (손상된 데이터) - 저장은 계속 진행합니다.", StatusLevel.Warn);
            }

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
            CollectSettingsFromUi();
            var settingsSnapshot = _settings;

            Enqueue(async () =>
            {
                // 1) 이미지 저장
                string path = SaveImageWithMessage(imageBytes, scan.Text, scan.Symbology, settingsSnapshot);
                await Dispatcher.BeginInvoke(() => SetStatus("이미지 저장 완료: " + path));
            });

            // 배경에서 바코드 위치 탐색 → 찾으면 은은한 바운딩 박스 표시 (판독·저장 흐름과 무관, 지연 없음)
            Task.Run(() =>
            {
                var pts = TryLocateBarcode(imageBytes);
                if (pts != null) Dispatcher.BeginInvoke(() => ShowPreview(imageBytes, pts));
            });

            // 대기 중 예약된 다음 스캔 촬영
            CaptureNext();
        }
        catch (Exception ex)
        {
            AppLog.Error("이미지 처리 오류", ex);
            SetStatus("이미지 처리 오류: " + ex.Message, StatusLevel.Error);
        }
    }

    /// <summary>이미지 저장. 실패 시 원인별로 사용자가 조치할 수 있는 메시지로 바꿔 던진다
    /// (워커 루프가 상태바에 표시).</summary>
    private static string SaveImageWithMessage(byte[] bytes, string barcode, string symbology, AppSettings settings)
    {
        try
        {
            return ImageSaveService.Save(bytes, barcode, symbology, "", settings);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"이미지 저장 실패 - 폴더에 쓰기 권한이 없습니다: {settings.ImageSaveDirectory} ({ex.Message})", ex);
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070070)) // ERROR_DISK_FULL
        {
            throw new IOException("이미지 저장 실패 - 디스크 공간이 부족합니다: " + settings.ImageSaveDirectory, ex);
        }
    }

    // ==================== 강제 스캔 모드 ====================
    // SNAPI에서는 '디코드 실패' 이벤트가 호스트로 전달되지 않아 "트리거 2회"를 감지할 수 없다.
    // 대신 모드를 켜면(F9) 트리거 1회 = 촬영이 되고, 촬영 이미지에서
    // 소프트웨어 → 하드웨어 순으로 바코드를 인식하고 이미지를 항상 저장한다.

    private void ForceScan_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AutoSaveSettings();
        if (_scanner is not { IsOpen: true })
        {
            if (ForceScanCheck.IsChecked == true)
                SetStatus("스캐너 미연결 - 강제 스캔 모드는 스캐너 연결 후 동작합니다.", StatusLevel.Warn);
            return;
        }
        if (ForceScanCheck.IsChecked == true && !ImagingSupported)
        {
            SetStatus("이 스캐너는 촬영(이미징)을 지원하지 않아 강제 스캔은 일반 디코드로 동작합니다.", StatusLevel.Warn);
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
        if (_closing) return;
        var sc = _scanner;
        if (sc is not { IsOpen: true }) return;
        if (System.Threading.Interlocked.CompareExchange(ref _scannerModeBusy, 1, 0) != 0)
        {
            // 이미 다른 EnsureScannerMode 재시도 루프가 진행 중 - 이번 요청은 버리지 않고
            // "보류" 표시만 해두면, 진행 중이던 루프가 끝나는 즉시 최신 UI 상태로 재실행된다.
            System.Threading.Interlocked.Exchange(ref _scannerModePending, 1);
            return;
        }
        bool multiTab, forceScan;
        try
        {
            multiTab = Dispatcher.Invoke(() => MainTabs.SelectedIndex == 1);
            forceScan = !multiTab && Dispatcher.Invoke(() => ForceScanCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            // 종료 중 Dispatcher가 닫힌 경우 등 - 조용히 포기
            AppLog.Warn("EnsureScannerMode UI 상태 조회 실패: " + ex.Message);
            System.Threading.Interlocked.Exchange(ref _scannerModeBusy, 0);
            return;
        }
        Task.Run(() =>
        {
          try
          {
            for (int attempt = 1; attempt <= 5 && !_closing; attempt++)
            {
                var dev = sc.ActiveScanner;
                if (dev == null)
                {
                    Thread.Sleep(700);
                    sc.RefreshScanners();
                    continue;
                }
                try
                {
                    // SNAPI 모드가 아니면 자동 전환 (촬영·트리거 제어에 필요, 수동 버튼 대체)
                    // 전환 후 스캐너가 재부팅되며 PnP 재연결 이벤트가 EnsureScannerMode를 다시 부른다.
                    bool imaging = dev.Type.Contains("SNAPI", StringComparison.OrdinalIgnoreCase);
                    if (!imaging && (DateTime.Now - _lastHostSwitch).TotalSeconds > 20)
                    {
                        if (_hostSwitchAttempts >= HostSwitchMaxAttempts)
                        {
                            // 전환 명령은 성공하는데 재연결 후에도 SNAPI가 아닌 기종(전환 미지원/크래들 등):
                            // 무한 재부팅 루프를 막기 위해 포기하고 바코드 전용으로 동작한다.
                            if (_hostSwitchAttempts == HostSwitchMaxAttempts)
                            {
                                _hostSwitchAttempts++;
                                Dispatcher.BeginInvoke(() => SetStatus(
                                    $"이 스캐너는 SNAPI(이미징) 모드로 전환되지 않아 바코드 스캔 전용으로 동작합니다 (사진 저장·강제 스캔 불가).",
                                    StatusLevel.Warn));
                            }
                        }
                        else
                        {
                            _hostSwitchAttempts++;
                            _lastHostSwitch = DateTime.Now;
                            Dispatcher.BeginInvoke(() => SetStatus(
                                $"{reason}: 스캐너를 SNAPI(이미징) 모드로 자동 전환 중... 재부팅 후 자동 재연결됩니다. ({_hostSwitchAttempts}/{HostSwitchMaxAttempts})"));
                            // 실행 중에만 SNAPI 사용: permanent=false → 스캐너 저장 설정은 바꾸지 않으므로
                            // 앱이 비정상 종료되어도 전원 재인가(케이블 재연결)만 하면 원래 모드로 복귀한다.
                            bool switched = sc.SwitchHostMode(dev.Id, _settings.PreferredHostMode, permanent: false);
                            if (switched) return; // 재부팅 → PnP 재연결 이벤트가 다음 EnsureScannerMode를 부름
                            // 전환 명령 자체가 실패 - 20초 쿨다운 후 워치독이 자동 재시도하도록 남겨두고 계속 진행
                            Dispatcher.BeginInvoke(() => SetStatus(
                                $"{reason}: SNAPI 모드 전환 명령 실패 - 잠시 후 자동 재시도됩니다.", StatusLevel.Warn));
                        }
                    }

                    // Caps Lock 토글의 원인인 HID 키보드 에뮬레이터는 항상 끈다 (자체 웨지로 대체)
                    sc.SetKeyboardEmulator(false);
                    sc.ScanEnable(dev.Id);
                    sc.SetBeepAfterGoodDecode(dev.Id, !multiTab); // 멀티 탭: 중복 무음, 신규만 앱 비프
                    // 이미징 미지원 기종/모드에서는 촬영 모드 대신 디코드 모드로 동작 (바코드 스캔 우선)
                    if (_closing) return;
                    bool ok = forceScan && imaging
                        ? sc.SetCaptureImageMode(dev.Id)
                        : sc.SetCaptureBarcodeMode(dev.Id);
                    if (ok)
                    {
                        Dispatcher.BeginInvoke(() => SetStatus(
                            $"{reason}: {(multiTab ? "멀티 스캔(트리거=1회 판독)" : forceScan ? "강제 스캔(트리거=촬영)" : "바코드 디코드")} 모드 준비 완료"));
                        return;
                    }
                }
                catch (Exception ex) { AppLog.Error($"EnsureScannerMode 시도 {attempt} 오류", ex); }
                Thread.Sleep(700);
            }
            if (!_closing)
                Dispatcher.BeginInvoke(() => SetStatus(
                    $"{reason}: 스캐너 모드 설정 실패 - USB를 재연결하면 자동으로 복구됩니다.", StatusLevel.Error));
          }
          finally
          {
              System.Threading.Interlocked.Exchange(ref _scannerModeBusy, 0);
              // 진행 중에 유실 방지로 보류해둔 요청이 있으면 최신 UI 상태로 즉시 재실행
              if (System.Threading.Interlocked.Exchange(ref _scannerModePending, 0) != 0 && !_closing)
                  EnsureScannerMode(reason);
          }
        });
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            ForceScanCheck.IsChecked = ForceScanCheck.IsChecked != true;
            e.Handled = true;
            return;
        }

        // 일반(HID 키보드) 스캐너: 빠른 연속 입력 직후의 Enter를 스캔 종료로 인식.
        // CoreScanner 스캐너가 연결돼 있으면 비활성 (에뮬레이터 에코로 인한 이중 입력 방지).
        // 텍스트 입력란에 포커스가 있으면 관여하지 않는다 (HID 입력창은 자체 처리).
        if (_scanner?.ActiveScanner != null) { _kbdScanBuf.Clear(); return; }
        if (e.Key is Key.Enter or Key.Return &&
            Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            if (_kbdScanBuf.Length >= 5 &&
                (DateTime.Now - _kbdScanLast).TotalMilliseconds <= KbdScanGapMs * 2)
            {
                string text = _kbdScanBuf.ToString();
                _kbdScanBuf.Clear();
                OnBarcode(new BarcodeData { Text = text, Symbology = "HID-KBD", Time = DateTime.Now });
                e.Handled = true;
            }
            else
            {
                _kbdScanBuf.Clear();
            }
        }
    }

    /// <summary>일반(비 Zebra) 키보드 스캐너 리스너: 창이 활성일 때 문자 입력을 관찰만 한다.
    /// 문자 간격이 사람 타이핑보다 빠른 연속 입력만 버퍼에 모으고, Enter에서 스캔으로 확정한다.
    /// CoreScanner(SNAPI) 스캐너는 키 입력이 아닌 이벤트로 들어오므로 중복 처리되지 않는다.</summary>
    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_scanner?.ActiveScanner != null) return; // CoreScanner 스캐너 연결 시 리스너 비활성
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return; // 직접 입력 중이면 무시
        var now = DateTime.Now;
        if ((now - _kbdScanLast).TotalMilliseconds > KbdScanGapMs) _kbdScanBuf.Clear();
        _kbdScanLast = now;
        foreach (char c in e.Text)
            if (!char.IsControl(c)) _kbdScanBuf.Append(c);
    }

    private void ProcessForceImage(byte[] bytes)
    {
        CollectSettingsFromUi();
        var settingsSnapshot = _settings;
        SetStatus("강제 스캔: 바코드 분석 중...");

        Enqueue(async () =>
        {
            // ① 고속 소프트웨어 디코드 (~수십 ms)
            float[]? swPoints = null;
            BarcodeData? sw = TrySoftwareDecode(bytes, thorough: false, out swPoints);

            // ② 실패 시 하드웨어 디코더 재판독 (최대 0.6초) - 일반 스캔과 동일 성능
            if (sw == null && _scanner?.ActiveScanner is { } hwDev)
            {
                await Dispatcher.BeginInvoke(() => SetStatus("강제 스캔: 하드웨어 디코더로 재판독 중..."));
                sw = await TryHardwareDecodeAsync(hwDev.Id);
            }

            // ③ 마지막으로 정밀 소프트웨어 디코드 (TryHarder+대비보정)
            if (sw == null) sw = TrySoftwareDecode(bytes, thorough: true, out swPoints);

            // ④ 이미지 저장 (바코드 인식 여부와 무관하게 항상 저장)
            string baseName = sw?.Text ?? "NOCODE";
            string path = SaveImageWithMessage(bytes, baseName, sw?.Symbology ?? "IMAGE", settingsSnapshot);

            await Dispatcher.BeginInvoke(() =>
            {
                if (swPoints != null) ShowPreview(bytes, swPoints); // 인식 위치 하이라이트
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
                // 실시간 목록 + 자체 키보드 웨지 (중복값 무시 옵션 반영)
                bool fsDuplicate = sw != null && RecordScan(sw);
                if (sw != null && !IsActive && !(fsDuplicate && DupIgnoreCheck.IsChecked == true))
                    SendWedge(sw.Text);
                if (sw != null)
                    SetStatus($"강제 스캔: 바코드 인식 ({sw.Symbology}) + 이미지 저장 완료");
                else
                    SetStatus("강제 스캔: 바코드 인식 실패 - 이미지는 NOCODE 이름으로 저장됨. 라벨을 다시 촬영하세요.", StatusLevel.Warn);
            });

            RearmForceCapture();
        });
    }

    /// <summary>촬영 이미지에서 ZXing으로 바코드 디코드 시도 (강제 스캔 모드용 폴백).
    /// points: 인식 위치 (x,y 쌍) - 화면 하이라이트 표시용.</summary>
    private static BarcodeData? TrySoftwareDecode(byte[] bytes, bool thorough, out float[]? points)
    {
        points = null;
        try
        {
            using var bmp = LoadBitmap(bytes);
            var d = thorough ? SoftwareDecoder.DecodeThorough(bmp) : SoftwareDecoder.DecodeFast(bmp);
            if (d == null) return null;
            points = d.Value.Points;
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
        if (_closing) return;
        var sc = _scanner;
        if (sc?.ActiveScanner is not { } dev) return;
        bool on;
        try { on = Dispatcher.Invoke(() => ForceScanCheck.IsChecked == true && MainTabs.SelectedIndex == 0); }
        catch { return; }
        if (!on) return;
        Task.Run(() =>
        {
            try
            {
                Thread.Sleep(60);
                if (!_closing) sc.SetCaptureImageMode(dev.Id);
            }
            catch (Exception ex) { AppLog.Error("강제 스캔 재무장 오류", ex); }
        });
    }

    /// <summary>일반 스캔 실시간 목록 갱신. 중복이면 기존 행 하이라이트만 하고 true 반환.
    /// 신규 행은 연속된 같은 LOT끼리 배경색으로 묶어 표시한다 (LOT가 바뀌면 새 묶음).</summary>
    private bool RecordScan(BarcodeData b)
    {
        if (_scanSeen.TryGetValue(b.Text, out var row))
        {
            ScanListGrid.ScrollIntoView(row);
            FlashRow(ScanListGrid, row);
            return true;
        }
        var ai = Gs1Parser.Parse(b.Text);
        string lot = ai.GetValueOrDefault("10", "");
        if (_scanRows.Count == 0 || lot != _scanLastLot) _scanGroupAlt = !_scanGroupAlt;
        _scanLastLot = lot;
        var nr = new ScanListRow
        {
            Lot = lot,
            Exp = Gs1Parser.FormatGs1Date(ai.GetValueOrDefault("17", "")),
            Pn = ai.GetValueOrDefault("240", ""),
            Sn = ai.GetValueOrDefault("21", ""),
            GroupBrush = _scanGroupAlt ? "#FFE8F1FA" : "#00FFFFFF", // 묶음별 교대 배경
        };
        _scanSeen[b.Text] = nr;
        _scanRows.Add(nr);
        ScanListBox.Header = $"실시간 목록 {_scanRows.Count}건 (같은 LOT끼리 묶음 표시)";
        ScanListGrid.ScrollIntoView(nr);
        FlashRow(ScanListGrid, nr);
        return false;
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

    /// <summary>미리보기 표시. 디코드 불가(손상/미지원 형식) 이미지는 false 반환 (저장 흐름은 계속).</summary>
    private bool ShowPreview(byte[] bytes, float[]? marks = null)
    {
        _lastImageBytes = bytes;
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
        }
        catch (Exception ex)
        {
            AppLog.Error("미리보기 디코드 실패", ex);
            return false;
        }
        _lastPixelW = bmp.PixelWidth;
        _lastPixelH = bmp.PixelHeight;
        BitmapSource src = bmp;
        if (marks is { Length: >= 2 })
        {
            try { src = RenderHighlight(bmp, marks); } catch { src = bmp; }
        }
        PreviewImage.Source = src;
        NoImageText.Visibility = Visibility.Collapsed;
        return true;
    }

    /// <summary>인식된 바코드 위치에 은은한 주황색 바운딩 박스를 그린 표시용 이미지 생성.
    /// (화면 표시 전용 - 저장 파일은 원본 그대로. 1회 렌더링이라 속도 영향 없음)</summary>
    private static BitmapSource RenderHighlight(BitmapSource bmp, float[] marks)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = 0, maxY = 0;
        for (int i = 0; i + 1 < marks.Length; i += 2)
        {
            minX = Math.Min(minX, marks[i]);     maxX = Math.Max(maxX, marks[i]);
            minY = Math.Min(minY, marks[i + 1]); maxY = Math.Max(maxY, marks[i + 1]);
        }
        // 1D 바코드는 포인트가 스캔 라인 위라 납작해지므로 최소 여백 확보
        double padX = Math.Max(14, (maxX - minX) * 0.08);
        double padY = Math.Max(26, (maxY - minY) * 0.08);
        double x0 = Math.Max(0, minX - padX), y0 = Math.Max(0, minY - padY);
        double x1 = Math.Min(bmp.PixelWidth, maxX + padX), y1 = Math.Min(bmp.PixelHeight, maxY + padY);
        if (x1 - x0 < 4 || y1 - y0 < 4) return bmp;

        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            var stroke = new System.Windows.Media.Pen(
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 255, 152, 0)),
                Math.Max(2.0, bmp.PixelWidth / 400.0));
            var fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(24, 255, 152, 0));
            dc.DrawRoundedRectangle(fill, stroke, new Rect(x0, y0, x1 - x0, y1 - y0), 8, 8);
        }
        var rtb = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>이미지에서 바코드 위치만 탐색 (배경 스레드용, 실패 시 null)</summary>
    private static float[]? TryLocateBarcode(byte[] bytes)
    {
        try
        {
            using var bmp = LoadBitmap(bytes);
            return SoftwareDecoder.DecodeFast(bmp)?.Points;
        }
        catch { return null; }
    }

    // ==================== Tab1 UI 핸들러 ====================

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        SetStatus(CurrentMode == 0 ? "모드: 바코드 리딩" : "모드: 바코드 리딩 + 이미지 캡처");
        AutoSaveSettings();
    }

    private void ClearScan_Click(object sender, RoutedEventArgs e)
    {
        // 목록을 지우면 중복 판정 기록도 함께 사라지므로(같은 라벨을 다시 찍으면 새 값으로 입력됨) 확인
        if (_scanRows.Count > 0)
        {
            var r = ShowConfirm(
                $"실시간 목록 {_scanRows.Count}건과 중복 판정 기록을 모두 지웁니다.\n" +
                "지운 뒤에는 이미 스캔했던 라벨도 새 값으로 처리(키보드 입력)됩니다.\n\n계속하시겠습니까?",
                "목록 지우기", MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        SetBarcodeDisplay("");
        SymbologyText.Text = "-";
        _fields.Clear();
        PreviewImage.Source = null;
        _lastImageBytes = null;
        NoImageText.Visibility = Visibility.Visible;
        // 실시간 목록·중복 기록·촬영 예약도 초기화
        _scanRows.Clear();
        _scanSeen.Clear();
        _scanLastLot = null;
        _scanGroupAlt = false;
        _captureQueue.Clear();
        ScanListBox.Header = "실시간 목록 (같은 LOT끼리 묶음 표시)";
        SetStatus("화면과 실시간 목록을 지웠습니다.");
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

    private void BrowseDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "이미지 저장 폴더 선택" };
        try { if (Directory.Exists(SaveDirText.Text)) dlg.InitialDirectory = SaveDirText.Text; } catch { }
        _dialogDepth++;
        try
        {
            if (dlg.ShowDialog(this) == true)
            {
                SaveDirText.Text = dlg.FolderName; // TextChanged가 자동 저장
            }
        }
        finally { _dialogDepth--; }
    }

    /// <summary>저장 폴더를 탐색기로 연다 (저장된 사진을 바로 확인하는 용도). 없으면 만든 뒤 연다.</summary>
    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        string dir = SaveDirText.Text.Trim();
        if (!SettingsService.IsValidDirectory(dir))
        {
            SetStatus("저장 경로가 올바르지 않아 폴더를 열 수 없습니다.", StatusLevel.Warn);
            return;
        }
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error("폴더 열기 실패", ex);
            SetStatus("폴더를 열 수 없습니다: " + ex.Message, StatusLevel.Error);
        }
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
        if (_scanner?.ActiveScanner == null) return;
        if (!ImagingSupported)
        {
            SetStatus("사진 저장: 이 스캐너는 촬영을 지원하지 않아 바코드만 기록합니다.", StatusLevel.Warn);
            return;
        }
        // 이전 촬영이 완료되지 않았으면 예약 (촬영 누락 방지; 8초 안에 이미지가 안 오면 타임아웃 후 다음 건 진행)
        if (_multiImageScan != null)
        {
            if (_multiCaptureQueue.Count >= CaptureQueueMax)
            {
                SetStatus($"사진 촬영 예약이 가득 참({CaptureQueueMax}건) - 이 판독의 사진은 생략됩니다.", StatusLevel.Warn);
                return;
            }
            _multiCaptureQueue.Enqueue(b);
            return;
        }
        StartMultiCapture(b);
    }

    private void StartMultiCapture(BarcodeData b)
    {
        var sc = _scanner;
        if (sc?.ActiveScanner is not { } dev) { _multiImageScan = null; return; }
        _multiImageScan = b;
        _multiImageTimeout.Stop();
        _multiImageTimeout.Start();
        Task.Run(() =>
        {
            bool ok = false;
            try
            {
                Thread.Sleep(35); // 디코드 세션 종료 대기 (최소화)
                ok = sc.CaptureImage(dev.Id);
            }
            catch (Exception ex) { AppLog.Error("멀티 촬영 명령 오류", ex); }
            if (!ok)
                Dispatcher.BeginInvoke(() =>
                {
                    if (_multiImageScan != b) return;
                    _multiImageScan = null;
                    _multiImageTimeout.Stop();
                    SetStatus("사진 촬영 명령 실패 - 바코드는 기록됨 (스캐너 연결 상태 확인)", StatusLevel.Error);
                    MultiCaptureNext();
                });
        });
    }

    private void MultiCaptureNext()
    {
        if (_closing || _multiImageScan != null || _multiCaptureQueue.Count == 0) return;
        StartMultiCapture(_multiCaptureQueue.Dequeue());
    }

    private void MultiImageTimeout_Tick(object? sender, EventArgs e)
    {
        _multiImageTimeout.Stop();
        if (_multiImageScan == null) return;
        var missed = _multiImageScan;
        _multiImageScan = null;
        if (_scanner?.ActiveScanner is { } d) SafeReleaseTrigger(d.Id);
        SetStatus($"사진 수신 시간 초과 - LOT {Gs1Parser.Parse(missed.Text).GetValueOrDefault("10", "?")} 판독의 사진은 저장되지 않았습니다 (바코드는 기록됨).",
            StatusLevel.Error);
        MultiCaptureNext();
    }

    /// <summary>가로 모드 행에 시리얼 반영: 실제 목록(Sn, CSV용)과 1~15 고정 슬롯(화면용),
    /// 총 수량(QTY)을 함께 갱신한다. 1~15 밖 시리얼은 SnExtra에 별도 누적.</summary>
    private static void MarkLotSerial(MultiLotRow row, string sn)
    {
        row.Serials.Add(sn);
        row.Serials.Sort((a, c) =>
            int.TryParse(a, out int x) && int.TryParse(c, out int y)
                ? x.CompareTo(y) : string.CompareOrdinal(a, c));
        row.Sn = string.Join(", ", row.Serials);
        row.Qty = row.Serials.Count;
        if (int.TryParse(sn, out int n) && n >= 1 && n <= 15)
            row.Slots[n - 1].Scanned = true;
        else
            row.SnExtra = row.SnExtra.Length == 0 ? sn : row.SnExtra + ", " + sn;
    }

    /// <summary>MFG 미기재 라벨의 제조일 역산: 엑셀 EDATE(EXP,-36)+1 (유효기간 36개월 가정).
    /// exp는 "yyyy-MM-dd" 또는 "yyyy-MM"(일자 00) 형식.</summary>
    private static bool TryComputeMfgFromExp(string exp, out string mfg)
    {
        mfg = "";
        if (string.IsNullOrEmpty(exp)) return false;
        string s = exp.Length == 7 ? exp + "-01" : exp; // "yyyy-MM" → 1일로 보정
        if (!DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d))
            return false;
        mfg = d.AddMonths(-36).AddDays(1).ToString("yyyy-MM-dd");
        return true;
    }

    /// <summary>멀티 스캔 중복 판정 키: LOT+SN이 있으면 그 조합(제품 1개 = 1키),
    /// 없으면 스캔 원문. CSV 불러오기로 복원한 항목과 새 스캔의 중복 판정에도 동일 적용.</summary>
    private static string MakeMultiKey(string udi, string lot, string sn) =>
        lot.Length > 0 && sn.Length > 0 ? lot + "|" + sn : udi;

    private void AddMultiScan(BarcodeData b)
    {
        _multiTotal++;

        var ai = Gs1Parser.Parse(b.Text);
        string lot = ai.GetValueOrDefault("10", "");
        string sn = ai.GetValueOrDefault("21", "");
        // AI 30: 사내 라벨은 'M…'=UPN, 표준 GS1의 숫자형 30(수량)은 UPN이 아님. 없으면 '-' 표시.
        string v30 = ai.GetValueOrDefault("30", "");
        string upn = v30.Length > 0 && !v30.All(char.IsDigit) ? v30 : "-";
        string exp = Gs1Parser.FormatGs1Date(ai.GetValueOrDefault("17", ""));
        // MFG(11)가 없으면 EXP 기준 역산: 엑셀 EDATE(EXP,-36)+1 (36개월 전 다음날), 파란색 표시
        string mfg = Gs1Parser.FormatGs1Date(ai.GetValueOrDefault("11", ""));
        bool mfgComputed = false;
        if (mfg.Length == 0 && TryComputeMfgFromExp(exp, out string computed))
        {
            mfg = computed;
            mfgComputed = true;
        }

        // ---- 세로 모드(마스터 데이터): 고유 코드당 1행, 중복은 행 추가 없이 하이라이트만 ----
        string key = MakeMultiKey(b.Text, lot, sn);
        bool isNewCode = !_multiSeen.TryGetValue(key, out var row);
        if (!isNewCode)
        {
            row!.Count++;
            MultiGrid.ScrollIntoView(row);
            FlashRow(MultiGrid, row);
        }
        else
        {
            row = new MultiScanRow
            {
                No = _multiRows.Count + 1,
                TimeText = b.Time.ToString("HH:mm:ss"),
                Gtin = ai.GetValueOrDefault("01", ""),
                Lot = lot,
                Mfg = mfg,
                MfgComputed = mfgComputed,
                Exp = exp,
                Pn = ai.GetValueOrDefault("240", ""),
                Sn = sn,
                Upn = upn,
                Raw = b.Text,
            };
            _multiSeen[key] = row;
            _multiRows.Add(row);
            _multiDirty = true;
            if (_multiSortV.Count > 0) UpdateVerticalGrouping(); // 정렬 중이면 묶음 색 재계산
            MultiGrid.ScrollIntoView(row);
            FlashRow(MultiGrid, row);
            // 신규 바코드에만 비프 1회 (중복은 무음)
            var bsc = _scanner;
            if (bsc?.ActiveScanner is { } bdev)
                Task.Run(() => { try { bsc.Beep(bdev.Id, 0); } catch (Exception ex) { AppLog.Error("비프 오류", ex); } });
            else
                System.Media.SystemSounds.Beep.Play(); // 일반(HID) 스캐너: PC 비프로 대체
        }

        // ---- 가로 모드(파생 뷰): LOT당 1행, SN은 1~15 고정 슬롯 + QTY(총 수량) ----
        string lotKey = lot.Length > 0 ? lot : "@" + b.Text; // LOT 없는 코드는 코드별 행
        if (_multiLotSeen.TryGetValue(lotKey, out var lrow))
        {
            if (isNewCode && sn.Length > 0 && !lrow.Serials.Contains(sn))
            {
                MarkLotSerial(lrow, sn);
                MultiLotGrid.Items.Refresh();
                MultiLotGrid.ScrollIntoView(lrow);
                FlashRow(MultiLotGrid, lrow);
                FlashCell(MultiLotGrid, lrow, MultiLotSnColumn); // 시리얼 추가는 SN 셀을 더 강하게 강조
            }
            else
            {
                MultiLotGrid.ScrollIntoView(lrow);
                FlashRow(MultiLotGrid, lrow);
            }
        }
        else
        {
            lrow = new MultiLotRow
            {
                No = _multiLotRows.Count + 1,
                TimeText = b.Time.ToString("HH:mm:ss"),
                Udi = b.Text,
                Gtin = ai.GetValueOrDefault("01", ""),
                Pn = ai.GetValueOrDefault("240", ""),
                Lot = lot,
                Mfg = mfg,
                MfgComputed = mfgComputed,
                Exp = exp,
                Upn = upn,
            };
            if (sn.Length > 0) MarkLotSerial(lrow, sn);
            _multiLotSeen[lotKey] = lrow;
            _multiLotRows.Add(lrow);
            MultiLotGrid.ScrollIntoView(lrow);
            FlashRow(MultiLotGrid, lrow);
        }

        UpdateMultiCountTexts();
    }

    private void UpdateMultiCountTexts()
    {
        MultiTotalText.Text = _multiTotal.ToString();
        BigCountText.Text = _multiSeen.Count.ToString();
        // 총 배치 수 = 중복 없는 고유 LOT 개수 (LOT 없는 코드의 행은 제외)
        BatchCountText.Text = _multiLotSeen.Keys.Count(k => !k.StartsWith('@')).ToString();
        UpdateMultiDirtyText();
    }

    /// <summary>CSV로 내보내지 않은 데이터가 있는지 표시 (종료/지우기 전 저장 유도)</summary>
    private void UpdateMultiDirtyText()
    {
        if (_multiRows.Count == 0)
        {
            MultiDirtyText.Text = "";
            return;
        }
        MultiDirtyText.Text = _multiDirty ? "● CSV 미저장" : "✓ CSV 저장됨";
        MultiDirtyText.Foreground = _multiDirty ? StatusErrorBrush
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
    }

    /// <summary>판독된 행을 연한 앰버색으로 잠깐 하이라이트 후 부드럽게 사라지게 표시.
    /// 종료 시 Background 로컬값을 지워 행 스타일(LOT 묶음 배경 등)이 다시 적용되게 한다.</summary>
    private static void FlashRow(System.Windows.Controls.DataGrid grid, object row)
    {
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.Controls.DataGridRow dgr)
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
        anim.Completed += (_, _) => dgr.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
    }

    private void MultiClear_Click(object sender, RoutedEventArgs e)
    {
        if (_multiRows.Count == 0) { SetStatus("지울 목록이 없습니다."); return; }
        // 되돌릴 수 없는 삭제: 건수와 저장 여부를 보여주고 확인 (기본 버튼은 '아니오')
        string saved = _multiDirty ? "아직 CSV로 내보내지 않았습니다!" : "CSV로 내보낸 상태입니다.";
        var r = ShowConfirm(
            $"멀티 스캔 목록 {_multiRows.Count}건(고유 판독)을 모두 지웁니다. 되돌릴 수 없습니다.\n{saved}\n\n정말 지우시겠습니까?",
            "목록 지우기", MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;

        _multiRows.Clear();
        _multiSeen.Clear();
        _multiLotRows.Clear();
        _multiLotSeen.Clear();
        _multiCaptureQueue.Clear();
        _multiTotal = 0;
        _multiDirty = false;
        UpdateMultiCountTexts();
        SetStatus("멀티 스캔 목록을 지웠습니다.");
    }

    /// <summary>세로/가로 보기 전환. 데이터는 두 뷰 모두 실시간 유지되므로 언제든 전환 가능.</summary>
    private void MultiView_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyMultiViewVisibility();
        AutoSaveSettings();
    }

    private void ApplyMultiViewVisibility()
    {
        bool horiz = MultiViewHorizontal.IsChecked == true;
        MultiGrid.Visibility = horiz ? Visibility.Collapsed : Visibility.Visible;
        MultiLotGrid.Visibility = horiz ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>헤더 클릭 정렬: 오름차순 → 내림차순 → 기본(해제) 순환.
    /// 서로 다른 컬럼을 이어서 클릭하면 클릭 순서대로 다중 정렬이 조합된다.
    /// 스캔으로 새 행이 추가될 때도 현재 정렬 순서에 맞는 위치에 실시간 삽입된다.</summary>
    private void MultiGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true; // 기본 정렬 동작 대신 순환/조합 규칙 적용
        var grid = (System.Windows.Controls.DataGrid)sender;
        var sorts = grid == MultiLotGrid ? _multiSortH : _multiSortV;
        string member = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(member)) return;

        int idx = sorts.FindIndex(s => s.Member == member);
        if (idx < 0)
            sorts.Add((member, System.ComponentModel.ListSortDirection.Ascending));
        else if (sorts[idx].Dir == System.ComponentModel.ListSortDirection.Ascending)
            sorts[idx] = (member, System.ComponentModel.ListSortDirection.Descending);
        else
            sorts.RemoveAt(idx); // 내림차순에서 한 번 더 클릭 → 기본(정렬 해제)

        ApplySorts(grid, sorts);
        if (grid == MultiGrid) UpdateVerticalGrouping();
    }

    /// <summary>세로 모드 정렬 시 첫 번째 정렬 컬럼의 같은 값끼리 배경색으로 묶어 표시
    /// (일반 스캔 실시간 목록의 LOT 묶음과 동일한 방식). 정렬 해제 시 색상 제거.</summary>
    private void UpdateVerticalGrouping()
    {
        string? member = _multiSortV.Count > 0 ? _multiSortV[0].Member : null;
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(MultiGrid.ItemsSource);
        var ordered = view.Cast<object>().OfType<MultiScanRow>().ToList();
        bool alt = false;
        string? prev = null;
        foreach (var r in ordered)
        {
            if (member == null)
            {
                r.GroupBrush = "#00FFFFFF";
                continue;
            }
            string cur = member switch
            {
                "No" => r.No.ToString(),
                "TimeText" => r.TimeText,
                "Raw" => r.Raw,
                "Gtin" => r.Gtin,
                "Pn" => r.Pn,
                "Lot" => r.Lot,
                "Sn" or "SnNum" => r.Sn,
                "Mfg" => r.Mfg,
                "Exp" => r.Exp,
                "Upn" => r.Upn,
                _ => "",
            };
            if (prev != null && cur != prev) alt = !alt;
            prev = cur;
            r.GroupBrush = alt ? "#FFE8F1FA" : "#00FFFFFF";
        }
        MultiGrid.Items.Refresh();
    }

    private static void ApplySorts(System.Windows.Controls.DataGrid grid,
        List<(string Member, System.ComponentModel.ListSortDirection Dir)> sorts)
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(grid.ItemsSource);
        if (view == null) return;
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (var (m, d) in sorts)
                view.SortDescriptions.Add(new System.ComponentModel.SortDescription(m, d));
        }
        // 헤더 정렬 화살표 표시 동기화
        foreach (var col in grid.Columns)
        {
            col.SortDirection = null;
            foreach (var (m, d) in sorts)
                if (m == col.SortMemberPath) { col.SortDirection = d; break; }
        }
    }

    /// <summary>특정 셀을 행 하이라이트보다 진하게 강조 (가로 모드에서 시리얼 추가 시 SN 셀).
    /// 템플릿 컬럼은 셀 내용의 부모가 ContentPresenter이므로 비주얼 트리를 거슬러 셀을 찾는다.</summary>
    private static void FlashCell(System.Windows.Controls.DataGrid grid, object row, DataGridColumn col)
    {
        grid.UpdateLayout();
        if (grid.ItemContainerGenerator.ContainerFromItem(row) is not System.Windows.Controls.DataGridRow dgr)
            return;
        DependencyObject? node = col.GetCellContent(dgr);
        while (node != null && node is not System.Windows.Controls.DataGridCell)
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        if (node is not System.Windows.Controls.DataGridCell cell)
            return;
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(170, 255, 152, 0)); // 진한 주황
        cell.Background = brush;
        var anim = new System.Windows.Media.Animation.ColorAnimation
        {
            To = System.Windows.Media.Color.FromArgb(0, 255, 152, 0),
            Duration = TimeSpan.FromMilliseconds(900),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        anim.Completed += (_, _) => cell.ClearValue(System.Windows.Controls.Control.BackgroundProperty);
        brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
    }

    /// <summary>CSV 불러오기: 이전에 내보낸 목록을 복원해 이어서 검사한다.
    /// 불러온 항목은 중복 판정(LOT+SN)에 포함되어, 이미 스캔했던 제품을 다시 찍으면
    /// 새 행이 생기지 않고 기존 행 하이라이트만 된다. 세로/가로 CSV 모두 지원.</summary>
    private void MultiImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "CSV 불러오기",
            Filter = "CSV 파일|*.csv|모든 파일|*.*",
        };
        _dialogDepth++;
        bool chosen;
        try { chosen = dlg.ShowDialog(this) == true; }
        finally { _dialogDepth--; }
        if (!chosen) return;

        try
        {
            // 인코딩: BOM이 있으면 그에 따르고(이 프로그램 내보내기=UTF-8 BOM), 없으면 UTF-8로 시도
            string[] lines;
            try { lines = File.ReadAllLines(dlg.FileName, new UTF8Encoding(false, true)); }
            catch (DecoderFallbackException)
            {
                // 엑셀 'CSV(쉼표로 분리)' 저장본은 ANSI(CP949) - 시스템 기본 코드페이지로 재시도
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                lines = File.ReadAllLines(dlg.FileName, Encoding.GetEncoding(949));
            }
            if (lines.Length < 2) { SetStatus("CSV 불러오기: 데이터가 없습니다.", StatusLevel.Warn); return; }

            // 헤더에서 컬럼 위치 파악 (세로/가로 CSV, 엑셀 재저장본 모두 허용)
            var header = CsvUtil.ParseLine(lines[0]).Select(h => CsvUtil.Unwrap(h).ToUpperInvariant()).ToList();
            int iUdi = header.IndexOf("UDI"), iGtin = header.IndexOf("GTIN"), iPn = header.IndexOf("PN");
            int iLot = header.IndexOf("LOT"), iSn = header.IndexOf("SN"), iMfg = header.IndexOf("MFG");
            int iExp = header.IndexOf("EXP"), iUpn = header.IndexOf("UPN"), iTime = header.IndexOf("TIME");
            if (iLot < 0 || iSn < 0)
            {
                SetStatus("CSV 불러오기 실패: LOT/SN 컬럼을 찾을 수 없습니다 (이 프로그램에서 내보낸 CSV를 사용하세요).", StatusLevel.Error);
                return;
            }

            static string Field(List<string> f, int i) => i >= 0 && i < f.Count ? CsvUtil.Unwrap(f[i]) : "";

            int imported = 0, skipped = 0, invalid = 0;
            for (int li = 1; li < lines.Length; li++)
            {
                if (string.IsNullOrWhiteSpace(lines[li])) continue;
                var f = CsvUtil.ParseLine(lines[li]);
                string lot = Field(f, iLot);
                string udi = Field(f, iUdi);
                // 가로 CSV는 SN 셀에 여러 시리얼이 콤마로 들어있음 → 시리얼별 1건으로 복원
                var serials = Field(f, iSn).Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
                if (serials.Count == 0) serials.Add("");
                // 식별 정보가 전혀 없는 줄(빈 셀만 있는 행, 엑셀 합계 행 등)은 건너뜀
                if (udi.Length == 0 && lot.Length == 0 && serials.All(s => s.Length == 0)) { invalid++; continue; }
                foreach (string sn in serials)
                {
                    if (ImportUnit(udi, Field(f, iGtin), Field(f, iPn), lot, sn,
                                   Field(f, iMfg), Field(f, iExp), Field(f, iUpn), Field(f, iTime)))
                        imported++;
                    else
                        skipped++;
                }
            }

            MultiGrid.Items.Refresh();
            MultiLotGrid.Items.Refresh();
            if (_multiSortV.Count > 0) UpdateVerticalGrouping();
            if (imported > 0) _multiDirty = true; // 불러온 뒤 이어 스캔한 결과는 다시 내보내야 완전한 기록
            UpdateMultiCountTexts();
            SetStatus($"CSV 불러오기 완료: {imported}건 복원" +
                      (skipped > 0 ? $" (중복 {skipped}건 제외)" : "") +
                      (invalid > 0 ? $" (식별 정보 없는 {invalid}줄 무시)" : "") +
                      " - 이어서 스캔하면 불러온 항목과의 중복이 자동 제외됩니다.",
                      imported == 0 ? StatusLevel.Warn : StatusLevel.Info);
        }
        catch (IOException ex)
        {
            AppLog.Error("CSV 불러오기 실패", ex);
            SetStatus("CSV 불러오기 실패 - 파일이 다른 프로그램(엑셀 등)에서 열려 있으면 닫은 뒤 다시 시도하세요. " + ex.Message, StatusLevel.Error);
        }
        catch (Exception ex)
        {
            AppLog.Error("CSV 불러오기 실패", ex);
            SetStatus("CSV 불러오기 실패: " + ex.Message, StatusLevel.Error);
        }
    }

    /// <summary>CSV의 1개 제품(시리얼 단위)을 목록에 복원. 이미 있으면 false.</summary>
    private bool ImportUnit(string udi, string gtin, string pn, string lot, string sn,
                            string mfg, string exp, string upn, string time)
    {
        string key = MakeMultiKey(udi.Length > 0 ? udi : $"{lot}|{sn}", lot, sn);
        if (_multiSeen.ContainsKey(key)) return false;

        bool mfgComputed = false;
        if (mfg.Length == 0 && TryComputeMfgFromExp(exp, out string computed))
        {
            mfg = computed;
            mfgComputed = true;
        }

        var row = new MultiScanRow
        {
            No = _multiRows.Count + 1,
            TimeText = time.Length > 0 ? time : "(가져옴)",
            Gtin = gtin, Lot = lot, Mfg = mfg, MfgComputed = mfgComputed,
            Exp = exp, Pn = pn, Sn = sn, Upn = upn.Length > 0 ? upn : "-", Raw = udi,
        };
        _multiSeen[key] = row;
        _multiRows.Add(row);
        _multiTotal++;

        string lotKey = lot.Length > 0 ? lot : "@" + (udi.Length > 0 ? udi : key);
        if (!_multiLotSeen.TryGetValue(lotKey, out var lrow))
        {
            lrow = new MultiLotRow
            {
                No = _multiLotRows.Count + 1,
                TimeText = row.TimeText,
                Udi = udi, Gtin = gtin, Pn = pn, Lot = lot,
                Mfg = mfg, MfgComputed = mfgComputed, Exp = exp,
                Upn = row.Upn,
            };
            _multiLotSeen[lotKey] = lrow;
            _multiLotRows.Add(lrow);
        }
        if (sn.Length > 0 && !lrow.Serials.Contains(sn)) MarkLotSerial(lrow, sn);
        return true;
    }

    private void MultiExport_Click(object sender, RoutedEventArgs e)
    {
        if (_multiRows.Count == 0) { SetStatus("내보낼 데이터가 없습니다.", StatusLevel.Warn); return; }
        bool horiz = MultiViewHorizontal.IsChecked == true;
        var dlg = new SaveFileDialog
        {
            Title = "CSV 내보내기",
            Filter = "CSV 파일|*.csv",
            FileName = $"multiscan_{(horiz ? "lot" : "scan")}_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        _dialogDepth++;
        bool chosen;
        try { chosen = dlg.ShowDialog(this) == true; }
        finally { _dialogDepth--; }
        if (!chosen) return;

        // 현재 보기 모드의 컬럼 구조와 화면 정렬 순서 그대로 내보낸다
        var sb = new StringBuilder();
        int rows = 0;
        if (horiz)
        {
            sb.AppendLine("No,Time,UDI,GTIN,PN,LOT,SN,QTY,MFG,EXP,UPN");
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(MultiLotGrid.ItemsSource);
            foreach (var o in view)
                if (o is MultiLotRow r)
                {
                    rows++;
                    sb.AppendLine($"{r.No},{r.TimeText},{CsvText(r.Udi)},{CsvText(r.Gtin)},{CsvText(r.Pn)},{CsvText(r.Lot)},{CsvText(r.Sn)},{r.Qty},{Csv(r.Mfg)},{Csv(r.Exp)},{CsvText(r.Upn)}");
                }
        }
        else
        {
            sb.AppendLine("No,Time,UDI,GTIN,PN,LOT,SN,MFG,EXP,UPN");
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(MultiGrid.ItemsSource);
            foreach (var o in view)
                if (o is MultiScanRow r)
                {
                    rows++;
                    sb.AppendLine($"{r.No},{r.TimeText},{CsvText(r.Raw)},{CsvText(r.Gtin)},{CsvText(r.Pn)},{CsvText(r.Lot)},{CsvText(r.Sn)},{Csv(r.Mfg)},{Csv(r.Exp)},{CsvText(r.Upn)}");
                }
        }
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            _multiDirty = false;
            UpdateMultiDirtyText();
            SetStatus($"CSV 저장 완료 ({rows}행, {(horiz ? "가로" : "세로")} 보기): " + dlg.FileName);
        }
        catch (IOException ex)
        {
            AppLog.Error("CSV 저장 실패", ex);
            SetStatus("CSV 저장 실패 - 파일이 다른 프로그램(엑셀 등)에서 열려 있는지 확인 후 다시 시도하세요. 목록은 그대로 유지됩니다.", StatusLevel.Error);
        }
        catch (Exception ex)
        {
            AppLog.Error("CSV 저장 실패", ex);
            SetStatus("CSV 저장 실패: " + ex.Message + " (목록은 그대로 유지됩니다)", StatusLevel.Error);
        }
    }

    private static string Csv(string s) => CsvUtil.Field(s);
    private static string CsvText(string s) => CsvUtil.TextField(s);

    // ==================== 탭 전환 ====================

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.OriginalSource != MainTabs) return;
        // 탭 전환마다 스캐너 상태를 재보장해 인식 불능 상태를 복구한다
        if (MainTabs.SelectedIndex == 1) MultiArm();
        else MultiDisarm();
    }
}
