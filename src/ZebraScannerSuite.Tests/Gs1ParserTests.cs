using ZebraScannerSuite.Services;
using Xunit;

namespace ZebraScannerSuite.Tests;

/// <summary>
/// GS1/UDI 파서 회귀 테스트.
/// 실제 현장 라벨에서 확인된 케이스를 고정하여, 파싱 규칙 변경 시
/// 과거에 검증된 라벨이 깨지지 않음을 자동으로 보증한다 (OQ 근거 자료).
/// </summary>
public class Gs1ParserTests
{
    private const string GS = "\u001D"; // FNC1 구분자

    // ---------- 실라벨 회귀 케이스 (검증 문서 SVRF805032501 및 현장 확인) ----------

    [Fact]
    public void GS없는_라벨_LOT8자리_고정_분리()
    {
        var r = Gs1Parser.Parse("010880636706765410250812151728092824011c0803217");
        Assert.Equal("08806367067654", r["01"]);
        Assert.Equal("25081215", r["10"]);
        Assert.Equal("280928", r["17"]);
        Assert.Equal("11c0803", r["240"]);
        Assert.Equal("7", r["21"]);
    }

    [Fact]
    public void GS없는_라벨_SN없는_변형()
    {
        var r = Gs1Parser.Parse("010880636706765410250812151728092824011c0803");
        Assert.Equal("25081215", r["10"]);
        Assert.Equal("280928", r["17"]);
        Assert.Equal("11c0803", r["240"]);
        Assert.False(r.ContainsKey("21") && r["21"].Length > 0);
    }

    [Fact]
    public void 제조일자_포함_라벨_PN_SN_분리()
    {
        var r = Gs1Parser.Parse("01088063670444021025040980172804081125040924001-0854211");
        Assert.Equal("08806367044402", r["01"]);
        Assert.Equal("25040980", r["10"]);
        Assert.Equal("280408", r["17"]);
        Assert.Equal("250409", r["11"]);
        Assert.Equal("01-0854", r["240"]);
        Assert.Equal("1", r["21"]);
    }

    [Fact]
    public void 검증문서_구조_예시_라벨()
    {
        var r = Gs1Parser.Parse("01088063670175811025090613172809181125091924012-0650211");
        Assert.Equal("08806367017581", r["01"]);
        Assert.Equal("25090613", r["10"]);
        Assert.Equal("280918", r["17"]);
        Assert.Equal("250919", r["11"]);
        Assert.Equal("12-0650", r["240"]);
        Assert.Equal("1", r["21"]);
    }

    [Fact]
    public void 잉여_끝GS_라벨_SN_UPN_분리()
    {
        // 라벨 끝에 GS가 하나 더 붙은 실측 케이스: SN=12, UPN=M00523870으로 분리되어야 함
        var r = Gs1Parser.Parse(
            "0108806367015945" + "1025070450" + GS + "17280928" + "24001-0624" + GS + "211230M00523870" + GS);
        Assert.Equal("25070450", r["10"]);
        Assert.Equal("01-0624", r["240"]);
        Assert.Equal("12", r["21"]);
        Assert.Equal("M00523870", r["30"]);
    }

    [Fact]
    public void 끝GS_없는_동일_라벨도_동일_결과()
    {
        var r = Gs1Parser.Parse(
            "0108806367015945" + "1025070450" + GS + "17280928" + "24001-0624" + GS + "211230M00523870");
        Assert.Equal("12", r["21"]);
        Assert.Equal("M00523870", r["30"]);
    }

    [Fact]
    public void GS전혀없는_라벨_PN꼬리에서_SN_UPN_분리()
    {
        var r = Gs1Parser.Parse("010880636701594510250704501728092824001-0624211230M00523870");
        Assert.Equal("25070450", r["10"]);
        Assert.Equal("280928", r["17"]);
        Assert.Equal("01-0624", r["240"]);
        Assert.Equal("12", r["21"]);
        Assert.Equal("M00523870", r["30"]);
    }

