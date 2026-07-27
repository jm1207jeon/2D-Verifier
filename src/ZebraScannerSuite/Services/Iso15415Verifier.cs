using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ZXing;
using ZXing.Common;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>
/// 스캔 직후 표시용 퀵 품질 지표 (스캐너 자체 품질값 미제공에 대한 대체 - 캡처 이미지 분석).
/// 흐림(초점), 낮은 대비, 부분 지워짐/보이드/가는 공백 선(저모듈 셀)을 감지한다.
/// </summary>
public sealed class QuickQuality
{
    public double ContrastPct { get; set; }
    public double Sharpness { get; set; }
    public double LowModPercent { get; set; }
    /// <summary>0=양호, 1=주의, 2=불량</summary>
    public int Level { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>
/// ISO/IEC 15415 기반 2D 바코드 품질 "시뮬레이션" 검증기.
///
/// [중요] 본 검증은 전용 검증기(Verifier) 하드웨어 없이 일반 스캐너(DS9908)의
/// 캡처 이미지를 사용한 근사치입니다. ISO 15415는 교정된 조명(45°/0°),
/// 정해진 개구(aperture), 교정된 반사율 기준을 요구하므로 본 결과는
/// 공식 성적서가 아닌 "경향 파악용" 참고 데이터입니다.
///
/// 산출 파라미터: Decode, SC, MOD(추정), AN(추정), GN(추정), UEC(추정), FPD(추정)
/// 종합 등급 = 파라미터 중 최저 등급 (ISO 15415 방식)
/// 추가 산출물:
///  - AnnotatedPng: 문제 영역 오버레이 (심볼 영역 / 저모듈레이션 셀 / 파인더·클록 상태)
///  - Recommendations: 등급이 낮은 파라미터별 개선 가이드
/// </summary>
public static class Iso15415Verifier
{
    /// <summary>오버레이 표시용 주석 정보</summary>
    private sealed class AnnotationInfo
    {
        public RectangleF Bbox;
        public List<RectangleF> LowModCells { get; } = new();               // MOD < 0.30 셀
        public List<(RectangleF Box, double Score)> Finders { get; } = new(); // QR 파인더
        public List<(PointF A, PointF B, bool Solid, double Score)> DmEdges { get; } = new();
    }

    public static VerificationResult Verify(Bitmap bmp)
    {
        var r = new VerificationResult();
        var ann = new AnnotationInfo();
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
        ann.Bbox = bbox;

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
                // ---- Modulation (추정) + 저모듈 셀 수집 ----
                double mod = EstimateModulation(gray, stride, bbox, gt, pitch, rmaxByte - rminByte, ann.LowModCells);
                r.Params.Add(Grade("Modulation (MOD, 추정)", $"{mod:0.00}",
                    mod >= 0.50 ? 4 : mod >= 0.40 ? 3 : mod >= 0.30 ? 2 : mod >= 0.20 ? 1 : 0,
                    "빨간 셀 = 저모듈레이션 의심 영역"));

                if (is2D)
                {
                    // ---- Axial Nonuniformity (추정) ----
                    double an = Math.Abs(pitchX - pitchY) / ((pitchX + pitchY) / 2);
                    r.Params.Add(Grade("Axial Nonuniformity (AN, 추정)", $"{an:0.000}",
                        an <= 0.06 ? 4 : an <= 0.08 ? 3 : an <= 0.10 ? 2 : an <= 0.12 ? 1 : 0,
                        $"X피치 {pitchX:0.0}px / Y피치 {pitchY:0.0}px"));

                    // ---- Grid Nonuniformity (추정) ----
                    double gn = EstimateGridNonuniformity(gray, stride, bbox, gt, pitch);
                    r.Params.Add(Grade("Grid Nonuniformity (GN, 추정)", $"{gn:0.00} 모듈",
                        gn <= 0.38 ? 4 : gn <= 0.50 ? 3 : gn <= 0.63 ? 2 : gn <= 0.75 ? 1 : 0,
                        "국부 피치 편차 기반 근사"));

                    // ---- Unused Error Correction (추정) ----
                    double uec = EstimateUec(z!, out string uecNote);
                    r.Params.Add(Grade("Unused EC (UEC, 추정)", $"{uec:0.00}",
                        uec >= 0.62 ? 4 : uec >= 0.50 ? 3 : uec >= 0.37 ? 2 : uec >= 0.25 ? 1 : 0, uecNote));

                    // ---- Fixed Pattern Damage (추정) + 파인더/엣지 주석 ----
                    double? fpd = EstimateFixedPattern(z!, gray, stride, w, h, gt, pitch, ann);
                    if (fpd.HasValue)
                    {
                        double f = fpd.Value;
                        r.Params.Add(Grade("Fixed Pattern Damage (FPD, 추정)", $"일치율 {f:P0}",
                            f >= 0.95 ? 4 : f >= 0.90 ? 3 : f >= 0.85 ? 2 : f >= 0.80 ? 1 : 0,
                            "오버레이의 파인더/클록 박스 색상 참조"));
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

        r.Recommendations = BuildRecommendations(r);
        r.Notes.Add("본 결과는 전용 검증기 없이 스캐너 이미지로 산출한 시뮬레이션(경향 파악용)이며 ISO/IEC 15415 공식 측정이 아닙니다.");

        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            r.ImagePng = ms.ToArray();
        }
        try { r.AnnotatedPng = Annotate(bmp, ann); }
        catch { r.AnnotatedPng = r.ImagePng; }
        return r;
    }

    private static ParamGrade Grade(string name, string value, double numeric, string note = "") => new()
    {
        Parameter = name, Value = value, Numeric = numeric, Letter = ParamGrade.ToLetter(numeric), Note = note,
    };

    // ---------------- 퀵 품질 평가 (스캔 직후 표시용, 수십 ms) ----------------

    /// <summary>캡처 이미지 중앙 영역을 빠르게 분석해 대비/초점/결손 셀 비율을 산출.
    /// DS9908(SNAPI)은 스캔별 품질값을 호스트로 주지 않으므로 이것이 대체 지표가 된다.</summary>
    public static QuickQuality QuickAssess(Bitmap bmp)
    {
        byte[] g = ToGray(bmp, out int stride);
        int w = bmp.Width, h = bmp.Height;
        var box = new RectangleF(w * 0.15f, h * 0.15f, w * 0.7f, h * 0.7f);

        var (lo, hi) = Percentiles(g, stride, box, 0.02, 0.98);
        double contrast = (hi - lo) / 2.55; // 0~100%
        double gt = (lo + hi) / 2.0;

        // 초점/흐림: 라플라시안 분산
        double sum = 0, sum2 = 0; long n = 0;
        for (int y = (int)box.Top + 1; y < (int)box.Bottom - 1; y += 2)
            for (int x = (int)box.Left + 1; x < (int)box.Right - 1; x += 2)
            {
                int i = y * stride + x;
                double lap = 4.0 * g[i] - g[i - 1] - g[i + 1] - g[i - stride] - g[i + stride];
                sum += lap; sum2 += lap * lap; n++;
            }
        double mean = n > 0 ? sum / n : 0;
        double sharp = n > 0 ? sum2 / n - mean * mean : 0;

        // 결손/번짐 셀 비율: 부분 지워짐·보이드·가는 공백 선이 저모듈레이션 셀로 나타남
        double lowPct = 0;
        double px = EstimatePitch(g, stride, box, gt, true);
        double py = EstimatePitch(g, stride, box, gt, false);
        double pitch = (px + py) / 2;
        if (pitch >= 1.5 && hi > lo)
        {
            int total = 0, low = 0;
            for (double cy = box.Top + pitch / 2; cy < box.Bottom - 1; cy += pitch)
                for (double cx = box.Left + pitch / 2; cx < box.Right - 1; cx += pitch)
                {
                    double v = SampleMean(g, stride, (int)cx, (int)cy);
                    double m = Math.Min(1.0, 2.0 * Math.Abs(v - gt) / (hi - lo));
                    total++;
                    if (m < 0.30) low++;
                }
            if (total > 0) lowPct = 100.0 * low / total;
        }

        int level = 0;
        if (contrast < 40 || sharp < 60 || lowPct > 15) level = 2;
        else if (contrast < 55 || sharp < 150 || lowPct > 7) level = 1;

        string sharpTxt = sharp >= 150 ? "선명" : sharp >= 60 ? "보통(약간 흐림)" : "흐림 - 초점/거리 조정 필요";
        string levelTxt = level == 0 ? "[양호]" : level == 1 ? "[주의]" : "[불량]";
        return new QuickQuality
        {
            ContrastPct = contrast,
            Sharpness = sharp,
            LowModPercent = lowPct,
            Level = level,
            Summary = $"{levelTxt} 대비 {contrast:0}% · 초점 {sharpTxt} · 결손/번짐 셀 {lowPct:0.0}%",
        };
    }

    // ---------------- 개선 권장사항 ----------------

    private static List<string> BuildRecommendations(VerificationResult r)
    {
        var recs = new List<string>();
        foreach (var p in r.Params)
        {
            if (p.Numeric < 0 || p.Numeric > 2) continue; // C(2.0) 이하만
            string name = p.Parameter;
            if (name.StartsWith("Decode"))
                recs.Add("디코드 실패: 스캐너와 심볼의 거리·초점을 조정하고 심볼 전체가 화면 중앙에 오도록 하세요. 인쇄 누락·심한 훼손 여부를 확인하세요.");
            else if (name.StartsWith("Symbol Contrast"))
                recs.Add("심볼 대비(SC) 부족: 잉크 농도(레이저면 마킹 에너지)를 높이고, 광택·투명 배경을 피하세요. 조명 반사가 심하면 스캐너 각도를 10~15° 기울여 보세요.");
            else if (name.StartsWith("Modulation"))
                recs.Add("모듈 균일성(MOD) 저하: 오버레이의 빨간 셀 위치를 확인하세요. 도트게인(잉크 번짐), 리본/프린트헤드 마모, 잉크 뭉침이 주원인입니다. 셀 크기 확대 또는 인쇄 해상도 향상을 검토하세요.");
            else if (name.StartsWith("Axial"))
                recs.Add("축 불균일(AN): 셀이 정사각형이 아닙니다. 프린터의 용지 이송 속도와 헤드 해상도 비율(X/Y 배율)을 보정하세요.");
            else if (name.StartsWith("Grid"))
                recs.Add("격자 불균일(GN): 곡면·주름 표면 부착, 라벨 부착 시 늘어짐이 주원인입니다. 평탄한 위치에 부착하고 프린터 용지 정렬을 점검하세요.");
            else if (name.StartsWith("Unused EC"))
                recs.Add("오류정정 여유(UEC) 부족: 심볼 표면의 오염·긁힘을 제거하세요. 특정 위치 손상이 반복되면 인쇄 공정(헤드 단선 등) 결함을 점검하세요.");
            else if (name.StartsWith("Fixed Pattern"))
                recs.Add("고정 패턴 손상(FPD): 오버레이에 주황/빨강으로 표시된 파인더·클록 트랙 영역의 오염이나 인쇄 결함을 제거하고, 심볼 주변 Quiet Zone(여백)을 확보하세요.");
        }
        if (recs.Count == 0 && r.Decoded)
            recs.Add("주요 파라미터가 모두 양호(B 이상)합니다. 현재 인쇄/마킹 조건을 유지하세요.");
        return recs;
    }

    // ---------------- 오버레이 렌더링 ----------------

    private static byte[] Annotate(Bitmap src, AnnotationInfo ann)
    {
        using var canvas = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.DrawImage(src, 0, 0, src.Width, src.Height);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            // 심볼 영역
            using (var penBox = new Pen(Color.DodgerBlue, 2f))
                g.DrawRectangle(penBox, ann.Bbox.X, ann.Bbox.Y, ann.Bbox.Width, ann.Bbox.Height);

            // 저모듈레이션 셀 (최대 400개)
            using (var lowBrush = new SolidBrush(Color.FromArgb(90, 255, 0, 0)))
            using (var lowPen = new Pen(Color.FromArgb(180, 255, 0, 0), 1f))
            {
                foreach (var c in ann.LowModCells.Take(400))
                {
                    g.FillRectangle(lowBrush, c);
                    g.DrawRectangle(lowPen, c.X, c.Y, c.Width, c.Height);
                }
            }

            // QR 파인더 패턴 상태
            foreach (var (box, score) in ann.Finders)
            {
                var color = score >= 0.95 ? Color.LimeGreen : score >= 0.85 ? Color.Orange : Color.Red;
                using var pen = new Pen(color, 2.5f);
                g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
                DrawLabel(g, $"{score:P0}", box.X, box.Y - 16, color);
            }

            // DataMatrix L-파인더 / 클록트랙
            foreach (var (a, b, solid, score) in ann.DmEdges)
            {
                var good = solid ? score >= 0.90 : score >= 0.50;
                var color = solid
                    ? (good ? Color.LimeGreen : Color.Red)
                    : (good ? Color.Orange : Color.Red);
                using var pen = new Pen(Color.FromArgb(200, color), 3f);
                g.DrawLine(pen, a, b);
            }

            // 범례
            float ly = 6;
            DrawLabel(g, "파랑=심볼 영역", 6, ly, Color.DodgerBlue); ly += 18;
            if (ann.LowModCells.Count > 0)
            { DrawLabel(g, $"빨간 셀={ann.LowModCells.Count}개 저모듈레이션(번짐/결손 의심)", 6, ly, Color.Red); ly += 18; }
            if (ann.Finders.Count > 0)
            { DrawLabel(g, "박스=파인더 상태(녹색 양호/주황 주의/빨강 손상)", 6, ly, Color.Orange); ly += 18; }
            if (ann.DmEdges.Count > 0)
                DrawLabel(g, "선=DM L-파인더(녹색)/클록트랙(주황), 빨강=손상 의심", 6, ly, Color.Orange);
        }
        using var ms = new MemoryStream();
        canvas.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static void DrawLabel(Graphics g, string text, float x, float y, Color color)
    {
        using var font = new Font("Malgun Gothic", 9f, System.Drawing.FontStyle.Bold);
        var size = g.MeasureString(text, font);
        using var bg = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
        g.FillRectangle(bg, x - 2, y - 1, size.Width + 4, size.Height + 2);
        using var fg = new SolidBrush(color);
        g.DrawString(text, font, fg, x, y);
    }

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
            bool prev = horizontal ? g[o * stride + inner0] < gt : g[inner0 * stride + o] < gt;
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

    /// <summary>셀 격자 샘플링으로 MOD 근사(하위 10퍼센타일). MOD&lt;0.30 셀은 lowCells에 수집.</summary>
    private static double EstimateModulation(byte[] g, int stride, RectangleF box, double gt,
        double pitch, double scByte, List<RectangleF> lowCells)
    {
        if (scByte <= 0) return 0;
        var mods = new List<double>();
        for (double cy = box.Top + pitch / 2; cy < box.Bottom - 1; cy += pitch)
            for (double cx = box.Left + pitch / 2; cx < box.Right - 1; cx += pitch)
            {
                double v = SampleMean(g, stride, (int)cx, (int)cy);
                double m = Math.Min(1.0, 2.0 * Math.Abs(v - gt) / scByte);
                mods.Add(m);
                if (m < 0.30 && lowCells.Count < 2000)
                    lowCells.Add(new RectangleF(
                        (float)(cx - pitch / 2), (float)(cy - pitch / 2), (float)pitch, (float)pitch));
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
                    double uec = Math.Max(0, 1.0 - ec / 8.0);
                    note = $"정정된 오류 {ec}개 기반 근사";
                    return uec;
                }
            }
        }
        note = "오류정정 통계 미제공 → 디코드 성공 기준 추정";
        return 1.0;
    }

    /// <summary>QR 파인더 패턴 / DataMatrix L-파인더+클록트랙 일치율 (0~1). 주석 정보도 수집.</summary>
    private static double? EstimateFixedPattern(Result z, byte[] g, int stride, int w, int h,
        double gt, double pitch, AnnotationInfo ann)
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
                if (n > 0)
                {
                    double score = (double)match / n;
                    total += score; cnt++;
                    float half = (float)(3.5 * pitch);
                    ann.Finders.Add((new RectangleF(p.X - half, p.Y - half, half * 2, half * 2), score));
                }
            }
            return cnt > 0 ? total / cnt : null;
        }

        if (z.BarcodeFormat == BarcodeFormat.DATA_MATRIX && z.ResultPoints.Length >= 4)
        {
            // 4 코너 → 각 변을 0.7모듈 안쪽에서 샘플링. 솔리드 L(항상 흑) 2변 + 클록트랙(교대) 2변
            var pts = z.ResultPoints;
            var edges = new List<(double blackFrac, double altFrac, PointF a, PointF b)>();
            double cx0 = pts.Average(p => p.X), cy0 = pts.Average(p => p.Y);
            for (int e = 0; e < 4; e++)
            {
                var a = pts[e]; var b = pts[(e + 1) % 4];
                int steps = Math.Max(8, (int)(Dist(a, b) / pitch));
                int black = 0, trans = 0, n = 0; bool? prev = null;
                PointF ia = default, ib = default;
                for (int i = 0; i <= steps; i++)
                {
                    double t = (double)i / steps;
                    double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
                    double vx = cx0 - x, vy = cy0 - y;
                    double vl = Math.Sqrt(vx * vx + vy * vy);
                    if (vl > 0) { x += vx / vl * pitch * 0.7; y += vy / vl * pitch * 0.7; }
                    if (i == 0) ia = new PointF((float)x, (float)y);
                    if (i == steps) ib = new PointF((float)x, (float)y);
                    if (x < 1 || y < 1 || x >= stride - 1) continue;
                    bool bl = SampleMean(g, stride, (int)x, (int)y) < gt;
                    if (bl) black++;
                    if (prev.HasValue && prev != bl) trans++;
                    prev = bl; n++;
                }
                if (n > 4) edges.Add(((double)black / n, Math.Min(1.0, trans / (double)(n / 2)), ia, ib));
            }
            if (edges.Count < 4) return null;
            var byBlack = edges.OrderByDescending(s => s.blackFrac).ToList();
            // 상위 2 = L-파인더(솔리드), 하위 2 = 클록트랙
            ann.DmEdges.Add((byBlack[0].a, byBlack[0].b, true, byBlack[0].blackFrac));
            ann.DmEdges.Add((byBlack[1].a, byBlack[1].b, true, byBlack[1].blackFrac));
            ann.DmEdges.Add((byBlack[2].a, byBlack[2].b, false, byBlack[2].altFrac));
            ann.DmEdges.Add((byBlack[3].a, byBlack[3].b, false, byBlack[3].altFrac));
            double solid = (byBlack[0].blackFrac + byBlack[1].blackFrac) / 2;
            double clock = (byBlack[2].altFrac + byBlack[3].altFrac) / 2;
            return Math.Min(solid, Math.Max(0.5, clock));
        }
        return null;
    }

    private static double Dist(ResultPoint a, ResultPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
