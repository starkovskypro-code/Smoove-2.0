using System.Diagnostics;
using System.Runtime.InteropServices;
using Smoove.Core;

namespace Smoove.Probe;

// Diagnostic receiver only. The product settings UI will use WinUI 3 after feasibility checks.
internal sealed class ProbeForm : Form
{
    private readonly WheelEngine _engine;
    private readonly ScrollReceiver _receiver = new() { Dock = DockStyle.Fill, TabStop = true };
    private readonly CheckBox _enabled = new() { Text = "Сглаживать", AutoSize = true, Checked = true };
    private readonly CheckBox _browsers = new() { Text = "Сглаживать в других программах", AutoSize = true, Checked = true };
    private readonly TextBox _exclusions = new() { Width = 190, PlaceholderText = "Процессы через запятую" };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _speed = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 4, Increment = 0.1m, Value = 1, Width = 65 };
    private readonly NumericUpDown _rise = new() { Minimum = 5, Maximum = 300, Value = 70, Increment = 5, Width = 65 };
    private readonly NumericUpDown _coast = new() { Minimum = 5, Maximum = 500, Value = 160, Increment = 5, Width = 65 };
    private readonly NumericUpDown _acceleration = new() { Minimum = 0, Maximum = 100, Value = 35, Increment = 5, Width = 65, AccessibleName = "Ускорение от темпа вращения, процентов" };
    private readonly NumericUpDown _smoothing = new() { Minimum = 50, Maximum = 200, Value = 100, Increment = 10, Width = 65, AccessibleName = "Сглаживание серии, процентов" };
    private readonly ComboBox _frequency = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 75, AccessibleName = "Частота выдачи событий, герц" };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _stats = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly NotifyIcon _tray;
    private readonly string? _integrationPath;
    private bool _updating, _exit;
    private readonly bool _settingsHost;
    private MotionProfile _hostProfile = MotionProfile.Responsive;
    private double _hostDistance = 1, _hostAcceleration = 0.6;
    private int _hostHz = 240;
    internal event Action? SettingsRequested;
    internal event Action? HostExitRequested;
    private int _hostEnabled = 1;
    private string _hostStatus = "Запуск…", _hostStatistics = "";
    internal bool HostEnabled => Volatile.Read(ref _hostEnabled) != 0;
    internal string HostStatus => Volatile.Read(ref _hostStatus);
    internal string HostStatistics => Volatile.Read(ref _hostStatistics);
    internal void ConfigureFromSettings(bool enabled, double rise, double coast, double distance, double acceleration, int hz, string exclusions)
    {
        _hostProfile = new(rise, coast); _hostDistance=distance; _hostAcceleration=acceleration; _hostHz=hz;
        _enabled.Checked = enabled;
        _engine.SetExclusions(exclusions);
        _engine.Configure(enabled, _hostProfile, distance, acceleration, hz);
    }
    internal void ExitFromSettings() => Exit();
    internal void SetPathExclusions(ApplicationExclusion[] entries) => _engine.SetPathExclusions(entries);
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(_settingsHost ? false : value);

    internal ProbeForm(string? integrationPath, bool browserCheck = false, bool telegramCheck = false, bool settingsHost = false,bool explorerCheck=false)
    {
        _integrationPath = integrationPath;
        _settingsHost = settingsHost;
        _engine = new(integrationPath is not null);
        if (integrationPath is not null) File.AppendAllText(integrationPath + ".progress.log", "Engine started\n");
        Text = "Smoove 2.0 — прототип 0.4 (ускорение и точная частота)";
        ClientSize = new(1000, 720);
        MinimumSize = new(800, 580);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new(14) };
        for (int i = 0; i < 5; i++) layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { AutoSize = true, Text = "Проверка единого движения серии шагов. Пока это исследовательский прототип, не готовый продукт.", Margin = new(0, 0, 0, 12) });
        var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        _preset.Items.AddRange(["Отзывчивый", "Тягучий"]);
        _preset.SelectedIndex = 0;
        _frequency.Items.AddRange(["60", "120", "240", "500"]);
        _frequency.SelectedIndex = 2;
        if (integrationPath is not null) _acceleration.Value = 0; // Distance checks explicitly disable optional gain.
        controls.Controls.AddRange([_enabled, _preset,
            new Label { Text = "Расстояние ×", AutoSize = true, Padding = new(0, 5, 0, 0) }, _speed,
            new Label { Text = "Разгон, мс", AutoSize = true, Padding = new(0, 5, 0, 0) }, _rise,
            new Label { Text = "Торможение, мс", AutoSize = true, Padding = new(0, 5, 0, 0) }, _coast,
            new Label { Text = "Ускорение от темпа, %", AutoSize = true, Padding = new(0, 5, 0, 0) }, _acceleration,
            new Label { Text = "Сглаживание серии, %", AutoSize = true, Padding = new(0, 5, 0, 0) }, _smoothing,
            new Label { Text = "События, Гц", AutoSize = true, Padding = new(0, 5, 0, 0) }, _frequency,
            new Label { Text = "Больше сглаживания — мягче и дольше отклик. Частота событий не равна FPS приложения.", AutoSize = true }]);
        var tips = new ToolTip();
        tips.SetToolTip(_acceleration, "0 — выключено. Быстрое вращение даёт до указанного процента дополнительного расстояния; медленное остаётся точным.");
        tips.SetToolTip(_smoothing, "Масштабирует разгон и торможение вместе, сохраняя общую траекторию серии. 100% — исходный профиль.");
        tips.SetToolTip(_frequency, "240 Гц по умолчанию. 500 — более частые небольшие события с большей нагрузкой. FPS ограничен экраном и приложением.");
        Disposed += (_, _) => tips.Dispose();
        layout.Controls.Add(controls);
        var rules = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var reset = new Button { Text = "Сбросить счётчики", AutoSize = true };
        reset.Click += (_, _) => { _engine.Cancel(); _receiver.Reset(); };
        var exit = new Button { Text = "Выход", AutoSize = true };
        exit.Click += (_, _) => Exit();
        rules.Controls.AddRange([_browsers, new Label { Text = "Исключения:", AutoSize = true, Padding = new(0, 5, 0, 0) }, _exclusions, reset, exit]);
        layout.Controls.Add(rules);
        layout.Controls.Add(_status);
        layout.Controls.Add(_stats);
        layout.Controls.Add(_receiver);
        Controls.Add(layout);

        _enabled.CheckedChanged += (_, _) => { Volatile.Write(ref _hostEnabled, _enabled.Checked ? 1 : 0); Apply(); };
        _speed.ValueChanged += (_, _) => Apply();
        _rise.ValueChanged += (_, _) => Apply();
        _coast.ValueChanged += (_, _) => Apply();
        _acceleration.ValueChanged += (_, _) => Apply();
        _smoothing.ValueChanged += (_, _) => Apply();
        _frequency.SelectedIndexChanged += (_, _) => Apply();
        _preset.SelectedIndexChanged += (_, _) =>
        {
            _updating = true;
            var profile = _preset.SelectedIndex == 0 ? MotionProfile.Responsive : MotionProfile.Gliding;
            _rise.Value = (decimal)(profile.RiseSeconds * 1000);
            _coast.Value = (decimal)(profile.CoastSeconds * 1000);
            _updating = false;
            Apply();
        };
        _browsers.CheckedChanged += (_, _) => { _engine.Cancel(); RefreshState(); };
        _exclusions.TextChanged += (_, _) => { _engine.SetExclusions(_exclusions.Text); RefreshState(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Настройки", null, (_, _) => { if (_settingsHost) SettingsRequested?.Invoke(); else { Show(); Activate(); } });
        var pause = new ToolStripMenuItem("Сглаживать") { CheckOnClick = true, Checked = true };
        pause.CheckedChanged += (_, _) => _enabled.Checked = pause.Checked;
        _enabled.CheckedChanged += (_, _) => pause.Checked = _enabled.Checked;
        menu.Items.Add(pause);
        menu.Items.Add("Выход", null, (_, _) => Exit());
        using(var iconStream=typeof(ProbeForm).Assembly.GetManifestResourceStream("Smoove.Icon.ico")!)
            Icon = new System.Drawing.Icon(iconStream, SystemInformation.SmallIconSize);
        _tray = new() { Icon = Icon, Text = "Smoove", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => { if (_settingsHost) SettingsRequested?.Invoke(); else { Show(); Activate(); } };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        FormClosing += (_, e) =>
        {
            if (!_exit && integrationPath is null) { e.Cancel = true; Hide(); }
        };
        Apply();
        Shown += (_, _) => { if(!explorerCheck){Native.ShowWindow(Handle, 5); Activate();}else Hide(); };
        if (integrationPath is not null) Shown += async (_, _) =>
        {
            if(explorerCheck)
            {
                try {File.WriteAllText(_integrationPath!,await ExplorerScrollCheck.Run(_engine,_integrationPath!));}
                catch(Exception ex){Environment.ExitCode=1;File.WriteAllText(_integrationPath!,$"FAIL: {ex}");}Exit();
            }
            else if (telegramCheck)
            {
                try { File.WriteAllText(_integrationPath!, await TelegramScrollCheck.Run(_engine, _integrationPath!)); }
                catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(_integrationPath!, $"FAIL: {ex}"); }
                Exit();
            }
            else if (browserCheck) await BrowserCheck();
            else await IntegrationCheck();
        };
    }

    private void Apply()
    {
        if (_updating) return;
        if (_settingsHost) { _engine.Configure(_enabled.Checked,_hostProfile,_hostDistance,_hostAcceleration,_hostHz); return; }
        double factor = (double)_smoothing.Value / 100;
        _engine.Configure(_enabled.Checked, new((double)_rise.Value / 1000 * factor, (double)_coast.Value / 1000 * factor),
            (double)_speed.Value, (double)_acceleration.Value / 100, int.Parse((string)_frequency.SelectedItem!));
    }

    private void RefreshState()
    {
        string reason = _engine.RefreshRoute(_browsers.Checked);
        _status.Text = _engine.Failure is { } error ? $"Ошибка: {error}. Будущий ввод проходит без обработки." :
            !_enabled.Checked ? "Пауза: исходное колесо" : reason + "; последнее колесо: " + _engine.LastBypass;
        _stats.Text = _engine.Statistics;
        Volatile.Write(ref _hostStatus, _status.Text);
        Volatile.Write(ref _hostStatistics, _stats.Text);
        _tray.Text = _engine.Failure is not null ? "Smoove — ошибка, исходный ввод" : _enabled.Checked ? "Smoove — прототип включён" : "Smoove — пауза";
    }

    private void SessionChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        _engine.Cancel();
        _engine.Configure(false, MotionProfile.Responsive, 1);
        if (IsHandleCreated) BeginInvoke(() => { _enabled.Checked = false; });
    }
    private void PowerChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        _engine.Cancel();
        _engine.Configure(false, MotionProfile.Responsive, 1);
        if (IsHandleCreated) BeginInvoke(() => { _enabled.Checked = false; });
    }
    private void Exit() { _exit = true; if (_settingsHost) HostExitRequested?.Invoke(); Close(); }

    private async Task BrowserCheck()
    {
        Native.GetCursorPos(out var oldCursor);
        nint window = 0;
        string result;
        try
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yandex", "YandexBrowser", "Application", "browser.exe");
            if (!File.Exists(exe)) throw new FileNotFoundException("Яндекс Браузер не найден", exe);
            string id = "SmooveCheck-" + Guid.NewGuid().ToString("N")[..8];
            string fixture = Path.ChangeExtension(_integrationPath!, ".html");
            File.WriteAllText(fixture, """
                <!doctype html><meta charset="utf-8"><title>__ID__|0|0|0|0|0</title>
                <style>body{font:20px system-ui;margin:40px;line-height:2}p{border-bottom:1px solid #ccc}header{position:fixed;top:0;background:white}</style>
                <header>Локальная проверка Smoove. Показатели считываются из заголовка.</header>
                <main></main><script>
                document.querySelector('main').innerHTML=Array.from({length:500},(_,i)=>`<p>${i+1}. Длинная страница — проверка единого движения всей серии колеса.</p>`).join('');
                let last=0,frames=0,maxStep=0,maxGap=0,lastChange=0,published=0;
                function frame(t){let y=scrollY,d=Math.abs(y-last);if(d>0){frames++;maxStep=Math.max(maxStep,d);if(lastChange)maxGap=Math.max(maxGap,t-lastChange);lastChange=t;}last=y;
                if(t-published>100){document.title=`__ID__|${Math.round(y)}|${frames}|${Math.round(maxStep)}|${Math.round(maxGap)}|${Math.round(lastChange)}`;published=t;}requestAnimationFrame(frame);}
                requestAnimationFrame(frame);
                </script>
                """.Replace("__ID__", id));
            // Separate local profile: never depend on or modify the user's existing tabs/settings.
            string browserProfile = Path.Combine(Path.GetDirectoryName(_integrationPath!)!, "browser-check-profile");
            Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = "--no-first-run --no-default-browser-check --disable-background-networking --user-data-dir=\"" + browserProfile +
                    "\" --app=\"" + new Uri(fixture).AbsoluteUri + "\"", UseShellExecute = true
            });
            for (int i = 0; i < 100 && window == 0; i++) { await Task.Delay(100); window = Native.FindWindowWithTitle(id); }
            if (window == 0) throw new InvalidOperationException("Локальная страница проверки не открылась");
            await Task.Delay(500);
            window = Native.FindWindowWithTitle(id);
            if (window == 0) throw new InvalidOperationException("Окно fixture исчезло при запуске");
            Native.ShowWindow(window, 9);
            Native.SetWindowPos(window, -1, 0, 0, 0, 0, 0x13); // Only the dedicated fixture, restored below.
            Native.SetForegroundWindow(window);
            await Task.Delay(150);
            if (!Native.GetWindowRect(window, out var rect)) throw new InvalidOperationException("Нет размеров окна браузера");
            Native.SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            _engine.AllowExternalTestWindow(window);
            await Task.Delay(300);
            // Focus the dedicated page's renderer, not the diagnostic window/browser chrome.
            if(!Native.GetCursorPos(out var fixturePoint) || Native.GetAncestor(Native.WindowFromPoint(fixturePoint),2)!=window ||
                Native.ModifiersOrButtons() || Native.SendInput(2,
                [new Native.Input{Mouse=new Native.MouseInput{Flags=2,Extra=Native.OwnMarker}},
                 new Native.Input{Mouse=new Native.MouseInput{Flags=4,Extra=Native.OwnMarker}}],Marshal.SizeOf<Native.Input>())!=2)
                throw new InvalidOperationException("Не удалось сфокусировать страницу fixture");
            await Task.Delay(200);
            if(Native.GetForegroundWindow()!=window)
                throw new InvalidOperationException("Fixture не стал активным окном браузера");
            RefreshState();
            long initialOutput = _engine.OutputSum, initialAccepted = _engine.Accepted, initialCancels = _engine.Cancellations;
            for(int i=0;i<3;i++)
            {
                if(!Native.GetCursorPos(out var cursor) || Native.GetAncestor(Native.WindowFromPoint(cursor),2)!=window || Native.ModifiersOrButtons())
                    throw new InvalidOperationException("Браузерный старт: курсор вне fixture");
                if(Native.SendInput(1,[Native.WheelInput(-120,0)],Marshal.SizeOf<Native.Input>())!=1)
                    throw new InvalidOperationException("Браузерный старт: SendInput не вставлен");
                await Task.Delay(60);
            }
            await Task.Delay(1800);
            string onsetTitle = Native.WindowTitle(window);
            var onsetValues = onsetTitle.Split('|');
            if(_engine.Accepted-initialAccepted!=3 || _engine.OutputSum-initialOutput!=-360 || _engine.Cancellations!=initialCancels ||
                onsetValues.Length<6 || !int.TryParse(onsetValues[1],out int onsetOffset) || onsetOffset<150)
                throw new InvalidOperationException($"Браузерный старт потерял движение: {onsetTitle}; {_engine.Statistics}");
            initialOutput = _engine.OutputSum;
            long accepted = _engine.Accepted;
            nint foreground = Native.GetForegroundWindow();
            for (int i = 0; i < 20; i++)
            {
                if (!Native.GetCursorPos(out var cursor) || Native.GetAncestor(Native.WindowFromPoint(cursor), 2) != window ||
                    Native.GetForegroundWindow() != foreground || !Native.WindowTitle(window).StartsWith(id, StringComparison.Ordinal) || Native.ModifiersOrButtons())
                    throw new InvalidOperationException($"Браузерная проверка остановлена: контекст изменился; foreground={Native.GetForegroundWindow():X}/{foreground:X}; root={Native.GetAncestor(Native.WindowFromPoint(cursor), 2):X}/{window:X}; modifiers={Native.ModifiersOrButtons()}");
                if (Native.SendInput(1, [Native.WheelInput(-120, 0)], Marshal.SizeOf<Native.Input>()) != 1)
                    throw new InvalidOperationException("SendInput в fixture не вставлен");
                await Task.Delay(100);
            }
            await Task.Delay(2000);
            string title = Native.WindowTitle(window);
            var values = title.Split('|');
            if (values.Length < 6 || !int.TryParse(values[1], out int offset) ||
                !int.TryParse(values[2], out int frames) || !int.TryParse(values[3], out int maxStep) ||
                _engine.Accepted - accepted != 20 || _engine.OutputSum-initialOutput != -2400 || offset < 500 || frames < 50)
                throw new InvalidOperationException($"Браузер не подтвердил движение: title={title}; {_engine.Statistics}; {_status.Text}");
            result = $"PASS: Yandex cold start: 3 notches delivered 360 units, scrollY={onsetOffset}px without cancellation; continued series scrollY={offset}; changed rAF frames={frames}; max frame step={maxStep}px\n{_engine.Statistics}\nTitle: {title}\nFixture: {fixture}";
        }
        catch (Exception ex) { Environment.ExitCode = 1; result = $"FAIL: {ex}"; }
        finally
        {
            _engine.AllowExternalTestWindow(0);
            if (window != 0) Native.SetWindowPos(window, -2, 0, 0, 0, 0, 0x13);
            Native.SetCursorPos(oldCursor.X, oldCursor.Y);
        }
        File.WriteAllText(_integrationPath!, result);
        Exit();
    }

    private async Task IntegrationCheck()
    {
        File.AppendAllText(_integrationPath + ".progress.log", "Shown / integration started\n");
        Native.GetCursorPos(out var oldCursor);
        string result;
        try
        {
            TopMost = true;
            Activate();
            Native.SetWindowPos(Handle,-1,0,0,0,0,0x13);
            Native.SetForegroundWindow(Handle);
            await Task.Delay(100);
            var point = _receiver.PointToScreen(new Point(_receiver.Width / 2, _receiver.Height / 2));
            if (!Native.SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Не удалось разместить курсор в receiver");
            _receiver.Focus();
            await Task.Delay(200);
            RefreshState();
            Native.GetCursorPos(out var actualPoint);
            nint actualHit = Native.WindowFromPoint(actualPoint);
            if (Native.GetAncestor(actualHit, 2) != Handle)
                throw new InvalidOperationException($"Курсор вне окна: requested={point}, actual={actualPoint.X},{actualPoint.Y}, hit={actualHit:X}, root={Native.GetAncestor(actualHit, 2):X}, form={Handle:X}, receiver bounds={_receiver.Bounds}, visible={Visible}, route={_status.Text}");
            nint testForeground = Native.GetForegroundWindow();
            if ((testForeground != Handle && !Native.RoutesToPointer()) || Native.ModifiersOrButtons())
                throw new InvalidOperationException("Пользовательский контекст изменился / нажаты модификаторы");
            void SendTest(int delta, nuint marker)
            {
                if (Native.GetForegroundWindow() != testForeground || !Native.GetCursorPos(out var cursor) ||
                    Native.WindowFromPoint(cursor) != _receiver.Handle || Native.ModifiersOrButtons())
                    throw new InvalidOperationException("Тест остановлен: контекст receiver изменился");
                if (Native.SendInput(1, [Native.WheelInput(delta, marker)], Marshal.SizeOf<Native.Input>()) != 1)
                    throw new InvalidOperationException("Тестовый SendInput не вставлен");
            }
            // Negative control uses the identical zero-extra source with smoothing paused.
            _enabled.Checked = false;
            _receiver.Reset();
            _receiver.BeginPaintCapture();
            for (int i = 0; i < 20; i++) { SendTest(-120, 0); await Task.Delay(100); }
            await Task.Delay(120);
            int rawPaints = _receiver.Paints.Count;
            if (_receiver.DeltaSum != -2400 || rawPaints is < 10 or > 22 || _engine.Accepted != 0)
                throw new InvalidOperationException($"Отрицательный контроль не подтвердил шаги: paints={rawPaints}, sum={_receiver.DeltaSum}");
            _receiver.WritePaintCapture(_integrationPath + ".raw-paints.csv");
            _receiver.Reset();
            _enabled.Checked = true;
            // Block the diagnostic/UI thread for longer than the old route timeout.
            // Input is injected from another thread; routing and motion must remain alive.
            await Task.Delay(100);
            long onsetInput=_engine.Accepted,onsetOutput=_engine.OutputSum;
            long onsetCancels=_engine.Cancellations;
            var duringUiStall=Task.Run(()=>
            {
                Thread.Sleep(350);
                for(int i=0;i<3;i++)
                {
                    if(Native.SendInput(1,[Native.WheelInput(-120,0)],Marshal.SizeOf<Native.Input>())!=1)
                        throw new InvalidOperationException("Stall test input failed");
                    Thread.Sleep(60);
                }
            });
            Thread.Sleep(800);
            await duringUiStall;
            await Task.Delay(1600);
            if(_engine.Accepted-onsetInput!=3 || _engine.OutputSum-onsetOutput!=-360 || _engine.Cancellations!=onsetCancels)
                throw new InvalidOperationException($"UI stall swallowed first notches: {_engine.Statistics}");
            for(int i=0;i<5;i++)
            {
                onsetInput=_engine.Accepted;onsetOutput=_engine.OutputSum;
                Native.SetCursorPos(point.X+(i%2)*32,point.Y);
                SendTest(-120,0);
                _engine.Configure(true,MotionProfile.Responsive,1);
                _engine.SetExclusions("");_engine.SetPathExclusions([]);
                await Task.Delay(1600);
                if(_engine.Accepted-onsetInput!=1 || _engine.OutputSum-onsetOutput!=-120)
                    throw new InvalidOperationException("Unchanged settings or idle restart lost a notch");
            }
            Native.SetCursorPos(point.X,point.Y);
            // Loading a page/video may replace the child HWND under a stationary pointer.
            // Accepted distance must survive; the top-level window and policy stay unchanged.
            for(int i=0;i<3;i++)
            {
                await Task.Delay(100);
                onsetInput=_engine.Accepted;onsetOutput=_engine.OutputSum;onsetCancels=_engine.Cancellations;
                nint oldHit=Native.WindowFromPoint(new Native.Point{X=point.X,Y=point.Y});
                SendTest(-120,0);
                await Task.Delay(40);
                using(var replacement=new ScrollReceiver{Bounds=_receiver.Bounds,TabStop=true})
                {
                    _receiver.Parent!.Controls.Add(replacement);
                    replacement.BringToFront();
                    replacement.Focus();
                    if(Native.WindowFromPoint(new Native.Point{X=point.X,Y=point.Y})==oldHit)
                        throw new InvalidOperationException("Child replacement test did not change HWND");
                    for(int notch=0;notch<2;notch++)
                    {
                        if(Native.SendInput(1,[Native.WheelInput(-120,0)],Marshal.SizeOf<Native.Input>())!=1)
                            throw new InvalidOperationException("Child replacement input failed");
                        await Task.Delay(60);
                    }
                    await Task.Delay(1600);
                    if(_engine.Accepted-onsetInput!=3 || _engine.OutputSum-onsetOutput!=-360 || _engine.Cancellations!=onsetCancels)
                        throw new InvalidOperationException($"Child HWND replacement truncated gesture: {_engine.Statistics}");
                }
                _receiver.Focus();
            }
            await Task.Delay(100);
            _receiver.Reset();
            long accepted = _engine.Accepted;
            long initialOutput=_engine.OutputSum;
            _receiver.BeginPaintCapture();
            long seriesStart = Stopwatch.GetTimestamp();
            File.AppendAllText(_integrationPath + ".progress.log", "Receiver context ready\n");
            for (int i = 0; i < 20; i++)
            {
                SendTest(-120, 0);
                await Task.Delay(100);
            }
            await Task.Delay(1800);
            File.AppendAllText(_integrationPath + ".progress.log", "First series delivered\n");
            if (_engine.Failure is not null || _engine.Accepted - accepted != 20 ||
                _engine.OutputSum-initialOutput != -2400 || _receiver.DeltaSum != -2400 ||
                _engine.Sent <= 20 || _engine.OwnObserved != _engine.Sent || _engine.IsBusy)
                throw new InvalidOperationException($"Несоответствие интеграции: {_engine.Statistics}; receiver={_receiver.DeltaSum}; route={_status.Text}; foreground={Native.GetForegroundWindow():X}; form={Handle:X}; receiver={_receiver.Handle:X}; size={Marshal.SizeOf<Native.Input>()}");
            var paints = _receiver.Paints.Where(p => p.Seconds > seriesStart / (double)Stopwatch.Frequency + 0.4 &&
                p.Seconds < seriesStart / (double)Stopwatch.Frequency + 1.8).ToArray();
            double paintGap = paints.Zip(paints.Skip(1), (a, b) => b.Seconds - a.Seconds).DefaultIfEmpty(1).Max();
            if (paints.Length < 40 || paintGap > 0.08)
                throw new InvalidOperationException($"Перерисовка осталась пошаговой: frames={paints.Length}, gap={paintGap * 1000:F2}ms");
            _receiver.WritePaintCapture(_integrationPath + ".paints.csv");
            int smoothPaints = _receiver.Paints.Count;
            bool inactiveVerified = false;
            if (Native.RoutesToPointer())
            {
                using var focusWindow = new Form { Text = "Smoove — проверка неактивной области", Size = new(260, 100),
                    StartPosition = FormStartPosition.Manual, Location = new(Left, Top) };
                focusWindow.Show();
                Native.SetForegroundWindow(focusWindow.Handle);
                await Task.Delay(150);
                testForeground = Native.GetForegroundWindow();
                if (testForeground != focusWindow.Handle) throw new InvalidOperationException("Не удалось проверить неактивное окно");
                RefreshState();
                long before = _receiver.DeltaSum;
                SendTest(30, 0);
                await Task.Delay(1700);
                if (_receiver.DeltaSum - before != 30) throw new InvalidOperationException("Неактивный receiver не получил сглаживание");
                inactiveVerified = true;
                focusWindow.Close();
                Activate();
                Native.SetForegroundWindow(Handle);
                await Task.Delay(150);
                testForeground = Native.GetForegroundWindow();
                RefreshState();
            }
            long sent = _engine.Sent;
            long pausedSum = _receiver.DeltaSum;
            _enabled.Checked = false;
            SendTest(-120, 0);
            await Task.Delay(150);
            if (_engine.Sent != sent || _receiver.DeltaSum - pausedSum != -120)
                throw new InvalidOperationException("Пауза не сохранила исходный ввод");

            _preset.SelectedIndex = 1;
            _enabled.Checked = true;
            long inputSum = _engine.InputSum, outputSum = _engine.OutputSum, receiverSum = _receiver.DeltaSum;
            accepted = _engine.Accepted;
            for (int i = 0; i < 12; i++) { SendTest(30, 0); await Task.Delay(i % 3 == 0 ? 100 : 50); }
            await Task.Delay(2800);
            if (_engine.Accepted - accepted != 12 || _engine.InputSum - inputSum != 360 ||
                _engine.OutputSum - outputSum != 360 || _receiver.DeltaSum - receiverSum != 360 || _engine.IsBusy)
                throw new InvalidOperationException($"Тягучий режим/малые дельты: {_engine.Statistics}; receiver={_receiver.DeltaSum}");

            accepted = _engine.Accepted;
            receiverSum = _receiver.DeltaSum;
            SendTest(30, 0x11223344);
            await Task.Delay(100);
            if (_engine.Accepted != accepted || _receiver.DeltaSum - receiverSum != 30)
                throw new InvalidOperationException("Чужой injected-ввод был преобразован или потерян");

            _preset.SelectedIndex = 0;
            var pacingCounts = new List<int>();
            foreach (string frequency in new[] { "60", "500" })
            {
                _frequency.SelectedItem = frequency;
                _receiver.BeginPaintCapture();
                long pacingStart = Stopwatch.GetTimestamp();
                outputSum = _engine.OutputSum;
                for (int i = 0; i < 12; i++) { SendTest(-120, 0); await Task.Delay(80); }
                await Task.Delay(1800);
                if (_engine.OutputSum - outputSum != -1440 || _engine.IsBusy)
                    throw new InvalidOperationException("Изменение частоты потеряло расстояние");
                double startSeconds = pacingStart / (double)Stopwatch.Frequency;
                pacingCounts.Add(_receiver.Paints.Count(p => p.Seconds > startSeconds + 0.25 && p.Seconds < startSeconds + 0.85));
            }
            if (pacingCounts[0] < 15 || pacingCounts[1] < pacingCounts[0] * 3)
                throw new InvalidOperationException($"Настройка частоты не действует: paints60={pacingCounts[0]}, paints500={pacingCounts[1]}");
            _frequency.SelectedItem = "240";
            _acceleration.Value = 35;
            outputSum = _engine.OutputSum;
            for (int i = 0; i < 16; i++) { SendTest(-120, 0); await Task.Delay(50); }
            await Task.Delay(1800);
            long acceleratedDistance = -( _engine.OutputSum - outputSum);
            if (acceleratedDistance <= 1920 * 1.1 || acceleratedDistance > 1920 * 1.35 + 1)
                throw new InvalidOperationException($"Ускорение темпа не действует: distance={acceleratedDistance}");
            _acceleration.Value = 0;

            _preset.SelectedIndex = 0;
            accepted = _engine.Accepted;
            inputSum = _engine.InputSum;
            outputSum = _engine.OutputSum;
            receiverSum = _receiver.DeltaSum;
            long cancellations = _engine.Cancellations;
            for (int i = 0; i < 80; i++)
            {
                SendTest(-1200, 0);
                await Task.Delay(5);
            }
            await Task.Delay(2800);
            if (_engine.Accepted - accepted != 80 || _engine.InputSum - inputSum != -96000 ||
                _engine.OutputSum - outputSum != -96000 || _receiver.DeltaSum - receiverSum != -96000 ||
                _engine.Cancellations != cancellations || _engine.MaxOutputDelta <= 240 || _engine.IsBusy)
                throw new InvalidOperationException($"Быстрый free-spin потерян/ограничен: maxDelta={_engine.MaxOutputDelta}; {_engine.Statistics}");

            outputSum = _engine.OutputSum;
            SendTest(120, 0);
            await Task.Delay(60);
            Native.SetCursorPos(point.X + 1, point.Y);
            await Task.Delay(2800);
            if (_engine.OutputSum - outputSum != 120 || _engine.IsBusy)
                throw new InvalidOperationException($"Дрожание курсора: distance={_engine.OutputSum - outputSum}, busy={_engine.IsBusy}; {_engine.Statistics}; {_status.Text}");

            outputSum=_engine.OutputSum;
            SendTest(120,0);
            await Task.Delay(40);
            using(var otherWindow=new Form{Text="Smoove — проверка смены окна",Size=new(260,100),StartPosition=FormStartPosition.Manual,Location=new(Left,Top)})
            {
                otherWindow.Show();
                Native.SetForegroundWindow(otherWindow.Handle);
                await Task.Delay(120);
                if(Native.GetForegroundWindow()!=otherWindow.Handle)
                    throw new InvalidOperationException("Window cancellation test did not change foreground");
                sent=_engine.Sent;
                await Task.Delay(200);
                if(_engine.Sent!=sent || _engine.IsBusy || _engine.OutputSum-outputSum>=120)
                    throw new InvalidOperationException("Gesture continued into another foreground window");
            }
            Activate();Native.SetForegroundWindow(Handle);
            await Task.Delay(150);

            outputSum = _engine.OutputSum;
            SendTest(120, 0);
            await Task.Delay(60);
            Native.SetCursorPos(point.X + 32, point.Y);
            await Task.Delay(100);
            sent = _engine.Sent;
            await Task.Delay(150);
            if (_engine.Sent != sent || _engine.IsBusy || _engine.OutputSum - outputSum >= 120)
                throw new InvalidOperationException("Перемещение курсора не отменило хвост");
            result = $"PASS: ordinary zero-extra source; no test input exception; both profiles; own-loop guard; pause; marked foreign bypass; jitter tolerance; cursor cancellation; foreground-window cancellation\n" +
                "Startup regression: 3 notches during 800ms UI stall and 5 idle restarts delivered; unchanged policy did not cancel\n" +
                "Dynamic page regression: 3 child HWND replacements during motion delivered all 1080 units without cancellation\n" +
                $"Raw control changed paints: {rawPaints}; transformed changed paints on same input series: {smoothPaints}\n" +
                $"Free-spin: 80 inputs, 96000 units delivered without cancellation; max output delta={_engine.MaxOutputDelta}\n" +
                $"Frequency paints in 0.6s: 60Hz={pacingCounts[0]}, 500Hz={pacingCounts[1]}; accelerated distance={acceleratedDistance}/1920\n" +
                $"Paint frames during steady series: {paints.Length}; max paint gap: {paintGap * 1000:F2}ms; inactive window verified: {inactiveVerified}\n{_engine.Statistics}\nReceiver sum: {_receiver.DeltaSum}";
        }
        catch (Exception ex) { Environment.ExitCode = 1; result = $"FAIL: {ex}"; }
        finally { Native.SetWindowPos(Handle,-2,0,0,0,0,0x13);Native.SetCursorPos(oldCursor.X, oldCursor.Y); }
        File.WriteAllText(_integrationPath!, result);
        Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            Microsoft.Win32.SystemEvents.SessionSwitch -= SessionChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= PowerChanged;
            _engine.Dispose();
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
            Icon?.Dispose();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class ScrollReceiver : Control
{
    private double _offset = 600;
    internal long DeltaSum { get; private set; }
    internal long EventCount { get; private set; }
    internal readonly record struct PaintSample(double Seconds, double Offset);
    internal List<PaintSample> Paints { get; } = [];
    private bool _capturePaints;
    internal void BeginPaintCapture() { Paints.Clear(); _capturePaints = true; }
    internal void WritePaintCapture(string path) => File.WriteAllLines(path,
        new[] { "seconds,offset" }.Concat(Paints.Select(p => FormattableString.Invariant($"{p.Seconds:G17},{p.Offset:G17}"))));
    internal ScrollReceiver()
    {
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        AccessibleName = "Диагностическая длинная страница. Прокручивайте колесом внутри области";
    }
    internal void Reset() { _offset = 600; DeltaSum = EventCount = 0; Invalidate(); }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.Wheel)
        {
            int delta = unchecked((short)((long)m.WParam >> 16));
            DeltaSum += delta;
            EventCount++;
            // Direct partial-delta receiver: no local animation or second smoothing filter.
            _offset = Math.Clamp(_offset - delta * 0.5, 0, 20000);
            Invalidate();
            m.Result = 0;
            return;
        }
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_capturePaints && Paints.Count < 10000 && (Paints.Count == 0 || Paints[^1].Offset != _offset))
            Paints.Add(new(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency, _offset));
        using var brush = new SolidBrush(ForeColor);
        e.Graphics.DrawString($"Приёмник малых дельт, без собственной анимации. Offset: {_offset:F1}; событий: {EventCount}", Font, brush, 10, 8);
        e.Graphics.SetClip(new Rectangle(0, 40, Width, Height - 40));
        for (int row = Math.Max(0, (int)(_offset / 44)); row < Math.Min(500, (_offset + Height) / 44 + 1); row++)
        {
            float y = (float)(50 + row * 44 - _offset);
            e.Graphics.DrawString($"{row + 1:000}     Длинная страница: серия шагов должна сливаться в непрерывное движение", Font, brush, 14, y);
            e.Graphics.DrawLine(SystemPens.ControlLight, 10, y + 31, Width - 10, y + 31);
        }
    }
}
