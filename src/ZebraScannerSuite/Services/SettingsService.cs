using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>설정을 %APPDATA%\ZebraScannerSuite\settings.json 에 저장/복원.
/// 프로그램 재실행 시 이미지 저장 경로 등 이전 값을 그대로 기억한다.
/// 저장은 임시 파일에 쓴 뒤 교체(원자적)하므로 저장 도중 전원이 꺼져도 설정이 깨지지 않는다.</summary>
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
                    return Normalize(s);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("설정 파일 손상 - 기본값으로 대체", ex);
        }
        return new AppSettings { RulesVersion = 2 };
    }

    /// <summary>손으로 편집했거나 일부 항목이 빠진(null) 설정 파일도 안전하게 사용할 수 있도록 보정</summary>
    public static AppSettings Normalize(AppSettings s)
    {
        var defaults = new AppSettings();
        if (!IsValidDirectory(s.ImageSaveDirectory)) s.ImageSaveDirectory = defaults.ImageSaveDirectory;
        if (string.IsNullOrWhiteSpace(s.FileNameRule)) s.FileNameRule = defaults.FileNameRule;
        if (string.IsNullOrWhiteSpace(s.PreferredHostMode)) s.PreferredHostMode = defaults.PreferredHostMode;
        s.MultiColWidthsV ??= new List<double>();
        s.MultiColWidthsH ??= new List<double>();
        s.ExtractionRules ??= AppSettings.DefaultExtractionRules();
        if (s.ExtractionRules.Count == 0) s.ExtractionRules = AppSettings.DefaultExtractionRules();
        return s;
    }

    /// <summary>이미지 저장 경로로 쓸 수 있는 절대 경로인지 (스캐너 오입력 등으로 깨진 값 차단)</summary>
    public static bool IsValidDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (!Path.IsPathRooted(path)) return false;
            Path.GetFullPath(path);
            return true;
        }
        catch { return false; }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOpts));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
