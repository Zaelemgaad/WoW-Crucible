using Avalonia;
using Avalonia.Media;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

public partial class MainWindow
{
    private void SaveDbcColumnWidths(VirtualDbcView view)
    {
        var document = _documents.FirstOrDefault(item => ReferenceEquals(item.ColumnLayout, view.ColumnLayout));
        if (document is null) return;
        var widths = document.ColumnLayout.ExportWidths();
        if (widths.Count == 0) _workspaceSession.Settings.DbcColumnWidths.Remove(document.ColumnLayoutKey);
        else _workspaceSession.Settings.DbcColumnWidths[document.ColumnLayoutKey] = widths;
        try { _workspaceSession.Settings.Save(); }
        catch (Exception exception)
        {
            DesktopCrashLogger.Log("DBC column widths could not be saved", exception);
            StatusText.Text = $"Column widths could not be remembered: {exception.Message}";
        }
    }

    private void PositionInlineCellEditor()
    {
        if (_dbcEditingEditor is not { } editor || _dbcEditingView is not { } view || _dbcEditingSelection is not { } selection) return;
        var bounds = view.GetCellBounds(selection.Row, selection.ColumnIndex);
        editor.Margin = new Thickness(bounds.X, bounds.Y, 0, 0);
        editor.Width = bounds.Width;
        editor.Height = bounds.Height;
        var key = _dbcEditingDocument?.Schema.KeyStrategy;
        var pinned = key?.Kind == DbcRecordKeyKind.PhysicalColumn && key.ColumnIndex == selection.ColumnIndex;
        var left = Math.Max(bounds.Left, pinned ? view.ColumnLayout.RowHeaderWidth : view.FrozenWidth);
        var top = Math.Max(bounds.Top, 32);
        var right = Math.Min(bounds.Right, view.Bounds.Width);
        var bottom = Math.Min(bounds.Bottom, view.Bounds.Height);
        editor.Clip = new RectangleGeometry(new Rect(left - bounds.Left, top - bounds.Top, Math.Max(0, right - left), Math.Max(0, bottom - top)));
    }
}
