using System.IO;
using System.Text.RegularExpressions;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>
/// 이미지 저장. 파일명 규칙 토큰:
///   {DATE}/{DATE:fmt} {TIME}/{TIME:fmt} {BARCODE} {SYMBOLOGY} {SEQ}/{SEQ:n} {OCR}
/// "###" 표기도 {SEQ:3} 으로 처리. 중복 시 SEQ 자동 증가.
/// </summary>
public static class ImageSaveService
{
    public static string DetectExtension(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8) return ".jpg";
        if (data.Length >= 2 && data[0] == 0x42 && data[1] == 0x4D) return ".bmp";
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50) return ".png";
        if (data.Length >= 4 && ((data[0] == 0x49 && data[1] == 0x49) || (data[0] == 0x4D && data[1] == 0x4D))) return ".tif";
        return ".jpg";
    }

    public static string SanitizeFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        s = s.Replace(' ', '_').Replace('\u001D', '_');
        return s.Length > 80 ? s[..80] : s;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
        "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
    };

    /// <summary>LOT 값을 하위 폴더명으로 정제. "."/".." 처럼 상위 폴더로 빠져나가는 이름,
    /// 윈도우 예약 이름(CON, NUL 등), 끝의 점/공백은 폴더 생성 실패나 경로 이탈을 일으키므로 보정한다.
    /// 쓸 수 있는 이름이 남지 않으면 빈 문자열(폴더 생략).</summary>
    public static string SanitizeFolderName(string s)
    {
        s = SanitizeFileName(s ?? "").Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s.All(c => c == '.' || c == '_')) return "";
        string stem = s.Split('.')[0];
        if (ReservedNames.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>이미지 저장 후 전체 경로 반환.
    /// 옵션에 따라 날짜(YYYY-MM-DD)·LOT명 하위 폴더를 자동 생성해 그 안에 저장한다.</summary>
    public static string Save(byte[] imageBytes, string barcode, string symbology, string ocr, AppSettings settings)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            throw new InvalidOperationException("이미지 데이터가 비어 있어 저장하지 않았습니다.");
        string dir = settings.ImageSaveDirectory;
        if (!SettingsService.IsValidDirectory(dir))
            throw new InvalidOperationException(
                $"이미지 저장 경로가 올바르지 않습니다: '{dir}' - [일반 스캔] 탭에서 저장 경로를 다시 지정하세요.");
        if (settings.SaveDateFolder)
            dir = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd"));
        if (settings.SaveLotFolder)
        {
            string lot = "";
            try { lot = Gs1Parser.Parse(barcode).GetValueOrDefault("10", ""); } catch { }
            string folder = SanitizeFolderName(lot);
            if (folder.Length > 0)
                dir = Path.Combine(dir, folder);
        }
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"저장 폴더를 만들 수 없습니다: {dir} - 드라이브 연결(네트워크/USB) 및 쓰기 권한을 확인하세요. ({ex.Message})", ex);
        }
        string ext = DetectExtension(imageBytes);
        string rule = string.IsNullOrWhiteSpace(settings.FileNameRule)
            ? "{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}" : settings.FileNameRule;

        string path = BuildPath(dir, rule, barcode, symbology, ocr, ext);
        File.WriteAllBytes(path, imageBytes);
        return path;
    }

    public static string BuildPath(string dir, string rule, string barcode, string symbology, string ocr, string ext)
    {
        var now = DateTime.Now;
        // ### → {SEQ:3}
        if (!rule.Contains("{SEQ", StringComparison.OrdinalIgnoreCase) && rule.Contains('#'))
        {
            var m = Regex.Match(rule, "#+");
            rule = rule.Remove(m.Index, m.Length).Insert(m.Index, "{SEQ:" + m.Length + "}");
        }

        string Expand(int seq)
        {
            string s = Regex.Replace(rule, @"\{DATE(?::([^}]+))?\}",
                mm => now.ToString(string.IsNullOrEmpty(mm.Groups[1].Value) ? "yyyyMMdd" : mm.Groups[1].Value));
            s = Regex.Replace(s, @"\{TIME(?::([^}]+))?\}",
                mm => now.ToString(string.IsNullOrEmpty(mm.Groups[1].Value) ? "HHmmss" : mm.Groups[1].Value));
            s = s.Replace("{BARCODE}", SanitizeFileName(barcode));
            s = s.Replace("{SYMBOLOGY}", SanitizeFileName(symbology));
            s = s.Replace("{OCR}", SanitizeFileName(ocr));
            s = Regex.Replace(s, @"\{SEQ(?::(\d+))?\}",
                mm => seq.ToString().PadLeft(mm.Groups[1].Success ? int.Parse(mm.Groups[1].Value) : 3, '0'));
            return SanitizeInvalidPathPart(s);
        }

        bool hasSeq = Regex.IsMatch(rule, @"\{SEQ(:\d+)?\}", RegexOptions.IgnoreCase);

        // 폴더 안 파일 목록을 한 번만 읽어 메모리에서 중복 검사 (매 저장마다 File.Exists를 반복
        // 호출하면 폴더에 파일이 많이 쌓일수록, 특히 네트워크 드라이브에서 느려진다)
        var existing = Directory.Exists(dir)
            ? new HashSet<string>(Directory.EnumerateFiles(dir).Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int seq = 1; seq <= 99999; seq++)
        {
            string name = Expand(seq) + ext;
            if (!existing.Contains(name)) return Path.Combine(dir, name);
            if (!hasSeq)
            {
                // SEQ 토큰이 없으면 뒤에 _(n) 부여
                name = Expand(seq) + $"_({seq})" + ext;
                if (!existing.Contains(name)) return Path.Combine(dir, name);
            }
        }
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
    }

    private static string SanitizeInvalidPathPart(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}
