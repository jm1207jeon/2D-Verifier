using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ZXing;
using ZXing.Common;
using ZXing.Multi;

namespace ZebraScannerSuite.Services;

public sealed class MultiViewResult
{
    public byte[] AnnotatedPng { get; set; } = Array.Empty<byte>();
    /// <summary>이번 프레임에서 처음 판독된 값들 (세션 기준 신규만)</summary>
    public List<(string Text, string Format)> NewDecodes { get; } = new();
    public int GreenCount { get; set; }
    public int OrangeCount { get; set; }
    public int RedCount { get; set; }
}

/// <summary>
/// 실시간 스캔 뷰 분석기: 연속 촬영 프레임에서
///  - 다중 바코드 판독(ZXing multi) → 녹색(값 표시)
///  - 바코드로 보이는 영역(엣지 밀도 후보)인데 미판독 → 주황
///  - 여러 프레임 연속 판독 실패 → 빨강(손상/판독불가 의심)
/// 영역은 프레임 간 위치로 추적된다.
/// </summary>
public sealed class MultiViewAnalyzer
{
    private sealed class Region
    {
        public RectangleF Rect;
        public string? Value;   // 판독 성공 값 (null이면 미판독)
        public string Format = "";
        public int Fails;       // 연속 판독 실패 관측 수
        public int Missed;      // 연속 미관측 프레임 수
    }

    private const int RedFailThreshold = 4;   // 이 횟수 이상 실패 관측 시 빨강
    private const int DropMissThreshold = 6;  // 이 프레임 수 미관측 시 추적 해제

    private readonly List<Region> _regions = new();
    private readonly HashSet<string> _seenValues = new();

    public void Reset()
    {
        _regions.Clear();
        _seenValues.Clear();
    }

