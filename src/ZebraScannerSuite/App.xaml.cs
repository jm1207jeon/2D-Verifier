using System.Windows;

namespace ZebraScannerSuite;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("예기치 않은 오류가 발생했습니다:\n" + args.Exception.Message,
                "Zebra Scanner Suite", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
    }
}
