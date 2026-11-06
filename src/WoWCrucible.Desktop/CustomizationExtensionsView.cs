using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

/// <summary>The user's customization rows for benilla and the server: list, validate, export. Adding a row is the CLI's.</summary>
internal sealed class CustomizationExtensionsView : UserControl
{
    private readonly TextBox _folder = new() { PlaceholderText = "Folder holding merged_*.tsv and user/" };
    private readonly TextBox _export = new() { PlaceholderText = "Server's customization folder, to export to" };
    private readonly ListBox _rows = new();
    private readonly ListBox _problems = new();
    private readonly TextBlock _summary = new() { Text = "Choose the customization folder.", TextWrapping = TextWrapping.Wrap };

    public event EventHandler? BackRequested;

    public CustomizationExtensionsView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var browse = new Button { Content = "Folder…" }; browse.Click += async (_, _) => await ChooseAsync(_folder);
        var check = new Button { Content = "Validate" }; check.Click += (_, _) => Refresh();
        var browseExport = new Button { Content = "Folder…" }; browseExport.Click += async (_, _) => await ChooseAsync(_export);
        var export = new Button { Content = "Export" }; export.Click += (_, _) => Export();
        var heading = new WrapPanel { Children = { back, new TextBlock { Text = "CUSTOMIZATION EXTENSIONS", FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center } } };
        heading.Classes.Add("workspace-toolbar");
        var path = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _folder, At(browse, 1), At(check, 2) } };
        var target = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { _export, At(browseExport, 1), At(export, 2) } };
        var lists = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _rows, At(_problems, 1) } };
        Grid.SetRow(path, 1); Grid.SetRow(target, 2); Grid.SetRow(_summary, 3); Grid.SetRow(lists, 4);
        Content = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*"), Margin = new Thickness(12), RowSpacing = 8, Children = { heading, path, target, _summary, lists } };
    }

    private static T At<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }

    private async Task ChooseAsync(TextBox into)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } folder) return;
        into.Text = folder;
        if (into == _folder) Refresh();
    }

    private void Refresh()
    {
        var folder = _folder.Text?.Trim() ?? "";
        if (!Directory.Exists(folder)) { _summary.Text = "No such folder."; return; }
        try
        {
            _rows.ItemsSource = CustomizationExtensionService.Columns.Keys
                .SelectMany(file => CustomizationExtensionService.ReadRows(folder, file))
                .Select(row => $"{row.File}:{row.Line}  {string.Join("  ", row.Cells.Values.Where(value => value.Length > 0))}")
                .ToArray();
            var problems = CustomizationExtensionService.Validate(folder);
            _problems.ItemsSource = problems.Select(problem => problem.ToString()).ToArray();
            var errors = problems.Count(problem => problem.Severity == "ERROR");
            _summary.Text = errors == 0 ? $"Valid, with {problems.Count:N0} note(s)." : $"{errors:N0} error(s). The game leaves those rows out.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _summary.Text = ex.Message; }
    }

    private void Export()
    {
        var folder = _folder.Text?.Trim() ?? ""; var target = _export.Text?.Trim() ?? "";
        if (!Directory.Exists(folder) || target.Length == 0) { _summary.Text = "Choose both folders."; return; }
        try
        {
            var written = CustomizationExtensionService.Export(folder, target, out var problems);
            _problems.ItemsSource = problems.Select(problem => problem.ToString()).ToArray();
            _summary.Text = written.Count > 0 ? $"Exported {written.Count:N0} file(s)." : problems.Any(problem => problem.Severity == "ERROR") ? "Not exported: fix the errors first." : "Nothing to export.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _summary.Text = ex.Message; }
    }
}
