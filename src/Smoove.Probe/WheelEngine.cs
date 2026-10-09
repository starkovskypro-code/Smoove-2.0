using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Smoove.Core;

namespace Smoove.Probe;

internal sealed class WheelEngine : IDisposable
{
    private sealed record Route(nint Foreground, nint Hit, long CheckedAt, bool Allowed, ExplorerScrollTarget? Explorer=null,int Revision=0,nint Target=0);
    private sealed record Policy(bool Enabled, MotionProfile Profile, double Multiplier, double Acceleration = 0, int OutputHz = 240);
    private readonly record struct WheelEvent(int Delta, long Time, nint Foreground, nint Hit, int Generation, bool Precise, bool Horizontal);
    private sealed class AxisState
    {
        internal readonly ScrollMotion Motion = new();
        internal readonly TempoAcceleration Tempo = new();
        internal WheelEvent Context;
        internal long FirstInput;
        internal double LastTempoTime;
        internal ExplorerScrollTarget? Explorer;
    }
    private readonly Channel<WheelEvent> _queue = Channel.CreateBounded<WheelEvent>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _started = new();
    private readonly Thread _hookThread, _worker;
    private readonly Thread _routeThread;
    private readonly AutoResetEvent _routeWake=new(false);
    private int _externalPrograms=1;
    private string _routeReason="Запуск маршрута…";
    private int _routeRevision;
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
    private long _maxOutputDelta;
    private long _wheelObserved, _injectedPassed;
    private nuint _lastExtra;
    private string _lastBypass = "Колесо ещё не поступало";
    private nint _externalTestWindow;
    private string[] _excluded = [];
    private long _explorerMoves;
    private ApplicationExclusion[] _pathExclusions = [];
    internal void SetPathExclusions(ApplicationExclusion[] entries)
    {
        var snapshot=entries.Select(e => e.LegacyName ? e : e with { Path = ApplicationExclusion.Normalize(e.Path) }).ToArray();
        if(Volatile.Read(ref _pathExclusions).SequenceEqual(snapshot))return;
        Volatile.Write(ref _pathExclusions,snapshot);
        Interlocked.Increment(ref _routeRevision);
        Volatile.Write(ref _route,Volatile.Read(ref _route) with {Allowed=false});
        _routeWake.Set();
        Cancel();
    }
    internal string LastBypass => Volatile.Read(ref _lastBypass);

    internal string? Failure => Volatile.Read(ref _failure);
    internal long Accepted => Interlocked.Read(ref _accepted);
    internal long Sent => Interlocked.Read(ref _sent);
    internal long OwnObserved => Interlocked.Read(ref _ownObserved);
    internal long InputSum => Interlocked.Read(ref _inputSum);
    internal long OutputSum => Interlocked.Read(ref _outputSum);
    internal bool IsBusy => Volatile.Read(ref _busy) != 0;
    internal long Cancellations => Interlocked.Read(ref _cancellations);
    internal long MaxOutputDelta => Interlocked.Read(ref _maxOutputDelta);
    internal void AllowExternalTestWindow(nint window) => Interlocked.Exchange(ref _externalTestWindow, window);
    internal void SetExclusions(string text)
    {
        var snapshot=text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray();
        if(Volatile.Read(ref _excluded).SequenceEqual(snapshot,StringComparer.OrdinalIgnoreCase))return;
        Volatile.Write(ref _excluded,snapshot);
        Interlocked.Increment(ref _routeRevision);
        Volatile.Write(ref _route,Volatile.Read(ref _route) with {Allowed=false});
        _routeWake.Set();
        Cancel();
    }
    internal string Statistics => $"Принято: {Accepted}; отправлено: {Sent}; свои события: {OwnObserved}\n" +
        $"Сумма входа/выхода: {InputSum}/{OutputSum}; пропущено: {Interlocked.Read(ref _passed)}; отмены: {Interlocked.Read(ref _cancellations)}\n" +
        $"Макс. hook: {Interlocked.Read(ref _maxHookTicks) * 1000.0 / Stopwatch.Frequency:F3} мс; " +
        $"первая отправка: {Interlocked.Read(ref _maxFirstTicks) * 1000.0 / Stopwatch.Frequency:F2} мс\n" +
        $"Wheel hook: {Interlocked.Read(ref _wheelObserved)}; чужие injected: {Interlocked.Read(ref _injectedPassed)}; extra: {_lastExtra:X}; Explorer pixel moves: {Interlocked.Read(ref _explorerMoves)}";

