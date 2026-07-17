using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

public partial class MainWindow : Window
{
    private WdbcFile? _file;
    private IReadOnlyList<DbcColumn> _columns = [];
    private CancellationTokenSource? _searchCancellation;
    private long _lastRenderReport;

    public MainWindow()
    {
        InitializeComponent();
        DbcView.SelectionChanged += (_, selection) =>
        {
            InspectorTitle.Text = selection.Column.Name;
            InspectorSummary.Text = selection.Value.Length == 0 ? "(empty)" : selection.Value;
            InspectorDetail.Text = $"Row       {selection.Row + 1:N0}\nColumn    {selection.ColumnIndex:N0}\nType      {selection.Column.Type}\nOffset    {selection.Column.Offset:N0} bytes\nSize      {selection.Column.Size:N0} bytes";
        };
        DbcView.RenderMeasured += (_, measurement) =>
        {
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_lastRenderReport, now).TotalMilliseconds < 500) return;
            _lastRenderReport = now;
            Dispatcher.UIThread.Post(() => RenderText.Text = $"Render {measurement.Milliseconds:0.00} ms · {measurement.VisibleRows} × {measurement.VisibleColumns} visible", DispatcherPriority.Background);
        };
    }

    public Task LoadPathAsync(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dbc", StringComparison.OrdinalIgnoreCase)
            ? LoadDbcAsync(path)
            : extension.Equals(".m2", StringComparison.OrdinalIgnoreCase)
                ? LoadM2Async(path)
                : ShowErrorAsync("Unsupported file", "The desktop preview currently opens .dbc and .m2 files.");
    }

    private async void OpenDbcClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a WotLK WDBC table",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WoW DBC tables") { Patterns = ["*.dbc"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await LoadDbcAsync(path);
    }

    private async Task LoadDbcAsync(string path)
    {
        SetBusy($"Loading {Path.GetFileName(path)}…");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var loaded = await Task.Run(() =>
            {
                var file = WdbcFile.Load(path);
                var tableName = Path.GetFileNameWithoutExtension(path);
                var resolution = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns(tableName, file.FieldCount);
                return (file, resolution);
            });
            _file = loaded.file;
            _columns = loaded.resolution.Columns;
            DbcView.SetDocument(_file, _columns);
            WelcomePanel.IsVisible = false;
            M2View.IsVisible = false;
            DbcView.IsVisible = true;
            DocumentTab.IsVisible = true;
            DocumentTabText.Text = Path.GetFileName(path) + (_file.IsDirty ? " *" : string.Empty);
            InspectorTitle.Text = Path.GetFileName(path);
            InspectorSummary.Text = $"{_file.RowCount:N0} records · {_file.FieldCount:N0} fields";
            InspectorDetail.Text = $"Container  WDBC\nRecord     {_file.RecordSize:N0} bytes\nStrings    {_file.StringTableSize:N0} bytes\nSchema     {loaded.resolution.MatchKind}\nSource     {path}";
            StatusText.Text = $"Loaded {_file.RowCount:N0} records in {stopwatch.Elapsed.TotalMilliseconds:0} ms";
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC open failed", exception);
            StatusText.Text = "Open failed";
            await ShowErrorAsync("Could not open DBC", exception.Message);
        }
    }

    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        SetBusy("Saving table with backup…");
        try
        {
            await Task.Run(() => _file.Save(_file.SourcePath, true));
            DocumentTabText.Text = Path.GetFileName(_file.SourcePath);
            StatusText.Text = "Saved safely; previous file retained as .bak";
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC save failed", exception);
            await ShowErrorAsync("Could not save DBC", exception.Message);
        }
    }

    private async void OpenM2Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Inspect a WotLK M2 model",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("WotLK M2 models") { Patterns = ["*.m2"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        await LoadM2Async(path);
    }

    private async Task LoadM2Async(string path)
    {
        SetBusy($"Reading {Path.GetFileName(path)}…");
        try
        {
            var geometry = await Task.Run(() => M2PreviewGeometryService.Load(path));
            WelcomePanel.IsVisible = false;
            DbcView.IsVisible = false;
            M2View.IsVisible = true;
            DocumentTab.IsVisible = true;
            DocumentTabText.Text = Path.GetFileName(path);
            M2View.SetGeometry(geometry);
            InspectorTitle.Text = Path.GetFileName(path);
            InspectorSummary.Text = $"{geometry.Vertices.Count:N0} vertices · {geometry.TriangleIndices.Count / 3:N0} triangles";
            InspectorDetail.Text = $"Model     {geometry.ModelPath}\nSkin      {geometry.SkinPath}\nMinimum   {geometry.Minimum}\nMaximum   {geometry.Maximum}";
            StatusText.Text = "Native model ready · drag to rotate · wheel to zoom";
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("M2 preview failed", exception);
            await ShowErrorAsync("Could not inspect model", exception.Message);
        }
    }

    private void OpenLogsClick(object? sender, RoutedEventArgs e) => DesktopCrashLogger.OpenDirectory();

    private async void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (_file is null) return;
        if (query.Length == 0)
        {
            DbcView.SetFilteredRows(null);
            StatusText.Text = $"Showing all {_file.RowCount:N0} records";
            return;
        }
        try
        {
            await Task.Delay(180, token);
            SetBusy($"Searching {_file.RowCount:N0} records…");
            var file = _file;
            var columns = _columns;
            var rows = await Task.Run(() =>
            {
                var matches = new List<int>();
                for (var row = 0; row < file.RowCount; row++)
                {
                    if ((row & 255) == 0) token.ThrowIfCancellationRequested();
                    if (file.RowContains(row, query, columns)) matches.Add(row);
                }
                return matches;
            }, token);
            if (token.IsCancellationRequested) return;
            DbcView.SetFilteredRows(rows);
            StatusText.Text = $"{rows.Count:N0} of {file.RowCount:N0} records match “{query}”";
        }
        catch (OperationCanceledException) { }
    }

    private void SetBusy(string message)
    {
        StatusText.Text = message;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(22),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 19, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button { Content = "Close", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right }
                }
            }
        };
        ((Button)((StackPanel)dialog.Content).Children[2]).Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
