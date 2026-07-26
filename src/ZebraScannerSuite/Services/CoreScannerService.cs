using System.Text;
using System.Xml.Linq;
using ZebraScannerSuite.Models;
using ZebraScannerSuite.Services.Interop;

namespace ZebraScannerSuite.Services;

/// <summary>
/// Zebra Scanner SDK for Windows(CoreScanner COM 드라이버) 래퍼.
/// - 바코드/이미지/PnP 이벤트 수신 (SNAPI / IBM-HID 모드)
/// - 트리거 제어, 이미지 캡처, 호스트 모드(HID/SNAPI 등) 전환
/// SDK 다운로드: zebra.com > Scanner SDK for Windows
/// </summary>
public sealed class CoreScannerService : IDisposable
{
    // ---- CoreScanner opcodes (Zebra 공식 샘플과 동일) ----
    private const int REGISTER_FOR_EVENTS   = 1001;
    private const int UNREGISTER_FOR_EVENTS = 1002;
    private const int DEVICE_AIM_OFF        = 2002;
    private const int DEVICE_AIM_ON         = 2003;
    private const int DEVICE_PULL_TRIGGER   = 2011;
    private const int DEVICE_RELEASE_TRIGGER= 2012;
    private const int DEVICE_SCAN_DISABLE   = 2013;
    private const int DEVICE_SCAN_ENABLE    = 2014;
    private const int DEVICE_CAPTURE_IMAGE  = 3000;
    private const int DEVICE_CAPTURE_BARCODE= 3500;
    private const int SET_ACTION            = 6000;
    private const int DEVICE_SWITCH_HOST_MODE = 6200;

    // ---- 호스트 모드 코드 (opcode 6200 arg-string) ----
    public static readonly (string Name, string Code)[] HostModes =
    {
        ("USB SNAPI (이미징 지원) - 권장", "XUA-45001-9"),
        ("USB SNAPI (이미징 미지원)",      "XUA-45001-10"),
        ("USB HID 키보드",                "XUA-45001-3"),
        ("USB IBM 핸드헬드",              "XUA-45001-1"),
        ("USB IBM 테이블탑",              "XUA-45001-2"),
        ("USB OPOS",                      "XUA-45001-8"),
    };

    private CoreScannerCom? _com;
    private ICoreScanner? _core;
    private readonly object _lock = new();

    public bool IsOpen { get; private set; }
    public List<ScannerInfo> Scanners { get; } = new();
    public ScannerInfo? ActiveScanner => Scanners.Count > 0 ? Scanners[0] : null;

    /// <summary>바코드 디코드 이벤트 (COM 스레드에서 호출됨 - UI는 Dispatcher 필요)</summary>
    public event Action<BarcodeData>? BarcodeScanned;
    /// <summary>이미지 캡처 완료 (raw bytes: JPEG/BMP/TIFF - magic byte로 판별)</summary>
    public event Action<byte[]>? ImageCaptured;
    public event Action? DevicesChanged;
    public event Action<string>? StatusMessage;

    public void Open()
    {
        lock (_lock)
        {
            if (IsOpen) return;
            _com = new CoreScannerCom();
            _core = _com.Api;
            _com.Sink.BarcodeXml = OnBarcodeXml;
            _com.Sink.ImageData = OnImageData;
            _com.Sink.Pnp = OnPnp;

            short[] scannerTypes = { 1 }; // 1 = 모든 스캐너 타입
            _core.Open(0, scannerTypes, 1, out int status);
            if (status != 0)
                throw new InvalidOperationException(
                    $"CoreScanner 초기화 실패 (status={status}). Zebra Scanner SDK(CoreScanner 드라이버) 설치 여부를 확인하세요.");

            // 이벤트 구독: 1=Barcode 2=Image 4=Video 8=RMD 16=PNP 32=Other
            string inXml = "<inArgs><cmdArgs><arg-int>6</arg-int><arg-int>1,2,4,8,16,32</arg-int></cmdArgs></inArgs>";
            _core.ExecCommand(REGISTER_FOR_EVENTS, ref inXml, out _, out status);
            IsOpen = true;
        }
        RefreshScanners();
    }

