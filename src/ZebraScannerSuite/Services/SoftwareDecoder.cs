using System.Drawing;
using System.Drawing.Imaging;
using ZXing.Common;

namespace ZebraScannerSuite.Services;

/// <summary>
/// 촬영 이미지에서 ZXing 소프트웨어 바코드 디코드 (강제 스캔 모드용).
/// 소프트웨어 디코더는 스캐너 하드웨어 디코더보다 약하므로
/// 원본 → 대비 스트레칭 → 2배 확대 → 확대+스트레칭 순으로 다단계 시도해 인식률을 높인다.
/// </summary>
public static class SoftwareDecoder
{
    /// <summary>고속 1차 시도: TryHarder 없이 원본만 분석 (~수십 ms).
    /// 선명한 바코드는 대부분 여기서 잡힌다. Points: 인식 위치 (x,y 쌍) - 하이라이트 표시용.</summary>
    public static (string Text, string Format, float[]? Points)? DecodeFast(Bitmap src)
    {
        var reader = new ZXing.Windows.Compatibility.BarcodeReader
        {
            AutoRotate = false,
            Options = new DecodingOptions { TryHarder = false, TryInverted = true },
        };
        var res = reader.Decode(src);
        if (res == null || string.IsNullOrEmpty(res.Text)) return null;
        return (res.Text, res.BarcodeFormat.ToString(), ToPoints(res.ResultPoints));
    }

    /// <summary>정밀 시도: TryHarder + 대비 스트레칭. 확대 재시도는 시간이 오래 걸려 제외
    /// (하드웨어 디코더 재판독이 그 역할을 대신한다).</summary>
    public static (string Text, string Format, float[]? Points)? DecodeThorough(Bitmap src)
    {
        var reader = new ZXing.Windows.Compatibility.BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        var res = reader.Decode(src);
        if (res == null)
        {
            using var st = StretchContrast(src);
            res = reader.Decode(st);
        }
        if (res == null || string.IsNullOrEmpty(res.Text)) return null;
        return (res.Text, res.BarcodeFormat.ToString(), ToPoints(res.ResultPoints));
    }

    private static float[]? ToPoints(ZXing.ResultPoint[]? pts)
    {
        if (pts == null || pts.Length == 0) return null;
        var list = new List<float>(pts.Length * 2);
        foreach (var p in pts)
            if (p != null) { list.Add(p.X); list.Add(p.Y); }
        return list.Count >= 2 ? list.ToArray() : null;
    }

    /// <summary>휘도 2%/98% 퍼센타일 대비 스트레칭 (저대비·흐린 인쇄 대응)</summary>
    private static Bitmap StretchContrast(Bitmap src)
    {
        byte[] gray = ToGray(src, out int w, out int h);
        var hist = new int[256];
        foreach (byte b in gray) hist[b]++;
        long total = (long)w * h, acc = 0;
        int lo = 0, hi = 255;
        for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= total * 0.02) { lo = v; break; } }
        acc = 0;
        for (int v = 255; v >= 0; v--) { acc += hist[v]; if (acc >= total * 0.02) { hi = v; break; } }
        if (hi <= lo + 5) return new Bitmap(src);

        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bd.Stride];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int v = (gray[y * w + x] - lo) * 255 / (hi - lo);
                    byte c = (byte)Math.Clamp(v, 0, 255);
                    row[x * 3] = c; row[x * 3 + 1] = c; row[x * 3 + 2] = c;
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + y * bd.Stride, bd.Stride);
            }
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    private static byte[] ToGray(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var gray = new byte[w * h];
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bd.Stride];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * bd.Stride, row, 0, bd.Stride);
                for (int x = 0; x < w; x++)
                {
                    int i = x * 3;
                    gray[y * w + x] = (byte)((row[i] * 114 + row[i + 1] * 587 + row[i + 2] * 299) / 1000);
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return gray;
    }
}
