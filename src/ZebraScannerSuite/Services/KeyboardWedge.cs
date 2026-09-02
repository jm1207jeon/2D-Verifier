using System.Runtime.InteropServices;

namespace ZebraScannerSuite.Services;

/// <summary>
/// 스캔 값을 현재 포커스된 창(엑셀 등)의 커서 위치에 타이핑하는 키보드 웨지.
/// SendInput의 유니코드 이벤트를 사용하므로 Caps Lock 상태를 건드리지 않고
/// 한글 IME 상태와도 무관하게 원문 그대로 입력된다.
/// (Zebra HID 키보드 에뮬레이터의 Caps Lock 토글 문제를 대체)
/// </summary>
public static class KeyboardWedge
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // 유니온 크기 확보용
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>활성 창 커서 위치에 문자열 타이핑. appendEnter 시 마지막에 Enter 전송(엑셀 다음 셀 이동).
    /// 반환값 false = 입력이 차단됨 (대상 프로그램이 관리자 권한으로 실행 중이면 UIPI가 SendInput을 막는다).</summary>
    public static bool TypeText(string text, bool appendEnter = true)
    {
        if (string.IsNullOrEmpty(text) && !appendEnter) return true;
        var list = new List<INPUT>(text.Length * 2 + 2);
        foreach (char c in text)
        {
            if (c == '\r' || c == '\n' || char.IsControl(c)) continue;
            list.Add(Make(0, c, KEYEVENTF_UNICODE));
            list.Add(Make(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        if (appendEnter)
        {
            list.Add(Make(VK_RETURN, 0, 0));
            list.Add(Make(VK_RETURN, 0, KEYEVENTF_KEYUP));
        }
        if (list.Count == 0) return true;
        try
        {
            uint sent = SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
            if (sent == list.Count) return true;
            AppLog.Warn($"키보드 웨지 입력 차단/부분 전송: {sent}/{list.Count} (Win32 오류 {Marshal.GetLastWin32Error()})");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Error("키보드 웨지 SendInput 실패", ex);
            return false;
        }
    }

    private static INPUT Make(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } },
    };
}
