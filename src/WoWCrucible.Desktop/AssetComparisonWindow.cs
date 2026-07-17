using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class AssetComparisonWindow : Window
{
    private const int PageSize = 96;
    private readonly TextBox _library = new() { Text = Directory.Exists(@"G:\Crucible-Extras-Processed") ? @"G:\Crucible-Extras-Processed" : string.Empty };
    private readonly TextBox _directorySearch = new() { PlaceholderText = "Filter content paths…" };
    private readonly ListBox _directories = new();
    private readonly TextBox _fileSearch = new() { PlaceholderText = "Optional filename filter…" };
    private readonly ComboBox _sourceFilter = new() { MinWidth = 180 };
    private readonly WrapPanel _cards = new() { Orientation = Orientation.Horizontal, ItemWidth = 176, ItemHeight = 206 };
    private readonly TextBlock _folderTitle = new() { FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _pageStatus = new() { Foreground = Brush.Parse("#8F9AAE") };
    private readonly TextBlock _status = new() { Text = "Choose a content-first asset library.", Foreground = Brush.Parse("#99A5B8") };
    private readonly Image[] _comparisonImages = [new() { Stretch = Stretch.Uniform }, new() { Stretch = Stretch.Uniform }];
    private readonly ScrollViewer[] _comparisonScrolls = [new(), new()];
    private readonly TextBlock[] _comparisonTitles = [new() { TextWrapping = TextWrapping.Wrap }, new() { TextWrapping = TextWrapping.Wrap }];
    private readonly Button[] _slotButtons = [new() { Content = "LEFT SLOT" }, new() { Content = "RIGHT SLOT" }];
    private readonly Bitmap?[] _comparisonBitmaps = new Bitmap?[2];
    private readonly List<Bitmap> _thumbnailBitmaps = [];
    private AssetComparisonIndex? _index; private IReadOnlyList<AssetComparisonEntry> _folderEntries = []; private IReadOnlyList<AssetComparisonEntry> _filteredEntries = [];
    private CancellationTokenSource? _thumbnailCancellation; private int _page; private int _activeSlot; private double _zoom = 1; private bool _syncingScroll; private bool _settingSourceFilter;

    public AssetComparisonWindow(string? libraryRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(libraryRoot)) { _library.Text = libraryRoot; Opened += async (_, _) => await LoadIndexAsync(); }
        Title = "WoW Crucible — Visual Asset Comparison"; Width = 1580; Height = 940; MinWidth = 1120; MinHeight = 700; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _directories.ItemTemplate = new FuncDataTemplate<AssetComparisonDirectory>((item, _) => new Grid
        {
            ColumnDefinitions = new("*,Auto"), Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = string.IsNullOrEmpty(item.LogicalPath) ? "(archive root)" : item.LogicalPath, TextTrimming = TextTrimming.CharacterEllipsis },
                WithColumn(new TextBlock { Text = $"{item.PngFiles:N0} · {item.ProvenanceSources:N0} src", Foreground = Brush.Parse("#7F8A9F"), FontSize = 10, Margin = new Thickness(8,0,0,0) }, 1)
            }
        });
        _directories.SelectionChanged += async (_, _) => await SelectDirectoryAsync(); _directorySearch.TextChanged += (_, _) => FilterDirectories();
        _fileSearch.TextChanged += async (_, _) => await FilterFilesAsync(); _sourceFilter.SelectionChanged += async (_, _) => { if (!_settingSourceFilter) await FilterFilesAsync(); };
        for (var index = 0; index < 2; index++) { var slot = index; _slotButtons[index].Click += (_, _) => SetActiveSlot(slot); }
        SetActiveSlot(0); Content = BuildLayout(); Closed += (_, _) => DisposeImages();
    }

    private Control BuildLayout()
    {
        var browse = new Button { Content = "Browse…" }; browse.Click += async (_, _) => await BrowseLibraryAsync();
        var load = AccentButton("Index library"); load.Click += async (_, _) => await LoadIndexAsync();
        var top = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), Margin = new Thickness(14, 12), ColumnSpacing = 8 };
        top.Children.Add(new TextBlock { Text = "ASSET LIBRARY", FontWeight = FontWeight.Bold, FontSize = 10, Foreground = Brush.Parse("#C58A2B"), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(_library, 1); top.Children.Add(_library); Grid.SetColumn(browse, 2); top.Children.Add(browse); Grid.SetColumn(load, 3); top.Children.Add(load);

        var left = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(10) };
        left.Children.Add(_directorySearch); Grid.SetRow(_directories, 1); _directories.Margin = new Thickness(0, 8, 0, 0); left.Children.Add(_directories);

        var previous = new Button { Content = "← Previous" }; previous.Click += async (_, _) => { if (_page > 0) { _page--; await RenderPageAsync(); } };
        var next = new Button { Content = "Next →" }; next.Click += async (_, _) => { if ((_page + 1) * PageSize < _filteredEntries.Count) { _page++; await RenderPageAsync(); } };
        var middleHeader = new StackPanel { Spacing = 8, Margin = new Thickness(10, 10, 10, 4), Children = { _folderTitle, new TextBlock { Text = "Every PNG directly in this content directory is shown, grouped by patch provenance. Filenames do not need to match.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8793A7"), FontSize = 11 }, new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8, Children = { _fileSearch, WithColumn(_sourceFilter, 1) } } } };
        var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(10, 5), Children = { previous, next, _pageStatus } };
        var middle = new Grid { RowDefinitions = new("Auto,*,Auto") }; middle.Children.Add(middleHeader);
        var cardScroll = new ScrollViewer { Content = _cards, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Margin = new Thickness(8) };
        Grid.SetRow(cardScroll, 1); middle.Children.Add(cardScroll); Grid.SetRow(pager, 2); middle.Children.Add(pager);

        var compare = BuildComparisonPane();
        var body = new Grid { ColumnDefinitions = new("330,520,*") };
        body.Children.Add(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,0,1,0), Child = left });
        Grid.SetColumn(middle, 1); body.Children.Add(middle); Grid.SetColumn(compare, 2); body.Children.Add(compare);
        var root = new Grid { RowDefinitions = new("Auto,*,Auto") }; root.Children.Add(top); Grid.SetRow(body, 1); root.Children.Add(body);
        var status = new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(14,7), Child = _status }; Grid.SetRow(status, 2); root.Children.Add(status); return root;
    }

    private Control BuildComparisonPane()
    {
        var zoom = new Slider { Minimum = 0.1, Maximum = 4, Value = 1, Width = 180 }; zoom.ValueChanged += (_, e) => { _zoom = e.NewValue; ApplyZoom(); };
        var openLeft = new Button { Content = "Reveal left" }; openLeft.Click += (_, _) => RevealSlot(0); var openRight = new Button { Content = "Reveal right" }; openRight.Click += (_, _) => RevealSlot(1);
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12), Children = { new TextBlock { Text = "SYNCHRONIZED ZOOM & PAN", VerticalAlignment = VerticalAlignment.Center, FontSize = 10, FontWeight = FontWeight.Bold }, zoom, openLeft, openRight } };
        var grid = new Grid { ColumnDefinitions = new("*,*"), RowDefinitions = new("Auto,*") };
        for (var index = 0; index < 2; index++)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _slotButtons[index], _comparisonTitles[index] } }; Grid.SetColumn(header, index); grid.Children.Add(header);
            _comparisonScrolls[index].Content = _comparisonImages[index]; _comparisonScrolls[index].HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto; _comparisonScrolls[index].VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            var scrollIndex = index; _comparisonScrolls[index].ScrollChanged += (_, _) => SyncScroll(scrollIndex);
            var imageBorder = new Border { Background = Brush.Parse("#090B0F"), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), Margin = new Thickness(3), ClipToBounds = true, Child = _comparisonScrolls[index] };
            Grid.SetRow(imageBorder, 1); Grid.SetColumn(imageBorder, index); grid.Children.Add(imageBorder);
        }
        var root = new Grid { RowDefinitions = new("Auto,*"), Margin = new Thickness(6) }; root.Children.Add(toolbar); Grid.SetRow(grid, 1); root.Children.Add(grid); return root;
    }

    private async Task LoadIndexAsync()
    {
        _status.Text = "Reading the catalog and grouping PNGs by content directory…";
        try { _index = await Task.Run(() => AssetComparisonService.BuildIndex(_library.Text ?? string.Empty)); FilterDirectories(); _status.Text = $"Indexed {_index.TotalPngFiles:N0} PNGs across {_index.Directories.Count:N0} content directories."; }
        catch (Exception exception) { DesktopCrashLogger.Log("Asset comparison indexing failed", exception); _status.Text = exception.Message; }
    }

    private void FilterDirectories()
    {
        if (_index is null) return; var query = _directorySearch.Text?.Trim() ?? string.Empty;
        _directories.ItemsSource = _index.Directories.Where(directory => query.Length == 0 || directory.LogicalPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private async Task SelectDirectoryAsync()
    {
        if (_index is null || _directories.SelectedItem is not AssetComparisonDirectory directory) return;
        _folderTitle.Text = string.IsNullOrEmpty(directory.LogicalPath) ? "(archive root)" : directory.LogicalPath; _status.Text = "Reading direct PNG variants…";
        _folderEntries = await Task.Run(() => AssetComparisonService.GetDirectoryPngs(_index, directory.LogicalPath));
        var sources = new[] { "All patch sources" }.Concat(_folderEntries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        _settingSourceFilter = true; _sourceFilter.ItemsSource = sources; _sourceFilter.SelectedIndex = 0; _settingSourceFilter = false; await FilterFilesAsync();
    }

    private async Task FilterFilesAsync()
    {
        var query = _fileSearch.Text?.Trim() ?? string.Empty; var source = _sourceFilter.SelectedItem as string;
        _filteredEntries = _folderEntries.Where(entry => (query.Length == 0 || entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) && (string.IsNullOrEmpty(source) || source == "All patch sources" || entry.Provenance.Equals(source, StringComparison.OrdinalIgnoreCase))).ToArray();
        _page = 0; await RenderPageAsync();
    }

    private async Task RenderPageAsync()
    {
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = new(); var token = _thumbnailCancellation.Token;
        foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose(); _thumbnailBitmaps.Clear(); _cards.Children.Clear();
        var page = _filteredEntries.Skip(_page * PageSize).Take(PageSize).ToArray(); var images = new List<(Image Image, AssetComparisonEntry Entry)>();
        foreach (var entry in page)
        {
            var image = new Image { Width = 148, Height = 148, Stretch = Stretch.Uniform };
            var card = new Button { Width = 170, Height = 200, Margin = new Thickness(3), Padding = new Thickness(7), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                Content = new StackPanel { Spacing = 4, Children = { image, new TextBlock { Text = entry.FileName, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 }, new TextBlock { Text = entry.Provenance, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brush.Parse("#C58A2B"), FontSize = 10 } } } };
            card.Click += async (_, _) => await SelectComparisonAsync(entry); _cards.Children.Add(card); images.Add((image, entry));
        }
        _pageStatus.Text = _filteredEntries.Count == 0 ? "No PNGs match." : $"{_page * PageSize + 1:N0}–{_page * PageSize + page.Length:N0} of {_filteredEntries.Count:N0}";
        _status.Text = $"Showing all direct PNGs in this path · {_folderEntries.Count:N0} total · {_folderEntries.Select(entry => entry.Provenance).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} patch sources.";
        foreach (var item in images)
        {
            try { var bitmap = await Task.Run(() => { token.ThrowIfCancellationRequested(); using var stream = File.OpenRead(item.Entry.FullPath); return Bitmap.DecodeToWidth(stream, 148, BitmapInterpolationMode.MediumQuality); }, token); if (!token.IsCancellationRequested) { item.Image.Source = bitmap; _thumbnailBitmaps.Add(bitmap); } else bitmap.Dispose(); }
            catch (OperationCanceledException) { break; } catch { }
        }
    }

    private async Task SelectComparisonAsync(AssetComparisonEntry entry)
    {
        var slot = _activeSlot; try
        {
            var bitmap = await Task.Run(() => new Bitmap(entry.FullPath)); _comparisonBitmaps[slot]?.Dispose(); _comparisonBitmaps[slot] = bitmap; _comparisonImages[slot].Source = bitmap;
            ApplyZoom();
            _comparisonTitles[slot].Text = $"{entry.Provenance}\n{entry.FileName}\n{bitmap.PixelSize.Width:N0}×{bitmap.PixelSize.Height:N0} · {entry.Bytes / 1024d:0.#} KiB";
            _comparisonImages[slot].Tag = entry; SetActiveSlot(slot == 0 ? 1 : 0);
        }
        catch (Exception exception) { DesktopCrashLogger.Log("Asset comparison image load failed", exception); _status.Text = exception.Message; }
    }

    private void SetActiveSlot(int slot) { _activeSlot = slot; for (var index = 0; index < 2; index++) { _slotButtons[index].Classes.Set("accent", index == slot); } }
    private void ApplyZoom() { for (var index = 0; index < 2; index++) if (_comparisonBitmaps[index] is { } bitmap) { _comparisonImages[index].Width = bitmap.PixelSize.Width * _zoom; _comparisonImages[index].Height = bitmap.PixelSize.Height * _zoom; } }
    private void SyncScroll(int source) { if (_syncingScroll) return; _syncingScroll = true; _comparisonScrolls[source == 0 ? 1 : 0].Offset = _comparisonScrolls[source].Offset; _syncingScroll = false; }
    private void RevealSlot(int slot) { if (_comparisonImages[slot].Tag is not AssetComparisonEntry entry) return; Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", entry.FullPath } }); }
    private async Task BrowseLibraryAsync() { var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select Crucible asset library", AllowMultiple = false }); var path = folders.FirstOrDefault()?.TryGetLocalPath(); if (path is not null) _library.Text = path; }
    private void DisposeImages() { _thumbnailCancellation?.Cancel(); foreach (var bitmap in _thumbnailBitmaps) bitmap.Dispose(); foreach (var bitmap in _comparisonBitmaps) bitmap?.Dispose(); }
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
}
