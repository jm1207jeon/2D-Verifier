namespace ZebraScannerSuite.Models;

public class BarcodeData
{
    public string Text { get; set; } = "";
    public byte[] Raw { get; set; } = Array.Empty<byte>();
    public int TypeCode { get; set; }
    public string Symbology { get; set; } = "";
    public int ScannerId { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
}

public class ScannerInfo
{
    public int Id { get; set; }
    public string Type { get; set; } = "";     // SNAPI / USBIBMHID / ...
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public override string ToString() => $"[{Id}] {Model} ({Serial}) - {Type}";
}

public class FieldValue
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>일반 스캔 탭 실시간 목록 행. GroupBrush: 연속된 같은 LOT 묶음의 배경색</summary>
public class ScanListRow
{
    public string Lot { get; set; } = "";
    public string Exp { get; set; } = "";
    public string Pn { get; set; } = "";
    public string Sn { get; set; } = "";
    public string GroupBrush { get; set; } = "#00FFFFFF";
}

/// <summary>멀티 스캔 세로 모드 행: 고유 코드당 1행 (중복 스캔은 행 추가 없이 내부 Count만 증가)</summary>
public class MultiScanRow
{
    public int No { get; set; }
    public string TimeText { get; set; } = "";
    public string Gtin { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Mfg { get; set; } = "";
    /// <summary>MFG가 EXP에서 역산된 값인지 (파란색 표시용)</summary>
    public bool MfgComputed { get; set; }
    public string Exp { get; set; } = "";
    public string Pn { get; set; } = "";
    public string Sn { get; set; } = "";
    public string Upn { get; set; } = "";
    public string Raw { get; set; } = "";
    public int Count { get; set; } = 1;
    /// <summary>정렬 시 같은 값끼리 묶어 보여주는 배경색 (미정렬 시 투명)</summary>
    public string GroupBrush { get; set; } = "#00FFFFFF";
}

/// <summary>가로 모드 SN 고정 슬롯(1~15): 15등분 셀 - 스캔 전 회색 숫자,
/// 스캔되면 셀 전체가 연한 녹색으로 채워지고 숫자는 검정으로 표시.</summary>
public class SnSlot
{
    public int Num { get; set; }
    public bool Scanned { get; set; }
}

/// <summary>멀티 스캔 가로 모드 행: LOT당 1행.
/// SN은 1~15 고정 위치 슬롯으로 표시(스캔된 번호만 검정), Sn 문자열은 CSV용 실제 시리얼 목록.</summary>
public class MultiLotRow
{
    public int No { get; set; }
    public string TimeText { get; set; } = "";
    public string Udi { get; set; } = "";
    public string Gtin { get; set; } = "";
    public string Pn { get; set; } = "";
    public string Lot { get; set; } = "";
    public List<string> Serials { get; } = new();
    /// <summary>CSV 내보내기용 실제 시리얼 목록 (오름차순 콤마)</summary>
    public string Sn { get; set; } = "";
    /// <summary>SN 고정 슬롯 1~15 (화면 표시용, 15등분 셀)</summary>
    public List<SnSlot> Slots { get; } = Enumerable.Range(1, 15)
        .Select(n => new SnSlot { Num = n }).ToList();
    /// <summary>1~15 범위 밖 시리얼 (슬롯 우측에 별도 표시)</summary>
    public string SnExtra { get; set; } = "";
    /// <summary>해당 로트의 스캔된 총 수량 (시리얼 개수)</summary>
    public string Qty { get; set; } = "";
    public string Mfg { get; set; } = "";
    /// <summary>MFG가 EXP에서 역산된 값인지 (파란색 표시용)</summary>
    public bool MfgComputed { get; set; }
    public string Exp { get; set; } = "";
    public string Upn { get; set; } = "";
}

