using System.Text.RegularExpressions;

namespace ZebraScannerSuite.Services;

/// <summary>GS1 토큰: AI(응용식별자) 또는 데이터 값</summary>
public readonly record struct Gs1Token(string Text, bool IsAi);

/// <summary>
/// GS1 응용식별자(AI) 파서. GS1-128 / GS1 DataMatrix / GS1 QR 데이터에서
/// AI별 값을 추출한다. FNC1 구분자는 GS(0x1D) 문자로 수신된다.
/// 토큰화(Tokenize)를 기반으로 하여 UI에서 AI만 색상 표시하는 용도로도 사용한다.
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

    /// <summary>스텐트 반제품 바코드: "01P"로 시작하면 AI 파싱을 하지 않고 전체를 LOT로 취급</summary>
    public static bool IsStentSemiProduct(string data)
    {
        string s = StripAim(data ?? "");
        return s.StartsWith("01P", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>데이터를 AI/값 토큰 열로 분해. 인식 불가 잔여분은 값 토큰으로 남긴다.</summary>
    public static List<Gs1Token> Tokenize(string data)
    {
        var tokens = new List<Gs1Token>();
        if (string.IsNullOrEmpty(data)) return tokens;

        string s = data;
        if (s.Length >= 3 && s[0] == ']')
        {
            tokens.Add(new Gs1Token(s[..3], false)); // AIM 식별자는 일반 표시
            s = s[3..];
        }
        // 스텐트 반제품(01P~): AI 분해 없이 전체를 값으로 표시
        if (s.StartsWith("01P", StringComparison.OrdinalIgnoreCase))
        {
            tokens.Add(new Gs1Token(s, false));
            return tokens;
        }
        // 괄호 표기 "(01)1234..." 도 지원
        if (s.StartsWith('(')) s = s.Replace("(", "").Replace(")", "");
        int pos = 0;

        while (pos < s.Length)
        {
            if (s[pos] == GS) { pos++; continue; } // GS 구분자는 표시하지 않음 (빈칸 방지)

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

            if (ai == null)
            {
                // 인식 불가 → 나머지는 값으로 표시하고 종료
                tokens.Add(new Gs1Token(s[pos..], false));
                break;
            }

            pos += ai.Length;
            string value;
            bool gsTerminated = false; // GS 구분자로 값이 명확히 끝났는지 (끝났으면 특례 분리 불필요)
            if (fixedLen >= 0)
            {
                int take = Math.Min(fixedLen, s.Length - pos);
                value = s.Substring(pos, take);
                pos += take;
            }
            else
            {
                int gs = s.IndexOf(GS, pos);
                gsTerminated = gs >= 0;
                value = gs < 0 ? s[pos..] : s[pos..gs];
                pos = gs < 0 ? s.Length : gs;
            }

            // ---- 라벨 특례: GS 구분자 없이 필드가 이어 붙는 라벨(고정폭) 지원 ----
            // 검증 문서 기준: LOT(10)=8자리 고정, SN(21)=1~2자리 숫자, UPN(30)='M'으로 시작.
            // GS로 값이 끝난 경우는 표준 GS1이므로 특례를 적용하지 않는다.

            // 특례 LOT: GS 없이 LOT 뒤에 다른 AI들이 이어 붙은 경우 → 정확히 8자만 LOT,
            //  나머지는 되돌려서 다음 AI(17/11/240/21…)로 계속 파싱
            //  예) "10 25081215 17 280928 240 11c0803 21 7"
            if (ai == "10" && !gsTerminated && value.Length > 8)
            {
                pos -= value.Length - 8;
                value = value[..8];
                tokens.Add(new Gs1Token(ai, true));
                tokens.Add(new Gs1Token(value, false));
                continue;
            }

            // 특례 A: AI 21 값이 "SN + 30 + M…" 형태 → SN과 UPN 분리
            if (ai == "21" && !gsTerminated)
            {
                var m = Regex.Match(value, @"^(\d{1,2})30(M.*)$");
                if (m.Success)
                {
                    tokens.Add(new Gs1Token("21", true));
                    tokens.Add(new Gs1Token(m.Groups[1].Value, false));
                    tokens.Add(new Gs1Token("30", true));
                    tokens.Add(new Gs1Token(m.Groups[2].Value, false));
                    continue;
                }
            }

            // 특례 B: PN(240) 값 끝에 "21 + SN(1~2자리) [+ 30 + M…]" 이 붙은 형태
            //  예) "24001-0854211" → PN=01-0854, SN=1
            //  greedy(.+)로 가장 오른쪽의 21을 찾으므로 값 중간의 '21'과 혼동하지 않음
            if (ai == "240" && !gsTerminated)
            {
                var m = Regex.Match(value, @"^(.+)21(\d{1,2})30(M.*)$");
                if (m.Success)
                {
                    tokens.Add(new Gs1Token(ai, true));
                    tokens.Add(new Gs1Token(m.Groups[1].Value, false));
                    tokens.Add(new Gs1Token("21", true));
                    tokens.Add(new Gs1Token(m.Groups[2].Value, false));
                    tokens.Add(new Gs1Token("30", true));
                    tokens.Add(new Gs1Token(m.Groups[3].Value, false));
                    continue;
                }
                m = Regex.Match(value, @"^(.+)21(\d{1,2})$");
                if (m.Success)
                {
                    tokens.Add(new Gs1Token(ai, true));
                    tokens.Add(new Gs1Token(m.Groups[1].Value, false));
                    tokens.Add(new Gs1Token("21", true));
                    tokens.Add(new Gs1Token(m.Groups[2].Value, false));
                    continue;
                }
            }

            tokens.Add(new Gs1Token(ai, true));
            if (value.Length > 0) tokens.Add(new Gs1Token(value, false));
        }
        return tokens;
    }

    public static Dictionary<string, string> Parse(string data)
    {
        var result = new Dictionary<string, string>();
        // 스텐트 반제품(01P~): AI 파싱 없이 전체 값을 LOT(10)로
        if (IsStentSemiProduct(data))
        {
            result["10"] = StripAim(data);
            return result;
        }
        string? currentAi = null;
        foreach (var t in Tokenize(data))
        {
            if (t.IsAi)
            {
                currentAi = t.Text;
                result.TryAdd(currentAi, "");
            }
            else if (currentAi != null && !string.IsNullOrWhiteSpace(t.Text))
            {
                result[currentAi] = t.Text;
                currentAi = null;
            }
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
