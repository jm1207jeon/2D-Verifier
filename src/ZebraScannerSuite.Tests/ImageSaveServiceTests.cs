using System.IO;
using ZebraScannerSuite.Models;
using ZebraScannerSuite.Services;
using Xunit;

namespace ZebraScannerSuite.Tests;

/// <summary>이미지 저장 서비스 회귀 테스트: 파일명 규칙, 순번, 폴더 자동 생성, 형식 판별</summary>
public class ImageSaveServiceTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "UdiCaptureTests_" + Guid.NewGuid().ToString("N"));

    public ImageSaveServiceTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 };

    // ---------- 확장자 자동 판별 ----------

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, ".jpg")]
    [InlineData(new byte[] { 0x42, 0x4D, 0x00 }, ".bmp")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png")]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00 }, ".tif")]
    [InlineData(new byte[] { 0x00, 0x01, 0x02, 0x03 }, ".jpg")] // 알 수 없으면 jpg 기본
    public void 확장자_매직바이트_판별(byte[] data, string expected)
    {
        Assert.Equal(expected, ImageSaveService.DetectExtension(data));
    }

    // ---------- 파일명 정제 ----------

    [Fact]
    public void 파일명_금지문자와_공백_GS는_밑줄로_치환()
    {
        Assert.Equal("a_b_c", ImageSaveService.SanitizeFileName("a b\u001Dc"));
        Assert.Equal("x_y", ImageSaveService.SanitizeFileName("x/y"));
    }

    [Fact]
    public void 파일명_80자_초과시_절단()
    {
        string longName = new string('A', 120);
        Assert.Equal(80, ImageSaveService.SanitizeFileName(longName).Length);
    }

    // ---------- 파일명 규칙 / 순번 ----------

    [Fact]
    public void 순번_기존파일과_충돌시_자동증가()
    {
        string rule = "{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}";
        string first = ImageSaveService.BuildPath(_tempDir, rule, "TEST123", "DM", "", ".jpg");
        Assert.EndsWith("_001.jpg", first);
        File.WriteAllBytes(first, JpegBytes);

        string second = ImageSaveService.BuildPath(_tempDir, rule, "TEST123", "DM", "", ".jpg");
        Assert.EndsWith("_002.jpg", second);
    }

    [Fact]
    public void 샵기호는_순번토큰으로_인식()
    {
        string path = ImageSaveService.BuildPath(_tempDir, "{BARCODE}_###", "AB", "DM", "", ".jpg");
        Assert.EndsWith("AB_001.jpg", path);
    }

    // ---------- 날짜/LOT 하위 폴더 저장 ----------

    [Fact]
    public void 날짜폴더_옵션시_YYYY_MM_DD_폴더에_저장()
    {
        var settings = new AppSettings
        {
            ImageSaveDirectory = _tempDir,
            SaveDateFolder = true,
            SaveLotFolder = false,
        };
        string path = ImageSaveService.Save(JpegBytes, "0108806367067654", "DM", "", settings);
        Assert.True(File.Exists(path));
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
    }

    [Fact]
    public void LOT폴더_옵션시_추출된_LOT명_폴더에_저장()
    {
        var settings = new AppSettings
        {
            ImageSaveDirectory = _tempDir,
            SaveDateFolder = false,
            SaveLotFolder = true,
        };
        string barcode = "010880636706765410250812151728092824011c0803217"; // LOT=25081215
        string path = ImageSaveService.Save(JpegBytes, barcode, "DM", "", settings);
        Assert.True(File.Exists(path));
        Assert.Equal("25081215", new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
    }

    [Fact]
    public void 날짜와_LOT_동시_사용시_날짜_LOT_순으로_중첩()
    {
        var settings = new AppSettings
        {
            ImageSaveDirectory = _tempDir,
            SaveDateFolder = true,
            SaveLotFolder = true,
        };
        string barcode = "010880636706765410250812151728092824011c0803217";
        string path = ImageSaveService.Save(JpegBytes, barcode, "DM", "", settings);
        var lotDir = new DirectoryInfo(Path.GetDirectoryName(path)!);
        Assert.Equal("25081215", lotDir.Name);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), lotDir.Parent!.Name);
    }

    [Fact]
    public void 반제품_01P_바코드는_전체값이_LOT_폴더명()
    {
        var settings = new AppSettings
        {
            ImageSaveDirectory = _tempDir,
            SaveDateFolder = false,
            SaveLotFolder = true,
        };
        string path = ImageSaveService.Save(JpegBytes, "01P2508121500123", "DM", "", settings);
        Assert.Equal("01P2508121500123", new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
    }

    [Fact]
    public void LOT없는_바코드는_LOT폴더_없이_상위에_저장()
    {
        var settings = new AppSettings
        {
            ImageSaveDirectory = _tempDir,
            SaveDateFolder = false,
            SaveLotFolder = true,
        };
        string path = ImageSaveService.Save(JpegBytes, "PLAINCODE99", "C128", "", settings);
        Assert.Equal(Path.GetFileName(_tempDir), new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
    }
}
