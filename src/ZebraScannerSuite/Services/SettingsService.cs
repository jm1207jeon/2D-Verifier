using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>설정을 %APPDATA%\ZebraScannerSuite\settings.json 에 저장/복원.
/// 프로그램 재실행 시 이미지 저장 경로 등 이전 값을 그대로 기억한다.</summary>
public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZebraScannerSuite");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOpts);
                if (s != null)
                {
                    // 구버전 설정의 추출 규칙을 새 기본값(GTIN/LOT/PN/MFG/EXP/SN/UPN)으로 마이그레이션
                    if (s.RulesVersion < 2)
                    {
                        s.ExtractionRules = AppSettings.DefaultExtractionRules();
                        s.RulesVersion = 2;
                    }
                    return s;
                }
            }
        }
        catch { /* 손상된 설정은 기본값으로 대체 */ }
        return new AppSettings { RulesVersion = 2 };
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOpts));
    }
}
