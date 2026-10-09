using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Smoove.Probe;

// Windows 11 high-resolution, one-shot timer. No global timer resolution or busy spin.
internal sealed class MotionWait : IDisposable
{
    private readonly EventWaitHandle _timer = new(false, EventResetMode.AutoReset);
    private readonly WaitHandle[] _handles;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long due, int period, nint callback, nint argument, nint resumeContext, uint tolerance);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CancelWaitableTimer(SafeWaitHandle timer);

    internal MotionWait(WaitHandle signal)
    {
        nint handle = CreateWaitableTimerExW(0, null, 2, 0x1F0003);
        if (handle == 0) { _timer.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        _timer.SafeWaitHandle = new SafeWaitHandle(handle, true);
        _handles = [signal, _timer];
    }

    internal void Wait(double seconds)
    {
        if (seconds <= 0) return;
        long due = -(long)Math.Max(1, Math.Ceiling(seconds * 10000000));
        if (!SetWaitableTimerEx(_timer.SafeWaitHandle, ref due, 0, 0, 0, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        WaitHandle.WaitAny(_handles);
        CancelWaitableTimer(_timer.SafeWaitHandle);
    }
    public void Dispose() => _timer.Dispose();
}
