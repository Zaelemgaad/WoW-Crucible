using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

public sealed partial class VirtualDbcView
{
    private Cursor? _resizeCursor;

    private bool HandleColumnHeaderPressed(PointerPressedEventArgs e, Point position)
    {
        if (position.Y < 0 || position.Y >= HeaderHeight) return false;
        var edge = _layout.ResizeEdgeAt(position.X, _horizontalOffset);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed && edge is { } column)
        {
            _autoFitRevision++;
            if (e.ClickCount >= 2) _ = AutoFitColumnAsync(column);
            else
            {
                _resizingColumn = column;
                _resizeStartWidth = _layout.GetWidth(column);
                _pressPoint = position;
                _resizePointer = e.Pointer;
                e.Pointer.Capture(this);
                Cursor = _resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast);
            }
        }
        else if (properties.IsRightButtonPressed && (edge ?? _layout.HitColumn(position.X, _horizontalOffset)) is { } clicked)
        {
            var fit = new MenuItem { Header = "Auto-fit column" };
            fit.Click += async (_, _) => await AutoFitColumnAsync(clicked);
            var reset = new MenuItem { Header = "Reset column width" };
            reset.Click += (_, _) => ResetColumnWidths(clicked);
            var resetAll = new MenuItem { Header = "Reset all column widths" };
            resetAll.Click += (_, _) => ResetColumnWidths();
            ContextMenu?.Close();
            ContextMenu = new ContextMenu { Items = { fit, reset, new Separator(), resetAll } };
            ContextMenu.Open(this);
        }
        e.Handled = true;
        return true;
    }

    private void UpdateResizeCursor(Point position)
    {
        var onEdge = position.Y >= 0 && position.Y < HeaderHeight && _layout.ResizeEdgeAt(position.X, _horizontalOffset) is not null;
        Cursor = onEdge ? _resizeCursor ??= new Cursor(StandardCursorType.SizeWestEast) : null;
        ToolTip.SetTip(this, onEdge ? "Drag to resize; double-click to fit contents" : null);
    }

    private void FinishColumnResize(bool cancel = false)
    {
        if (_resizingColumn is not { } column) return;
        _resizingColumn = null;
        var pointer = _resizePointer;
        _resizePointer = null;
        if (cancel) _layout.SetWidth(column, _resizeStartWidth);
        pointer?.Capture(null);
        Cursor = null;
        ColumnWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        FinishColumnResize();
        _selectingRange = _pendingRangeDrag = false;
        _dragPress = null;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_resizingColumn is null) Cursor = null;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) UpdateViewport();
    }

    private void ColumnLayoutChanged(object? sender, EventArgs e)
    {
        _autoFitRevision++;
        UpdateViewport();
    }

    private void UpdateViewport()
    {
        ClampOffsets();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetColumnWidths(int? column = null)
    {
        _autoFitRevision++;
        _layout.Reset(column);
        ColumnWidthsChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AutoFitColumnAsync(int column)
    {
        var file = _file;
        if (file is null) return;
        var revision = ++_autoFitRevision;
        var pinned = column == DbcColumnLayout.RecordKey || column == _idColumnIndex;
        var header = column == DbcColumnLayout.RowHeader ? "Row" : pinned
            ? _keyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? "No record ID" : "Record ID"
            : _columns[column].Name;
        var width = MeasureColumnText(header, HeaderTypeface, 12);
        var rows = _filteredRows;
        var count = VisibleRowCount;
        try
        {
            // Yield between batches: a large spell/description column must not lock the editor.
            for (var displayRow = 0; displayRow < count; displayRow++)
            {
                var row = rows is null ? displayRow : rows[displayRow];
                var value = column == DbcColumnLayout.RowHeader ? (row + 1).ToString("N0", CultureInfo.InvariantCulture)
                    : pinned ? RecordKey(row) : CachedValue(row, column);
                width = Math.Max(width, MeasureColumnText(value, RegularTypeface, column == DbcColumnLayout.RowHeader ? 11 : 13));
                if ((displayRow & 127) == 127)
                {
                    await Dispatcher.UIThread.Resume(DispatcherPriority.Background);
                    if (revision != _autoFitRevision || !ReferenceEquals(file, _file)) return;
                }
            }
            if (revision != _autoFitRevision || !ReferenceEquals(file, _file)) return;
            _layout.SetWidth(column, Math.Ceiling(width) + 16);
            ColumnWidthsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) { InteractionFailed?.Invoke(this, exception); }
    }

    private static double MeasureColumnText(string text, Typeface typeface, double size) => new FormattedText(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, TextBrush)
    { MaxLineCount = 1 }.WidthIncludingTrailingWhitespace;
}
