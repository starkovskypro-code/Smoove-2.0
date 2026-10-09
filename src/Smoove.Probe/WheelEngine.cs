using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Smoove.Core;

namespace Smoove.Probe;

internal sealed class WheelEngine : IDisposable
{
    private sealed record Route(nint Foreground, nint Hit, long CheckedAt, bool Allowed);
    private sealed record Policy(bool Enabled, MotionProfile Profile, double Multiplier);
    private readonly record struct WheelEvent(int Delta, long Time, Native.Point Point, nint Foreground, nint Hit, int Generation);
    private readonly Channel<WheelEvent> _queue = Channel.CreateBounded<WheelEvent>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new();
    private readonly Thread _hookThread, _worker;
    private readonly Native.HookProc _callback;
    private readonly bool _integration;
    private Route _route = new(0, 0, 0, false);
    private Policy _policy = new(false, MotionProfile.Responsive, 1);
    private nint _hook;
    private uint _hookThreadId;
    private int _generation, _busy, _stopping, _pending;
    private long _heartbeat;
    private string? _failure;
    private long _accepted, _sent, _ownObserved, _passed, _cancellations, _inputSum, _outputSum;
    private long _maxHookTicks, _maxFirstTicks;
    private long _wheelObserved, _injectedPassed;
    private nuint _lastExtra;
    private long _anchor;
    private string _lastBypass = "Колесо ещё не поступало";
    private nint _externalTestWindow;
    private string[] _excluded = [];
    internal string LastBypass => Volatile.Read(ref _lastBypass);

    internal string? Failure => Volatile.Read(ref _failure);
    internal long Accepted => Interlocked.Read(ref _accepted);
    internal long Sent => Interlocked.Read(ref _sent);
    internal long OwnObserved => Interlocked.Read(ref _ownObserved);
    internal long InputSum => Interlocked.Read(ref _inputSum);
    internal long OutputSum => Interlocked.Read(ref _outputSum);
    internal bool IsBusy => Volatile.Read(ref _busy) != 0;
    internal void AllowExternalTestWindow(nint window) => Interlocked.Exchange(ref _externalTestWindow, window);
    internal void SetExclusions(string text)
    {
        Volatile.Write(ref _excluded, text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray());
        Cancel();
    }
    internal string Statistics => $"Принято: {Accepted}; отправлено: {Sent}; свои события: {OwnObserved}\n" +
        $"Сумма входа/выхода: {InputSum}/{OutputSum}; пропущено: {Interlocked.Read(ref _passed)}; отмены: {Interlocked.Read(ref _cancellations)}\n" +
        $"Макс. hook: {Interlocked.Read(ref _maxHookTicks) * 1000.0 / Stopwatch.Frequency:F3} мс; " +
        $"первая отправка: {Interlocked.Read(ref _maxFirstTicks) * 1000.0 / Stopwatch.Frequency:F2} мс\n" +
        $"Wheel hook: {Interlocked.Read(ref _wheelObserved)}; чужие injected: {Interlocked.Read(ref _injectedPassed)}; extra: {_lastExtra:X}";

