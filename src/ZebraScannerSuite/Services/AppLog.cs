using System.IO;

namespace ZebraScannerSuite.Services;

/// <summary>간단한 진단 로그 (%APPDATA%\ZebraScannerSuite\app.log).
/// 화면 상태바에만 잠깐 표시되고 사라지는 오류를 사후 추적할 수 있도록 남긴다.
/// 로그 기록 자체가 실패해도 앱 동작에는 영향을 주지 않는다 (1MB 초과 시 이전 로그로 교체).</summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZebraScannerSuite");
    public static readonly string FilePath = Path.Combine(Dir, "app.log");
    private const long MaxBytes = 1024 * 1024;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Dir, "app.prev.log"), overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
