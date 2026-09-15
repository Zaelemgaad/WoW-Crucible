using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private bool CommitPendingDbcEdits() => CommitInlineCellEdit(null) && RowEditor.TryCommitPending();

    private string? ApplyRowFieldEdit(DbcDocumentSession document, int row, DbcColumn column, string value)
    {
        var view = ReferenceEquals(DocumentForView(ActiveDbcView), document) ? ActiveDbcView
            : ReferenceEquals(DocumentForView(DbcView), document) ? DbcView : SecondaryDbcView;
        var edit = new Controls.DbcCellEditCommitEventArgs(row, column.Index, column, value);
        CommitCellEdit(document, view, edit, refreshSelection: false);
        return edit.Accepted ? null : edit.Error ?? "The field value was rejected.";
    }

    private void RememberDbcDirectory(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (_workspaceSession.Settings.LastDbcDirectory == directory) return;
        _workspaceSession.Settings.LastDbcDirectory = directory;
        _workspaceSession.Settings.Save();
    }

    private void RestoreDbcFilter(DbcDocumentSession document)
    {
        if (SearchBox.Text == document.FilterText) SearchChanged(SearchBox, new TextChangedEventArgs(TextBox.TextChangedEvent));
        else SearchBox.Text = document.FilterText;
    }

    private void ClearFilterClick(object? sender, RoutedEventArgs e) => ClearFilter();

    private void ToggleReplaceClick(object? sender, RoutedEventArgs e)
    {
        ReplaceControls.IsVisible = ReplaceToggle.IsChecked == true;
        if (ReplaceControls.IsVisible) OpenFindReplace(true);
    }

    private async void GoToIdClick(object? sender, RoutedEventArgs e) => await GoToRecordIdAsync();

    private async void GoToIdKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await GoToRecordIdAsync();
    }

    private async Task GoToRecordIdAsync()
    {
        if (!CommitPendingDbcEdits() || Current is not { } document) return;
        if (!uint.TryParse(GoToIdBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            StatusText.Text = "Enter a record ID between 0 and 4294967295.";
            GoToIdBox.Focus();
            return;
        }
        var view = ActiveDbcView;
        try
        {
            var rows = await Task.Run(() => DbcRecordIdentity.IndexRows(document.File, document.Schema.Columns, document.Schema.KeyStrategy));
            if (!ReferenceEquals(document, Current) || !ReferenceEquals(view, ActiveDbcView)) return;
            if (!rows.TryGetValue(id, out var row))
            {
                StatusText.Text = $"Record ID {id} was not found in {document.DisplayName}.";
                return;
            }
            ClearFilter();
            view.SelectSourceRow(row, document.Schema.KeyStrategy.ColumnIndex ?? 0);
            view.Focus();
            StatusText.Text = $"Record ID {id} - row {row + 1:N0} of {document.File.RowCount:N0}";
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC record lookup failed", exception);
            StatusText.Text = $"Cannot look up record ID: {exception.Message}";
        }
    }
}
