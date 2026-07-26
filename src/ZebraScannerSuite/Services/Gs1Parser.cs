namespace ZebraScannerSuite.Services;

/// <summary>
/// GS1 응용식별자(AI) 파서. GS1-128 / GS1 DataMatrix / GS1 QR 데이터에서
/// AI별 값을 추출한다. FNC1 구분자는 GS(0x1D) 문자로 수신된다.
/// </summary>
public static class Gs1Parser
{
    private const char GS = '\u001D';

    // 고정 길이 AI (AI → 데이터 길이)
    private static readonly Dictionary<string, int> Fixed = new()
    {
        {"00",18},{"01",14},{"02",14},{"03",14},
        {"11",6},{"12",6},{"13",6},{"15",6},{"16",6},{"17",6},
        {"20",2},
        {"410",13},{"411",13},{"412",13},{"413",13},{"414",13},{"415",13},{"416",13},{"417",13},
        {"8005",6},{"8100",6},{"8101",10},{"8102",2},{"8111",4},
    };

    private static readonly HashSet<string> Var2 = new()
        { "10","21","22","30","37","90","91","92","93","94","95","96","97","98","99" };
    private static readonly HashSet<string> Var3 = new()
        { "235","240","241","242","243","250","251","253","254","255","400","401","402","403",
          "420","421","422","423","424","425","426","427","710","711","712","713","714","715" };
    private static readonly HashSet<string> Var4 = new()
        { "7001","7002","7003","7004","7005","7006","7007","7008","7009","7010","7020","7021","7022","7023",
          "8002","8003","8004","8007","8008","8009","8010","8011","8012","8013","8017","8018","8019","8020",
          "8026","8110","8112","8200" };

    /// <summary>AIM 심볼로지 식별자(]C1, ]d2, ]Q3 등) 제거</summary>
    public static string StripAim(string data) =>
        data.Length >= 3 && data[0] == ']' ? data[3..] : data;

    public static Dictionary<string, string> Parse(string data)
    {
        var result = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(data)) return result;

        string s = StripAim(data);
        // 괄호 표기 "(01)1234..." 도 지원
        if (s.StartsWith('(')) s = s.Replace("(", "").Replace(")", "");
        int pos = 0;

        while (pos < s.Length)
        {
            if (s[pos] == GS) { pos++; continue; }
            string? ai = null;
            int fixedLen = -1;

            foreach (int n in new[] { 2, 3, 4 })
            {
                if (pos + n > s.Length) break;
                string cand = s.Substring(pos, n);
                if (!cand.All(char.IsDigit)) break;
                if (Fixed.TryGetValue(cand, out int fl)) { ai = cand; fixedLen = fl; break; }
                // 계량형 AI 31xx~36xx: 4자리, 데이터 6자리
                if (n == 4 && cand[0] == '3' && cand[1] >= '1' && cand[1] <= '6') { ai = cand; fixedLen = 6; break; }
                if (n == 4 && (cand.StartsWith("390") || cand.StartsWith("391") || cand.StartsWith("392") ||
                               cand.StartsWith("393") || cand.StartsWith("394"))) { ai = cand; fixedLen = -2; break; }
                if (n == 2 && Var2.Contains(cand)) { ai = cand; fixedLen = -2; break; }
                if (n == 3 && Var3.Contains(cand)) { ai = cand; fixedLen = -2; break; }
                if (n == 4 && Var4.Contains(cand)) { ai = cand; fixedLen = -2; break; }
            }

            if (ai == null) break; // 인식 불가 → 중단

            pos += ai.Length;
            string value;
            if (fixedLen >= 0)
            {
                int take = Math.Min(fixedLen, s.Length - pos);
                value = s.Substring(pos, take);
                pos += take;
            }
            else
            {
                int gs = s.IndexOf(GS, pos);
                value = gs < 0 ? s[pos..] : s[pos..gs];
                pos = gs < 0 ? s.Length : gs + 1;
            }
            result[ai] = value;
        }
        return result;
    }

    /// <summary>GS1 날짜(YYMMDD) → YYYY-MM-DD. DD=00이면 YYYY-MM 로.</summary>
    public static string FormatGs1Date(string v)
    {
        if (v.Length != 6 || !v.All(char.IsDigit)) return v;
        int yy = int.Parse(v[..2]);
        int century = yy <= 50 ? 2000 : 1900; // GS1 규칙 근사
        string y = (century + yy).ToString();
        string mm = v[2..4], dd = v[4..6];
        return dd == "00" ? $"{y}-{mm}" : $"{y}-{mm}-{dd}";
    }
}
