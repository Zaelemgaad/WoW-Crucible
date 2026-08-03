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
    private int _rangeAnchorDisplayRow = -1;
    private int _rangeEndDisplayRow = -1;
    private int _rangeAnchorOrder = -1;
    private int _rangeEndOrder = -1;
    private bool _selectingRange;
    private bool _pendingRangeDrag;
    private bool _selectingWholeRows;
    private Point _pressPoint;
    private PointerPressedEventArgs? _dragPress;

    public event EventHandler<DbcSelectionEventArgs>? SelectionChanged;
    public event EventHandler<DbcCellEditRequestEventArgs>? CellEditRequested;
    public event EventHandler<DbcRangeDragRequestEventArgs>? RangeDragRequested;
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
        ClearRangeSelection();
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
        SetSingleCellRange(_selectedDisplayRow, _selectedColumn);
        _verticalOffset = Math.Max(0, _selectedDisplayRow * RowHeight - Math.Max(0, Bounds.Height - HeaderHeight) * 0.45);
        EnsureSelectionVisible(); ClampOffsets(); InvalidateVisual();
        if (_selectedColumn >= 0) SelectionChanged?.Invoke(this, new(sourceRow, _selectedColumn, _columns[_selectedColumn], CachedValue(sourceRow, _selectedColumn)));
    }

    public void SetFilteredRows(IReadOnlyList<int>? rows)
    {
        _filteredRows = rows;
        _verticalOffset = 0;
        _selectedDisplayRow = -1;
        ClearRangeSelection();
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
            var wholeRowSelected = IsRangeCellSelected(displayRow, _columns.Count == 0 ? -1 : VisualColumnOrder()[0]) && SelectedColumnIndices().Count == _columns.Count;
            var background = wholeRowSelected ? SelectionBrush : (sourceRow & 1) == 0 ? RowBrush : AlternateRowBrush;
            context.FillRectangle(background, new Rect(0, y, Bounds.Width, RowHeight));
            DrawText(context, (sourceRow + 1).ToString("N0", CultureInfo.InvariantCulture), 8, y + 6, MutedTextBrush, RegularTypeface, 10);
            if (IsRangeCellSelected(displayRow, _idColumnIndex))
                context.FillRectangle(SelectionCellBrush, new Rect(RowNumberWidth, y, PinnedKeyWidth, RowHeight));
            DrawText(context, RecordKey(sourceRow), RowNumberWidth + 8, y + 5, _keyStrategy.Kind == DbcRecordKeyKind.NoStableKey ? MutedTextBrush : TextBrush, RegularTypeface, 11);
            if (displayRow == _selectedDisplayRow && _selectedPinned)
                context.DrawRectangle(_selectionPen, new Rect(RowNumberWidth + 1, y + 1, PinnedKeyWidth - 2, RowHeight - 2));

            for (var visibleColumn = 0; visibleColumn < displayedColumns; visibleColumn++)
            {
                var columnIndex = _scrollColumnIndices[firstColumn + visibleColumn];
                var x = FrozenWidth - partialX + visibleColumn * DefaultColumnWidth;
                if (IsRangeCellSelected(displayRow, columnIndex))
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
        if (!TryHit(position, out var displayRow, out var column, out var pinned, out var rowHeader)) return;
        var order = VisualColumnOrder();
        var orderIndex = rowHeader ? 0 : Array.IndexOf(order, column);
        if (orderIndex < 0) return;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && SelectionContains(displayRow, rowHeader ? order[0] : column))
        {
            _pendingRangeDrag = true;
            _pressPoint = position;
            _dragPress = e;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || _rangeAnchorDisplayRow < 0)
        {
            _rangeAnchorDisplayRow = displayRow;
            _rangeAnchorOrder = rowHeader ? 0 : orderIndex;
        }
        _rangeEndDisplayRow = displayRow;
        _rangeEndOrder = rowHeader ? order.Length - 1 : orderIndex;
        _selectingWholeRows = rowHeader;
        _selectingRange = true;
        _pressPoint = position;
        _dragPress = e;
        e.Pointer.Capture(this);
        _selectedDisplayRow = displayRow;
        _selectedColumn = rowHeader ? order[^1] : column;
        _selectedPinned = !rowHeader && pinned;
        var sourceRow = _filteredRows is null ? displayRow : _filteredRows[displayRow];
        SelectionChanged?.Invoke(this, new(sourceRow, _selectedColumn, _columns[_selectedColumn], CachedValue(sourceRow, _selectedColumn)));
        if (!rowHeader && e.ClickCount >= 2) RequestEdit(sourceRow, column, pinned);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_file is null) return;
        var position = e.GetPosition(this);
        if (_pendingRangeDrag && _dragPress is not null && Math.Abs(position.X - _pressPoint.X) + Math.Abs(position.Y - _pressPoint.Y) >= 6)
        {
            _pendingRangeDrag = false;
            e.Pointer.Capture(null);
            var selection = GetRangeSelection();
            if (selection is not null) RangeDragRequested?.Invoke(this, new(_dragPress, selection));
            e.Handled = true;
            return;
        }
        if (!_selectingRange || !TryHit(position, out var displayRow, out var column, out _, out _)) return;
        var order = VisualColumnOrder();
        _rangeEndDisplayRow = displayRow;
        _rangeEndOrder = _selectingWholeRows ? order.Length - 1 : Array.IndexOf(order, column);
        _selectedDisplayRow = displayRow;
        _selectedColumn = _selectingWholeRows ? order[^1] : column;
        _selectedPinned = _selectedColumn == _idColumnIndex;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selectingRange = false;
        _pendingRangeDrag = false;
        _dragPress = null;
        e.Pointer.Capture(null);
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

    public DbcRangeSelection? GetRangeSelection()
    {
        if (_file is null || _rangeAnchorDisplayRow < 0 || _rangeEndDisplayRow < 0) return null;
        var firstRow = Math.Min(_rangeAnchorDisplayRow, _rangeEndDisplayRow);
        var lastRow = Math.Max(_rangeAnchorDisplayRow, _rangeEndDisplayRow);
        var rows = Enumerable.Range(firstRow, lastRow - firstRow + 1)
            .Select(displayRow => _filteredRows is null ? displayRow : _filteredRows[displayRow]).ToArray();
        return new(rows, SelectedColumnIndices());
    }

    public int SourceRowAt(Point position)
    {
        if (!TryHit(position, out var displayRow, out _, out _, out _)) return Math.Max(0, SelectedSourceRow);
        return _filteredRows is null ? displayRow : _filteredRows[displayRow];
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
        SetSingleCellRange(_selectedDisplayRow, _selectedColumn);
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

    private bool TryHit(Point position, out int displayRow, out int column, out bool pinned, out bool rowHeader)
    {
        displayRow = -1; column = -1; pinned = false; rowHeader = false;
        if (position.Y < HeaderHeight) return false;
        displayRow = (int)((position.Y - HeaderHeight + _verticalOffset) / RowHeight);
        if (displayRow < 0 || displayRow >= VisibleRowCount) return false;
        if (position.X < RowNumberWidth)
        {
            var order = VisualColumnOrder();
            if (order.Length == 0) return false;
            column = order[0]; rowHeader = true; return true;
        }
        pinned = position.X < FrozenWidth;
        if (pinned)
        {
            if (_idColumnIndex < 0) return false;
            column = _idColumnIndex; return true;
        }
        var scrollColumn = (int)((position.X - FrozenWidth + _horizontalOffset) / DefaultColumnWidth);
        if (scrollColumn < 0 || scrollColumn >= _scrollColumnIndices.Count) return false;
        column = _scrollColumnIndices[scrollColumn]; return true;
    }

    private int[] VisualColumnOrder() => _idColumnIndex >= 0
        ? new[] { _idColumnIndex }.Concat(_scrollColumnIndices).ToArray()
        : _scrollColumnIndices.ToArray();

    private IReadOnlyList<int> SelectedColumnIndices()
    {
        var order = VisualColumnOrder();
        if (order.Length == 0 || _rangeAnchorOrder < 0 || _rangeEndOrder < 0) return [];
        var first = Math.Clamp(Math.Min(_rangeAnchorOrder, _rangeEndOrder), 0, order.Length - 1);
        var last = Math.Clamp(Math.Max(_rangeAnchorOrder, _rangeEndOrder), 0, order.Length - 1);
        return order[first..(last + 1)];
    }

    private bool SelectionContains(int displayRow, int column)
    {
        if (_rangeAnchorDisplayRow < 0 || _rangeEndDisplayRow < 0) return false;
        if (displayRow < Math.Min(_rangeAnchorDisplayRow, _rangeEndDisplayRow) || displayRow > Math.Max(_rangeAnchorDisplayRow, _rangeEndDisplayRow)) return false;
        return SelectedColumnIndices().Contains(column);
    }

    private bool IsRangeCellSelected(int displayRow, int column) => column >= 0 && SelectionContains(displayRow, column);

    private void SetSingleCellRange(int displayRow, int column)
    {
        var order = VisualColumnOrder();
        var orderIndex = Array.IndexOf(order, column);
        _rangeAnchorDisplayRow = _rangeEndDisplayRow = displayRow;
        _rangeAnchorOrder = _rangeEndOrder = orderIndex;
        _selectingWholeRows = false;
    }

    private void ClearRangeSelection()
    {
        _rangeAnchorDisplayRow = _rangeEndDisplayRow = -1;
        _rangeAnchorOrder = _rangeEndOrder = -1;
        _selectingRange = _pendingRangeDrag = _selectingWholeRows = false;
        _dragPress = null;
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
public sealed record DbcRangeSelection(IReadOnlyList<int> SourceRows, IReadOnlyList<int> ColumnIndices);
public sealed record DbcRangeDragRequestEventArgs(PointerPressedEventArgs Trigger, DbcRangeSelection Selection);
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