    internal WheelEngine(bool integration)
    {
        _integration = integration;
        _callback = Hook;
        _worker = new Thread(Work) { IsBackground = true, Name = "Smoove motion" };
        _hookThread = new Thread(InstallHook) { IsBackground = true, Name = "Smoove hook" };
        _worker.Start();
        _hookThread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(3)))
        {
            Dispose();
            throw new TimeoutException("Hook thread did not start.");
        }
        if (Failure is not null)
        {
            string error = Failure;
            Dispose();
            throw new InvalidOperationException(error);
        }
    }

    internal void Configure(bool enabled, MotionProfile profile, double multiplier)
    {
        profile.Validate();
        if (!double.IsFinite(multiplier) || multiplier is < 0.1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        Volatile.Write(ref _policy, new(enabled && Failure is null, profile, multiplier));
        Cancel();
    }

    // Expensive process/token checks run on the diagnostic UI timer, never in the hook.
    internal string RefreshRoute(bool externalPrograms)
    {
        bool allowed = false;
        string reason = "Исходный ввод: контекст неизвестен";
        nint foreground = Native.GetForegroundWindow();
        Native.GetCursorPos(out var point);
        nint hit = Native.WindowFromPoint(point);
        nint root = Native.GetAncestor(hit, 2);
        try
        {
            Native.GetWindowThreadProcessId(root, out uint pid);
            Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            using var process = Process.GetProcessById(checked((int)pid));
            string name = process.ProcessName;
            bool candidate = pid == Environment.ProcessId || (externalPrograms &&
                (!_integration || root == Interlocked.CompareExchange(ref _externalTestWindow, 0, 0)) &&
                !Volatile.Read(ref _excluded).Contains(name, StringComparer.OrdinalIgnoreCase));
            bool sameWindow = root == foreground && pid == foregroundPid;
            bool routed = sameWindow || Native.RoutesToPointer();
            uint? integrity = Native.Integrity(pid);
            allowed = candidate && routed && integrity is <= 0x2000;
            reason = allowed ? $"Разрешён контекст: {name} (эксперимент)" :
                integrity is null or > 0x2000 ? "Исходный ввод: права не подтверждены / повышены" :
                !routed ? "Исходный ввод: системная маршрутизация требует активного окна" : $"Исходный ввод: {name} не разрешён";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            // Unknown process => leave physical input untouched.
        }
        var previous = Volatile.Read(ref _route);
        Volatile.Write(ref _route, new(foreground, hit, Stopwatch.GetTimestamp(), allowed));
        if (previous.Foreground != foreground || previous.Hit != hit || previous.Allowed != allowed) Cancel();
        return reason;
    }

    internal void Cancel()
    {
        Interlocked.Increment(ref _generation);
        _wake.Set();
    }

    private void InstallHook()
    {
        try
        {
            _hookThreadId = Native.GetCurrentThreadId();
            Native.PeekMessageW(out _, 0, 0, 0, 0); // Create queue before stop can post WM_QUIT.
            _hook = Native.SetWindowsHookExW(14, _callback, Native.GetModuleHandleW(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _started.Set();
            while (Volatile.Read(ref _stopping) == 0)
            {
                int result = Native.GetMessageW(out _, 0, 0, 0);
                if (result == 0) break;
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) { Fail($"Hook: {ex.Message}"); }
        finally
        {
            if (_hook != 0) Native.UnhookWindowsHookEx(_hook);
            _started.Set();
        }
    }

    private nint Hook(int code, nuint message, nint data)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (code < 0) return Native.CallNextHookEx(_hook, code, message, data);
            var mouse = Marshal.PtrToStructure<Native.MouseHook>(data);
            if ((int)message == Native.Wheel) { Interlocked.Increment(ref _wheelObserved); _lastExtra = mouse.Extra; }
            if (mouse.Extra == Native.OwnMarker)
            {
                if ((int)message == Native.Wheel) Interlocked.Increment(ref _ownObserved);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            bool injected = (mouse.Flags & Native.Injected) != 0;
            // The user's real wheel arrives as injected with Extra=0. This is also the
            // standard proxy/driver input path. Only marked foreign transformations bypass us.
            // Never create a privileged input-filter exception just for integration tests.
            if (!WheelSource.Transformable(injected, mouse.Extra, Native.OwnMarker))
            {
                if ((int)message == Native.Wheel)
                {
                    Interlocked.Increment(ref _injectedPassed);
                    Volatile.Write(ref _lastBypass, "Событие помечено Windows как injected: пропущено без изменения");
                }
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            if ((int)message != Native.Wheel)
            {
                if ((int)message is 0x0200 or 0x0201 or 0x0204 or 0x0207 or 0x020B or Native.HWheel)
                {
                    bool move = (int)message == 0x0200;
                    if ((Volatile.Read(ref _busy) != 0 || Volatile.Read(ref _pending) != 0) &&
                        (!move || !NearAnchor(mouse.Point) || Native.WindowFromPoint(mouse.Point) != Volatile.Read(ref _route).Hit)) Cancel();
                }
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            var policy = Volatile.Read(ref _policy);
            var route = Volatile.Read(ref _route);
            bool healthy = Volatile.Read(ref _busy) == 0 ||
                start - Interlocked.Read(ref _heartbeat) < Stopwatch.Frequency / 10;
            bool fresh = start - route.CheckedAt < Stopwatch.Frequency / 4;
            if (!policy.Enabled || Failure is not null || !route.Allowed || !healthy || !fresh ||
                Native.ModifiersOrButtons() || Native.GetForegroundWindow() != route.Foreground ||
                Native.WindowFromPoint(mouse.Point) != route.Hit)
            {
                Volatile.Write(ref _lastBypass, !policy.Enabled ? "Пауза" : Failure is not null ? "Ошибка движка" :
                    !route.Allowed ? "Приложение или маршрут не разрешены" : !healthy ? "Поток движения задержан" :
                    !fresh ? "Сведения о маршруте устарели" : Native.ModifiersOrButtons() ? "Нажата кнопка / модификатор" : "Окно под курсором изменилось");
                Cancel();
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            short delta = unchecked((short)(mouse.Data >> 16));
            if (delta == 0) return Native.CallNextHookEx(_hook, code, message, data);
            if (Math.Abs(delta * policy.Multiplier) > 32767)
            {
                Cancel();
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(new(delta, start, mouse.Point, route.Foreground, route.Hit, Volatile.Read(ref _generation))))
            {
                Interlocked.Decrement(ref _pending);
                Cancel();
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            Interlocked.Increment(ref _accepted);
            Interlocked.Add(ref _inputSum, delta);
            Interlocked.Exchange(ref _anchor, ((long)mouse.Point.X << 32) | unchecked((uint)mouse.Point.Y));
            Volatile.Write(ref _lastBypass, "Обработано сглаживанием");
            _wake.Set();
            return 1;
        }
        catch (Exception ex)
        {
            Fail($"Callback: {ex.Message}");
            return Native.CallNextHookEx(_hook, code, message, data);
        }
        finally { SetMax(ref _maxHookTicks, Stopwatch.GetTimestamp() - start); }
    }

    private void Work()
    {
        var motion = new ScrollMotion();
        var inputs = new Native.Input[1];
        WheelEvent context = default;
        long firstInput = 0;
        long previousTick = Stopwatch.GetTimestamp();
        int generation = 0;
        try
        {
            while (Volatile.Read(ref _stopping) == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (motion.Active && now - previousTick > Stopwatch.Frequency / 10) Cancel();
                previousTick = now;
                Interlocked.Exchange(ref _heartbeat, now);
                int current = Volatile.Read(ref _generation);
                if (current != generation)
                {
                    if (motion.Active) Interlocked.Increment(ref _cancellations);
                    motion.Cancel(now / (double)Stopwatch.Frequency);
                    firstInput = 0;
                    generation = current;
                }
                while (_queue.Reader.TryRead(out var next))
                {
                    Interlocked.Decrement(ref _pending);
                    if (next.Generation != generation) continue;
                    if (!motion.Active) firstInput = next.Time;
                    context = next;
                    var policy = Volatile.Read(ref _policy);
                    motion.Configure(policy.Profile);
                    // Advance in dequeue order, without moving time backwards after a tick.
                    motion.Add(next.Delta * policy.Multiplier, now / (double)Stopwatch.Frequency);
                }
                bool valid = Volatile.Read(ref _policy).Enabled && Failure is null && generation == Volatile.Read(ref _generation);
                if (motion.Active && (!valid || !ContextValid(context, now)))
                {
                    Cancel();
                    continue;
                }
                motion.Advance(now / (double)Stopwatch.Frequency);
                int delta = motion.TakeDelta();
                if (delta != 0)
                {
                    if (Math.Abs(delta) > 240 || !ContextValid(context, Stopwatch.GetTimestamp()) ||
                        generation != Volatile.Read(ref _generation))
                    {
                        Cancel();
                        continue;
                    }
                    inputs[0] = Native.WheelInput(delta, Native.OwnMarker);
                    if (Native.SendInput(1, inputs, Marshal.SizeOf<Native.Input>()) != 1)
                    {
                        Fail($"SendInput: {Marshal.GetLastWin32Error()} (причина UIPI может не сообщаться)");
                        continue;
                    }
                    Interlocked.Increment(ref _sent);
                    Interlocked.Add(ref _outputSum, delta);
                    if (firstInput != 0)
                    {
                        SetMax(ref _maxFirstTicks, Stopwatch.GetTimestamp() - firstInput);
                        firstInput = 0;
                    }
                }
                Volatile.Write(ref _busy, motion.Active ? 1 : 0);
                // No polling/timer when idle. Wait is interrupted by input, pause or disposal.
                _wake.WaitOne(motion.Active ? 8 : Timeout.Infinite);
            }
        }
        catch (Exception ex) { Fail($"Motion worker: {ex.Message}"); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private bool ContextValid(WheelEvent context, long now)
    {
        var route = Volatile.Read(ref _route);
        return now - context.Time < 4 * Stopwatch.Frequency && route.Allowed &&
            now - route.CheckedAt < Stopwatch.Frequency / 4 &&
            Native.GetForegroundWindow() == context.Foreground && Native.GetCursorPos(out var point) &&
            NearAnchor(point) &&
            Native.WindowFromPoint(point) == context.Hit && !Native.ModifiersOrButtons();
    }

    private bool NearAnchor(Native.Point point)
    {
        long anchor = Interlocked.Read(ref _anchor);
        long dx = (long)point.X - (int)(anchor >> 32), dy = (long)point.Y - unchecked((int)anchor);
        // Tolerate hand jitter, but cancel on a meaningful move or immediately on HWND change.
        return dx * dx + dy * dy <= 64;
    }

    private void Fail(string message)
    {
        Interlocked.CompareExchange(ref _failure, message, null);
        var policy = Volatile.Read(ref _policy);
        Volatile.Write(ref _policy, policy with { Enabled = false });
        Cancel();
    }

    private static void SetMax(ref long location, long value)
    {
        long previous;
        do { previous = Interlocked.Read(ref location); if (value <= previous) return; }
        while (Interlocked.CompareExchange(ref location, value, previous) != previous);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        Volatile.Write(ref _policy, Volatile.Read(ref _policy) with { Enabled = false });
        Cancel();
        if (_hookThreadId != 0) Native.PostThreadMessageW(_hookThreadId, Native.Quit, 0, 0);
        bool hookStopped = _hookThread.Join(TimeSpan.FromSeconds(2));
        bool workerStopped = _worker.Join(TimeSpan.FromSeconds(2));
        if (hookStopped && workerStopped) { _wake.Dispose(); _started.Dispose(); }
    }
}
