using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace ZebraScannerSuite.Services.Interop;

/// <summary>
/// Zebra CoreScanner COM 수동 interop.
/// COMReference(타입 라이브러리) 없이 직접 선언하여 'dotnet build' 만으로 빌드 가능.
/// GUID/DISPID 출처: Zebra 공식 샘플 (Scanner-SDK-for-Windows/SampleApp_CPP/_CoreScanner_i.c, EventSink.cpp)
/// </summary>
internal static class CoreScannerGuids
{
    public const string ClsidCoreScanner = "9F8D4F16-0F61-4A38-98B3-1F6F80F11C87";
    public const string IidICoreScanner = "2105896C-2B38-4031-BD0B-7A9C4A39FB93";
    public const string DiidEvents      = "981E3D8B-C756-4195-A702-F198965031C6";
}

/// <summary>ICoreScanner 듀얼 인터페이스 (vtable 순서: Open, Close, GetScanners, ExecCommand, ExecCommandAsync)</summary>
[ComImport]
[Guid(CoreScannerGuids.IidICoreScanner)]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface ICoreScanner
{
    void Open(
        int appHandle,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I2)] short[] scannerTypes,
        short lengthOfTypes,
        out int status);

    void Close(int appHandle, out int status);

    void GetScanners(
        out short numberOfScanners,
        [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] scannerIDList,
        [MarshalAs(UnmanagedType.BStr)] out string outXML,
        out int status);

    void ExecCommand(
        int opcode,
        [MarshalAs(UnmanagedType.BStr)] ref string inXML,
        [MarshalAs(UnmanagedType.BStr)] out string outXML,
        out int status);

    void ExecCommandAsync(
        int opcode,
        [MarshalAs(UnmanagedType.BStr)] ref string inXML,
        out int status);
}

/// <summary>
/// _ICoreScannerEvents 디스패치 인터페이스 싱크용 IDispatch vtable 선언.
/// (dispinterface 클라이언트는 이 IID로 QI 후 IDispatch::Invoke 로 이벤트를 전달한다)
/// </summary>
[ComImport]
[Guid(CoreScannerGuids.DiidEvents)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICoreScannerEventsSink
{
    [PreserveSig] int GetTypeInfoCount(out int pctinfo);
    [PreserveSig] int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);
    [PreserveSig] int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId);
    [PreserveSig]
    int Invoke(
        int dispIdMember, ref Guid riid, int lcid, short wFlags,
        ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
}

/// <summary>
/// CoreScanner 이벤트 수신 싱크.
/// DISPID: 1=ImageEvent, 2=VideoEvent, 3=BarcodeEvent(ScanData), 4=PNPEvent,
///         5=CommandResponse, 6=ScanRMD, 8=Notification (Zebra EventSink.cpp 기준)
/// </summary>
[ComVisible(true)]
internal sealed class CoreScannerEventSink : ICoreScannerEventsSink
{
    private const int DISPID_IMAGE = 1;
    private const int DISPID_BARCODE = 3;
    private const int DISPID_PNP = 4;

    public Action<string>? BarcodeXml;   // scandata XML
    public Action<byte[]>? ImageData;    // 이미지 raw bytes
    public Action? Pnp;

    public int GetTypeInfoCount(out int pctinfo) { pctinfo = 0; return 0; }

    public int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo)
    { ppTInfo = IntPtr.Zero; return unchecked((int)0x80004001); } // E_NOTIMPL

    public int GetIDsOfNames(ref Guid riid, IntPtr rgszNames, int cNames, int lcid, IntPtr rgDispId)
        => unchecked((int)0x80004001);

    public int Invoke(int dispIdMember, ref Guid riid, int lcid, short wFlags,
        ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr)
    {
        try
        {
            switch (dispIdMember)
            {
                case DISPID_BARCODE:
                {
                    var args = ReadArgs(ref pDispParams);
                    var xml = args.OfType<string>().FirstOrDefault(s => s.Length > 0);
                    if (xml != null) BarcodeXml?.Invoke(xml);
                    break;
                }
                case DISPID_IMAGE:
                {
                    var args = ReadArgs(ref pDispParams);
                    var bytes = args.OfType<byte[]>().FirstOrDefault();
                    if (bytes is { Length: > 0 }) ImageData?.Invoke(bytes);
                    break;
                }
                case DISPID_PNP:
                    Pnp?.Invoke();
                    break;
            }
        }
        catch { /* 이벤트 처리 오류가 COM 채널을 끊지 않도록 */ }
        return 0; // S_OK
    }

    /// <summary>DISPPARAMS의 VARIANT 배열을 자연 순서의 object 배열로 변환 (rgvarg는 역순 저장)</summary>
    private static object[] ReadArgs(ref DISPPARAMS dp)
    {
        int n = dp.cArgs;
        if (n <= 0 || dp.rgvarg == IntPtr.Zero) return Array.Empty<object>();
        int variantSize = IntPtr.Size == 8 ? 24 : 16;
        var result = new List<object>(n);
        for (int i = n - 1; i >= 0; i--) // 역순 → 자연 순서
        {
            try
            {
                object? v = Marshal.GetObjectForNativeVariant(IntPtr.Add(dp.rgvarg, i * variantSize));
                if (v != null) result.Add(v);
            }
            catch { }
        }
        return result.ToArray();
    }
}

/// <summary>CoreScanner COM 객체 생성/이벤트 연결 도우미</summary>
internal sealed class CoreScannerCom : IDisposable
{
    public ICoreScanner Api { get; }
    public CoreScannerEventSink Sink { get; } = new();

    private IConnectionPoint? _cp;
    private int _cookie;

    public CoreScannerCom()
    {
        Type? t = Type.GetTypeFromCLSID(new Guid(CoreScannerGuids.ClsidCoreScanner));
        object? instance = t != null ? Activator.CreateInstance(t) : null;
        if (instance is not ICoreScanner api)
            throw new InvalidOperationException(
                "CoreScanner COM 객체를 생성할 수 없습니다. Zebra Scanner SDK for Windows(CoreScanner 드라이버)를 설치하세요.");
        Api = api;

        // 이벤트 연결 (IConnectionPointContainer → _ICoreScannerEvents)
        var cpc = (IConnectionPointContainer)Api;
        Guid diid = new(CoreScannerGuids.DiidEvents);
        cpc.FindConnectionPoint(ref diid, out _cp);
        _cp!.Advise(Sink, out _cookie);
    }

    public void Dispose()
    {
        try { if (_cp != null && _cookie != 0) _cp.Unadvise(_cookie); }
        catch { }
        try { if (_cp != null) Marshal.ReleaseComObject(_cp); }
        catch { }
        _cp = null;
        try { Marshal.FinalReleaseComObject(Api); } catch { }
    }
}
