using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ZebraScannerSuite.Services;

/// <summary>
/// Windows 10/11 내장 OCR(Windows.Media.Ocr) 사용 - 별도 설치 불필요, 빠른 처리.
/// 사전에 설정된 정규식 패턴에 일치하는 문자만 채택한다.
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngine? _engine;

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
    }

    public bool IsAvailable => _engine != null;
    public string EngineDescription => _engine == null
        ? "OCR 엔진 없음 (Windows 언어팩 OCR 구성요소 필요)"
        : "Windows OCR (" + _engine.RecognizerLanguage.DisplayName + ")";

    /// <summary>이미지 바이트(JPEG/BMP/PNG)에서 전체 텍스트 인식</summary>
    public async Task<string> RecognizeAsync(byte[] imageBytes)
    {
        if (_engine == null) return "";
        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(imageBytes.AsBuffer());
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);

        // Windows OCR 최대 치수 제한 대응
        uint maxDim = OcrEngine.MaxImageDimension;
        var transform = new BitmapTransform
        {
            ScaledWidth = decoder.PixelWidth,
            ScaledHeight = decoder.PixelHeight,
        };
        if (decoder.PixelWidth > maxDim || decoder.PixelHeight > maxDim)
        {
            double scale = Math.Min((double)maxDim / decoder.PixelWidth, (double)maxDim / decoder.PixelHeight);
            transform.ScaledWidth = (uint)(decoder.PixelWidth * scale);
            transform.ScaledHeight = (uint)(decoder.PixelHeight * scale);
        }

        var bmp = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        try
        {
            var result = await _engine.RecognizeAsync(bmp);
            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        }
        finally
        {
            bmp.Dispose();
        }
    }

    /// <summary>패턴(정규식) 목록과 일치하는 부분만 추출. 패턴이 없으면 전체 텍스트 반환.</summary>
    public static List<string> FilterByPatterns(string text, IEnumerable<string> patterns)
    {
        var pats = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (pats.Count == 0)
            return string.IsNullOrWhiteSpace(text) ? new List<string>() : new List<string> { text.Trim() };

        var matches = new List<string>();
        foreach (var p in pats)
        {
            try
            {
                foreach (Match m in Regex.Matches(text, p, RegexOptions.None, TimeSpan.FromSeconds(1)))
                    if (m.Success && !matches.Contains(m.Value)) matches.Add(m.Value);
            }
            catch { /* 잘못된 정규식은 무시 */ }
        }
        return matches;
    }
}
