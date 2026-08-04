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

    /// <summary>파일명 규칙. 토큰: {DATE:yyyyMMdd} {TIME:HHmmss} {BARCODE} {SYMBOLOGY} {SEQ:3} (### 도 {SEQ:3}으로 인식)</summary>
    public string FileNameRule { get; set; } = "{DATE:yyyyMMdd}_{BARCODE}_{SEQ:3}";

    /// <summary>0=바코드만, 1=바코드+이미지</summary>
    public int ScanMode { get; set; } = 0;

    /// <summary>선호 호스트 모드 코드 (XUA-45001-x)</summary>
    public string PreferredHostMode { get; set; } = "XUA-45001-9"; // USB SNAPI with imaging

    /// <summary>스캔값을 현재 포커스된 창(엑셀 등)에 키보드로 입력 + Enter (자체 웨지, Caps Lock 무영향)</summary>
    public bool WedgeOutput { get; set; } = true;

    /// <summary>추출 규칙 기본값 버전 (마이그레이션용, 구버전 파일은 0으로 역직렬화됨)</summary>
    public int RulesVersion { get; set; }

    /// <summary>강제 스캔 모드 활성 상태 (F9, 트리거=촬영)</summary>
    public bool ForceScanEnabled { get; set; }

    /// <summary>멀티 스캔 탭: 판독 시마다 배경에서 사진 자동 저장 (화면 표시 없음)</summary>
    public bool MultiSaveImage { get; set; }

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
