using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

/// <summary>
/// A direct-rendered WDBC viewport. It creates no control per row or cell and
/// therefore keeps UI cost proportional to the visible rectangle, not the file.
/// </summary>
public sealed class VirtualDbcView : Control
{
    private const double HeaderHeight = 32;
    private const double RowHeight = 25;
    private const double RowNumberWidth = 58;
    private const double PinnedKeyWidth = 108;
    private const double FrozenWidth = RowNumberWidth + PinnedKeyWidth;
    private const double DefaultColumnWidth = 156;
    private const int TextCacheLimit = 4096;

    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#151C28"));
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#0D121A"));
    private static readonly IBrush AlternateRowBrush = new SolidColorBrush(Color.Parse("#101721"));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#263D58"));
    private static readonly IBrush SelectionCellBrush = new SolidColorBrush(Color.Parse("#315477"));
    private static readonly IBrush EditorBorderBrush = new SolidColorBrush(Color.Parse("#E0A33C"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#242D3D"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#D8DEE9"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#7E8A9F"));
    private static readonly IBrush HeaderTextBrush = new SolidColorBrush(Color.Parse("#BFC8D8"));
    private static readonly Typeface RegularTypeface = new("Inter");
    private static readonly Typeface HeaderTypeface = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private readonly Pen _gridPen = new(GridBrush, 1);
    private readonly Pen _selectionPen = new(EditorBorderBrush, 2);
    private readonly Dictionary<long, string> _displayCache = new();

    private WdbcFile? _file;
    private IReadOnlyList<DbcColumn> _columns = [];
    private IReadOnlyList<int> _scrollColumnIndices = [];
    private DbcRecordKeyStrategy _keyStrategy = DbcRecordKeyStrategy.None;
    private int _idColumnIndex = -1;
    private IReadOnlyList<int>? _filteredRows;
    private string _tableName = string.Empty;
    private bool _decoded = true;
    private double _verticalOffset;
    private double _horizontalOffset;
    private int _selectedDisplayRow = -1;
    private int _selectedColumn = -1;
    private bool _selectedPinned;

    public event EventHandler<DbcSelectionEventArgs>? SelectionChanged;
    public event EventHandler<DbcCellEditRequestEventArgs>? CellEditRequested;
    public event EventHandler<ViewportPerformanceEventArgs>? RenderMeasured;

    public WdbcFile? File => _file;
    public IReadOnlyList<DbcColumn> Columns => _columns;
    public int VisibleRowCount => _filteredRows?.Count ?? _file?.RowCount ?? 0;
    public int SelectedSourceRow => _selectedDisplayRow < 0 ? -1 : _filteredRows is null ? _selectedDisplayRow : _filteredRows[_selectedDisplayRow];
    public int SelectedColumn => _selectedColumn;
    public double VerticalOffset => _verticalOffset;
    public double HorizontalOffset => _horizontalOffset;
    public double VerticalMaximum => Math.Max(0, VisibleRowCount * RowHeight - Math.Max(0, Bounds.Height - HeaderHeight));
    public double HorizontalMaximum => Math.Max(0, _scrollColumnIndices.Count * DefaultColumnWidth - Math.Max(0, Bounds.Width - FrozenWidth));

    public VirtualDbcView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetDocument(WdbcFile file, IReadOnlyList<DbcColumn> columns, DbcRecordKeyStrategy keyStrategy, string tableName, bool decoded)
    {
        _file = file;
        _columns = columns;
        _keyStrategy = keyStrategy;
        _idColumnIndex = keyStrategy.Kind == DbcRecordKeyKind.PhysicalColumn && keyStrategy.ColumnIndex is >= 0 &&
                         keyStrategy.ColumnIndex < columns.Count ? keyStrategy.ColumnIndex.Value : -1;
        _scrollColumnIndices = Enumerable.Range(0, columns.Count).Where(index => index != _idColumnIndex).ToArray();
        _tableName = tableName;
        _decoded = decoded;
        _filteredRows = null;
        _verticalOffset = 0;
        _horizontalOffset = 0;
        _selectedDisplayRow = -1;
        _selectedColumn = -1;
        _selectedPinned = false;
        _displayCache.Clear();
        InvalidateVisual();
    }

    public void SetDecoded(bool decoded)
    {
        if (_decoded == decoded) return;
        _decoded = decoded;
        _displayCache.Clear();
        InvalidateVisual();
    }

    public void RefreshDocument(int selectedSourceRow = -1)
    {
        _displayCache.Clear();
        if (selectedSourceRow >= 0)
        {
            _selectedDisplayRow = _filteredRows is null ? selectedSourceRow : IndexOf(_filteredRows, selectedSourceRow);
            if (_selectedDisplayRow >= 0)
                _verticalOffset = Math.Max(0, _selectedDisplayRow * RowHeight - Math.Max(0, Bounds.Height - HeaderHeight) * 0.45);
        }
        ClampOffsets();
        InvalidateVisual();
    }

    public void SelectSourceRow(int sourceRow, int column = 0)
    {
        if (_file is null || sourceRow < 0 || sourceRow >= _file.RowCount) return;
        _selectedDisplayRow = _filteredRows is null ? sourceRow : IndexOf(_filteredRows, sourceRow);
        if (_selectedDisplayRow < 0) { _filteredRows = null; _selectedDisplayRow = sourceRow; }
        _selectedColumn = _columns.Count == 0 ? -1 : Math.Clamp(column, 0, _columns.Count - 1);
        _selectedPinned = _selectedColumn == _idColumnIndex;
        _verticalOffset = Math.Max(0, _selectedDisplayRow * RowHeight - Math.Max(0, Bounds.Height - HeaderHeight) * 0.45);
        EnsureSelectionVisible(); ClampOffsets(); InvalidateVisual();
        if (_selectedColumn >= 0) SelectionChanged?.Invoke(this, new(sourceRow, _selectedColumn, _columns[_selectedColumn], CachedValue(sourceRow, _selectedColumn)));
    }

    public void SetFilteredRows(IReadOnlyList<int>? rows)
    {
        _filteredRows = rows;
        _verticalOffset = 0;
        _selectedDisplayRow = -1;
        _displayCache.Clear();
        InvalidateVisual();
    }

    public void SetScrollOffsets(double horizontal, double vertical)
    {
        _horizontalOffset = horizontal;
        _verticalOffset = vertical;
        ClampOffsets();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var started = Stopwatch.GetTimestamp();
        base.Render(context);
        context.FillRectangle(RowBrush, Bounds);
        if (_file is null || _columns.Count == 0 || Bounds.Width <= FrozenWidth || Bounds.Height <= HeaderHeight)
            return;

        ClampOffsets();
        var firstDisplayRow = Math.Max(0, (int)(_verticalOffset / RowHeight));
        var partialY = _verticalOffset % RowHeight;
        var displayedRows = Math.Min(VisibleRowCount - firstDisplayRow, (int)Math.Ceiling((Bounds.Height - HeaderHeight + partialY) / RowHeight));
        var firstColumn = Math.Max(0, (int)(_horizontalOffset / DefaultColumnWidth));
        var partialX = _horizontalOffset % DefaultColumnWidth;
        var displayedColumns = Math.Min(_scrollColumnIndices.Count - firstColumn, (int)Math.Ceiling((Bounds.Width - FrozenWidth + partialX) / DefaultColumnWidth));

        context.FillRectangle(HeaderBrush, new Rect(0, 0, Bounds.Width, HeaderHeight));
        DrawText(context, "ROW", 8, 8, HeaderTextBrush, HeaderTypeface, 10);
        context.DrawLine(_gridPen, new Point(RowNumberWidth, 0), new Point(RowNumberWidth, Bounds.Height));
        DrawText(context, _keyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? "ID UNAVAILABLE" : "RECORD ID", RowNumberWidth + 8, 8, HeaderTextBrush, HeaderTypeface, 10);
        context.DrawLine(_gridPen, new Point(FrozenWidth, 0), new Point(FrozenWidth, Bounds.Height));

        for (var visibleColumn = 0; visibleColumn < displayedColumns; visibleColumn++)
        {
            var columnIndex = _scrollColumnIndices[firstColumn + visibleColumn];
            var x = FrozenWidth - partialX + visibleColumn * DefaultColumnWidth;
            context.DrawLine(_gridPen, new Point(x, 0), new Point(x, Bounds.Height));
            DrawText(context, Trim(_columns[columnIndex].Name, 22), x + 8, 8, HeaderTextBrush, HeaderTypeface, 10.5);
        }
        context.DrawLine(_gridPen, new Point(0, HeaderHeight), new Point(Bounds.Width, HeaderHeight));

        for (var visibleRow = 0; visibleRow < displayedRows; visibleRow++)
        {
            var displayRow = firstDisplayRow + visibleRow;
            var sourceRow = _filteredRows is null ? displayRow : _filteredRows[displayRow];
            var y = HeaderHeight - partialY + visibleRow * RowHeight;
            var background = displayRow == _selectedDisplayRow ? SelectionBrush : (sourceRow & 1) == 0 ? RowBrush : AlternateRowBrush;
            context.FillRectangle(background, new Rect(0, y, Bounds.Width, RowHeight));
            DrawText(context, (sourceRow + 1).ToString("N0", CultureInfo.InvariantCulture), 8, y + 6, MutedTextBrush, RegularTypeface, 10);
            if (displayRow == _selectedDisplayRow && _selectedPinned)
                context.FillRectangle(SelectionCellBrush, new Rect(RowNumberWidth, y, PinnedKeyWidth, RowHeight));
            DrawText(context, RecordKey(sourceRow), RowNumberWidth + 8, y + 5, _keyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? MutedTextBrush : TextBrush, RegularTypeface, 11);
            if (displayRow == _selectedDisplayRow && _selectedPinned)
                context.DrawRectangle(_selectionPen, new Rect(RowNumberWidth + 1, y + 1, PinnedKeyWidth - 2, RowHeight - 2));

            for (var visibleColumn = 0; visibleColumn < displayedColumns; visibleColumn++)
            {
                var columnIndex = _scrollColumnIndices[firstColumn + visibleColumn];
                var x = FrozenWidth - partialX + visibleColumn * DefaultColumnWidth;
                if (displayRow == _selectedDisplayRow && !_selectedPinned && columnIndex == _selectedColumn)
                    context.FillRectangle(SelectionCellBrush, new Rect(x, y, DefaultColumnWidth, RowHeight));
                var value = CachedValue(sourceRow, columnIndex);
                DrawText(context, Trim(value, 25), x + 8, y + 5, TextBrush, RegularTypeface, 11);
                if (displayRow == _selectedDisplayRow && !_selectedPinned && columnIndex == _selectedColumn)
                    context.DrawRectangle(_selectionPen, new Rect(x + 1, y + 1, DefaultColumnWidth - 2, RowHeight - 2));
            }
            context.DrawLine(_gridPen, new Point(0, y + RowHeight), new Point(Bounds.Width, y + RowHeight));
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RenderMeasured?.Invoke(this, new(elapsed, displayedRows, displayedColumns + 1));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            _horizontalOffset -= e.Delta.Y * DefaultColumnWidth * 0.8;
        else
            _verticalOffset -= e.Delta.Y * RowHeight * 3;
        ClampOffsets();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_file is null) return;
        Focus();
        var position = e.GetPosition(this);
        if (position.Y < HeaderHeight || position.X < RowNumberWidth) return;
        var displayRow = (int)((position.Y - HeaderHeight + _verticalOffset) / RowHeight);
        if (displayRow < 0 || displayRow >= VisibleRowCount) return;
        var pinned = position.X < FrozenWidth;
        int column;
        if (pinned)
        {
            if (_idColumnIndex < 0) return;
            column = _idColumnIndex;
        }
        else
        {
            var scrollColumn = (int)((position.X - FrozenWidth + _horizontalOffset) / DefaultColumnWidth);
            if (scrollColumn < 0 || scrollColumn >= _scrollColumnIndices.Count) return;
            column = _scrollColumnIndices[scrollColumn];
        }
        _selectedDisplayRow = displayRow;
        _selectedColumn = column;
        _selectedPinned = pinned;
        var sourceRow = _filteredRows is null ? displayRow : _filteredRows[displayRow];
        SelectionChanged?.Invoke(this, new(sourceRow, column, _columns[column], CachedValue(sourceRow, column)));
        if (e.ClickCount >= 2) RequestEdit(sourceRow, column, pinned);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_file is null) return;
        if (e.Key is Key.Enter or Key.F2 && _selectedDisplayRow >= 0 && _selectedColumn >= 0)
        {
            RequestEdit(SelectedSourceRow, _selectedColumn, _selectedPinned);
            e.Handled = true;
            return;
        }
        if (_selectedDisplayRow >= 0 && _selectedColumn >= 0 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Tab)
        {
            var move = e.Key switch
            {
                Key.Left => DbcCellMove.PreviousColumn,
                Key.Right => DbcCellMove.NextColumn,
                Key.Up => DbcCellMove.PreviousRow,
                Key.Down => DbcCellMove.NextRow,
                Key.Tab when e.KeyModifiers.HasFlag(KeyModifiers.Shift) => DbcCellMove.PreviousColumn,
                _ => DbcCellMove.NextColumn
            };
            MoveSelection(move);
            e.Handled = true;
            return;
        }
        var delta = e.Key switch
        {
            Key.PageDown => Math.Max(RowHeight, Bounds.Height - HeaderHeight),
            Key.PageUp => -Math.Max(RowHeight, Bounds.Height - HeaderHeight),
            Key.Home => -double.MaxValue,
            Key.End => double.MaxValue,
            _ => 0
        };
        if (delta == 0) return;
        _verticalOffset += delta;
        ClampOffsets();
        InvalidateVisual();
        e.Handled = true;
    }

    public void BeginSelectedEdit()
    {
        if (_selectedDisplayRow < 0 || _selectedColumn < 0) return;
        RequestEdit(SelectedSourceRow, _selectedColumn, _selectedPinned);
    }

    private void RequestEdit(int sourceRow, int column, bool pinned)
    {
        if (_file is null || sourceRow < 0 || sourceRow >= _file.RowCount || column < 0 || column >= _columns.Count) return;
        _selectedDisplayRow = _filteredRows is null ? sourceRow : IndexOf(_filteredRows, sourceRow);
        _selectedColumn = column;
        _selectedPinned = pinned && column == _idColumnIndex;
        EnsureSelectionVisible();
        InvalidateVisual();
        var bounds = CellBounds(sourceRow, column, _selectedPinned);
        CellEditRequested?.Invoke(this, new(new(sourceRow, column, _columns[column], CachedValue(sourceRow, column)), bounds));
    }

    public void MoveSelection(DbcCellMove move)
    {
        if (_file is null || _selectedDisplayRow < 0) return;
        var order = _idColumnIndex >= 0
            ? new[] { _idColumnIndex }.Concat(_scrollColumnIndices).ToArray()
            : _scrollColumnIndices.ToArray();
        if (order.Length == 0) return;
        var orderIndex = Array.IndexOf(order, _selectedColumn);
        if (orderIndex < 0) orderIndex = 0;
        switch (move)
        {
            case DbcCellMove.NextColumn:
                orderIndex++;
                if (orderIndex >= order.Length) { orderIndex = 0; _selectedDisplayRow = Math.Min(VisibleRowCount - 1, _selectedDisplayRow + 1); }
                break;
            case DbcCellMove.PreviousColumn:
                orderIndex--;
                if (orderIndex < 0) { orderIndex = order.Length - 1; _selectedDisplayRow = Math.Max(0, _selectedDisplayRow - 1); }
                break;
            case DbcCellMove.NextRow:
                _selectedDisplayRow = Math.Min(VisibleRowCount - 1, _selectedDisplayRow + 1);
                break;
            case DbcCellMove.PreviousRow:
                _selectedDisplayRow = Math.Max(0, _selectedDisplayRow - 1);
                break;
        }
        _selectedColumn = order[orderIndex];
        _selectedPinned = _selectedColumn == _idColumnIndex;
        EnsureSelectionVisible();
        var sourceRow = SelectedSourceRow;
        SelectionChanged?.Invoke(this, new(sourceRow, _selectedColumn, _columns[_selectedColumn], CachedValue(sourceRow, _selectedColumn)));
        InvalidateVisual();
    }

    private void EnsureSelectionVisible()
    {
        if (_selectedDisplayRow < 0 || _selectedColumn < 0) return;
        var visibleHeight = Math.Max(RowHeight, Bounds.Height - HeaderHeight);
        var rowTop = _selectedDisplayRow * RowHeight;
        if (rowTop < _verticalOffset) _verticalOffset = rowTop;
        else if (rowTop + RowHeight > _verticalOffset + visibleHeight) _verticalOffset = rowTop + RowHeight - visibleHeight;
        if (!_selectedPinned)
        {
            var scrollIndex = IndexOf(_scrollColumnIndices, _selectedColumn);
            if (scrollIndex >= 0)
            {
                var visibleWidth = Math.Max(DefaultColumnWidth, Bounds.Width - FrozenWidth);
                var columnLeft = scrollIndex * DefaultColumnWidth;
                if (columnLeft < _horizontalOffset) _horizontalOffset = columnLeft;
                else if (columnLeft + DefaultColumnWidth > _horizontalOffset + visibleWidth) _horizontalOffset = columnLeft + DefaultColumnWidth - visibleWidth;
            }
        }
        ClampOffsets();
    }

    private Rect CellBounds(int sourceRow, int column, bool pinned)
    {
        var displayRow = _filteredRows is null ? sourceRow : IndexOf(_filteredRows, sourceRow);
        if (displayRow < 0) return default;
        var y = HeaderHeight + displayRow * RowHeight - _verticalOffset;
        double x;
        double width;
        if (pinned)
        {
            x = RowNumberWidth;
            width = PinnedKeyWidth;
        }
        else
        {
            var scrollIndex = IndexOf(_scrollColumnIndices, column);
            if (scrollIndex < 0) return default;
            x = FrozenWidth + scrollIndex * DefaultColumnWidth - _horizontalOffset;
            width = DefaultColumnWidth;
        }
        return new Rect(x, y, width, RowHeight);
    }

    private string RecordKey(int sourceRow)
    {
        if (_file is null || _keyStrategy.Kind == DbcRecordKeyKind.NoStableKey) return "—";
        try { return DbcRecordIdentity.GetKey(_file, sourceRow, _columns, _keyStrategy).ToString("N0", CultureInfo.InvariantCulture); }
        catch { return "INVALID"; }
    }

    private string CachedValue(int sourceRow, int columnIndex)
    {
        var key = ((long)sourceRow << 32) | (uint)columnIndex;
        if (_displayCache.TryGetValue(key, out var value)) return value;
        var column = _columns[columnIndex];
        var semantic = _decoded ? DbcSemanticCatalog.Get(_tableName, column.Index, _file, sourceRow) : null;
        value = semantic?.Format(_file!.GetRaw(sourceRow, column))
            ?? Convert.ToString(_file!.GetDisplayValue(sourceRow, column), CultureInfo.InvariantCulture)
            ?? string.Empty;
        if (_displayCache.Count >= TextCacheLimit) _displayCache.Clear();
        _displayCache[key] = value;
        return value;
    }

    private void ClampOffsets()
    {
        _verticalOffset = Math.Clamp(_verticalOffset, 0, VerticalMaximum);
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0, HorizontalMaximum);
    }

    private static void DrawText(DrawingContext context, string text, double x, double y, IBrush brush, Typeface typeface, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);
        context.DrawText(formatted, new Point(x, y));
    }

    private static string Trim(string value, int maximum) => value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum - 1), "…");

    private static int IndexOf(IReadOnlyList<int> rows, int sourceRow)
    {
        for (var index = 0; index < rows.Count; index++)
            if (rows[index] == sourceRow) return index;
        return -1;
    }

}

public sealed record DbcSelectionEventArgs(int Row, int ColumnIndex, DbcColumn Column, string Value);
public sealed record DbcCellEditRequestEventArgs(DbcSelectionEventArgs Selection, Rect Bounds);
public enum DbcCellMove { NextColumn, PreviousColumn, NextRow, PreviousRow }
public sealed class DbcCellEditCommitEventArgs(int row, int columnIndex, DbcColumn column, string value)
{
    public int Row { get; } = row;
    public int ColumnIndex { get; } = columnIndex;
    public DbcColumn Column { get; } = column;
    public string Value { get; } = value;
    public bool Accepted { get; set; }
    public string? Error { get; set; }
}
public sealed record ViewportPerformanceEventArgs(double Milliseconds, int VisibleRows, int VisibleColumns);
