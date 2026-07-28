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

/// <summary>강제 스캔(OCR) 모드에서 텍스트 라벨을 값으로 변환하는 규칙.
/// 정규식 캡처 그룹을 Output의 $1,$2.. 로 조합한다.</summary>
public class ForceOcrRule
{
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Output { get; set; } = "$1";
}

public class AppSettings
{
    public string ImageSaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ZebraScans");

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

    /// <summary>스캔값을 현재 포커스된 창(엑셀 등)에 키보드로 입력 + Enter (자체 웨지, Caps Lock 무영향)</summary>
    public bool WedgeOutput { get; set; } = true;

    /// <summary>추출 규칙 기본값 버전 (마이그레이션용, 구버전 파일은 0으로 역직렬화됨)</summary>
    public int RulesVersion { get; set; }

    /// <summary>강제 스캔 모드 활성 상태 (F9, 트리거=촬영)</summary>
    public bool ForceScanEnabled { get; set; }

    /// <summary>강제 스캔 시 OCR 수행 여부 (강제 스캔이 켜져 있을 때만 유효)</summary>
    public bool ForceOcrEnabled { get; set; } = true;

    /// <summary>강제 OCR 변환 규칙 (위에서부터 순서대로 첫 일치 규칙 적용)</summary>
    public ObservableCollection<ForceOcrRule> ForceOcrRules { get; set; } = new()
    {
        // 유형1: "Lot No. 26070124" + "SN. 14" → 26070124-14 (SN 1자리면 -1)
        new ForceOcrRule
        {
            Name = "유형1 Lot+SN",
            Pattern = @"Lot\s*No\.?\s*[:.]?\s*(\d{4,12})[\s\S]{0,60}?S\s*/?\s*N\.?\s*[:.]?\s*(\d{1,4})",
            Output = "$1-$2",
        },
        // 유형2: "26070124-11" / "26070124-1" → 그대로
        new ForceOcrRule
        {
            Name = "유형2 결합형",
            Pattern = @"(\d{7,10})\s*-\s*(\d{1,4})",
            Output = "$1-$2",
        },
    };

    public ObservableCollection<ExtractionRule> ExtractionRules { get; set; } = DefaultExtractionRules();

    /// <summary>기본 추출 필드: GTIN(01) / LOT(10) / PN(240) / MFG DATE(11) / EXP DATE(17) / SN(21) / UPN(30)</summary>
    public static ObservableCollection<ExtractionRule> DefaultExtractionRules() => new()
    {
        new ExtractionRule { Name = "GTIN",     Type = "GS1", Param1 = "01" },
        new ExtractionRule { Name = "LOT",      Type = "GS1", Param1 = "10" },
        new ExtractionRule { Name = "PN",       Type = "GS1", Param1 = "240" },
        new ExtractionRule { Name = "MFG DATE", Type = "GS1", Param1 = "11", DateConvert = true },
        new ExtractionRule { Name = "EXP DATE", Type = "GS1", Param1 = "17", DateConvert = true },
        new ExtractionRule { Name = "SN",       Type = "GS1", Param1 = "21" },
        new ExtractionRule { Name = "UPN",      Type = "GS1", Param1 = "30" },
    };
}
