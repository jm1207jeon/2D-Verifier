using System.Collections.ObjectModel;
using System.IO;

namespace ZebraScannerSuite.Models;

/// <summary>데이터 추출 규칙. Type: "GS1"(응용식별자) / "REGEX"(정규식) / "SUBSTR"(위치 지정)</summary>
public class ExtractionRule
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "GS1";
    /// <summary>GS1: AI 코드(예 "17") / REGEX: 패턴 / SUBSTR: 시작위치(1부터)</summary>
    public string Param1 { get; set; } = "";
    /// <summary>SUBSTR: 길이. 그 외 미사용</summary>
    public string Param2 { get; set; } = "";
    /// <summary>YYMMDD/YYYYMMDD 값을 YYYY-MM-DD 로 변환</summary>
    public bool DateConvert { get; set; }
}

public class AppSettings
{
    public string ImageSaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ZebraScans");

    public string ReportDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ZebraVerifyReports");

    /// <summary>파일명 규칙. 토큰: {DATE:yyyyMMdd} {TIME:HHmmss} {BARCODE} {SYMBOLOGY} {SEQ:3} (### 도 {SEQ:3}으로 인식)</summary>
    public string FileNameRule { get; set; } = "{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}";

    /// <summary>0=바코드만, 1=바코드+이미지, 2=바코드+이미지+OCR</summary>
    public int ScanMode { get; set; } = 0;

    /// <summary>OCR 허용 패턴(정규식, 줄 단위). 일치하는 문자만 채택하고 나머지는 무시.</summary>
    public List<string> OcrPatterns { get; set; } = new()
    {
        @"\d{4}-\d{2}-\d{2}",       // YYYY-MM-DD
        @"\b[A-Z0-9]{8}\b",         // LOT 8자리
    };

    public bool CopyOcrToClipboard { get; set; } = true;

    /// <summary>선호 호스트 모드 코드 (XUA-45001-x)</summary>
    public string PreferredHostMode { get; set; } = "XUA-45001-9"; // USB SNAPI with imaging

    public bool MultiAutoRetrigger { get; set; } = true;

    public ObservableCollection<ExtractionRule> ExtractionRules { get; set; } = new()
    {
        new ExtractionRule { Name = "GTIN/품번",  Type = "GS1", Param1 = "01" },
        new ExtractionRule { Name = "제조일자",   Type = "GS1", Param1 = "11", DateConvert = true },
        new ExtractionRule { Name = "유효기한",   Type = "GS1", Param1 = "17", DateConvert = true },
        new ExtractionRule { Name = "LOT",       Type = "GS1", Param1 = "10" },
        new ExtractionRule { Name = "시리얼",     Type = "GS1", Param1 = "21" },
    };
}
