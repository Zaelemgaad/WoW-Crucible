using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ItemBrowserWindow : Window
{
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _search = new() { PlaceholderText = "Item name or exact ID" };
    private readonly ComboBox _realm = new() { PlaceholderText = "Connected world database" };
    private readonly ListBox _results = new();
    private readonly TextBlock _status = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _find = new() { Content = "Search" };
    private readonly Button _open = new() { Content = "Open selected item", IsEnabled = false };
    private readonly Button _previous = new() { Content = "Previous", IsEnabled = false };
    private readonly Button _next = new() { Content = "Next", IsEnabled = false };
    private readonly CancellationTokenSource _stop = new();
    private DatabaseConnectionProfile? _resultProfile;
    private DatabaseTableCapability? _table;
    private int _offset;
    private bool _initializing = true;
    private bool _busy;
    internal sealed record RealmChoice(uint Id, string Name) { public override string ToString() => Name; }

    public ItemBrowserWindow(DesktopWorkspaceSession session)
    {
        _session=session;Title="Open existing item";Width=850;Height=560;MinWidth=540;MinHeight=320;
        _results.ItemTemplate=new FuncDataTemplate<SqlRowRecord>((row,_)=>new TextBlock {
            Text=row is null ? "" : $"{row.Values.GetValueOrDefault("entry")}    {row.Values.GetValueOrDefault("name")}    · item level {row.Values.GetValueOrDefault("ItemLevel")}",
            Margin=new Thickness(6) });
        var searchRow=new Grid { ColumnDefinitions=new("*,Auto"),ColumnSpacing=8,Children={_search,_find} };Grid.SetColumn(_find,1);
        var footer=new StackPanel {Orientation=Orientation.Horizontal,Spacing=8,Children={_previous,_next,_open}};
        var root=new Grid {RowDefinitions=new("Auto,Auto,*,Auto,Auto"),RowSpacing=10,Margin=new Thickness(12),Children={_realm,searchRow,_results,footer,_status}};
        Grid.SetRow(searchRow,1);Grid.SetRow(_results,2);Grid.SetRow(footer,3);Grid.SetRow(_status,4);Content=root;
        _find.Click+=async(_,_)=>{_offset=0;await SearchAsync();};
        _search.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter&&!_busy){e.Handled=true;_offset=0;await SearchAsync();}};
        _previous.Click+=async(_,_)=>{_offset=Math.Max(0,_offset-50);await SearchAsync();};
        _next.Click+=async(_,_)=>{_offset+=50;await SearchAsync();};
        _results.SelectionChanged+=(_,_)=>_open.IsEnabled=_results.SelectedItem is SqlRowRecord;
        _results.DoubleTapped+=async(_,_)=>await OpenAsync();_open.Click+=async(_,_)=>await OpenAsync();
        _realm.SelectionChanged+=async(_,_)=>{if(_initializing||_realm.SelectedItem is not RealmChoice choice)return;try{SetBusy(true);_results.ItemsSource=null;_resultProfile=null;await _session.SelectRealmAsync(choice.Id,_stop.Token);_offset=0;}catch(Exception e){_status.Text="Realm connection failed: "+e.Message;return;}finally{SetBusy(false);}await SearchAsync();};
        Opened+=async(_,_)=>await InitializeAsync();Closed+=(_,_)=>_stop.Cancel();
    }

    private async Task InitializeAsync()
    {
        SetBusy(true);
        try {
            var catalog=await _session.GetRealmsAsync(_stop.Token);
            _realm.ItemsSource=catalog.Realms.Select(r=>new RealmChoice(r.Id,$"{r.Name} · {r.Server.WorldDatabase.Database}")).ToArray();
            _realm.SelectedItem=_realm.Items.OfType<RealmChoice>().FirstOrDefault(r=>r.Id==_session.Settings.ActiveRealmId);
            if(!_session.DatabaseTested && !string.IsNullOrWhiteSpace(_session.Settings.ServerRootPath))await _session.DetectServerAndConnectAsync(_session.Settings.ServerRootPath,_stop.Token);
            _realm.SelectedItem=_realm.Items.OfType<RealmChoice>().FirstOrDefault(r=>r.Id==_session.Settings.ActiveRealmId);
            SetBusy(false);await SearchAsync();
        } catch(Exception e){_status.Text="Cannot open database items: "+e.Message;} finally{_initializing=false;SetBusy(false);}
    }

    internal async Task SearchAsync()
    {
        if (_busy) return;
        SetBusy(true);_results.ItemsSource=null;_resultProfile=null;
        try {
            var profile=_session.DatabaseProfile ?? throw new InvalidOperationException("Connect a world database in Workspace setup first.");
            var table=_session.DatabaseCapabilities?.FindTable("item_template") ?? throw new InvalidOperationException("The connected database has no inspected item_template table.");
            var query=_search.Text?.Trim()??"";var exact=uint.TryParse(query,out var id);
            var page=await new SqlWorkspaceService().ReadPageAsync(profile,table,_offset,50,search:exact?null:query,filterColumn:exact?"entry":null,filterValue:exact?id.ToString():null,cancellationToken:_stop.Token);
            if(profile!=_session.DatabaseProfile)throw new InvalidOperationException("The selected realm changed. Search again in the current realm.");
            _resultProfile=profile;_table=table;_results.ItemsSource=page.Rows;
            _previous.IsEnabled=_offset>0;_next.IsEnabled=_offset+page.Rows.Count<page.TotalRows;
            _status.Text=$"{profile.Database} · {page.TotalRows:N0} matching items · showing {(page.Rows.Count == 0 ? 0 : _offset+1)}–{_offset+page.Rows.Count}. Double-click an item to edit it.";
        } catch(OperationCanceledException) { } catch(Exception e){_status.Text="Item search failed: "+e.Message;} finally{SetBusy(false);}
    }

    private async Task OpenAsync()
    {
        if(_busy || _results.SelectedItem is not SqlRowRecord selected || _resultProfile is null || _table is null)return;
        SetBusy(true);
        try {
            if(_resultProfile!=_session.DatabaseProfile)throw new InvalidOperationException("The selected realm changed. Search again before opening an item.");
            var row=await new SqlWorkspaceService().ReadRowAsync(_resultProfile,_table,selected.Key,_stop.Token) ?? throw new InvalidOperationException("This item no longer exists.");
            if(_resultProfile!=_session.DatabaseProfile)throw new InvalidOperationException("The realm changed while reading this item.");
            Close(row.Values);
        } catch(OperationCanceledException) { } catch(Exception e){_status.Text=e.Message;} finally{SetBusy(false);}
    }
    private void SetBusy(bool value){_busy=value;_search.IsEnabled=!value;_find.IsEnabled=_realm.IsEnabled=_results.IsEnabled=!value;_open.IsEnabled=!value&&_results.SelectedItem is SqlRowRecord;if(value){_previous.IsEnabled=false;_next.IsEnabled=false;_status.Text="Reading database…";}}
}