    public MultiViewResult Analyze(Bitmap frame)
    {
        var result = new MultiViewResult();
        int w = frame.Width, h = frame.Height;
        byte[] gray = ToGray(frame, out _);

        // ---- 1) 다중 바코드 판독 ----
        var decodes = new List<(RectangleF Rect, string Text, string Format)>();
        try
        {
            var lum = new ZXing.Windows.Compatibility.BitmapLuminanceSource(frame);
            var bb = new BinaryBitmap(new HybridBinarizer(lum));
            var hints = new Dictionary<DecodeHintType, object>
            {
                { DecodeHintType.TRY_HARDER, true },
                { DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat>
                    {
                        BarcodeFormat.DATA_MATRIX, BarcodeFormat.QR_CODE, BarcodeFormat.AZTEC,
                        BarcodeFormat.PDF_417, BarcodeFormat.CODE_128, BarcodeFormat.CODE_39,
                        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.ITF,
                    } },
            };
            var found = new GenericMultipleBarcodeReader(new MultiFormatReader()).decodeMultiple(bb, hints);
            if (found != null)
                foreach (var r in found)
                {
                    if (string.IsNullOrEmpty(r.Text) || r.ResultPoints is not { Length: > 0 }) continue;
                    decodes.Add((PointsToRect(r.ResultPoints, w, h), r.Text, r.BarcodeFormat.ToString()));
                }
        }
        catch { /* 판독 실패 = 빈 목록 */ }

        // ---- 2) 바코드 후보 영역 검출 (엣지 밀도) ----
        var candidates = FindCandidates(gray, w, h);

        // ---- 3) 영역 추적 갱신 ----
        foreach (var reg in _regions) reg.Missed++;

        foreach (var (rect, text, format) in decodes)
        {
            var reg = MatchRegion(rect) ?? AddRegion(rect);
            reg.Rect = rect;
            reg.Value = text;
            reg.Format = format;
            reg.Fails = 0;
            reg.Missed = 0;
            if (_seenValues.Add(format + "|" + text))
                result.NewDecodes.Add((text, format));
        }

        foreach (var cand in candidates)
        {
            // 이번 프레임에 판독된 영역과 겹치면 후보 아님
            if (decodes.Any(d => Overlap(d.Rect, cand) > 0.2)) continue;
            var reg = MatchRegion(cand);
            if (reg == null)
            {
                reg = AddRegion(cand);
                reg.Fails = 1;
            }
            else
            {
                reg.Rect = cand;
                if (reg.Value == null) reg.Fails++;   // 미판독 관측 누적
            }
            reg.Missed = 0;
        }

        _regions.RemoveAll(r => r.Missed > DropMissThreshold);

        // ---- 4) 렌더링 ----
        using var canvas = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.DrawImage(frame, 0, 0, w, h);
            using var font = new Font("Malgun Gothic", 10f, System.Drawing.FontStyle.Bold);
            foreach (var reg in _regions.Where(r => r.Missed <= 2))
            {
                Color color;
                string label;
                if (reg.Value != null)
                {
                    color = Color.LimeGreen;
                    label = reg.Value.Length > 20 ? reg.Value[..20] + "…" : reg.Value;
                    result.GreenCount++;
                }
                else if (reg.Fails >= RedFailThreshold)
                {
                    color = Color.Red;
                    label = "판독 불가 의심";
                    result.RedCount++;
                }
                else
                {
                    color = Color.Orange;
                    label = "미판독";
                    result.OrangeCount++;
                }
                using var pen = new Pen(color, 3f);
                g.DrawRectangle(pen, reg.Rect.X, reg.Rect.Y, reg.Rect.Width, reg.Rect.Height);
                DrawLabel(g, font, label, reg.Rect.X, Math.Max(0, reg.Rect.Y - 18), color);
            }
            DrawLabel(g, font,
                $"판독 {result.GreenCount} · 미판독 {result.OrangeCount} · 불가의심 {result.RedCount}",
                6, 6, Color.White);
        }
        using var ms = new MemoryStream();
        canvas.Save(ms, ImageFormat.Png);
        result.AnnotatedPng = ms.ToArray();
        return result;
    }

    // ---------------- 추적 유틸 ----------------

    private Region? MatchRegion(RectangleF rect)
    {
        float cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        Region? best = null;
        float bestDist = float.MaxValue;
        foreach (var r in _regions)
        {
            float rx = r.Rect.X + r.Rect.Width / 2, ry = r.Rect.Y + r.Rect.Height / 2;
            float dist = Math.Abs(cx - rx) + Math.Abs(cy - ry);
            float limit = Math.Max(r.Rect.Width, r.Rect.Height) * 0.7f + 20;
            if (dist < limit && dist < bestDist) { best = r; bestDist = dist; }
        }
        return best;
    }

    private Region AddRegion(RectangleF rect)
    {
        var reg = new Region { Rect = rect };
        _regions.Add(reg);
        return reg;
    }

    private static RectangleF PointsToRect(ResultPoint[] pts, int w, int h)
    {
        float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
        float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        float mx = Math.Max(6, (maxX - minX) * 0.15f), my = Math.Max(6, (maxY - minY) * 0.15f);
        return RectangleF.FromLTRB(
            Math.Max(0, minX - mx), Math.Max(0, minY - my),
            Math.Min(w - 1, maxX + mx), Math.Min(h - 1, maxY + my));
    }

    private static float Overlap(RectangleF a, RectangleF b)
    {
        var inter = RectangleF.Intersect(a, b);
        if (inter.IsEmpty) return 0;
        float areaI = inter.Width * inter.Height;
        float areaMin = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return areaMin > 0 ? areaI / areaMin : 0;
    }

    // ---------------- 후보 영역 검출 ----------------

    /// <summary>16px 셀 단위 엣지 밀도로 바코드 후보 블롭을 찾는다 (정확한 심볼로지 판별 없이 영역만)</summary>
    private static List<RectangleF> FindCandidates(byte[] g, int w, int h)
    {
        const int cell = 16;
        int gw = w / cell, gh = h / cell;
        var boxes = new List<RectangleF>();
        if (gw < 4 || gh < 4) return boxes;

        var score = new double[gw, gh];
        double total = 0;
        for (int cy = 0; cy < gh; cy++)
            for (int cx = 0; cx < gw; cx++)
            {
                double s = 0;
                int x0 = cx * cell, y0 = cy * cell;
                for (int y = y0 + 1; y < y0 + cell - 1; y += 2)
                    for (int x = x0 + 1; x < x0 + cell - 1; x += 2)
                    {
                        int i = y * w + x;
                        s += Math.Abs(g[i + 1] - g[i - 1]) + Math.Abs(g[i + w] - g[i - w]);
                    }
                score[cx, cy] = s;
                total += s;
            }

        double mean = total / (gw * gh);
        double thr = Math.Max(mean * 1.8, 1500); // 배경 노이즈 하한

        var busy = new bool[gw, gh];
        for (int cy = 0; cy < gh; cy++)
            for (int cx = 0; cx < gw; cx++)
                busy[cx, cy] = score[cx, cy] > thr;

        var visited = new bool[gw, gh];
        var stack = new Stack<(int x, int y)>();
        for (int cy = 0; cy < gh; cy++)
            for (int cx = 0; cx < gw; cx++)
            {
                if (!busy[cx, cy] || visited[cx, cy]) continue;
                int minX = cx, maxX = cx, minY = cy, maxY = cy, count = 0;
                stack.Push((cx, cy));
                visited[cx, cy] = true;
                while (stack.Count > 0)
                {
                    var (x, y) = stack.Pop();
                    count++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    foreach (var (nx, ny) in new[] { (x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= gw || ny >= gh) continue;
                        if (busy[nx, ny] && !visited[nx, ny]) { visited[nx, ny] = true; stack.Push((nx, ny)); }
                    }
                }

                float bw = (maxX - minX + 1) * cell, bh = (maxY - minY + 1) * cell;
                float fill = count / ((maxX - minX + 1f) * (maxY - minY + 1f));
                float aspect = bw / bh;
                if (bw < 40 || bh < 40) continue;                        // 너무 작음
                if (bw > w * 0.85f || bh > h * 0.85f) continue;          // 배경 전체
                if (aspect < 0.25f || aspect > 4f) continue;             // 비정상 비율
                if (fill < 0.45f) continue;                              // 산발 노이즈
                boxes.Add(new RectangleF(minX * cell, minY * cell, bw, bh));
            }
        return boxes;
    }

    private static void DrawLabel(Graphics g, Font font, string text, float x, float y, Color color)
    {
        var size = g.MeasureString(text, font);
        using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        g.FillRectangle(bg, x - 2, y - 1, size.Width + 4, size.Height + 2);
        using var fg = new SolidBrush(color);
        g.DrawString(text, font, fg, x, y);
    }

    private static byte[] ToGray(Bitmap bmp, out int stride)
    {
        int w = bmp.Width, h = bmp.Height;
        stride = w;
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
