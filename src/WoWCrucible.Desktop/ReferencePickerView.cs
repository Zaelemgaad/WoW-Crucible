using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed record ReferenceDbcSource(WdbcFile File, IReadOnlyList<DbcColumn> Columns, int IdColumn, int NameColumn, int[] DetailColumns);
internal sealed record ReferencePickerRequest(ReferenceDomain Domain, string FieldLabel, uint CurrentId, Action<uint> Apply, ReferenceDbcSource? DbcSource = null);

internal sealed class ReferencePickerView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly ReferencePickerRequest _request;
    private readonly TextBox _search = new() { PlaceholderText = "Search by exact ID or name…", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _results = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#98A5B8") };
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;
    public event EventHandler? SelectionApplied;

    public ReferencePickerView(DesktopWorkspaceSession session, ReferencePickerRequest request)
    {
        _session = session; _request = request;
        _results.ItemTemplate = new FuncDataTemplate<ReferenceLookupEntry>((entry, _) => entry is null ? new TextBlock() : new Grid
        {
            ColumnDefinitions = new("Auto,2*,*,Auto"), ColumnSpacing = 10, Margin = new Thickness(5, 3),
            Children =
            {
                new TextBlock { Text = entry.Id.ToString("N0"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                WithColumn(new TextBlock { Text = string.IsNullOrWhiteSpace(entry.Name) ? "(unnamed)" : entry.Name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, 1),
                WithColumn(new TextBlock { Text = entry.Details, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8491A5"), VerticalAlignment = VerticalAlignment.Center }, 2),
                WithColumn(new TextBlock { Text = entry.Source, Foreground = Brush.Parse("#C58A2B"), VerticalAlignment = VerticalAlignment.Center }, 3)
            }
        });
        _results.DoubleTapped += (_, _) => ApplySelected();
        _search.TextChanged += (_, _) => QueueSearch();
        _search.KeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; _ = SearchAsync(); } };
        var back = new Button { Content = "← Back" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var find = AccentButton("Search"); find.Click += async (_, _) => await SearchAsync();
        var use = AccentButton("Use selected ID"); use.Click += (_, _) => ApplySelected();
        var clear = new Button { Content = "Clear" }; clear.Click += (_, _) => _search.Text = string.Empty;
        var header = new Grid
        {
            ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(14, 9),
            Children =
            {
                back,
                WithColumn(new StackPanel { Spacing = 2, Children = { new TextBlock { Text = $"SELECT {DomainName(request.Domain).ToUpperInvariant()} REFERENCE", FontSize = 19, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"Field: {request.FieldLabel} · current ID {(request.CurrentId == 0 ? "none" : request.CurrentId.ToString("N0"))}. Results merge available client DBC and live SQL names without changing either source.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A8") } } }, 1)
            }
        };
        var searchBar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Margin = new Thickness(14, 8), Children = { _search, WithColumn(find, 1), WithColumn(clear, 2) } };
        var footer = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(14, 8), Children = { _status, WithColumn(use, 1) } };
        Content = new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto"),
            Children = { header, WithRow(searchBar, 1), WithRow(new Border { Margin = new Thickness(14, 0), BorderBrush = Brush.Parse("#2B3548"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _results }, 2), WithRow(footer, 3) }
        };
        _search.Text = request.CurrentId == 0 ? string.Empty : request.CurrentId.ToString();
        Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
    }

    private void QueueSearch()
    {
        _operation?.Cancel(); _operation?.Dispose(); var cancellation = _operation = new CancellationTokenSource();
        _ = DebouncedSearchAsync(cancellation);
    }

    private async Task DebouncedSearchAsync(CancellationTokenSource cancellation)
    {
        try { await Task.Delay(160, cancellation.Token); await SearchAsync(cancellation); }
        catch (OperationCanceledException) { }
    }

    private async Task SearchAsync(CancellationTokenSource? existing = null)
    {
        if (existing is null) { _operation?.Cancel(); _operation?.Dispose(); existing = _operation = new CancellationTokenSource(); }
        var token = existing.Token; var query = _search.Text ?? string.Empty; _status.Text = $"Searching {DomainName(_request.Domain).ToLowerInvariant()} references…";
        try
        {
            var pages = new List<ReferenceLookupPage>(); var tasks = new List<Task<ReferenceLookupPage>>();
            if (_session.DatabaseProfile is { } profile && _session.DatabaseCapabilities is { } capabilities)
                tasks.Add(new ReferenceLookupService().SearchSqlAsync(profile, capabilities, _request.Domain, query, 500, token));
            if (_request.DbcSource is { } source)
                tasks.Add(Task.Run(() => ReferenceLookupService.SearchDbc(_request.Domain, source.File, source.Columns, source.IdColumn, source.NameColumn, query, 500, source.DetailColumns), token));
            if (tasks.Count > 0) pages.AddRange(await Task.WhenAll(tasks));
            token.ThrowIfCancellationRequested();
            var merged = ReferenceLookupService.Merge(_request.Domain, query, 500, pages.ToArray());
            _results.ItemsSource = merged.Entries; if (merged.Entries.Count == 1) _results.SelectedIndex = 0;
            _status.Text = $"{merged.Entries.Count:N0} result(s) from {(merged.Sources.Count == 0 ? "no available source" : string.Join(" + ", merged.Sources))}.{(merged.HasMore ? " Refine the search to see beyond the first 500 matches." : string.Empty)}";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _status.Text = $"Reference search failed safely: {exception.Message}"; DesktopCrashLogger.Log("Reference search failed", exception); }
    }

    private void ApplySelected()
    {
        if (_results.SelectedItem is not ReferenceLookupEntry selected) { _status.Text = "Select a reference row first."; return; }
        _request.Apply(selected.Id); SelectionApplied?.Invoke(this, EventArgs.Empty);
    }

    private static string DomainName(ReferenceDomain domain) => domain switch
    {
        ReferenceDomain.Spell => "Spell", ReferenceDomain.Item => "Item", ReferenceDomain.Creature => "Creature", ReferenceDomain.Quest => "Quest", ReferenceDomain.GameObject => "Gameobject",
        ReferenceDomain.SpellCastTime => "Spell cast time", ReferenceDomain.SpellDuration => "Spell duration", ReferenceDomain.SpellRange => "Spell range", ReferenceDomain.SpellRuneCost => "Spell rune cost",
        ReferenceDomain.SpellVisual => "Spell visual", ReferenceDomain.SpellIcon => "Spell icon", ReferenceDomain.SpellDifficulty => "Spell difficulty", _ => domain.ToString()
    };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }
}