    [Fact]
    public void 표준GS1_21과30이_GS로_정상분리된_라벨()
    {
        var r = Gs1Parser.Parse(
            "0108806367015945" + "1025070450" + GS + "17280928" + "2112" + GS + "30M00523870");
        Assert.Equal("12", r["21"]);
        Assert.Equal("M00523870", r["30"]);
    }

    [Fact]
    public void GS로_정상종결된_8자리_LOT과_단독_SN()
    {
        var r = Gs1Parser.Parse("0108806367067654" + "1025081215" + GS + "17280928" + "217");
        Assert.Equal("25081215", r["10"]);
        Assert.Equal("7", r["21"]);
    }

    [Fact]
    public void 스텐트_반제품_01P는_전체값이_LOT()
    {
        var r = Gs1Parser.Parse("01P2508121500123");
        Assert.Equal("01P2508121500123", r["10"]);
        Assert.Single(r); // 다른 AI로 분해되지 않아야 함
    }

    [Fact]
    public void AIM_심볼로지_접두는_제거되고_동일하게_파싱()
    {
        var r = Gs1Parser.Parse("]d2010880636706765410250812151728092824011c0803217");
        Assert.Equal("08806367067654", r["01"]);
        Assert.Equal("25081215", r["10"]);
    }

    [Fact]
    public void 괄호표기_라벨_지원()
    {
        var r = Gs1Parser.Parse("(01)08806367067654(10)25081215");
        Assert.Equal("08806367067654", r["01"]);
        Assert.Equal("25081215", r["10"]);
    }

    // ---------- 특례 안전장치 ----------

    [Fact]
    public void SN특례는_값이_40자를_넘으면_적용되지_않음()
    {
        // 오분리 방지 안전장치: 21 값이 비정상적으로 길면 그대로 유지
        string longSn = "1230M" + new string('0', 40); // 45자
        var r = Gs1Parser.Parse("0108806367015945" + "21" + longSn);
        Assert.Equal(longSn, r["21"]);
        Assert.False(r.ContainsKey("30"));
    }

    [Fact]
    public void IsStentSemiProduct_판별()
    {
        Assert.True(Gs1Parser.IsStentSemiProduct("01P2508121500123"));
        Assert.True(Gs1Parser.IsStentSemiProduct("]d201P2508121500123"));
        Assert.False(Gs1Parser.IsStentSemiProduct("0108806367067654"));
    }

    // ---------- 날짜 변환 ----------

    [Theory]
    [InlineData("280928", "2028-09-28")]
    [InlineData("250919", "2025-09-19")]
    [InlineData("990101", "1999-01-01")] // 슬라이딩 윈도우: 과거 세기 (2048년까지 유효한 기대값)
    [InlineData("280900", "2028-09")]    // DD=00 → 연-월만
    [InlineData("ABC123", "ABC123")]     // 숫자 아님 → 원본 유지
    [InlineData("12345", "12345")]       // 길이 불일치 → 원본 유지
    public void 날짜변환_YYMMDD(string input, string expected)
    {
        Assert.Equal(expected, Gs1Parser.FormatGs1Date(input));
    }

    [Fact]
    public void 날짜변환_현재연도는_항상_당해로_해석()
    {
        string yy = (DateTime.Now.Year % 100).ToString("00");
        Assert.Equal($"{DateTime.Now.Year}-01-15", Gs1Parser.FormatGs1Date(yy + "0115"));
    }

    // ---------- 토큰화 (UI 색상 표시용) ----------

    [Fact]
    public void 토큰화_AI와_값이_교대로_생성됨()
    {
        var tokens = Gs1Parser.Tokenize("0108806367067654" + "1025081215");
        Assert.Equal(4, tokens.Count);
        Assert.True(tokens[0].IsAi);
        Assert.Equal("01", tokens[0].Text);
        Assert.False(tokens[1].IsAi);
        Assert.True(tokens[2].IsAi);
        Assert.Equal("10", tokens[2].Text);
    }

    [Fact]
    public void 토큰화_GS는_표시토큰으로_나오지_않음()
    {
        var tokens = Gs1Parser.Tokenize("1025081215" + GS + "17280928");
        Assert.DoesNotContain(tokens, t => t.Text.Contains('\u001D'));
    }
}
