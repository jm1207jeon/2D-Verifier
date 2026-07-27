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
    public static (string Text, string Format)? Decode(Bitmap src)
    {
        var reader = new ZXing.Windows.Compatibility.BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
            },
        };

        var res = reader.Decode(src);
        if (res == null)
        {
            using var st = StretchContrast(src);
            res = reader.Decode(st);
        }
        if (res == null)
        {
            using var up = Upscale(src, 2);
            res = reader.Decode(up);
        }
        if (res == null)
        {
            using var up = Upscale(src, 2);
            using var st = StretchContrast(up);
            res = reader.Decode(st);
        }

        if (res == null || string.IsNullOrEmpty(res.Text)) return null;
        return (res.Text, res.BarcodeFormat.ToString());
    }

    private static Bitmap Upscale(Bitmap src, int factor)
    {
        var bmp = new Bitmap(src.Width * factor, src.Height * factor, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
        return bmp;
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