    internal WheelEngine(bool integration)
    {
        _integration = integration;
        _callback = Hook;
        _worker = new Thread(Work) { IsBackground = true, Name = "Smoove motion" };
        _hookThread = new Thread(InstallHook) { IsBackground = true, Name = "Smoove hook" };
        _worker.Start();
        _routeThread=new Thread(()=>
        {
            try
            {
                while(Volatile.Read(ref _stopping)==0)
                {
                    UpdateRoute(Volatile.Read(ref _externalPrograms)!=0);
                    _routeWake.WaitOne(20);
                }
            }
            catch(Exception ex){Fail("Маршрут: "+ex.Message);}
        }){IsBackground=true,Name="Smoove route"};
        _routeThread.Start();
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

    internal void Configure(bool enabled, MotionProfile profile, double multiplier, double acceleration = 0, int outputHz = 240)
    {
        profile.Validate();
        if (!double.IsFinite(multiplier) || multiplier is < 0.1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        if (!double.IsFinite(acceleration) || acceleration is < 0 or > 1 || outputHz is < 60 or > 500)
            throw new ArgumentOutOfRangeException(nameof(acceleration));
        var next=new Policy(enabled && Failure is null, profile, multiplier, acceleration, outputHz);
        var old=Volatile.Read(ref _policy);
        if(old==next)return;
        Volatile.Write(ref _policy,next);
        // A tuning adjustment continues the trajectory. Only pausing discards the gesture.
        if(next.Enabled && old.Enabled){_wake.Set();return;}
        Cancel();
    }

    // UI reads state; process/token/UIA discovery runs on the independent route thread.
    internal string RefreshRoute(bool externalPrograms)
    {
        int next=externalPrograms?1:0;
        if(Interlocked.Exchange(ref _externalPrograms,next)!=next)
        {
            Interlocked.Increment(ref _routeRevision);
            Volatile.Write(ref _route,Volatile.Read(ref _route) with {Allowed=false});
            Cancel();
        }
        _routeWake.Set();
        return Volatile.Read(ref _routeReason);
    }
    private void UpdateRoute(bool externalPrograms)
    {
        int revision=Volatile.Read(ref _routeRevision);
        bool allowed = false;
        string reason = "Исходный ввод: контекст неизвестен";
        nint foreground = Native.GetForegroundWindow();
        Native.GetCursorPos(out var point);
        nint hit = Native.WindowFromPoint(point);
        nint root = Native.GetAncestor(hit, 2);
        var previous = Volatile.Read(ref _route);
        // Revalidate tokens periodically, not on every 20ms cursor/window probe.
        if(previous.Hit==hit && previous.Foreground==foreground && previous.Revision==revision &&
            previous.Allowed && Stopwatch.GetTimestamp()-previous.CheckedAt<Stopwatch.Frequency)return;
        ExplorerScrollTarget? explorer=null;
        try
        {
            Native.GetWindowThreadProcessId(root, out uint pid);
            Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            using var process = Process.GetProcessById(checked((int)pid));
            string name = process.ProcessName;
            string? executable = process.MainModule?.FileName;
            bool excludedByPath = executable is not null && Volatile.Read(ref _pathExclusions).Any(e => e.Matches(executable));
            bool candidate = !excludedByPath && !Volatile.Read(ref _excluded).Contains(name, StringComparer.OrdinalIgnoreCase) &&
                (pid == Environment.ProcessId || (externalPrograms &&
                (!_integration || root == Interlocked.CompareExchange(ref _externalTestWindow, 0, 0)) &&
                !Volatile.Read(ref _excluded).Contains("*")));
            bool sameWindow = root == foreground && pid == foregroundPid;
            bool routed = sameWindow || Native.RoutesToPointer();
            uint? integrity = Native.Integrity(pid);
            allowed = candidate && routed && integrity is <= 0x2000;
            if(allowed && name.Equals("explorer",StringComparison.OrdinalIgnoreCase))
                explorer=previous.Hit==hit && previous.Allowed && previous.Explorer is not null?previous.Explorer:ExplorerScrollTarget.TryCreate(hit);
            reason = allowed ? $"Разрешён контекст: {name} (эксперимент)" :
                integrity is null or > 0x2000 ? "Исходный ввод: права не подтверждены / повышены" :
                !routed ? "Исходный ввод: системная маршрутизация требует активного окна" : $"Исходный ввод: {name} не разрешён";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            // Unknown process => leave physical input untouched.
        }
        if(revision!=Volatile.Read(ref _routeRevision))return;
        Volatile.Write(ref _route, new(foreground, hit, Stopwatch.GetTimestamp(), allowed,explorer,revision,explorer is null ? root : explorer.Handle));
        Volatile.Write(ref _routeReason,reason);
        // Worker validates its captured window; refreshing the cache must not discard new input.
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
                if ((int)message is Native.Wheel or Native.HWheel) Interlocked.Increment(ref _ownObserved);
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
                        (!move || !TargetMatches(mouse.Point, Volatile.Read(ref _route)))) Cancel();
                }
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            var policy = Volatile.Read(ref _policy);
            var route = Volatile.Read(ref _route);
            bool healthy = Volatile.Read(ref _busy) == 0 ||
                start - Interlocked.Read(ref _heartbeat) < Stopwatch.Frequency / 10;
            if (!policy.Enabled || Failure is not null || !route.Allowed || route.Revision!=Volatile.Read(ref _routeRevision) || !healthy ||
                Native.ModifiersOrButtons(allowShift:true) || Native.GetForegroundWindow() != route.Foreground ||
                !TargetMatches(mouse.Point, route))
            {
                Volatile.Write(ref _lastBypass, !policy.Enabled ? "Пауза" : Failure is not null ? "Ошибка движка" :
                    !route.Allowed ? "Приложение или маршрут не разрешены" : !healthy ? "Поток движения задержан" :
                    Native.ModifiersOrButtons() ? "Нажата кнопка / модификатор" : "Окно под курсором изменилось");
                _routeWake.Set();
                Cancel();
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            short delta = unchecked((short)(mouse.Data >> 16));
            if (delta == 0) return Native.CallNextHookEx(_hook, code, message, data);
            bool horizontal=(Native.GetAsyncKeyState(0x10)&0x8000)!=0;
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(new(delta, start, route.Foreground, route.Target, Volatile.Read(ref _generation), route.Explorer is not null, horizontal)))
            {
                Interlocked.Decrement(ref _pending);
                Cancel();
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            Interlocked.Increment(ref _accepted);
            Interlocked.Add(ref _inputSum, delta);
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
        AxisState[] axes = [new(), new()];
        // Split only for the signed 16-bit wheel-message representation, never as a speed cap.
        var inputs = new Native.Input[66];
        long previousTick = Stopwatch.GetTimestamp();
        long nextOutput = 0;
        int generation = 0;
        try
        {
            using var pacing = new MotionWait(_wake);
            while (Volatile.Read(ref _stopping) == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (axes.Any(axis => axis.Motion.Active) && now - previousTick > Stopwatch.Frequency / 2) Cancel();
                previousTick = now;
                Interlocked.Exchange(ref _heartbeat, now);
                int current = Volatile.Read(ref _generation);
                if (current != generation)
                {
                    if (axes.Any(axis => axis.Motion.Active)) Interlocked.Increment(ref _cancellations);
                    foreach(var axis in axes)
                    {
                        axis.Motion.Cancel(now / (double)Stopwatch.Frequency);
                        axis.Tempo.Reset(now / (double)Stopwatch.Frequency);
                        axis.LastTempoTime = now / (double)Stopwatch.Frequency;
                        axis.Explorer = null;
                        axis.FirstInput = 0;
                    }
                    nextOutput = now;
                    generation = current;
                }
                while (_queue.Reader.TryRead(out var next))
                {
                    Interlocked.Decrement(ref _pending);
                    if (next.Generation != generation) continue;
                    var axis = axes[next.Horizontal ? 1 : 0];
                    if (!axis.Motion.Active)
                    {
                        axis.FirstInput = next.Time;
                        axis.Explorer=Volatile.Read(ref _route).Explorer?.ForAxis();
                        if(axis.Explorer is not null)
                        {
                            try{axis.Explorer.BeginGesture(next.Horizontal);}
                            catch(Exception ex)when(ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException)
                            {Fail("Проводник: область прокрутки недоступна; включите исходное колесо");continue;}
                        }
                    }
                    axis.Context = next;
                    var policy = Volatile.Read(ref _policy);
                    axis.Motion.Configure(policy.Profile);
                    // Advance in dequeue order, without moving time backwards after a tick.
                    double inputTime = Math.Max(axis.LastTempoTime, next.Time / (double)Stopwatch.Frequency);
                    double scaled = axis.Tempo.Scale(next.Delta, inputTime, policy.Acceleration);
                    axis.LastTempoTime = inputTime;
                    axis.Motion.Add(scaled * policy.Multiplier, Math.Max(next.Time/(double)Stopwatch.Frequency,axis.Motion.Time));
                }
                // Input can arrive while draining the queue, after this tick's initial timestamp.
                now = Stopwatch.GetTimestamp();
                bool valid = Volatile.Read(ref _policy).Enabled && Failure is null && generation == Volatile.Read(ref _generation);
                bool outputDue = now >= nextOutput;
                foreach(var axis in axes)
                {
                    if (axis.Motion.Active && (!valid || !ContextValid(axis.Context, now)))
                    {
                        Cancel();
                        continue;
                    }
                    if (!axis.Motion.Active) continue;
                    axis.Motion.Advance(now / (double)Stopwatch.Frequency);
                    int delta = 0;
                    if (outputDue || !axis.Motion.Active) delta = axis.Motion.TakeDelta();
                    if (delta != 0)
                    {
                        if (!ContextValid(axis.Context, Stopwatch.GetTimestamp()) ||
                            generation != Volatile.Read(ref _generation))
                        {
                            Cancel();
                            continue;
                        }
                        if(axis.Explorer is not null)
                        {
                            try{axis.Explorer.Move(delta);Interlocked.Increment(ref _explorerMoves);}
                            catch(Exception ex)when(ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException)
                            {Fail("Проводник: ошибка прокрутки; исходный ввод восстановлен");continue;}
                        }
                        else
                        {
                        int remaining = delta;
                        int count = 0;
                        int required = (int)((Math.Abs((long)delta) + 32766) / 32767);
                        if (required > inputs.Length) Array.Resize(ref inputs, required);
                        // Apps such as Figma swap wheel axes while Shift is held.
                        bool horizontalMessage=axis.Context.Horizontal && (Native.GetAsyncKeyState(0x10)&0x8000)==0;
                        while (remaining != 0 && count < inputs.Length)
                        {
                            int part = Math.Clamp(remaining, -32767, 32767);
                            if(axis.Context.Horizontal && !horizontalMessage)
                            {
                                Native.GetCursorPos(out var cursor);
                                // Capture Shift in the message: releasing the physical key must not change queued input's axis.
                                nuint wheelParam=unchecked((nuint)(((uint)(ushort)part<<16)|4u));
                                nint position=unchecked((nint)(((uint)(ushort)cursor.Y<<16)|(ushort)cursor.X));
                                if(!Native.PostMessageW(Native.WindowFromPoint(cursor),Native.Wheel,wheelParam,position))
                                {Fail($"Shift wheel: {Marshal.GetLastWin32Error()}");break;}
                                Interlocked.Increment(ref _sent);
                            }
                            else inputs[count++] = Native.WheelInput(horizontalMessage ? -part : part, Native.OwnMarker, horizontalMessage);
                            remaining -= part;
                        }
                        uint inserted = count==0?0:Native.SendInput((uint)count, inputs, Marshal.SizeOf<Native.Input>());
                        if (inserted != count)
                        {
                            Fail($"SendInput: {Marshal.GetLastWin32Error()} (причина UIPI может не сообщаться)");
                            continue;
                        }
                        Interlocked.Add(ref _sent, count);
                        }
                        SetMax(ref _maxOutputDelta, Math.Abs((long)delta));
                        Interlocked.Add(ref _outputSum, delta);
                        if (axis.FirstInput != 0)
                        {
                            SetMax(ref _maxFirstTicks, Stopwatch.GetTimestamp() - axis.FirstInput);
                            axis.FirstInput = 0;
                        }
                    }
                }
                if(outputDue) nextOutput = now + Stopwatch.Frequency / Volatile.Read(ref _policy).OutputHz;
                bool active = axes.Any(axis => axis.Motion.Active);
                Volatile.Write(ref _busy, active ? 1 : 0);
                // No polling/timer when idle. Wait is interrupted by input, pause or disposal.
                if (active) pacing.Wait((nextOutput - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                else _wake.WaitOne();
            }
        }
        catch (Exception ex) { Fail($"Motion worker: {ex.Message}"); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private bool ContextValid(WheelEvent context, long now)
    {
        var route = Volatile.Read(ref _route);
        return now - context.Time < 10 * Stopwatch.Frequency && route.Allowed && route.Revision==Volatile.Read(ref _routeRevision) &&
            route.Target==context.Hit && (route.Explorer is not null)==context.Precise && route.Foreground==context.Foreground &&
            Native.GetForegroundWindow() == context.Foreground && Native.GetCursorPos(out var point) &&
            TargetMatches(point,route) && !Native.ModifiersOrButtons(allowShift:true);
    }

    private static bool TargetMatches(Native.Point point, Route route)
    {
        nint hit = Native.WindowFromPoint(point);
        // Browser render/video child windows can change without leaving the page.
        // Explorer's pixel adapter must remain bound to its exact file view.
        return route.Target != 0 && (route.Explorer is null ? Native.GetAncestor(hit, 2) : ExplorerScrollTarget.FileViewHandle(hit)) == route.Target;
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
        _routeWake.Set();
        bool routeStopped=_routeThread.Join(TimeSpan.FromSeconds(2));
        if (hookStopped && workerStopped && routeStopped) { _routeWake.Dispose();_wake.Dispose(); _started.Dispose(); }
    }
}
