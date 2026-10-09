using System.Text.Json;
using Smoove.Probe;
namespace Smoove.Settings.Views;
public partial class MainPage : Page
{
    private readonly SettingsHost _host;
    private bool _ready;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Smoove", "settings.json");
    private sealed record Values(bool Enabled, double Distance, double Acceleration, double Smooth, double Rise, double Coast, int Frequency, bool External, string Excluded, int Theme, int Version = 1);
    public event Action? ExitRequested;
    public MainPage(SettingsHost host)
    {
        _host = host;
        InitializeComponent();
        Defaults();
        Load();
        _ready = true;
        Apply();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) =>
        {
            StatusText.Text = _host.Status; StatisticsText.Text = _host.Statistics;
            if (Enabled.IsOn != _host.Enabled) { _ready=false; Enabled.IsOn=_host.Enabled; _ready=true; Apply(); }
        };
        _timer.Start();
    }
    private void Changed(object sender, object args) { if (_ready) Apply(); }
    private void Apply()
    {
        DistanceValue.Text = $"{Distance.Value:F1}×";
        AccelerationValue.Text = $"{Acceleration.Value:F0}%";
        SmoothValue.Text = $"{Smooth.Value:F0}%";
        RiseValue.Text = $"{Rise.Value:F0} мс";
        CoastValue.Text = $"{Coast.Value:F0} мс";
        int hz = int.Parse((string)((ComboBoxItem)Frequency.SelectedItem).Tag);
        _host.Configure(Enabled.IsOn, Rise.Value / 1000 * Smooth.Value / 100, Coast.Value / 1000 * Smooth.Value / 100,
            Distance.Value, Acceleration.Value / 100, hz, External.IsOn ? Excluded.Text : "*");
        Connection.Title = Enabled.IsOn ? "Smoove работает" : "Прокрутка на паузе";
        Connection.Severity = Enabled.IsOn ? InfoBarSeverity.Success : InfoBarSeverity.Informational;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Values(Enabled.IsOn, Distance.Value, Acceleration.Value, Smooth.Value, Rise.Value, Coast.Value, Frequency.SelectedIndex, External.IsOn, Excluded.Text, ThemeChoice.SelectedIndex)));
            File.Move(temp, SettingsPath, true);
        }
        catch (IOException) { Connection.Message = "Не удалось сохранить настройки. Проверьте доступ к папке."; }
        catch (UnauthorizedAccessException) { Connection.Message = "Нет доступа к сохранению настроек."; }
    }
    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var v = JsonSerializer.Deserialize<Values>(File.ReadAllText(SettingsPath));
            if (v is null || v.Distance < 0.5 || v.Smooth < 50 || v.Rise < 5 || v.Coast < 5 ||
                !double.IsFinite(v.Distance+v.Acceleration+v.Smooth+v.Rise+v.Coast)) return;
            Enabled.IsOn=v.Enabled; Distance.Value=Math.Clamp(v.Distance,0.5,4); Acceleration.Value=Math.Clamp(v.Acceleration,0,100);
            Smooth.Value=Math.Clamp(v.Smooth,50,200); Rise.Value=Math.Clamp(v.Rise,5,300); Coast.Value=Math.Clamp(v.Coast,5,500);
            Frequency.SelectedIndex=Math.Clamp(v.Frequency,0,3); External.IsOn=v.External; Excluded.Text=v.Excluded ?? ""; ThemeChoice.SelectedIndex=Math.Clamp(v.Theme,0,2);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Connection.Message="Не удалось прочитать настройки. Используются рекомендуемые значения."; }
    }
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        _ready=false; Rise.Value=Preset.SelectedIndex==0?70:110; Coast.Value=Preset.SelectedIndex==0?160:280; _ready=true; Apply();
    }
    private void ResetClick(object sender,RoutedEventArgs e)
    {
        _ready=false; Defaults(); _ready=true; Apply();
    }
    private void Defaults()
    {
        Distance.Value=1; Acceleration.Value=60; Smooth.Value=110; Rise.Value=70; Coast.Value=160;
        Frequency.SelectedIndex=2; Enabled.IsOn=true; External.IsOn=true; Preset.SelectedIndex=0;
    }
    private void ThemeChanged(object sender,SelectionChangedEventArgs e)
    {
        RequestedTheme=ThemeChoice.SelectedIndex switch {1=>ElementTheme.Light,2=>ElementTheme.Dark,_=>ElementTheme.Default};
        if (_ready) Apply();
    }
    private void Navigate(NavigationView sender,NavigationViewSelectionChangedEventArgs e)
    {
        if (ScrollSection is null) return;
        string tag=(string)((NavigationViewItem)e.SelectedItem).Tag;
        ScrollSection.Visibility=tag=="scroll"?Visibility.Visible:Visibility.Collapsed;
        AppsSection.Visibility=tag=="apps"?Visibility.Visible:Visibility.Collapsed;
        DiagnosticsSection.Visibility=tag=="diagnostics"?Visibility.Visible:Visibility.Collapsed;
        TitleText.Text=tag switch {"apps"=>"Приложения","diagnostics"=>"Диагностика",_=>"Прокрутка"};
        SubtitleText.Text=tag switch {"apps"=>"Где применять сглаживание и как выглядит окно.","diagnostics"=>"Состояние обработки и технические счётчики.",_=>"Настройте движение под свой ритм. Изменения применяются сразу."};
    }
    private void ExitClick(object sender,RoutedEventArgs e) { _timer.Stop(); ExitRequested?.Invoke(); }
}
