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
    private readonly CheckBox _browsers = new() { Text = "Эксперимент в Edge / Chrome / Firefox", AutoSize = true };
    private readonly ComboBox _preset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown _speed = new() { DecimalPlaces = 1, Minimum = 0.1m, Maximum = 4, Increment = 0.1m, Value = 1, Width = 65 };
    private readonly NumericUpDown _rise = new() { Minimum = 5, Maximum = 300, Value = 45, Increment = 5, Width = 65 };
    private readonly NumericUpDown _coast = new() { Minimum = 5, Maximum = 500, Value = 90, Increment = 5, Width = 65 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _stats = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly NotifyIcon _tray;
    private readonly string? _integrationPath;
    private bool _updating, _exit;

    internal ProbeForm(string? integrationPath)
    {
        _integrationPath = integrationPath;
        _engine = new(integrationPath is not null);
        if (integrationPath is not null) File.AppendAllText(integrationPath + ".progress.log", "Engine started\n");
        Text = "Smoove 2.0 — прототип ввода";
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
        controls.Controls.AddRange([_enabled, _preset,
            new Label { Text = "Расстояние ×", AutoSize = true, Padding = new(0, 5, 0, 0) }, _speed,
            new Label { Text = "Разгон, мс", AutoSize = true, Padding = new(0, 5, 0, 0) }, _rise,
            new Label { Text = "Торможение, мс", AutoSize = true, Padding = new(0, 5, 0, 0) }, _coast]);
        layout.Controls.Add(controls);
        var rules = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var reset = new Button { Text = "Сбросить счётчики", AutoSize = true };
        reset.Click += (_, _) => { _engine.Cancel(); _receiver.Reset(); };
        var exit = new Button { Text = "Выход", AutoSize = true };
        exit.Click += (_, _) => Exit();
        rules.Controls.AddRange([_browsers, reset, exit]);
        layout.Controls.Add(rules);
        layout.Controls.Add(_status);
        layout.Controls.Add(_stats);
        layout.Controls.Add(_receiver);
        Controls.Add(layout);

        _enabled.CheckedChanged += (_, _) => Apply();
        _speed.ValueChanged += (_, _) => Apply();
        _rise.ValueChanged += (_, _) => Apply();
        _coast.ValueChanged += (_, _) => Apply();
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
        var menu = new ContextMenuStrip();
        menu.Items.Add("Открыть диагностику", null, (_, _) => { Show(); Activate(); });
        var pause = new ToolStripMenuItem("Сглаживать") { CheckOnClick = true, Checked = true };
        pause.CheckedChanged += (_, _) => _enabled.Checked = pause.Checked;
        _enabled.CheckedChanged += (_, _) => pause.Checked = _enabled.Checked;
        menu.Items.Add(pause);
        menu.Items.Add("Выход", null, (_, _) => Exit());
        _tray = new() { Icon = SystemIcons.Application, Text = "Smoove — прототип", Visible = true, ContextMenuStrip = menu };
        _tray.DoubleClick += (_, _) => { Show(); Activate(); };
        _timer.Tick += (_, _) => RefreshState();
        _timer.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += SessionChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += PowerChanged;
        FormClosing += (_, e) =>
        {
            if (!_exit && integrationPath is null) { e.Cancel = true; Hide(); }
        };
        Apply();
        Shown += (_, _) => Native.ShowWindow(Handle, 5);
        if (integrationPath is not null) Shown += async (_, _) => await IntegrationCheck();
    }

    private void Apply()
    {
        if (_updating) return;
        _engine.Configure(_enabled.Checked, new((double)_rise.Value / 1000, (double)_coast.Value / 1000), (double)_speed.Value);
    }

    private void RefreshState()
    {
        string reason = _engine.RefreshRoute(_browsers.Checked);
        _status.Text = _engine.Failure is { } error ? $"Ошибка: {error}. Будущий ввод проходит без обработки." :
            !_enabled.Checked ? "Пауза: исходное колесо" : reason + "; Ctrl / Shift / Alt, кнопки и перемещение курсора прекращают хвост.";
        _stats.Text = _engine.Statistics;
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
    private void Exit() { _exit = true; Close(); }

    private async Task IntegrationCheck()
    {
        File.AppendAllText(_integrationPath + ".progress.log", "Shown / integration started\n");
        Native.GetCursorPos(out var oldCursor);
        string result;
        try
        {
            Activate();
            if (!Native.SetForegroundWindow(Handle)) throw new InvalidOperationException("Не удалось активировать тестовое окно");
            var point = _receiver.PointToScreen(new Point(_receiver.Width / 2, _receiver.Height / 2));
            if (!Native.SetCursorPos(point.X, point.Y)) throw new InvalidOperationException("Не удалось разместить курсор в receiver");
            _receiver.Focus();
            await Task.Delay(200);
            RefreshState();
            Native.GetCursorPos(out var actualPoint);
            nint actualHit = Native.WindowFromPoint(actualPoint);
            if (Native.GetAncestor(actualHit, 2) != Handle)
                throw new InvalidOperationException($"Курсор вне окна: requested={point}, actual={actualPoint.X},{actualPoint.Y}, hit={actualHit:X}, root={Native.GetAncestor(actualHit, 2):X}, form={Handle:X}, receiver bounds={_receiver.Bounds}, visible={Visible}, route={_status.Text}");
            if (Native.GetForegroundWindow() != Handle || Native.ModifiersOrButtons())
                throw new InvalidOperationException("Пользовательский контекст изменился / нажаты модификаторы");
            void SendTest(int delta, nuint marker)
            {
                if (Native.GetForegroundWindow() != Handle || !Native.GetCursorPos(out var cursor) ||
                    Native.WindowFromPoint(cursor) != _receiver.Handle || Native.ModifiersOrButtons())
                    throw new InvalidOperationException("Тест остановлен: контекст receiver изменился");
                if (Native.SendInput(1, [Native.WheelInput(delta, marker)], Marshal.SizeOf<Native.Input>()) != 1)
                    throw new InvalidOperationException("Тестовый SendInput не вставлен");
            }
            long accepted = _engine.Accepted;
            File.AppendAllText(_integrationPath + ".progress.log", "Receiver context ready\n");
            for (int i = 0; i < 20; i++)
            {
                SendTest(-120, Native.TestMarker);
                await Task.Delay(50);
            }
            await Task.Delay(1100);
            File.AppendAllText(_integrationPath + ".progress.log", "First series delivered\n");
            if (_engine.Failure is not null || _engine.Accepted - accepted != 20 ||
                _engine.OutputSum != -2400 || _receiver.DeltaSum != -2400 ||
                _engine.Sent <= 20 || _engine.OwnObserved != _engine.Sent || _engine.IsBusy)
                throw new InvalidOperationException($"Несоответствие интеграции: {_engine.Statistics}; receiver={_receiver.DeltaSum}; route={_status.Text}; foreground={Native.GetForegroundWindow():X}; form={Handle:X}; receiver={_receiver.Handle:X}; size={Marshal.SizeOf<Native.Input>()}");
            long sent = _engine.Sent;
            _enabled.Checked = false;
            SendTest(-120, Native.TestMarker);
            await Task.Delay(150);
            if (_engine.Sent != sent || _receiver.DeltaSum != -2520)
                throw new InvalidOperationException("Пауза не сохранила исходный ввод");

            _preset.SelectedIndex = 1;
            _enabled.Checked = true;
            long inputSum = _engine.InputSum, outputSum = _engine.OutputSum, receiverSum = _receiver.DeltaSum;
            accepted = _engine.Accepted;
            for (int i = 0; i < 12; i++) { SendTest(30, Native.TestMarker); await Task.Delay(i % 3 == 0 ? 100 : 50); }
            await Task.Delay(1500);
            if (_engine.Accepted - accepted != 12 || _engine.InputSum - inputSum != 360 ||
                _engine.OutputSum - outputSum != 360 || _receiver.DeltaSum - receiverSum != 360 || _engine.IsBusy)
                throw new InvalidOperationException($"Тягучий режим/малые дельты: {_engine.Statistics}; receiver={_receiver.DeltaSum}");

            accepted = _engine.Accepted;
            receiverSum = _receiver.DeltaSum;
            SendTest(30, 0x11223344);
            await Task.Delay(100);
            if (_engine.Accepted != accepted || _receiver.DeltaSum - receiverSum != 30)
                throw new InvalidOperationException("Чужой injected-ввод был преобразован или потерян");

            outputSum = _engine.OutputSum;
            SendTest(120, Native.TestMarker);
            await Task.Delay(60);
            Native.SetCursorPos(point.X + 1, point.Y);
            await Task.Delay(100);
            sent = _engine.Sent;
            await Task.Delay(150);
            if (_engine.Sent != sent || _engine.IsBusy || _engine.OutputSum - outputSum >= 120)
                throw new InvalidOperationException("Перемещение курсора не отменило хвост");
            result = $"PASS: controlled hook/motion/SendInput; both profiles; small deltas; own-loop guard; pause; foreign injected bypass; cursor cancellation\n{_engine.Statistics}\nReceiver sum: {_receiver.DeltaSum}";
        }
        catch (Exception ex) { Environment.ExitCode = 1; result = $"FAIL: {ex}"; }
        finally { Native.SetCursorPos(oldCursor.X, oldCursor.Y); }
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
