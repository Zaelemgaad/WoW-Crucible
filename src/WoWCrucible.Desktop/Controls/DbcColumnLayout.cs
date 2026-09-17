namespace WoWCrucible.Desktop.Controls;

// The renderer, hit tests, scrolling and inline editor share this geometry.
public sealed class DbcColumnLayout
{
    public const int RowHeader = -2;
    public const int RecordKey = -1;
    public const double MinimumWidth = 32;
    public const double DefaultFieldWidth = 156;
    private readonly int _columnCount;
    private readonly int _idColumn;
    private readonly Dictionary<int, double> _widths = [];
    private readonly int[] _scrollColumns;
    private readonly double[] _offsets;

    public event EventHandler? Changed;
    public IReadOnlyList<int> ScrollColumns => _scrollColumns;
    public double RowHeaderWidth => GetWidth(RowHeader);
    public double KeyWidth => GetWidth(RecordKey);
    public double FrozenWidth => RowHeaderWidth + KeyWidth;
    public double ContentWidth => _offsets[^1];

    public DbcColumnLayout(int columnCount, int idColumn = -1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        if (idColumn < -1 || idColumn >= columnCount) throw new ArgumentOutOfRangeException(nameof(idColumn));
        _columnCount = columnCount;
        _idColumn = idColumn;
        _scrollColumns = Enumerable.Range(0, columnCount).Where(index => index != idColumn).ToArray();
        _offsets = new double[_scrollColumns.Length + 1];
        Recalculate();
    }

    public double GetWidth(int column)
    {
        column = Normalize(column);
        return _widths.GetValueOrDefault(column, DefaultWidth(column));
    }

    public void SetWidth(int column, double width)
    {
        column = Normalize(column);
        if (!double.IsFinite(width)) throw new ArgumentOutOfRangeException(nameof(width));
        width = Math.Max(MinimumWidth, width);
        if (!double.IsFinite(ContentWidth + FrozenWidth - GetWidth(column) + width))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (GetWidth(column) == width) return;
        if (width == DefaultWidth(column)) _widths.Remove(column);
        else _widths[column] = width;
        Recalculate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset(int? column = null)
    {
        if (column is { } index) { SetWidth(index, DefaultWidth(Normalize(index))); return; }
        if (_widths.Count == 0) return;
        _widths.Clear();
        Recalculate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Dictionary<int, double> ExportWidths() => new(_widths);

    public void RestoreWidths(IReadOnlyDictionary<int, double> widths)
    {
        Reset();
        foreach (var (column, width) in widths)
        {
            if (column < RowHeader || column >= _columnCount || !double.IsFinite(width) || width < MinimumWidth) continue;
            if (!double.IsFinite(ContentWidth + FrozenWidth - GetWidth(column) + width)) continue;
            SetWidth(column, width);
        }
    }

    public double ViewportWidth(double viewportWidth) => Math.Max(0, viewportWidth - FrozenWidth);
    public double MaximumOffset(double viewportWidth) => Math.Max(0, ContentWidth - ViewportWidth(viewportWidth));

    public DbcColumnSpan Bounds(int column, double horizontalOffset)
    {
        var normalized = Normalize(column);
        if (normalized == RowHeader) return new(RowHeader, 0, RowHeaderWidth);
        if (normalized == RecordKey) return new(column, RowHeaderWidth, KeyWidth);
        var order = Array.IndexOf(_scrollColumns, column);
        return new(column, FrozenWidth + _offsets[order] - horizontalOffset, GetWidth(column));
    }

    public int? HitColumn(double x, double horizontalOffset)
    {
        if (x < 0 || !double.IsFinite(x)) return null;
        if (x < RowHeaderWidth) return RowHeader;
        if (x < FrozenWidth) return RecordKey;
        var index = ScrollIndexAt(x - FrozenWidth + horizontalOffset);
        return index < _scrollColumns.Length ? _scrollColumns[index] : null;
    }

    public int? ResizeEdgeAt(double x, double horizontalOffset, double tolerance = 4)
    {
        if (x < 0 || !double.IsFinite(x)) return null;
        if (Math.Abs(x - RowHeaderWidth) <= tolerance) return RowHeader;
        if (Math.Abs(x - FrozenWidth) <= tolerance) return RecordKey;
        if (x < FrozenWidth) return null;
        var index = ScrollIndexAt(x - FrozenWidth + horizontalOffset);
        foreach (var candidate in new[] { index - 1, index })
        {
            if (candidate < 0 || candidate >= _scrollColumns.Length) continue;
            var right = FrozenWidth + _offsets[candidate + 1] - horizontalOffset;
            if (right > FrozenWidth && Math.Abs(x - right) <= tolerance) return _scrollColumns[candidate];
        }
        return null;
    }

    public IEnumerable<DbcColumnSpan> VisibleColumns(double horizontalOffset, double viewportWidth)
    {
        if (ViewportWidth(viewportWidth) <= 0) yield break;
        for (var index = ScrollIndexAt(horizontalOffset); index < _scrollColumns.Length; index++)
        {
            var left = FrozenWidth + _offsets[index] - horizontalOffset;
            if (left >= viewportWidth) yield break;
            yield return new(_scrollColumns[index], left, GetWidth(_scrollColumns[index]));
        }
    }

    public double OffsetToReveal(int column, double offset, double viewportWidth)
    {
        if (Normalize(column) >= 0)
        {
            var order = Array.IndexOf(_scrollColumns, column);
            var left = _offsets[order];
            var width = GetWidth(column);
            var viewport = ViewportWidth(viewportWidth);
            if (left < offset || width >= viewport) offset = left;
            else if (left + width > offset + viewport) offset = left + width - viewport;
        }
        return Math.Clamp(offset, 0, MaximumOffset(viewportWidth));
    }

    private int ScrollIndexAt(double contentX)
    {
        if (contentX <= 0) return 0;
        if (contentX >= ContentWidth) return _scrollColumns.Length;
        var found = Array.BinarySearch(_offsets, contentX);
        return found >= 0 ? found : ~found - 1;
    }

    private int Normalize(int column)
    {
        if (column < RowHeader || column >= _columnCount) throw new ArgumentOutOfRangeException(nameof(column));
        return column == _idColumn && column >= 0 ? RecordKey : column;
    }

    private static double DefaultWidth(int column) => column switch { RowHeader => 58, RecordKey => 108, _ => DefaultFieldWidth };

    private void Recalculate()
    {
        for (var index = 0; index < _scrollColumns.Length; index++)
            _offsets[index + 1] = _offsets[index] + GetWidth(_scrollColumns[index]);
    }
}

public readonly record struct DbcColumnSpan(int Column, double Left, double Width)
{
    public double Right => Left + Width;
}
