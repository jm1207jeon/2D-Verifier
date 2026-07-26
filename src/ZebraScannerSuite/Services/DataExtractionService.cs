using System.Text.RegularExpressions;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>
/// 획득된 바코드에서 위치/응용식별자/정규식 기반으로 원하는 데이터만 추출하여
/// 별도 필드로 표시하기 위한 서비스. (예: YYYY-MM-DD, LOT 8자리, 품번)
/// </summary>
public static class DataExtractionService
{
    public static List<FieldValue> Apply(string barcodeText, IEnumerable<ExtractionRule> rules)
    {
        var fields = new List<FieldValue>();
        Dictionary<string, string>? gs1 = null;

        foreach (var rule in rules)
        {
            string value = "";
            try
            {
                switch (rule.Type?.Trim().ToUpperInvariant())
                {
                    case "GS1":
                        gs1 ??= Gs1Parser.Parse(barcodeText);
                        gs1.TryGetValue(rule.Param1.Trim(), out value!);
                        value ??= "";
                        if (rule.DateConvert && value.Length == 6)
                            value = Gs1Parser.FormatGs1Date(value);
                        break;

                    case "REGEX":
                        var m = Regex.Match(barcodeText, rule.Param1);
                        if (m.Success)
                            value = m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value;
                        if (rule.DateConvert) value = ConvertDate(value);
                        break;

                    case "SUBSTR":
                        var clean = Gs1Parser.StripAim(barcodeText);
                        if (int.TryParse(rule.Param1, out int start) && int.TryParse(rule.Param2, out int len)
                            && start >= 1 && start <= clean.Length)
                        {
                            len = Math.Min(len, clean.Length - (start - 1));
                            value = clean.Substring(start - 1, len);
                        }
                        if (rule.DateConvert) value = ConvertDate(value);
                        break;
                }
            }
            catch (Exception ex)
            {
                value = "(규칙 오류: " + ex.Message + ")";
            }
            fields.Add(new FieldValue { Name = rule.Name, Value = value });
        }
        return fields;
    }

    private static string ConvertDate(string v)
    {
        if (v.Length == 6 && v.All(char.IsDigit)) return Gs1Parser.FormatGs1Date(v);
        if (v.Length == 8 && v.All(char.IsDigit)) return $"{v[..4]}-{v[4..6]}-{v[6..8]}";
        return v;
    }
}
