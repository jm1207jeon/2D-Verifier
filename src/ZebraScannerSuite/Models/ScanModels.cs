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

/// <summary>멀티 스캔 판독 목록 행 (응용식별자별 분리, 중복은 Count만 증가)</summary>
public class MultiScanRow
{
    public int No { get; set; }
    public string TimeText { get; set; } = "";
    public string Gtin { get; set; } = "";
    public string Lot { get; set; } = "";
    public string Mfg { get; set; } = "";
    public string Exp { get; set; } = "";
    public string Pn { get; set; } = "";
    public string Sn { get; set; } = "";
    public string Upn { get; set; } = "";
    public string Raw { get; set; } = "";
    public int Count { get; set; } = 1;
}

