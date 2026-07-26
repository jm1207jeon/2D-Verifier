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

    /// <summary>이미지 저장 후 전체 경로 반환</summary>
    public static string Save(byte[] imageBytes, string barcode, string symbology, string ocr, AppSettings settings)
    {
        Directory.CreateDirectory(settings.ImageSaveDirectory);
        string ext = DetectExtension(imageBytes);
        string rule = string.IsNullOrWhiteSpace(settings.FileNameRule)
            ? "{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}" : settings.FileNameRule;

        string path = BuildPath(settings.ImageSaveDirectory, rule, barcode, symbology, ocr, ext);
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
        for (int seq = 1; seq <= 99999; seq++)
        {
            string candidate = Path.Combine(dir, Expand(seq) + ext);
            if (!File.Exists(candidate)) return candidate;
            if (!hasSeq)
            {
                // SEQ 토큰이 없으면 뒤에 _(n) 부여
                candidate = Path.Combine(dir, Expand(seq) + $"_({seq})" + ext);
                if (!File.Exists(candidate)) return candidate;
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
