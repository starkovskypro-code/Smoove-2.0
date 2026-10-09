using System.Runtime.InteropServices;
using System.Text;

namespace Smoove.Probe;

internal static class Native
{
    internal const int Wheel = 0x020A, HWheel = 0x020E, Quit = 0x0012;
    internal const uint Injected = 1;
    // Keep markers within 32 bits: some Windows input paths truncate extra info.
    internal static readonly nuint OwnMarker = 0x534D5632;
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 1, 2, 4, 5, 6];
    internal delegate nint HookProc(int code, nuint message, nint data);
    internal delegate bool WindowCallback(nint window, nint param);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHook { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    // INPUT union has size 32 on x64 (MOUSEINPUT is its largest member), offset 8.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window; public uint Id; public nuint WParam; public nint LParam;
        public uint Time; public Point Point; public uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAttributes { public nint Sid; public uint Attributes; }

    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookExW(int id, HookProc proc, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern int GetMessageW(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessageW(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(WindowCallback callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfoW(uint action, uint param, out uint result, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int kind, nint data, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);

    internal static bool ModifiersOrButtons()
    {
        foreach (int key in ModifierKeys)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) return true;
        return false;
    }

    internal static bool RoutesToPointer() => SystemParametersInfoW(0x201C, 0, out uint routing, 0) && routing == 2;
    internal static string WindowTitle(nint window)
    {
        var text = new StringBuilder(512);
        GetWindowTextW(window, text, text.Capacity);
        return text.ToString();
    }

    internal static nint FindWindowWithTitle(string prefix)
    {
        nint result = 0;
        EnumWindows((window, _) => { if (!WindowTitle(window).StartsWith(prefix, StringComparison.Ordinal)) return true; result = window; return false; }, 0);
        return result;
    }

    internal static uint? Integrity(uint pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        nint token = 0, buffer = 0;
        try
        {
            if (!OpenProcessToken(process, 8, out token)) return null;
            GetTokenInformation(token, 25, 0, 0, out int needed);
            if (needed is < 1 or > 4096) return null;
            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, 25, buffer, needed, out _)) return null;
            var label = Marshal.PtrToStructure<SidAttributes>(buffer);
            byte count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Sid));
            return count == 0 ? null : unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(label.Sid, (uint)count - 1)));
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            if (token != 0) CloseHandle(token);
            CloseHandle(process);
        }
    }

    internal static Input WheelInput(int delta, nuint marker) => new()
    {
        Type = 0, Mouse = new() { Data = unchecked((uint)delta), Flags = 0x0800, Extra = marker }
    };
}
