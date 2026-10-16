using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Smoove.Core;

namespace Smoove.Probe;

internal sealed class WheelEngine : IDisposable
{
    private sealed record Route(nint Foreground, nint Hit, long CheckedAt, bool Allowed, PixelScrollTarget? Pixel=null,int Revision=0,nint Target=0,
        string? Executable = null, Native.Rect? SafeArea = null, bool ReaperTracks=false, bool MidiEditor=false);
    private sealed record Policy(bool Enabled, MotionProfile Profile, double Multiplier, double Acceleration = 0, int OutputHz = 240, double AnimationTempo = 1, long ChangedAt = 0,
        MotionEngineKind Engine = MotionEngineKind.Smoove, SmoothieProfile? Experimental = null, int EngineRevision = 0);
    private readonly record struct WheelEvent(int Delta, long Time, nint Foreground, nint Hit, int Generation, bool Precise, bool Horizontal, int EngineRevision,
        MotionEngineKind Engine, int RouteRevision, bool NativeHorizontal, bool ReaperTracks, bool MidiEditor);
    private sealed class AxisState
    {
        internal IScrollMotion Motion = new ScrollMotion();
        internal readonly TempoAcceleration Tempo = new();
        internal readonly ReaperWheelOutput ReaperOutput = new();
        internal WheelEvent Context;
        internal long FirstInput;
        internal double LastTempoTime;
        internal PixelScrollTarget? Pixel;
        internal Policy? MotionPolicy;
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
    private string _lastWorkerFilter="none";
    private long _wheelObserved, _injectedPassed;
    private nuint _lastExtra;
    private string _lastBypass = "Колесо ещё не поступало";
    private nint _externalTestWindow;
    private string[] _excluded = [];
    private long _pixelMoves;
    private ApplicationExclusion[] _pathExclusions = [];
    private sealed record Applications(ApplicationProfile[] Profiles, bool OnlySelected, long ChangedAt);
    private Applications _applications = new([], false, 0);
    private readonly TerminalHover _terminalHover = new();
    private int _monitorHz = 60;
    private long _monitorCheckedAt;
    internal void SetApplicationProfiles(ApplicationProfile[] profiles, bool onlySelected)
    {
        var snapshot = profiles.Select(p => p.Validated()).ToArray();
        var old = Volatile.Read(ref _applications);
        if (old.OnlySelected == onlySelected && old.Profiles.SequenceEqual(snapshot)) return;
        Volatile.Write(ref _applications, new(snapshot, onlySelected, Stopwatch.GetTimestamp()));
        var oldRoutes = old.Profiles.Select(p => (p.Path, p.Mode, p.Enabled, p.LegacyName, Engine: p.Mode == ApplicationScrollMode.Custom ? p.Settings?.Engine : null));
        var nextRoutes = snapshot.Select(p => (p.Path, p.Mode, p.Enabled, p.LegacyName, Engine: p.Mode == ApplicationScrollMode.Custom ? p.Settings?.Engine : null));
        if (old.OnlySelected == onlySelected && oldRoutes.SequenceEqual(nextRoutes))
        {
            // Tuning belongs to the motion state; preserve the trajectory when routing stays the same.
            _wake.Set(); return;
        }
        Interlocked.Increment(ref _routeRevision);
        Volatile.Write(ref _route, Volatile.Read(ref _route) with { Allowed = false });
        Cancel(); _routeWake.Set();
    }
    private Policy CurrentPolicy(Route? route = null)
    {
        route ??= Volatile.Read(ref _route);
        var policy = Volatile.Read(ref _policy);
        var applications = Volatile.Read(ref _applications);
        string? executable = route.Executable;
        var app = executable is null ? null : ApplicationProfile.Find(applications.Profiles, executable);
        var settings = app?.Mode == ApplicationScrollMode.Custom ? app.Settings : null;
        var effective=settings is null ? policy : policy with
        {
            Profile = settings.MotionProfile, Multiplier = settings.Distance, Acceleration = settings.Acceleration / 100,
            AnimationTempo = settings.AnimationTempo, Engine = settings.Engine, Experimental = settings.Experimental ?? new(),
            // Custom engine changes are independent of edits to the global engine selector.
            EngineRevision = -1 - route.Revision,
            ChangedAt = Math.Max(policy.ChangedAt, applications.ChangedAt)
        };
        // MIDI note rows are already discrete; a long track-style coast makes one notch feel over-sensitive.
        return route.MidiEditor ? effective with { Profile=effective.Profile with {CoastSeconds=Math.Min(effective.Profile.CoastSeconds,MotionProfile.Responsive.CoastSeconds)} } : effective;
    }
    internal int EffectiveOutputHz => ScrollRouting.OutputFrequency(Volatile.Read(ref _policy).OutputHz, Volatile.Read(ref _monitorHz));
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
    internal string LastWorkerFilter => Volatile.Read(ref _lastWorkerFilter);
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
        $"Wheel hook: {Interlocked.Read(ref _wheelObserved)}; чужие injected: {Interlocked.Read(ref _injectedPassed)}; extra: {_lastExtra:X}; Pixel moves: {Interlocked.Read(ref _pixelMoves)}\n" +
        $"Выдача: {EffectiveOutputHz} Гц; монитор: {Volatile.Read(ref _monitorHz)} Гц; режим: {(Volatile.Read(ref _policy).OutputHz == 0 ? "Авто" : "фиксированный")}";

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

