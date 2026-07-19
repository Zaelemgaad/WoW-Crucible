using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class KnowledgeWorkspaceView : UserControl, IDisposable
{
    private readonly KnowledgeReferenceService _service = new();
    private readonly TextBox _root = new() { Text = KnowledgeReferenceService.FindWikiRoot(CruciblePaths.ApplicationDirectory) ?? string.Empty, PlaceholderText = "Local wiki root…" };
    private readonly TextBox _search = new() { PlaceholderText = "Search tables, fields, flags, commands, systems…" };
    private readonly ComboBox _locale = new() { ItemsSource = new[] { "All languages" }, SelectedIndex = 0 };
    private readonly ListBox _results = new();
    private readonly TextBlock _summary = Status("Index the local wiki to search reference material without leaving Crucible.");
    private readonly TextBlock _articleTitle = new() { Text = "No reference selected", FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _articleContext = Status("Select a result to read its matching section.");
    private readonly TextBox _article = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas"), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _searchCancellation;
    private int _loadRequest;
    private KnowledgeSearchHit? _selected;

    public event EventHandler? BackRequested;

    public KnowledgeWorkspaceView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = new Button { Content = "Wiki folder…" }; browse.Click += async (_, _) => await ChooseRootAsync();
        var rebuild = AccentButton("Build / refresh index"); rebuild.Click += async (_, _) => await BuildAsync();
        var reveal = new Button { Content = "Reveal source" }; reveal.Click += (_, _) => RevealSource();
        var copy = new Button { Content = "Copy source path" }; copy.Click += async (_, _) => await CopySourceAsync();
        var heading = new WrapPanel { Children = { back, new TextBlock { Text = "OFFLINE KNOWLEDGE & FIELD REFERENCE", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12,0) } } };
        var source = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _root, WithColumn(browse, 1), WithColumn(rebuild, 2) } };
        var filter = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _search, WithColumn(_locale, 1) } };
        _results.ItemTemplate = new FuncDataTemplate<KnowledgeSearchHit>((hit, _) => hit is null ? new Grid() : ResultCard(hit));
        _results.SelectionChanged += (_, _) => ShowSelection();
        _search.TextChanged += (_, _) => ScheduleSearch(); _locale.SelectionChanged += (_, _) => Search();
        var left = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8, Children = { filter, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _results }, 2), WithRow(_summary, 3) } };
        var articleHeader = new Grid { ColumnDefinitions = new("*,Auto"), Children = { new StackPanel { Spacing = 3, Children = { _articleTitle, _articleContext } }, WithColumn(new WrapPanel { Children = { reveal, copy } }, 1) } };
        var right = new Grid { RowDefinitions = new("Auto,*"), RowSpacing = 8, Children = { articleHeader, WithRow(new Border { BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Child = _article }, 1) } };
        var body = new ResponsiveSplitGrid(left, right, 1.1, 2);
        Content = new Grid { RowDefinitions = new("Auto,Auto,*"), Margin = new Thickness(12), RowSpacing = 8, Children = { heading, WithRow(source, 1), WithRow(body, 2) } };
    }

    public async Task ActivateAsync(string? query = null)
    {
        if (!string.IsNullOrWhiteSpace(query)) _search.Text = query;
        if (_service.Index is null || !_service.Index.RootPath.Equals(NormalizedRoot(), StringComparison.OrdinalIgnoreCase)) await BuildAsync();
        else Search();
    }

    private async Task ChooseRootAsync()
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return;
        var selected = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select the local Markdown wiki root", AllowMultiple = false });
        var path = selected.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        _root.Text = path; await BuildAsync();
    }

    private async Task BuildAsync()
    {
        _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token; var request = ++_loadRequest; _results.ItemsSource = null; _summary.Text = "Indexing local Markdown reference documents…";
        DesktopCrashLogger.Debug("KNOWLEDGE", "index-start", ("root", _root.Text));
        try
        {
            var index = await Task.Run(() => _service.Build(_root.Text ?? string.Empty, token), token);
            if (request != _loadRequest || token.IsCancellationRequested) return;
            _locale.ItemsSource = new[] { "All languages" }.Concat(index.Locales).ToArray(); _locale.SelectedIndex = 0;
            _summary.Text = $"{index.Articles.Count:N0} document(s) · {index.SectionCount:N0} searchable section(s) · {FormatBytes(index.SourceBytes)} · fully offline";
            DesktopCrashLogger.Debug("KNOWLEDGE", "index-success", ("root", index.RootPath), ("documents", index.Articles.Count), ("sections", index.SectionCount), ("bytes", index.SourceBytes)); Search();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { if (request != _loadRequest) return; _summary.Text = $"Knowledge indexing failed: {exception.Message}"; DesktopCrashLogger.Log("Knowledge index failed", exception); }
    }

    private void ScheduleSearch()
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); _searchCancellation = new CancellationTokenSource(); var token = _searchCancellation.Token;
        _ = Task.Run(async () => { try { await Task.Delay(120, token); await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(Search); } catch (OperationCanceledException) { } });
    }

    private void Search()
    {
        if (_service.Index is null) return;
        try
        {
            var locale = _locale.SelectedIndex <= 0 ? null : Convert.ToString(_locale.SelectedItem);
            var hits = _service.Search(_search.Text, locale, 300); _results.ItemsSource = hits;
            if (hits.Count > 0) { _results.SelectedIndex = 0; _results.ScrollIntoView(hits[0]); }
            else { _selected = null; _articleTitle.Text = "No matching reference"; _articleContext.Text = "Try fewer terms, a table name, field name, flag, or command."; _article.Text = string.Empty; }
            _summary.Text = $"{hits.Count:N0} visible match(es) · {_service.Index.Articles.Count:N0} document(s) · {_service.Index.SectionCount:N0} indexed section(s) · offline";
        }
        catch (Exception exception) { _summary.Text = $"Knowledge search failed: {exception.Message}"; DesktopCrashLogger.Log("Knowledge search failed", exception); }
    }

    private void ShowSelection()
    {
        if (_results.SelectedItem is not KnowledgeSearchHit hit) return; _selected = hit;
        _articleTitle.Text = hit.Title; _articleContext.Text = $"{hit.Heading} · {hit.Locale} · {hit.RelativePath} · score {hit.Score:N0}";
        _article.Text = hit.PlainText.Length == 0 ? "This section contains no readable text." : hit.PlainText; _article.CaretIndex = 0;
    }

    private void RevealSource()
    {
        if (_selected is null) { _summary.Text = "Select a reference result first."; return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_selected.SourcePath}\"") { UseShellExecute = true }); }
        catch (Exception exception) { _summary.Text = $"Could not reveal the reference source: {exception.Message}"; }
    }

    private async Task CopySourceAsync()
    {
        if (_selected is null) { _summary.Text = "Select a reference result first."; return; }
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard; if (clipboard is null) return; await clipboard.SetTextAsync(_selected.SourcePath); _summary.Text = "Copied the exact local source path.";
    }

    private string NormalizedRoot() { try { return Path.GetFullPath(_root.Text ?? string.Empty); } catch { return _root.Text ?? string.Empty; } }
    public void Dispose() { _loadCancellation?.Cancel(); _loadCancellation?.Dispose(); _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); }
    private static Control ResultCard(KnowledgeSearchHit hit) => new Border { BorderBrush = Brush.Parse("#273044"), BorderThickness = new Thickness(0,0,0,1), Padding = new Thickness(8,6), Child = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = hit.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = hit.Heading, Foreground = Brush.Parse("#D0A45C"), FontSize = 11, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = hit.Excerpt, Foreground = Brush.Parse("#96A1B4"), FontSize = 10, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = $"{hit.Locale} · {hit.RelativePath}", Foreground = Brush.Parse("#68758A"), FontSize = 9, TextWrapping = TextWrapping.Wrap } } } };
    private static string FormatBytes(long value) => value >= 1024 * 1024 ? $"{value / 1024d / 1024d:0.0} MiB" : $"{value / 1024d:0.0} KiB";
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