    public void RefreshScanners()
    {
        if (_core == null) return;
        lock (_lock)
        {
            Scanners.Clear();
            int[] ids = new int[255];
            _core.GetScanners(out short num, ids, out string outXml, out int status);
            if (status != 0 || string.IsNullOrWhiteSpace(outXml)) return;
            try
            {
                var doc = XDocument.Parse(Sanitize(outXml));
                foreach (var sc in doc.Descendants("scanner"))
                {
                    Scanners.Add(new ScannerInfo
                    {
                        Id = (int?)sc.Element("scannerID") ?? 0,
                        Type = (string?)sc.Attribute("type") ?? "",
                        Model = ((string?)sc.Element("modelnumber") ?? "").Trim(),
                        Serial = ((string?)sc.Element("serialnumber") ?? "").Trim(),
                        Firmware = ((string?)sc.Element("firmware") ?? "").Trim(),
                    });
                }
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke("스캐너 목록 파싱 오류: " + ex.Message);
            }
        }
    }

    // ---------------- 명령 ----------------

    private (int status, string outXml) Exec(int opcode, string inXml)
    {
        lock (_lock)
        {
            if (_core == null) return (-1, "");
            _core.ExecCommand(opcode, ref inXml, out string outXml, out int status);
            return (status, outXml ?? "");
        }
    }

    private static string ScannerXml(int id) => $"<inArgs><scannerID>{id}</scannerID></inArgs>";

    public bool PullTrigger(int scannerId)  => Exec(DEVICE_PULL_TRIGGER,   ScannerXml(scannerId)).status == 0;
    public bool ReleaseTrigger(int scannerId) => Exec(DEVICE_RELEASE_TRIGGER, ScannerXml(scannerId)).status == 0;
    public bool AimOn(int scannerId)  => Exec(DEVICE_AIM_ON,  ScannerXml(scannerId)).status == 0;
    public bool AimOff(int scannerId) => Exec(DEVICE_AIM_OFF, ScannerXml(scannerId)).status == 0;
    public bool ScanEnable(int scannerId)  => Exec(DEVICE_SCAN_ENABLE,  ScannerXml(scannerId)).status == 0;

    /// <summary>다음 트리거를 이미지 캡처 모드로 전환 (SNAPI 이미징 모드 필요)</summary>
    public bool SetCaptureImageMode(int scannerId) => Exec(DEVICE_CAPTURE_IMAGE, ScannerXml(scannerId)).status == 0;

    /// <summary>디코드(바코드) 모드로 복귀</summary>
    public bool SetCaptureBarcodeMode(int scannerId) => Exec(DEVICE_CAPTURE_BARCODE, ScannerXml(scannerId)).status == 0;

    /// <summary>이미지 캡처 시퀀스: 이미지 모드 전환 후 트리거 당김. 완료시 ImageCaptured 이벤트 발생.</summary>
    public bool CaptureImage(int scannerId)
    {
        if (!SetCaptureImageMode(scannerId)) return false;
        Thread.Sleep(60); // 모드 전환 안정화
        return PullTrigger(scannerId);
    }

    /// <summary>호스트 모드 전환 (예: HID → SNAPI). 스캐너가 재부팅되며 재열거된다.</summary>
    public bool SwitchHostMode(int scannerId, string code, bool permanent)
    {
        string inXml =
            $"<inArgs><scannerID>{scannerId}</scannerID><cmdArgs>" +
            $"<arg-string>{code}</arg-string>" +
            "<arg-bool>TRUE</arg-bool>" +                       // silent reboot
            $"<arg-bool>{(permanent ? "TRUE" : "FALSE")}</arg-bool>" + // 영구 적용
            "</cmdArgs></inArgs>";
        var (status, _) = Exec(DEVICE_SWITCH_HOST_MODE, inXml);
        return status == 0;
    }

    /// <summary>비프/LED 액션 (actionCode: SDK 문서 참조, 예 1=짧은 고음 1회)</summary>
    public void Beep(int scannerId, int actionCode = 1)
    {
        Exec(SET_ACTION, $"<inArgs><scannerID>{scannerId}</scannerID><cmdArgs><arg-int>{actionCode}</arg-int></cmdArgs></inArgs>");
    }

    // ---------------- 이벤트 핸들러 (COM 콜백 스레드) ----------------

