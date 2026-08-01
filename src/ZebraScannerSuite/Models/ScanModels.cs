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

public class ScanRecord
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Barcode { get; set; } = "";
    public string Symbology { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public string TimeText => Time.ToString("HH:mm:ss");
}

public class FieldValue
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public class MultiScanRow
{
    public int No { get; set; }
    public string TimeText { get; set; } = "";
    public string Symbology { get; set; } = "";
    public string Data { get; set; } = "";
    public int Count { get; set; } = 1;
}

