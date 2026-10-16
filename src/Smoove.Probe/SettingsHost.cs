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
    public event Action<int,int>? TrayMenuRequested;
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
                _form.TrayMenuRequested += (x,y) => TrayMenuRequested?.Invoke(x,y);
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
    public void SetApplicationProfiles(ApplicationProfile[] profiles, bool onlySelected)
    {
        var snapshot = profiles.Select(p => p.Validated()).ToArray();
        _form?.BeginInvoke(() => _form.SetApplicationProfiles(snapshot, onlySelected));
    }
    public void SetExclusions(ApplicationExclusion[] entries)
    {
        var snapshot = entries.ToArray();
        _form?.BeginInvoke(() => _form.SetPathExclusions(snapshot));
    }
    public void Configure(bool enabled, double rise, double coast, double distance, double acceleration, int hz, string exclusions, double animationTempo = 1,
        MotionEngineKind engine = MotionEngineKind.Smoove, SmoothieProfile? experimental = null)
    {
        new MotionProfile(rise, coast).Validate();
        experimental?.Validate();
        _form?.BeginInvoke(() => _form.ConfigureFromSettings(enabled, rise, coast, distance, acceleration, hz, exclusions, animationTempo, engine, experimental));
    }
    public void Dispose()
    {
        if (_form is { IsDisposed: false }) _form.BeginInvoke(() => _form.ExitFromSettings());
        _thread.Join(TimeSpan.FromSeconds(3));
        _ready.Dispose();
    }
}