    private void OnBarcodeXml(string pscanData)
    {
        try
        {
            var doc = XDocument.Parse(Sanitize(pscanData));
            var scan = doc.Descendants("scandata").FirstOrDefault() ?? doc.Root;
            if (scan == null) return;

            int typeCode = (int?)scan.Element("datatype") ?? 0;
            string hex = (string?)scan.Element("datalabel") ?? "";
            byte[] raw = HexToBytes(hex);

            var data = new BarcodeData
            {
                Raw = raw,
                Text = BytesToText(raw),
                TypeCode = typeCode,
                Symbology = SymbologyMap.Name(typeCode),
                ScannerId = (int?)doc.Descendants("scannerID").FirstOrDefault() ?? (ActiveScanner?.Id ?? 1),
            };
            BarcodeScanned?.Invoke(data);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke("바코드 이벤트 파싱 오류: " + ex.Message);
        }
    }

    private void OnImageData(byte[] bytes)
    {
        try
        {
            if (bytes.Length > 0)
                ImageCaptured?.Invoke(bytes);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke("이미지 이벤트 오류: " + ex.Message);
        }
    }

    private void OnPnp()
    {
        try
        {
            RefreshScanners();
            DevicesChanged?.Invoke();
        }
        catch { }
    }

    // ---------------- 유틸 ----------------

    private static string Sanitize(string xml)
    {
        int i = xml.IndexOf('<');
        if (i > 0) xml = xml[i..];
        return xml.Replace("\0", "").Trim();
    }

    private static byte[] HexToBytes(string hexLabel)
    {
        // 형식: "0x30 0x31 0x1d ..." 또는 연속 hex
        var parts = hexLabel.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<byte>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p[2..] : p;
            if (byte.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                list.Add(b);
        }
        return list.ToArray();
    }

    private static string BytesToText(byte[] raw)
    {
        try { return new UTF8Encoding(false, true).GetString(raw); }
        catch { return Encoding.Latin1.GetString(raw); }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_core != null)
            {
                try
                {
                    string inXml = "<inArgs><cmdArgs><arg-int>6</arg-int><arg-int>1,2,4,8,16,32</arg-int></cmdArgs></inArgs>";
                    _core.ExecCommand(UNREGISTER_FOR_EVENTS, ref inXml, out _, out _);
                    _core.Close(0, out _);
                }
                catch { }
                _core = null;
            }
            try { _com?.Dispose(); } catch { }
            _com = null;
            IsOpen = false;
        }
    }
}

/// <summary>Zebra datatype 코드 → 심볼로지 이름</summary>
public static class SymbologyMap
{
    private static readonly Dictionary<int, string> Map = new()
    {
        {1,"CODE39"},{2,"CODABAR"},{3,"CODE128"},{4,"DISCRETE 2OF5"},{5,"IATA"},
        {6,"INTERLEAVED 2OF5"},{7,"CODE93"},{8,"UPC-A"},{9,"UPC-E0"},{10,"EAN-8"},
        {11,"EAN-13"},{12,"CODE11"},{13,"CODE49"},{14,"MSI"},{15,"GS1-128"},
        {16,"UPC-E1"},{17,"PDF417"},{18,"CODE16K"},{19,"CODE39 FULL ASCII"},{20,"UPC-D"},
        {21,"TRIOPTIC"},{22,"BOOKLAND"},{23,"COUPON"},{24,"NW7"},{25,"ISBT-128"},
        {26,"MICRO PDF"},{27,"DATAMATRIX"},{28,"QR CODE"},{29,"MICRO PDF CCA"},
        {30,"POSTNET US"},{31,"PLANET CODE"},{32,"CODE32"},{33,"ISBT-128 CON"},
        {34,"JAPAN POSTAL"},{35,"AUS POSTAL"},{36,"DUTCH POSTAL"},{37,"MAXICODE"},
        {38,"CANADIAN POSTAL"},{39,"UK POSTAL"},{40,"MACRO PDF"},{44,"MICRO QR"},
        {45,"AZTEC"},{48,"GS1 DATABAR"},{49,"GS1 DATABAR LIMITED"},{50,"GS1 DATABAR EXPANDED"},
        {55,"SCANLET"},{72,"UPC-A + 2"},{73,"UPC-E0 + 2"},{74,"EAN-8 + 2"},{75,"EAN-13 + 2"},
        {80,"UPC-A + 5"},{81,"UPC-E0 + 5"},{82,"EAN-8 + 5"},{83,"EAN-13 + 5"},
        {99,"GS1 DATAMATRIX"},
    };

    public static string Name(int code) => Map.TryGetValue(code, out var n) ? n : $"TYPE({code})";

    public static bool Is2D(string name) =>
        name.Contains("QR") || name.Contains("DATAMATRIX") || name.Contains("AZTEC") ||
        name.Contains("PDF") || name.Contains("MAXICODE");
}
