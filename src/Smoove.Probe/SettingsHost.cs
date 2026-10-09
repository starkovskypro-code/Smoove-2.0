using Smoove.Core;
namespace Smoove.Probe;

public sealed class SettingsHost : IDisposable
{
    private readonly Thread _thread;
    private ProbeForm? _form;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _error;
    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public bool Enabled => _form?.HostEnabled ?? false;
    public SettingsHost()
    {
        _thread = new Thread(() =>
        {
            try
            {
                ApplicationConfiguration.Initialize();
                _form = new ProbeForm(null, settingsHost: true);
                _form.SettingsRequested += () => OpenRequested?.Invoke();
                _form.HostExitRequested += () => ExitRequested?.Invoke();
                _ = _form.Handle;
                _ready.Set();
                Application.Run(_form);
            }
            catch (Exception ex) { _error = ex; _ready.Set(); }
        }) { IsBackground = true, Name = "Smoove tray host" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Input host did not start");
        if (_error is not null) throw new InvalidOperationException("Input host failed", _error);
    }
    public string Status => _form?.HostStatus ?? "Запуск…";
    public string Statistics => _form?.HostStatistics ?? "";
    public void Configure(bool enabled, double rise, double coast, double distance, double acceleration, int hz, string exclusions)
    {
        new MotionProfile(rise, coast).Validate();
        _form?.BeginInvoke(() => _form.ConfigureFromSettings(enabled, rise, coast, distance, acceleration, hz, exclusions));
    }
    public void Dispose()
    {
        if (_form is { IsDisposed: false }) _form.BeginInvoke(() => _form.ExitFromSettings());
        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }
}
