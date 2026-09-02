using System.Threading;
using System.Windows;
using ZebraScannerSuite.Services;

namespace ZebraScannerSuite;

public partial class App : Application
{
    private const string AppTitle = "UDI Capture_ver.1";
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 중복 실행 방지: 두 개가 동시에 뜨면 같은 스캔이 두 번 처리되고(키보드 입력 2회, 사진 2장)
        // 스캐너 설정을 서로 되돌리는 충돌이 생기므로 한 번에 하나만 실행한다.
        _singleInstance = new Mutex(true, @"Local\UDICapture_ver1_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("UDI Capture가 이미 실행 중입니다.\n실행 중인 창을 사용하세요.",
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppLog.Info($"앱 시작 v{typeof(App).Assembly.GetName().Version}");

        // UI 스레드 예외: 사용자에게 알리고 앱은 계속 실행 (스캔 중 작업 손실 방지)
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI 예외", args.Exception);
            MessageBox.Show("예기치 않은 오류가 발생했습니다:\n" + args.Exception.Message +
                            "\n\n프로그램은 계속 실행됩니다. 문제가 반복되면 로그 파일을 확인하세요:\n" + AppLog.FilePath,
                AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // 배경 작업(Task.Run)에서 관찰되지 않은 예외: 조용히 사라지지 않도록 로그에 남긴다
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("배경 작업 예외", args.Exception);
            args.SetObserved();
        };
        // 그 외 스레드(COM 콜백 등)의 치명적 예외: 종료 전 최소한 원인을 남긴다
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Error("치명적 예외 (프로세스 종료)", args.ExceptionObject as Exception);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("앱 종료");
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