    internal void Configure(bool enabled, MotionProfile profile, double multiplier, double acceleration = 0, int outputHz = 240, double animationTempo = 1,
        MotionEngineKind engine = MotionEngineKind.Smoove, SmoothieProfile? experimental = null)
    {
        profile.Validate();
        if (!double.IsFinite(multiplier) || multiplier is < 0.1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        if (!double.IsFinite(acceleration) || acceleration is < 0 or > 1 || (outputHz != 0 && outputHz is < 60 or > 500))
            throw new ArgumentOutOfRangeException(nameof(acceleration));
        if (!double.IsFinite(animationTempo) || animationTempo is < 0.5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(animationTempo));
        if (!Enum.IsDefined(engine)) throw new ArgumentOutOfRangeException(nameof(engine));
        experimental ??= new();
        experimental.Validate();
        var old=Volatile.Read(ref _policy);
        var next=new Policy(enabled && Failure is null, profile, multiplier, acceleration, outputHz, animationTempo,
            Engine:engine, Experimental:experimental, EngineRevision:old.EngineRevision + (old.Engine == engine ? 0 : 1));
        if(old with { ChangedAt = 0 } == next)return;
        next = next with { ChangedAt = Stopwatch.GetTimestamp() };
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
        long routeNow = Stopwatch.GetTimestamp();
        if (routeNow - _monitorCheckedAt > 2 * Stopwatch.Frequency)
        {
            Volatile.Write(ref _monitorHz, Native.MonitorFrequency(point));
            _monitorCheckedAt = routeNow;
        }
        nint hit = Native.WindowFromPoint(point);
        nint root = Native.GetAncestor(hit, 2);
        var previous = Volatile.Read(ref _route);
        // Revalidate tokens periodically, not on every 20ms cursor/window probe.
        if(previous.Hit==hit && previous.Foreground==foreground && previous.Revision==revision &&
            previous.Allowed && previous.SafeArea is null && Stopwatch.GetTimestamp()-previous.CheckedAt<Stopwatch.Frequency)return;
        PixelScrollTarget? pixel=null;
        ScrollSettings? settings = null;
        string? executable = null;
        Native.Rect? safeArea = null;
        bool reaperTracks=false,midiEditor=false;
        try
        {
            Native.GetWindowThreadProcessId(root, out uint pid);
            Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
            using var process = Process.GetProcessById(checked((int)pid));
            string name = process.ProcessName;
            if(name.Equals("Reaper",StringComparison.OrdinalIgnoreCase))
            {
                for(nint child=hit;child!=0 && child!=root;child=Native.GetParent(child))
                {
                    string className=Native.ClassName(child);
                    if(className is "REAPERTrackListWindow" or "REAPERTCPDisplay" or "MIDIWindow" or "MIDIPianoWindow" or "MIDIScoreWindow")reaperTracks=true;
                    if(className is "MIDIWindow" or "MIDIPianoWindow" or "MIDIScoreWindow")midiEditor=true;
                    if(reaperTracks && midiEditor)break;
                }
            }
            executable = process.MainModule?.FileName;
            var applications = Volatile.Read(ref _applications);
            var appProfile = executable is null ? null : ApplicationProfile.Find(applications.Profiles, executable);
            if (appProfile?.Mode == ApplicationScrollMode.Custom) settings = appProfile.Settings;
            bool excludedByPath = executable is not null && Volatile.Read(ref _pathExclusions).Any(e => e.Matches(executable));
            bool candidate = ApplicationProfile.Allows(appProfile, applications.OnlySelected) && !excludedByPath && !Volatile.Read(ref _excluded).Contains(name, StringComparer.OrdinalIgnoreCase) &&
                (pid == Environment.ProcessId || (externalPrograms &&
                (!_integration || root == Interlocked.CompareExchange(ref _externalTestWindow, 0, 0)) &&
                !Volatile.Read(ref _excluded).Contains("*")));
            bool sameWindow = pid == foregroundPid && (root == foreground || Native.GetAncestor(foreground, 2) == root);
            bool routed = sameWindow || Native.RoutesToPointer();
            uint? integrity = Native.Integrity(pid);
            allowed = candidate && routed && integrity is <= 0x2000;
            bool terminal = false;
            if (allowed)
            {
                (terminal, safeArea) = _terminalHover.Check(name, root, point);
                allowed = !terminal;
            }
            if(allowed)
                pixel=previous.Hit==hit && previous.Allowed && previous.Pixel is not null?previous.Pixel:PixelScrollTarget.TryCreate(hit);
            reason = terminal ? $"Исходный ввод: терминал / неподтверждённая область IDE ({name})" :
                allowed ? $"Разрешён контекст: {name} ({(settings is null ? "общие настройки" : "свой профиль")})" :
                integrity is null or > 0x2000 ? "Исходный ввод: права не подтверждены / повышены" :
                !routed ? "Исходный ввод: системная маршрутизация требует активного окна" : $"Исходный ввод: {name} не разрешён";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or OverflowException)
        {
            // Unknown process => leave physical input untouched.
        }
        if(revision!=Volatile.Read(ref _routeRevision))return;
        Volatile.Write(ref _route, new(foreground, hit, Stopwatch.GetTimestamp(), allowed,pixel,revision,pixel is null ? root : pixel.Handle, executable, safeArea,reaperTracks,midiEditor));
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
            if ((int)message is Native.Wheel or Native.HWheel) { Interlocked.Increment(ref _wheelObserved); _lastExtra = mouse.Extra; }
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
            if ((int)message is not (Native.Wheel or Native.HWheel))
            {
                if ((int)message is 0x0200 or 0x0201 or 0x0204 or 0x0207 or 0x020B or Native.HWheel)
                {
                    bool move = (int)message == 0x0200;
                    if ((Volatile.Read(ref _busy) != 0 || Volatile.Read(ref _pending) != 0) &&
                        (!move || !TargetMatches(mouse.Point, Volatile.Read(ref _route)))) Cancel();
                }
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            var route = Volatile.Read(ref _route);
            var policy = CurrentPolicy(route);
            bool healthy = Volatile.Read(ref _busy) == 0 ||
                start - Interlocked.Read(ref _heartbeat) < Stopwatch.Frequency / 10;
            if (!policy.Enabled || Failure is not null || !route.Allowed || start - route.CheckedAt > Stopwatch.Frequency * 3 / 2 || route.Revision!=Volatile.Read(ref _routeRevision) || !healthy ||
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
            bool nativeHorizontal = (int)message == Native.HWheel;
            bool horizontal = nativeHorizontal || (Native.GetAsyncKeyState(0x10)&0x8000)!=0;
            int motionDelta = ScrollRouting.MotionDelta(delta, nativeHorizontal);
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(new(motionDelta, start, route.Foreground, route.Target, Volatile.Read(ref _generation), route.Pixel is not null, horizontal, policy.EngineRevision,
                policy.Engine, route.Revision, nativeHorizontal,route.ReaperTracks,route.MidiEditor)))
            {
                Interlocked.Decrement(ref _pending);
                Cancel();
                Interlocked.Increment(ref _passed);
                return Native.CallNextHookEx(_hook, code, message, data);
            }
            Interlocked.Increment(ref _accepted);
            Interlocked.Add(ref _inputSum, motionDelta);
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
        var experimentalRate = new SmoothieInputRate();
        double lastExperimentalInput = 0;
        int engineRevision = -1;
        MotionEngineKind selectedEngine = MotionEngineKind.Smoove;
        void SelectEngine(Policy policy, double seconds)
        {
            if (engineRevision == policy.EngineRevision && selectedEngine == policy.Engine) return;
            // Switching stops the old tail; queued input belongs only to the engine that captured it.
            if (axes.Any(axis => axis.Motion.Active)) Interlocked.Increment(ref _cancellations);
            experimentalRate.Reset();
            lastExperimentalInput = seconds;
            foreach (var axis in axes)
            {
                axis.Motion = policy.Engine == MotionEngineKind.Smoove ? new ScrollMotion() : new SmoothieMotion(experimentalRate);
                axis.Motion.Cancel(seconds);
                axis.ReaperOutput.Reset();
                axis.MotionPolicy = null;
                axis.Tempo.Reset(seconds);
                axis.LastTempoTime = seconds;
                axis.Pixel = null;
                axis.FirstInput = 0;
            }
            engineRevision = policy.EngineRevision;
            selectedEngine = policy.Engine;
        }
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
                SelectEngine(CurrentPolicy(), now / (double)Stopwatch.Frequency);
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
                        axis.ReaperOutput.Reset();
                        axis.Tempo.Reset(now / (double)Stopwatch.Frequency);
                        axis.LastTempoTime = now / (double)Stopwatch.Frequency;
                        axis.Pixel = null;
                        axis.FirstInput = 0;
                    }
                    experimentalRate.Reset();
                    lastExperimentalInput = now / (double)Stopwatch.Frequency;
                    nextOutput = now;
                    generation = current;
                }
                while (_queue.Reader.TryRead(out var next))
                {
                    Interlocked.Decrement(ref _pending);
                    var policy = CurrentPolicy();
                    SelectEngine(policy, Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                    if (next.Generation != generation){Volatile.Write(ref _lastWorkerFilter,"generation");continue;}
                    if (next.EngineRevision != engineRevision){Volatile.Write(ref _lastWorkerFilter,$"engine revision {next.EngineRevision}/{engineRevision}");continue;}
                    if (next.Engine != selectedEngine || next.RouteRevision != Volatile.Read(ref _routeRevision)){Volatile.Write(ref _lastWorkerFilter,$"policy/route {next.Engine}/{selectedEngine}, {next.RouteRevision}/{Volatile.Read(ref _routeRevision)}");continue;}
                    if (!ContextValid(next, Stopwatch.GetTimestamp())){Volatile.Write(ref _lastWorkerFilter,"context");continue;}
                    Volatile.Write(ref _lastWorkerFilter,"accepted by worker");
                    if (axes.Any(a => a.Motion.Active && (a.Context.Hit != next.Hit || a.Context.Foreground != next.Foreground || a.Context.Precise != next.Precise || a.Context.ReaperTracks!=next.ReaperTracks || a.Context.MidiEditor!=next.MidiEditor)))
                    {
                        // New recipient: discard the old window's tail, not the new window's accepted notch.
                        Interlocked.Increment(ref _cancellations);
                        foreach (var previous in axes)
                        {
                            double time = Math.Max(previous.Motion.Time, next.Time / (double)Stopwatch.Frequency);
                            previous.Motion.Cancel(time); previous.Tempo.Reset(time); previous.LastTempoTime = time;
                            previous.ReaperOutput.Reset();
                            previous.Pixel = null; previous.FirstInput = 0;
                        }
                        experimentalRate.Reset();
                    }
                    var axis = axes[next.Horizontal ? 1 : 0];
                    if (!axis.Motion.Active)
                    {
                        axis.FirstInput = next.Time;
                        axis.Pixel=Volatile.Read(ref _route).Pixel?.ForAxis();
                        if(axis.Pixel is not null)
                        {
                            try{axis.Pixel.BeginGesture(next.Horizontal);}
                            catch(Exception ex)when(ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException)
                            {Fail("Область прокрутки недоступна; включите исходное колесо");continue;}
                        }
                    }
                    if(axis.Context.Hit!=next.Hit || axis.Context.Foreground!=next.Foreground || axis.Context.ReaperTracks!=next.ReaperTracks || axis.Context.MidiEditor!=next.MidiEditor)
                        axis.ReaperOutput.Reset();
                    axis.Context = next;
                    axis.ReaperOutput.AddInput();
                    ApplyMotionPolicy(axis, policy, Math.Max(next.Time/(double)Stopwatch.Frequency,axis.Motion.Time));
                    // Advance in dequeue order, without moving time backwards after a tick.
                    double inputTime = Math.Max(axis.LastTempoTime, next.Time / (double)Stopwatch.Frequency);
                    if (policy.Engine == MotionEngineKind.SmoothieExperimental)
                    {
                        inputTime = Math.Max(inputTime, Math.Max(axis.Motion.Time, lastExperimentalInput));
                        lastExperimentalInput = inputTime;
                    }
                    double distance=ScrollRouting.MotionScale(policy.Multiplier,next.MidiEditor);
                    double scaled = policy.Engine == MotionEngineKind.Smoove
                        ? axis.Tempo.Scale(next.Delta, inputTime, policy.Acceleration) * distance : next.Delta * distance;
                    axis.LastTempoTime = inputTime;
                    axis.Motion.Add(scaled, Math.Max(inputTime,axis.Motion.Time));
                }
                // Input can arrive while draining the queue, after this tick's initial timestamp.
                now = Stopwatch.GetTimestamp();
                bool valid = Volatile.Read(ref _policy).Enabled && Failure is null && generation == Volatile.Read(ref _generation)
                    && engineRevision == CurrentPolicy().EngineRevision && selectedEngine == CurrentPolicy().Engine;
                bool outputDue = now >= nextOutput;
                foreach(var axis in axes)
                {
                    var policy = CurrentPolicy();
                    if (policy.EngineRevision != engineRevision || policy.Engine != selectedEngine) break;
                    if ((axis.Motion.Active || axis.ReaperOutput.Active) && (!valid || !ContextValid(axis.Context, now)))
                    {
                        if (engineRevision != CurrentPolicy().EngineRevision) break;
                        Cancel();
                        continue;
                    }
                    if (!axis.Motion.Active && !axis.ReaperOutput.Active) continue;
                    ApplyMotionPolicy(axis, policy, now / (double)Stopwatch.Frequency);
                    if (policy.Engine == MotionEngineKind.SmoothieExperimental && !outputDue) continue;
                    axis.Motion.Advance(now / (double)Stopwatch.Frequency);
                    int delta = 0;
                    if (outputDue || !axis.Motion.Active) delta = axis.Motion.TakeDelta();
                    if(axis.Context.ReaperTracks)
                    {
                        double tail=axis.Context.Horizontal ? axis.Motion switch
                        {
                            ScrollMotion motion=>motion.Target-motion.Position,
                            SmoothieMotion motion=>motion.Remaining,
                            _=>double.NaN
                        } : double.NaN;
                        // REAPER treats many tiny MIDI wheel messages as separate steps.
                        int quantum=axis.Context.MidiEditor && !axis.Context.Horizontal ? 60 : axis.Context.Horizontal ? 4 : 12;
                        delta=axis.ReaperOutput.Take(delta,quantum,tail,now/(double)Stopwatch.Frequency);
                    }
                    if (delta != 0)
                    {
                        if (engineRevision != CurrentPolicy().EngineRevision) break;
                        if (!ContextValid(axis.Context, Stopwatch.GetTimestamp()) ||
                            generation != Volatile.Read(ref _generation))
                        {
                            if (engineRevision != CurrentPolicy().EngineRevision) break;
                            Cancel();
                            continue;
                        }
                        if(axis.Pixel is not null)
                        {
                            try{axis.Pixel.Move(delta);Interlocked.Increment(ref _pixelMoves);}
                            catch(Exception ex)when(ex is System.Windows.Automation.ElementNotAvailableException or InvalidOperationException or COMException)
                            {Fail("Ошибка области прокрутки; исходный ввод восстановлен");continue;}
                        }
                        else
                        {
                        int remaining = delta;
                        int count = 0;
                        bool nativeShiftTail=axis.Context.ReaperTracks && axis.Context.Horizontal && !axis.Context.NativeHorizontal &&
                            (Native.GetAsyncKeyState(0x10)&0x8000)==0;
                        int required = (int)((Math.Abs((long)delta) + 32766) / 32767)*(nativeShiftTail ? 3 : 1);
                        if (required > inputs.Length) Array.Resize(ref inputs, required);
                        // Apps such as Figma swap wheel axes while Shift is held.
                        bool horizontalMessage=axis.Context.Horizontal && (axis.Context.NativeHorizontal || (Native.GetAsyncKeyState(0x10)&0x8000)==0);
                        while (remaining != 0 && count < inputs.Length)
                        {
                            int limit=axis.Context.ReaperTracks ? 32760 : 32767;
                            int part = Math.Clamp(remaining, -limit, limit);
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
                            else if(nativeShiftTail)
                            {
                                // REAPER dispatches Shift+wheel from key state, not the message modifier flag.
                                inputs[count++]=Native.ShiftInput(true);
                                inputs[count++]=Native.WheelInput(part,Native.OwnMarker);
                                inputs[count++]=Native.ShiftInput(false);
                                Interlocked.Add(ref _sent,3);
                            }
                            else inputs[count++] = Native.WheelInput(ScrollRouting.OutputDelta(part,horizontalMessage,axis.Context.NativeHorizontal,axis.Context.ReaperTracks), Native.OwnMarker, horizontalMessage);
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
                if(outputDue) nextOutput = now + Stopwatch.Frequency / EffectiveOutputHz;
                bool active = axes.Any(axis => axis.Motion.Active || axis.ReaperOutput.Active);
                Volatile.Write(ref _busy, active ? 1 : 0);
                // No polling/timer when idle. Wait is interrupted by input, pause or disposal.
                if (active) pacing.Wait((nextOutput - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
                else _wake.WaitOne();
            }
        }
        catch (Exception ex) { Fail($"Motion worker: {ex.Message}"); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private static void ApplyMotionPolicy(AxisState axis, Policy policy, double seconds)
    {
        if (axis.MotionPolicy == policy ||
            (axis.MotionPolicy is not null && seconds < policy.ChangedAt / (double)Stopwatch.Frequency)) return;
        // Finish elapsed time with the old tempo before applying the new policy.
        axis.Motion.Advance(Math.Clamp(policy.ChangedAt / (double)Stopwatch.Frequency, axis.Motion.Time, seconds));
        if (axis.Motion is ScrollMotion smoove) smoove.Configure(policy.Profile, policy.AnimationTempo);
        else if (axis.Motion is SmoothieMotion experimental) experimental.Configure(policy.Experimental!);
        axis.MotionPolicy = policy;
    }

    private bool ContextValid(WheelEvent context, long now)
    {
        var route = Volatile.Read(ref _route);
        return now - context.Time < 10 * Stopwatch.Frequency && now - route.CheckedAt <= Stopwatch.Frequency * 3 / 2 && route.Allowed && route.Revision==Volatile.Read(ref _routeRevision) &&
            context.RouteRevision == route.Revision && context.Engine == CurrentPolicy(route).Engine &&
            route.Target==context.Hit && (route.Pixel is not null)==context.Precise && route.Foreground==context.Foreground && context.ReaperTracks==route.ReaperTracks && context.MidiEditor==route.MidiEditor &&
            Native.GetForegroundWindow() == context.Foreground && Native.GetCursorPos(out var point) &&
            TargetMatches(point,route) && !Native.ModifiersOrButtons(allowShift:true);
    }

    private static bool TargetMatches(Native.Point point, Route route)
    {
        nint hit = Native.WindowFromPoint(point);
        if (route.SafeArea is { } bounds && (point.X < bounds.Left || point.X >= bounds.Right || point.Y < bounds.Top || point.Y >= bounds.Bottom)) return false;
        // Browser render/video child windows can change without leaving the page.
        // A pixel provider stays bound to its exact native control.
        return route.Target != 0 && (route.Pixel is null ? Native.GetAncestor(hit, 2) : PixelScrollTarget.TargetHandle(hit)) == route.Target;
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
