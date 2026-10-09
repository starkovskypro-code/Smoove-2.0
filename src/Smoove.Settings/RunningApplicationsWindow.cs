using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Smoove.Settings;

internal sealed class RunningApplicationsWindow : Window
{
    private readonly ListView _list=new() { SelectionMode=ListViewSelectionMode.Multiple, HorizontalContentAlignment=HorizontalAlignment.Stretch };
    private readonly TextBox _search=new() { PlaceholderText="Поиск по названию или пути" };
    private readonly TextBlock _status=new() { Text="Загрузка программ…", TextWrapping=TextWrapping.Wrap };
    private ApplicationChoice[] _programs=[];
    private readonly HashSet<string> _selected=new(StringComparer.OrdinalIgnoreCase);
    private bool _filtering;
    private bool _closed;
    public RunningApplicationsWindow(Window owner,Action<string[]> add)
    {
        Title="Запущенные программы — Smoove";
        Closed+=(_,_)=>_closed=true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_search,"Поиск программы");
        SystemBackdrop=new MicaBackdrop();
        var grid=new Grid { Padding=new Thickness(24), RowSpacing=16 };
        grid.RowDefinitions.Add(new(){Height=GridLength.Auto}); grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)}); grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        grid.Children.Add(_search); Grid.SetRow(_status,1); grid.Children.Add(_status); Grid.SetRow(_list,2); grid.Children.Add(_list);
        var button=new Button{Content="Добавить выбранные",HorizontalAlignment=HorizontalAlignment.Right,IsEnabled=false};
        Grid.SetRow(button,3); grid.Children.Add(button); Content=grid;
        _search.TextChanged+=(_,_)=>Filter();
        _list.SelectionChanged+=(_,e)=>
        {
            if(_filtering)return;
            foreach(ListViewItem item in e.AddedItems)_selected.Add((string)item.Tag);
            foreach(ListViewItem item in e.RemovedItems)_selected.Remove((string)item.Tag);
            button.IsEnabled=_selected.Count>0;
        };
        button.Click+=(_,_)=>{add(_selected.ToArray());Close();};
        AppWindow.Resize(new SizeInt32(800,600));
        var parent=owner.AppWindow;
        AppWindow.Move(new PointInt32(parent.Position.X+(parent.Size.Width-800)/2,parent.Position.Y+(parent.Size.Height-600)/2));
        if(AppWindow.Presenter is OverlappedPresenter presenter)presenter.IsResizable=true;
        _=Load();
    }
    private async Task Load()
    {
        try { _programs=await Task.Run(ApplicationCatalog.Running); if(!_closed)Filter(); }
        catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        { _status.Text="Не удалось получить список программ. Можно выбрать EXE на компьютере."; }
    }
    private void Filter()
    {
        _filtering=true; _list.Items.Clear();
        string query=_search.Text.Trim();
        var matches=_programs.Where(p=>p.Name.Contains(query,StringComparison.CurrentCultureIgnoreCase)||p.Path.Contains(query,StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach(var p in matches)
        {
            var image=new Image { Width=32,Height=32,Margin=new Thickness(0,0,12,0) };
            var text=new StackPanel{Spacing=4}; text.Children.Add(new TextBlock{Text=p.Name,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
            text.Children.Add(new TextBlock{Text=p.Path,TextWrapping=TextWrapping.Wrap,FontSize=12});
            var row=new Grid{ColumnSpacing=12};row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            row.Children.Add(image);Grid.SetColumn(text,1);row.Children.Add(text);
            var item=new ListViewItem{Tag=p.Path,Content=row,Padding=new Thickness(12),HorizontalContentAlignment=HorizontalAlignment.Stretch};
            _list.Items.Add(item);if(_selected.Contains(p.Path))_list.SelectedItems.Add(item);
            _=LoadIcon(image,p.Path);
        }
        _status.Text=$"Найдено: {matches.Length}. Можно выбрать несколько программ.";
        _filtering=false;
    }
    private static async Task LoadIcon(Image image,string path) { try { image.Source=await ApplicationCatalog.Icon(path); } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { } }
}
