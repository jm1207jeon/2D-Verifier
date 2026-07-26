using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ZXing;
using ZXing.Common;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>
/// ISO/IEC 15415 기반 2D 바코드 품질 "시뮬레이션" 검증기.
///
/// [중요] 본 검증은 전용 검증기(Verifier) 하드웨어 없이 일반 스캐너(DS9908)의
/// 캡처 이미지를 사용한 근사치입니다. ISO 15415는 교정된 조명(45°/0°),
/// 정해진 개구(aperture), 교정된 반사율 기준을 요구하므로 본 결과는
/// 공식 성적서가 아닌 "경향 파악용" 참고 데이터입니다.
///
/// 산출 파라미터:
///  - Decode(디코드), Symbol Contrast(SC), Modulation(MOD, 추정),
///    Axial Nonuniformity(AN, 추정), Grid Nonuniformity(GN, 추정),
///    Unused Error Correction(UEC, 추정), Fixed Pattern Damage(FPD, 추정)
/// 종합 등급 = 파라미터 중 최저 등급 (ISO 15415 방식)
/// </summary>
public static class Iso15415Verifier
{
    public static VerificationResult Verify(Bitmap bmp)
    {
        var r = new VerificationResult();
        int w = bmp.Width, h = bmp.Height;
        byte[] gray = ToGray(bmp, out int stride);

        // ---- 디코드 ----
        var reader = new ZXing.Windows.Compatibility.BarcodeReader
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = new List<BarcodeFormat>
                {
                    BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC,
                    BarcodeFormat.PDF_417, BarcodeFormat.MAXICODE,
                    BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93,
                    BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
                    BarcodeFormat.ITF, BarcodeFormat.CODABAR, BarcodeFormat.RSS_14, BarcodeFormat.RSS_EXPANDED,
                }
            }
        };
        Result? z = null;
        try { z = reader.Decode(bmp); } catch { }

        r.Decoded = z != null;
        r.DecodedText = z?.Text ?? "";
        r.Format = z?.BarcodeFormat.ToString() ?? "(디코드 실패)";
        bool is2D = z != null && (z.BarcodeFormat is BarcodeFormat.QR_CODE or BarcodeFormat.DATA_MATRIX
            or BarcodeFormat.AZTEC or BarcodeFormat.PDF_417 or BarcodeFormat.MAXICODE);

        r.Params.Add(new ParamGrade
        {
            Parameter = "Decode (디코드)",
            Value = r.Decoded ? "성공" : "실패",
            Numeric = r.Decoded ? 4 : 0,
            Letter = r.Decoded ? "A" : "F",
        });

        // ---- 심볼 영역(bbox) ----
        RectangleF bbox;
        if (z?.ResultPoints is { Length: > 0 })
        {
            float minX = z.ResultPoints.Min(p => p.X), maxX = z.ResultPoints.Max(p => p.X);
            float minY = z.ResultPoints.Min(p => p.Y), maxY = z.ResultPoints.Max(p => p.Y);
            float mX = Math.Max(8, (maxX - minX) * 0.18f), mY = Math.Max(8, (maxY - minY) * 0.18f);
            bbox = RectangleF.FromLTRB(
                Math.Max(0, minX - mX), Math.Max(0, minY - mY),
                Math.Min(w - 1, maxX + mX), Math.Min(h - 1, maxY + mY));
        }
        else
        {
            bbox = new RectangleF(w * 0.2f, h * 0.2f, w * 0.6f, h * 0.6f);
            r.Notes.Add("디코드 실패로 이미지 중앙 영역을 기준으로 반사율만 산출했습니다.");
        }

        // ---- 반사율 / Symbol Contrast ----
        var (rminByte, rmaxByte) = Percentiles(gray, stride, bbox, 0.02, 0.98);
        double rmin = rminByte / 2.55, rmax = rmaxByte / 2.55; // 0~100%
        double sc = rmax - rmin;
        double gt = (rminByte + rmaxByte) / 2.0; // byte 단위 전역 임계값

        r.Params.Add(Grade("Symbol Contrast (SC)", $"{sc:0.0}%",
            sc >= 70 ? 4 : sc >= 55 ? 3 : sc >= 40 ? 2 : sc >= 20 ? 1 : 0));
        r.Params.Add(new ParamGrade
        {
            Parameter = "Rmin / Rmax / GT",
            Value = $"{rmin:0.0}% / {rmax:0.0}% / {gt / 2.55:0.0}%",
            Numeric = -1, Letter = "-", Note = "참고값",
        });

        if (r.Decoded)
        {
            // ---- 모듈 피치 추정 (이진화 런 길이 중앙값) ----
            double pitchX = EstimatePitch(gray, stride, bbox, gt, horizontal: true);
            double pitchY = EstimatePitch(gray, stride, bbox, gt, horizontal: false);
            double pitch = (pitchX + pitchY) / 2;

            if (pitch >= 1.5)
            {
                // ---- Modulation (추정) ----
                double mod = EstimateModulation(gray, stride, bbox, gt, pitch, rmaxByte - rminByte);
                r.Params.Add(Grade("Modulation (MOD, 추정)", $"{mod:0.00}",
                    mod >= 0.50 ? 4 : mod >= 0.40 ? 3 : mod >= 0.30 ? 2 : mod >= 0.20 ? 1 : 0,
                    "이미지 기반 추정치"));

                if (is2D)
                {
                    // ---- Axial Nonuniformity (추정) ----
                    double an = Math.Abs(pitchX - pitchY) / ((pitchX + pitchY) / 2);
                    r.Params.Add(Grade("Axial Nonuniformity (AN, 추정)", $"{an:0.000}",
                        an <= 0.06 ? 4 : an <= 0.08 ? 3 : an <= 0.10 ? 2 : an <= 0.12 ? 1 : 0,
                        "X/Y 모듈 피치 비교"));

                    // ---- Grid Nonuniformity (추정): 사분면 피치 편차 ----
                    double gn = EstimateGridNonuniformity(gray, stride, bbox, gt, pitch);
                    r.Params.Add(Grade("Grid Nonuniformity (GN, 추정)", $"{gn:0.00} 모듈",
                        gn <= 0.38 ? 4 : gn <= 0.50 ? 3 : gn <= 0.63 ? 2 : gn <= 0.75 ? 1 : 0,
                        "국부 피치 편차 기반 근사"));

                    // ---- Unused Error Correction (추정) ----
                    double uec = EstimateUec(z!, out string uecNote);
                    r.Params.Add(Grade("Unused EC (UEC, 추정)", $"{uec:0.00}",
                        uec >= 0.62 ? 4 : uec >= 0.50 ? 3 : uec >= 0.37 ? 2 : uec >= 0.25 ? 1 : 0, uecNote));

                    // ---- Fixed Pattern Damage (추정) ----
                    double? fpd = EstimateFixedPattern(z!, gray, stride, w, h, gt, pitch);
                    if (fpd.HasValue)
                    {
                        double f = fpd.Value;
                        r.Params.Add(Grade("Fixed Pattern Damage (FPD, 추정)", $"일치율 {f:P0}",
                            f >= 0.95 ? 4 : f >= 0.90 ? 3 : f >= 0.85 ? 2 : f >= 0.80 ? 1 : 0,
                            "파인더/클록 패턴 샘플링"));
                    }
                    else
                    {
                        r.Params.Add(new ParamGrade { Parameter = "Fixed Pattern Damage", Value = "N/A", Note = "QR/DataMatrix만 지원" });
                    }
                }
                else
                {
                    r.Notes.Add("1D 심볼로지는 ISO/IEC 15416 대상입니다. Decode/SC/MOD만 산출했습니다.");
                }
            }
            else
            {
                r.Notes.Add("모듈 피치가 너무 작아(<1.5px) 세부 파라미터를 산출할 수 없습니다. 스캐너를 심볼에 더 가까이 하세요.");
            }
        }

        // ---- 종합 등급: 최저 등급 (ISO 15415 방식) ----
        var graded = r.Params.Where(p => p.Numeric >= 0).ToList();
        r.OverallNumeric = graded.Count > 0 ? graded.Min(p => p.Numeric) : 0;
        r.OverallLetter = ParamGrade.ToLetter(r.OverallNumeric);

        r.Notes.Add("본 결과는 전용 검증기 없이 스캐너 이미지로 산출한 시뮬레이션(경향 파악용)이며 ISO/IEC 15415 공식 측정이 아닙니다.");

        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            r.ImagePng = ms.ToArray();
        }
        return r;
    }

    private static ParamGrade Grade(string name, string value, double numeric, string note = "") => new()
    {
        Parameter = name, Value = value, Numeric = numeric, Letter = ParamGrade.ToLetter(numeric), Note = note,
    };

    // ---------------- 이미지 분석 유틸 ----------------

    private static byte[] ToGray(Bitmap bmp, out int stride)
    {
        int w = bmp.Width, h = bmp.Height;
        stride = w;
        var gray = new byte[w * h];
        var rect = new Rectangle(0, 0, w, h);
        BitmapData bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int srcStride = bd.Stride;
            var row = new byte[srcStride];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bd.Scan0 + y * srcStride, row, 0, srcStride);
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

    private static (byte lo, byte hi) Percentiles(byte[] g, int stride, RectangleF box, double pLo, double pHi)
    {
        var hist = new int[256];
        int x0 = (int)box.Left, x1 = (int)box.Right, y0 = (int)box.Top, y1 = (int)box.Bottom;
        long n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) { hist[g[y * stride + x]]++; n++; }
        if (n == 0) return (0, 255);

        byte lo = 0, hi = 255;
        long acc = 0, tLo = (long)(n * pLo), tHi = (long)(n * pHi);
        for (int v = 0; v < 256; v++)
        {
            acc += hist[v];
            if (lo == 0 && acc >= tLo && tLo > 0) lo = (byte)v;
            if (acc >= tHi) { hi = (byte)v; break; }
        }
        if (hi <= lo) hi = (byte)Math.Min(255, lo + 1);
        return (lo, hi);
    }

    /// <summary>이진화된 흑/백 런 길이의 중앙값으로 모듈 피치를 추정</summary>
    private static double EstimatePitch(byte[] g, int stride, RectangleF box, double gt, bool horizontal)
    {
        var runs = new List<int>();
        int x0 = (int)box.Left, x1 = (int)box.Right, y0 = (int)box.Top, y1 = (int)box.Bottom;
        int outer0 = horizontal ? y0 : x0, outer1 = horizontal ? y1 : x1;
        int inner0 = horizontal ? x0 : y0, inner1 = horizontal ? x1 : y1;

        for (int o = outer0; o < outer1; o += 3)
        {
            bool prev = SampleIsBlack(g, stride, horizontal ? inner0 : o, horizontal ? o : inner0, gt, horizontal);
            int run = 1;
            for (int i = inner0 + 1; i < inner1; i++)
            {
                bool cur = horizontal ? g[o * stride + i] < gt : g[i * stride + o] < gt;
                if (cur == prev) run++;
                else { if (run >= 2 && run <= 60) runs.Add(run); run = 1; prev = cur; }
            }
        }
        if (runs.Count < 10) return 0;
        runs.Sort();
        return runs[runs.Count / 2];
    }

    private static bool SampleIsBlack(byte[] g, int stride, int x, int y, double gt, bool _)
        => g[y * stride + x] < gt;

    /// <summary>셀 격자 샘플링으로 MOD 근사(하위 10퍼센타일)</summary>
    private static double EstimateModulation(byte[] g, int stride, RectangleF box, double gt, double pitch, double scByte)
    {
        if (scByte <= 0) return 0;
        var mods = new List<double>();
        for (double cy = box.Top + pitch / 2; cy < box.Bottom - 1; cy += pitch)
            for (double cx = box.Left + pitch / 2; cx < box.Right - 1; cx += pitch)
            {
                double v = SampleMean(g, stride, (int)cx, (int)cy);
                mods.Add(Math.Min(1.0, 2.0 * Math.Abs(v - gt) / scByte));
            }
        if (mods.Count == 0) return 0;
        mods.Sort();
        return mods[(int)(mods.Count * 0.10)];
    }

    private static double SampleMean(byte[] g, int stride, int cx, int cy)
    {
        double sum = 0; int n = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int idx = (cy + dy) * stride + (cx + dx);
                if (idx >= 0 && idx < g.Length) { sum += g[idx]; n++; }
            }
        return n > 0 ? sum / n : 0;
    }

    /// <summary>사분면별 피치 편차로 GN 근사 (모듈 단위)</summary>
    private static double EstimateGridNonuniformity(byte[] g, int stride, RectangleF box, double gt, double globalPitch)
    {
        if (globalPitch <= 0) return 1.0;
        double maxDev = 0;
        float hw = box.Width / 2, hh = box.Height / 2;
        foreach (var q in new[]
        {
            new RectangleF(box.Left, box.Top, hw, hh),
            new RectangleF(box.Left + hw, box.Top, hw, hh),
            new RectangleF(box.Left, box.Top + hh, hw, hh),
            new RectangleF(box.Left + hw, box.Top + hh, hw, hh),
        })
        {
            double px = EstimatePitch(g, stride, q, gt, true);
            double py = EstimatePitch(g, stride, q, gt, false);
            foreach (double p in new[] { px, py })
                if (p > 0) maxDev = Math.Max(maxDev, Math.Abs(p - globalPitch) / globalPitch);
        }
        // 상대 편차가 심볼 반폭에 누적된다고 근사 → 모듈 단위 편차
        double halfModules = Math.Max(4, box.Width / 2 / globalPitch);
        return Math.Min(2.0, maxDev * halfModules * 0.25);
    }

    /// <summary>ZXing 메타데이터 기반 UEC 근사.
    /// (ERRORS_CORRECTED 키는 ZXing 버전/심볼로지에 따라 없을 수 있어 이름으로 탐색)</summary>
    private static double EstimateUec(Result z, out string note)
    {
        if (z.ResultMetadata != null)
        {
            foreach (var kv in z.ResultMetadata)
            {
                if (kv.Key.ToString().Contains("ERRORS_CORRECTED", StringComparison.OrdinalIgnoreCase) &&
                    kv.Value is int ec)
                {
                    // 정정된 오류 수 기반 근사 (정정 용량은 심볼 크기에 따라 다름 → 보수적 근사)
                    double uec = Math.Max(0, 1.0 - ec / 8.0);
                    note = $"정정된 오류 {ec}개 기반 근사";
                    return uec;
                }
            }
        }
        note = "오류정정 통계 미제공 → 디코드 성공 기준 추정";
        return 1.0;
    }

    /// <summary>QR 파인더 패턴 / DataMatrix L-파인더+클록트랙 일치율 (0~1)</summary>
    private static double? EstimateFixedPattern(Result z, byte[] g, int stride, int w, int h, double gt, double pitch)
    {
        if (z.ResultPoints == null || z.ResultPoints.Length < 3 || pitch < 2) return null;

        if (z.BarcodeFormat == BarcodeFormat.QR_CODE)
        {
            // ResultPoints[0..2] = 파인더 패턴 중심 (bottomLeft, topLeft, topRight)
            double total = 0; int cnt = 0;
            for (int k = 0; k < 3 && k < z.ResultPoints.Length; k++)
            {
                var p = z.ResultPoints[k];
                int match = 0, n = 0;
                for (int dy = -3; dy <= 3; dy++)
                    for (int dx = -3; dx <= 3; dx++)
                    {
                        int ring = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        bool idealBlack = ring <= 1 || ring == 3; // 3x3 코어 + 외곽 링 = 흑
                        int x = (int)(p.X + dx * pitch), y = (int)(p.Y + dy * pitch);
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        bool black = SampleMean(g, stride, x, y) < gt;
                        if (black == idealBlack) match++;
                        n++;
                    }
                if (n > 0) { total += (double)match / n; cnt++; }
            }
            return cnt > 0 ? total / cnt : null;
        }

        if (z.BarcodeFormat == BarcodeFormat.DATA_MATRIX && z.ResultPoints.Length >= 4)
        {
            // 4 코너 → 각 변을 0.5모듈 안쪽에서 샘플링.
            // 솔리드 L(항상 흑) 2변 + 클록트랙(교대) 2변
            var pts = z.ResultPoints;
            var edgeScores = new List<(double blackFrac, double altFrac)>();
            for (int e = 0; e < 4; e++)
            {
                var a = pts[e]; var b = pts[(e + 1) % 4];
                // 심볼 중심 방향으로 0.7 모듈 인셋
                double cx0 = pts.Average(p => p.X), cy0 = pts.Average(p => p.Y);
                int steps = Math.Max(8, (int)(Dist(a, b) / pitch));
                int black = 0, trans = 0, n = 0; bool? prev = null;
                for (int i = 0; i <= steps; i++)
                {
                    double t = (double)i / steps;
                    double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
                    double vx = cx0 - x, vy = cy0 - y;
                    double vl = Math.Sqrt(vx * vx + vy * vy);
                    if (vl > 0) { x += vx / vl * pitch * 0.7; y += vy / vl * pitch * 0.7; }
                    if (x < 1 || y < 1 || x >= stride - 1) continue;
                    bool bl = SampleMean(g, stride, (int)x, (int)y) < gt;
                    if (bl) black++;
                    if (prev.HasValue && prev != bl) trans++;
                    prev = bl; n++;
                }
                if (n > 4) edgeScores.Add(((double)black / n, Math.Min(1.0, trans / (double)(n / 2))));
            }
            if (edgeScores.Count < 4) return null;
            var byBlack = edgeScores.OrderByDescending(s => s.blackFrac).ToList();
            double solid = (byBlack[0].blackFrac + byBlack[1].blackFrac) / 2;   // L-파인더
            double clock = (byBlack[2].altFrac + byBlack[3].altFrac) / 2;        // 클록트랙
            return Math.Min(solid, Math.Max(0.5, clock));
        }
        return null;
    }

    private static double Dist(ResultPoint a, ResultPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
