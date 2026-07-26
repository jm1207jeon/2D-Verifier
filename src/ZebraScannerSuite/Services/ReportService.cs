using System.IO;
using System.Text;
using ZebraScannerSuite.Models;

namespace ZebraScannerSuite.Services;

/// <summary>BARCODE VERIFY 결과를 이미지 포함 HTML 리포트로 저장 (브라우저에서 인쇄/PDF 저장 가능)</summary>
public static class ReportService
{
    public static string SaveReport(IList<VerificationResult> results, string reportDir, string scannerInfo)
    {
        Directory.CreateDirectory(reportDir);
        string baseName = "VERIFY_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string htmlPath = Path.Combine(reportDir, baseName + ".html");

        // 원본 및 오버레이 이미지도 개별 PNG로 저장
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].ImagePng.Length > 0)
                File.WriteAllBytes(Path.Combine(reportDir, $"{baseName}_{i + 1:00}.png"), results[i].ImagePng);
            if (results[i].AnnotatedPng.Length > 0)
                File.WriteAllBytes(Path.Combine(reportDir, $"{baseName}_{i + 1:00}_overlay.png"), results[i].AnnotatedPng);
        }

        File.WriteAllText(htmlPath, BuildHtml(results, scannerInfo), Encoding.UTF8);
        return htmlPath;
    }

    public static string BuildHtml(IList<VerificationResult> results, string scannerInfo)
    {
        var sb = new StringBuilder();
        sb.Append("""
<!DOCTYPE html>
<html lang="ko"><head><meta charset="utf-8">
<title>Barcode Verification Report (ISO/IEC 15415 Simulation)</title>
<style>
 body{font-family:'Segoe UI',Malgun Gothic,sans-serif;margin:24px;color:#222}
 h1{font-size:20px;border-bottom:3px solid #1a5276;padding-bottom:6px}
 h2{font-size:16px;margin-top:28px;color:#1a5276}
 table{border-collapse:collapse;width:100%;margin:8px 0}
 th,td{border:1px solid #bbb;padding:5px 9px;font-size:13px;text-align:left}
 th{background:#eaf1f7}
 .gA{background:#d4efdf;font-weight:bold}.gB{background:#eafaf1}
 .gC{background:#fef9e7}.gD{background:#fdebd0}.gF{background:#fadbd8;font-weight:bold}
 .overall{font-size:26px;font-weight:bold;padding:8px 16px;display:inline-block;border-radius:6px}
 img.cap{max-width:420px;border:1px solid #999;margin:6px 0}
 .disclaimer{background:#fdf2e9;border:1px solid #e59866;padding:10px;font-size:12px;margin-top:24px}
 .meta{font-size:12px;color:#555}
 @media print {.measure{page-break-inside:avoid}}
</style></head><body>
""");
        sb.Append("<h1>Barcode Verification Report <span style='font-size:13px'>(ISO/IEC 15415 시뮬레이션)</span></h1>");
        sb.Append($"<p class='meta'>생성일시: {DateTime.Now:yyyy-MM-dd HH:mm:ss}<br>스캐너: {Html(scannerInfo)}<br>측정 수: {results.Count}건</p>");

        if (results.Count > 1)
        {
            double avg = results.Average(r => r.OverallNumeric);
            sb.Append($"<p>세션 평균 등급: <b>{ParamGrade.ToLetter(avg)} ({avg:0.0})</b> / 최저: <b>{results.Min(r => r.OverallNumeric):0.0}</b></p>");
        }

        int idx = 0;
        foreach (var r in results)
        {
            idx++;
            sb.Append($"<div class='measure'><h2>측정 #{idx} - {r.TimeText}</h2>");
            sb.Append($"<span class='overall g{r.OverallLetter}'>종합 등급: {r.OverallLetter} ({r.OverallNumeric:0.0})</span>");
            sb.Append($"<p><b>심볼로지:</b> {Html(r.Format)}<br><b>디코드 값:</b> <code>{Html(r.DecodedText)}</code></p>");
            var img = r.AnnotatedPng.Length > 0 ? r.AnnotatedPng : r.ImagePng;
            if (img.Length > 0)
                sb.Append($"<img class='cap' src='data:image/png;base64,{Convert.ToBase64String(img)}'>" +
                          "<div class='meta'>(문제 영역 오버레이: 파랑=심볼 영역, 빨간 셀=저모듈레이션, 박스/선=파인더·클록 상태)</div>");
            sb.Append("<table><tr><th>파라미터</th><th>측정값</th><th>등급</th><th>비고</th></tr>");
            foreach (var p in r.Params)
            {
                string cls = p.Numeric >= 0 ? "g" + p.Letter : "";
                string grade = p.Numeric >= 0 ? $"{p.Letter} ({p.Numeric:0.0})" : "-";
                sb.Append($"<tr><td>{Html(p.Parameter)}</td><td>{Html(p.Value)}</td><td class='{cls}'>{grade}</td><td>{Html(p.Note)}</td></tr>");
            }
            sb.Append("</table>");
            if (r.Recommendations.Count > 0)
            {
                sb.Append("<p><b>개선 권장사항</b></p><ul>");
                foreach (var rec in r.Recommendations) sb.Append($"<li>{Html(rec)}</li>");
                sb.Append("</ul>");
            }
            if (r.Notes.Count > 0)
                sb.Append("<ul class='meta'>" + string.Join("", r.Notes.Select(n => $"<li>{Html(n)}</li>")) + "</ul>");
            sb.Append("</div>");
        }

        sb.Append("""
<div class='disclaimer'><b>고지:</b> 본 리포트는 전용 검증기(ISO 규격 조명/개구/교정) 없이 일반 이미저 스캐너의
캡처 이미지를 알고리즘으로 분석한 <b>시뮬레이션 결과</b>입니다. 인쇄 품질의 대략적인 경향 파악 용도로만 사용하시고,
공식 품질 성적이 필요한 경우 ISO/IEC 15415 인증 검증기를 사용하십시오.</div>
</body></html>
""");
        return sb.ToString();
    }

    private static string Html(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
