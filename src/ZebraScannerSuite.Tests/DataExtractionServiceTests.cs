using ZebraScannerSuite.Models;
using ZebraScannerSuite.Services;
using Xunit;

namespace ZebraScannerSuite.Tests;

/// <summary>UDI 데이터 추출(기본 7필드) 회귀 테스트: 일반 스캔 탭 'UDI 데이터' 표와 동일 경로</summary>
public class DataExtractionServiceTests
{
    [Fact]
    public void 기본_추출규칙_7필드가_실라벨에서_정상_추출됨()
    {
        string barcode = "01088063670444021025040980172804081125040924001-0854211";
        var fields = DataExtractionService.Apply(barcode, AppSettings.DefaultExtractionRules());
        var map = fields.ToDictionary(f => f.Name, f => f.Value);

        Assert.Equal("08806367044402", map["GTIN"]);
        Assert.Equal("25040980", map["LOT"]);
        Assert.Equal("01-0854", map["PN"]);
        Assert.Equal("2025-04-09", map["MFG DATE"]); // DateConvert 적용
        Assert.Equal("2028-04-08", map["EXP DATE"]);
        Assert.Equal("1", map["SN"]);
    }

    [Fact]
    public void 규칙에_없는_AI는_빈값으로_반환()
    {
        var fields = DataExtractionService.Apply("0108806367044402", AppSettings.DefaultExtractionRules());
        var map = fields.ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("", map["LOT"]);
        Assert.Equal("", map["SN"]);
    }

    [Fact]
    public void REGEX_규칙_그룹1_추출과_날짜변환()
    {
        var rules = new[]
        {
            new ExtractionRule { Name = "R", Type = "REGEX", Param1 = @"LOT(\d{6})", DateConvert = true },
        };
        var fields = DataExtractionService.Apply("XXLOT250409YY", rules);
        Assert.Equal("2025-04-09", fields[0].Value);
    }

    [Fact]
    public void SUBSTR_규칙_1기반_시작위치와_길이()
    {
        var rules = new[]
        {
            new ExtractionRule { Name = "S", Type = "SUBSTR", Param1 = "3", Param2 = "4" },
        };
        var fields = DataExtractionService.Apply("ABCDEFGH", rules);
        Assert.Equal("CDEF", fields[0].Value);
    }

    [Fact]
    public void 잘못된_REGEX는_예외없이_오류표시_반환()
    {
        var rules = new[]
        {
            new ExtractionRule { Name = "BAD", Type = "REGEX", Param1 = "(" }, // 비정상 패턴
        };
        var fields = DataExtractionService.Apply("DATA", rules);
        Assert.StartsWith("(규칙 오류", fields[0].Value);
    }
}
