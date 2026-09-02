using System.Text;

namespace ZebraScannerSuite.Services;

/// <summary>CSV 읽기/쓰기 보조. 내보내기는 엑셀에서 GTIN 등이 숫자(지수 표기)로 바뀌지 않도록
/// ="값" 수식 형태로 감싸고, 불러오기는 그 래핑과 엑셀 재저장본(일반 값) 모두를 허용한다.</summary>
public static class CsvUtil
{
    /// <summary>CSV 한 줄 분해 (따옴표 필드, "" 이스케이프 지원)</summary>
    public static List<string> ParseLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>엑셀 텍스트 보호용 ="값" 래핑 제거 (엑셀에서 재저장한 일반 값도 그대로 통과)</summary>
    public static string Unwrap(string f)
    {
        f = f.Trim();
        if (f.StartsWith("=\"") && f.EndsWith("\"") && f.Length >= 3)
            f = f[2..^1];
        else if (f.StartsWith('=') && f.Length > 1)
            f = f[1..].Trim('"');
        return f.Trim();
    }

    /// <summary>일반 필드 인용 (콤마/따옴표/줄바꿈 포함 시에만 따옴표)</summary>
    public static string Field(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>엑셀에서 숫자(지수 표기)로 변환되지 않도록 ="값" 수식 형태로 감싼다 (GTIN 등)</summary>
    public static string TextField(string s) =>
        string.IsNullOrEmpty(s) ? "" : "\"=\"\"" + s.Replace("\"", "\"\"") + "\"\"\"";
}
