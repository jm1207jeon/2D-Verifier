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
    public string OcrValue { get; set; } = "";
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

/// <summary>ISO/IEC 15415 파라미터 등급 (시뮬레이션/추정치)</summary>
public class ParamGrade
{
    public string Parameter { get; set; } = "";
    public string Value { get; set; } = "";
    /// <summary>4.0=A ... 0.0=F, -1 = N/A(등급 미포함)</summary>
    public double Numeric { get; set; } = -1;
    public string Letter { get; set; } = "-";
    public string Note { get; set; } = "";

    public static string ToLetter(double n) => n switch
    {
        >= 3.5 => "A", >= 2.5 => "B", >= 1.5 => "C", >= 0.5 => "D", >= 0 => "F", _ => "-"
    };
}

public class VerificationResult
{
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Decoded { get; set; }
    public string DecodedText { get; set; } = "";
    public string Format { get; set; } = "";
    public List<ParamGrade> Params { get; set; } = new();
    public double OverallNumeric { get; set; }
    public string OverallLetter { get; set; } = "F";
    public List<string> Notes { get; set; } = new();
    public byte[] ImagePng { get; set; } = Array.Empty<byte>();
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
    public string Summary => $"{TimeText}  {OverallLetter} ({OverallNumeric:0.0})  {Format}  {Truncate(DecodedText, 30)}";
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
