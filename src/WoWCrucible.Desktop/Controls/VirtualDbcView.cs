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
    private const double DefaultColumnWidth = 156;
    private const int TextCacheLimit = 4096;

    private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#151C28"));
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#0D121A"));
    private static readonly IBrush AlternateRowBrush = new SolidColorBrush(Color.Parse("#101721"));
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.Parse("#263D58"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#242D3D"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#D8DEE9"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#7E8A9F"));
    private static readonly IBrush HeaderTextBrush = new SolidColorBrush(Color.Parse("#BFC8D8"));
    private static readonly Typeface RegularTypeface = new("Inter");
    private static readonly Typeface HeaderTypeface = new("Inter", FontStyle.Normal, FontWeight.SemiBold);
    private readonly Pen _gridPen = new(GridBrush, 1);
    private readonly Dictionary<long, string> _displayCache = new();

    private WdbcFile? _file;
    private IReadOnlyList<DbcColumn> _columns = [];
    private IReadOnlyList<int>? _filteredRows;
    private string _tableName = string.Empty;
    private bool _decoded = true;
    private double _verticalOffset;
    private double _horizontalOffset;
    private int _selectedDisplayRow = -1;
    private int _selectedColumn = -1;

    public event EventHandler<DbcSelectionEventArgs>? SelectionChanged;
    public event EventHandler<DbcSelectionEventArgs>? CellEditRequested;
    public event EventHandler<ViewportPerformanceEventArgs>? RenderMeasured;

    public WdbcFile? File => _file;
    public IReadOnlyList<DbcColumn> Columns => _columns;
    public int VisibleRowCount => _filteredRows?.Count ?? _file?.RowCount ?? 0;
    public int SelectedSourceRow => _selectedDisplayRow < 0 ? -1 : _filteredRows is null ? _selectedDisplayRow : _filteredRows[_selectedDisplayRow];
    public int SelectedColumn => _selectedColumn;
    public double VerticalOffset => _verticalOffset;
    public double HorizontalOffset => _horizontalOffset;
    public double VerticalMaximum => Math.Max(0, VisibleRowCount * RowHeight - Math.Max(0, Bounds.Height - HeaderHeight));
    public double HorizontalMaximum => Math.Max(0, _columns.Count * DefaultColumnWidth - Math.Max(0, Bounds.Width - RowNumberWidth));

    public VirtualDbcView()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetDocument(WdbcFile file, IReadOnlyList<DbcColumn> columns, string tableName, bool decoded)
    {
        _file = file;
        _columns = columns;
        _tableName = tableName;
        _decoded = decoded;
        _filteredRows = null;
        _verticalOffset = 0;
        _horizontalOffset = 0;
        _selectedDisplayRow = -1;
        _selectedColumn = -1;
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
        _verticalOffset = Math.Max(0, _selectedDisplayRow * RowHeight - Math.Max(0, Bounds.Height - HeaderHeight) * 0.45);
        ClampOffsets(); InvalidateVisual();
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
        if (_file is null || _columns.Count == 0 || Bounds.Width <= RowNumberWidth || Bounds.Height <= HeaderHeight)
            return;

        ClampOffsets();
        var firstDisplayRow = Math.Max(0, (int)(_verticalOffset / RowHeight));
        var partialY = _verticalOffset % RowHeight;
        var displayedRows = Math.Min(VisibleRowCount - firstDisplayRow, (int)Math.Ceiling((Bounds.Height - HeaderHeight + partialY) / RowHeight));
        var firstColumn = Math.Max(0, (int)(_horizontalOffset / DefaultColumnWidth));
        var partialX = _horizontalOffset % DefaultColumnWidth;
        var displayedColumns = Math.Min(_columns.Count - firstColumn, (int)Math.Ceiling((Bounds.Width - RowNumberWidth + partialX) / DefaultColumnWidth));

        context.FillRectangle(HeaderBrush, new Rect(0, 0, Bounds.Width, HeaderHeight));
        DrawText(context, "ROW", 8, 8, HeaderTextBrush, HeaderTypeface, 10);
        context.DrawLine(_gridPen, new Point(RowNumberWidth, 0), new Point(RowNumberWidth, Bounds.Height));

        for (var visibleColumn = 0; visibleColumn < displayedColumns; visibleColumn++)
        {
            var columnIndex = firstColumn + visibleColumn;
            var x = RowNumberWidth - partialX + visibleColumn * DefaultColumnWidth;
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

            for (var visibleColumn = 0; visibleColumn < displayedColumns; visibleColumn++)
            {
                var columnIndex = firstColumn + visibleColumn;
                var x = RowNumberWidth - partialX + visibleColumn * DefaultColumnWidth;
                var value = CachedValue(sourceRow, columnIndex);
                DrawText(context, Trim(value, 25), x + 8, y + 5, TextBrush, RegularTypeface, 11);
            }
            context.DrawLine(_gridPen, new Point(0, y + RowHeight), new Point(Bounds.Width, y + RowHeight));
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        RenderMeasured?.Invoke(this, new(elapsed, displayedRows, displayedColumns));
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
        var column = (int)((position.X - RowNumberWidth + _horizontalOffset) / DefaultColumnWidth);
        if (displayRow < 0 || displayRow >= VisibleRowCount || column < 0 || column >= _columns.Count) return;
        _selectedDisplayRow = displayRow;
        _selectedColumn = column;
        var sourceRow = _filteredRows is null ? displayRow : _filteredRows[displayRow];
        SelectionChanged?.Invoke(this, new(sourceRow, column, _columns[column], CachedValue(sourceRow, column)));
        if (e.ClickCount >= 2)
            CellEditRequested?.Invoke(this, new(sourceRow, column, _columns[column], CachedValue(sourceRow, column)));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var delta = e.Key switch
        {
            Key.Down => RowHeight,
            Key.Up => -RowHeight,
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
public sealed record ViewportPerformanceEventArgs(double Milliseconds, int VisibleRows, int VisibleColumns);
