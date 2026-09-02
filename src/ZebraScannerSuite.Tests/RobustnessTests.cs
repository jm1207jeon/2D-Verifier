using System.IO;
using ZebraScannerSuite.Models;
using ZebraScannerSuite.Services;
using Xunit;

namespace ZebraScannerSuite.Tests;

/// <summary>예외 상황 회귀 테스트: CSV 파싱, 폴더명 정제, 비정상 날짜, 손상된 설정값 보정</summary>
public class RobustnessTests
{
    // ---------- CSV ----------

    [Fact]
    public void CSV_따옴표_필드와_이스케이프를_분해()
    {
        var f = CsvUtil.ParseLine("1,\"a,b\",\"say \"\"hi\"\"\",x");
        Assert.Equal(new[] { "1", "a,b", "say \"hi\"", "x" }, f);
    }

    [Fact]
    public void CSV_빈줄과_빈필드는_빈문자열()
    {
        Assert.Equal(new[] { "" }, CsvUtil.ParseLine(""));
        Assert.Equal(new[] { "", "", "" }, CsvUtil.ParseLine(",,"));
    }

    [Theory]
    [InlineData("\"=\"\"08806367\"\"\"", "08806367")]   // 이 프로그램 내보내기 형식 (="값")
    [InlineData("=\"25081215\"", "25081215")]           // 따옴표 한 겹만 남은 경우
    [InlineData("25081215", "25081215")]                // 엑셀 재저장본(일반 값)
    [InlineData("  1, 2, 3 ", "1, 2, 3")]               // 앞뒤 공백 제거
    public void CSV_텍스트_보호_래핑_제거(string raw, string expected)
    {
        // ParseLine이 바깥 따옴표를 벗긴 뒤 Unwrap이 ="…" 래핑을 벗긴다
        string field = CsvUtil.ParseLine(raw)[0];
        Assert.Equal(expected, CsvUtil.Unwrap(field));
    }

    [Fact]
    public void CSV_내보내기_형식은_다시_읽으면_원래값()
    {
        string[] samples = { "08806367067654", "01-0854", "M00523870", "a,b", "q\"uote" };
        foreach (string s in samples)
        {
            string line = CsvUtil.TextField(s) + "," + CsvUtil.Field(s);
            var f = CsvUtil.ParseLine(line);
            Assert.Equal(s, CsvUtil.Unwrap(f[0]));
            Assert.Equal(s, f[1]);
        }
    }

    // ---------- LOT 폴더명 정제 ----------

    [Theory]
    [InlineData("25081215", "25081215")]
    [InlineData("..", "")]                 // 상위 폴더 이탈 차단
    [InlineData(".", "")]
    [InlineData("   ", "")]
    [InlineData("LOT.", "LOT")]            // 끝의 점 제거 (윈도우에서 생성 불가)
    [InlineData("CON", "_CON")]            // 예약 이름
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("A/B:C", "A_B_C")]
    public void LOT폴더명_위험한_이름_보정(string lot, string expected)
    {
        Assert.Equal(expected, ImageSaveService.SanitizeFolderName(lot));
    }

    [Fact]
    public void 이미지저장_경로가_상대경로면_명확한_오류()
    {
        var settings = new AppSettings { ImageSaveDirectory = "relative\\dir", SaveDateFolder = false };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ImageSaveService.Save(new byte[] { 0xFF, 0xD8, 0xFF }, "X", "DM", "", settings));
        Assert.Contains("저장 경로", ex.Message);
    }

    [Fact]
    public void 이미지저장_빈_데이터는_저장하지_않음()
    {
        var settings = new AppSettings { ImageSaveDirectory = Path.GetTempPath() };
        Assert.Throws<InvalidOperationException>(() =>
            ImageSaveService.Save(Array.Empty<byte>(), "X", "DM", "", settings));
    }

    // ---------- 날짜 ----------

    [Theory]
    [InlineData("251345")] // 13월
    [InlineData("250032")] // 32일
    [InlineData("250001")] // 0월
    public void 날짜변환_달력범위_밖이면_원문_유지(string raw)
    {
        Assert.Equal(raw, Gs1Parser.FormatGs1Date(raw));
    }

    [Fact]
    public void 날짜변환_null이나_빈값은_빈문자열()
    {
        Assert.Equal("", Gs1Parser.FormatGs1Date(""));
        Assert.Equal("", Gs1Parser.FormatGs1Date(null!));
    }

    // ---------- 설정 보정 ----------

    [Fact]
    public void 설정_null_컬렉션과_빈_경로는_기본값으로_보정()
    {
        var s = new AppSettings
        {
            ImageSaveDirectory = "",
            FileNameRule = " ",
            PreferredHostMode = "",
            MultiColWidthsV = null!,
            MultiColWidthsH = null!,
            ExtractionRules = null!,
        };
        var n = SettingsService.Normalize(s);
        Assert.True(SettingsService.IsValidDirectory(n.ImageSaveDirectory));
        Assert.Equal("{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}", n.FileNameRule);
        Assert.Equal("XUA-45001-9", n.PreferredHostMode);
        Assert.NotNull(n.MultiColWidthsV);
        Assert.NotNull(n.MultiColWidthsH);
        Assert.NotEmpty(n.ExtractionRules);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("images", false)]           // 상대 경로
    [InlineData("C:\\UDI|Images", false)]  // 금지 문자
    public void 저장경로_유효성(string? path, bool expected)
    {
        Assert.Equal(expected, SettingsService.IsValidDirectory(path));
    }

    [Fact]
    public void 저장경로_절대경로는_유효()
    {
        Assert.True(SettingsService.IsValidDirectory(Path.GetTempPath()));
    }

    // ---------- 모델 ----------

    [Fact]
    public void SN_정렬키는_숫자순()
    {
        var rows = new[] { "10", "2", "1", "abc", "" }
            .Select(sn => new MultiScanRow { Sn = sn }).OrderBy(r => r.SnNum).Select(r => r.Sn).ToList();
        Assert.Equal("1", rows[0]);
        Assert.Equal("2", rows[1]);
        Assert.Equal("10", rows[2]);
    }
}
