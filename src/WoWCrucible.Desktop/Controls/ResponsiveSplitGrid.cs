using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WoWCrucible.Desktop;

/// <summary>
/// Keeps two workspace panes side by side while the available shape is wide,
/// then reflows the same live controls into a vertical splitter when squeezing
/// them would make either pane unusable. It imposes no minimum or maximum size.
/// </summary>
internal sealed class ResponsiveSplitGrid : Grid
{
    private readonly Control _first;
    private readonly Control _second;
    private readonly GridSplitter _splitter;
    private readonly double _firstWeight;
    private readonly double _secondWeight;
    private readonly double _wideAspect;
    private readonly double _compactFirstWeight;
    private readonly double _compactSecondWeight;
    private bool? _wide;

    public ResponsiveSplitGrid(Control first, Control second, double firstWeight = 1, double secondWeight = 1, double wideAspect = 1.85,
        double? compactFirstWeight = null, double? compactSecondWeight = null)
    {
        if (!double.IsFinite(firstWeight) || firstWeight <= 0) throw new ArgumentOutOfRangeException(nameof(firstWeight));
        if (!double.IsFinite(secondWeight) || secondWeight <= 0) throw new ArgumentOutOfRangeException(nameof(secondWeight));
        if (!double.IsFinite(wideAspect) || wideAspect <= 0) throw new ArgumentOutOfRangeException(nameof(wideAspect));
        if (compactFirstWeight is { } compactFirst && (!double.IsFinite(compactFirst) || compactFirst <= 0)) throw new ArgumentOutOfRangeException(nameof(compactFirstWeight));
        if (compactSecondWeight is { } compactSecond && (!double.IsFinite(compactSecond) || compactSecond <= 0)) throw new ArgumentOutOfRangeException(nameof(compactSecondWeight));
        _first = first; _second = second; _firstWeight = firstWeight; _secondWeight = secondWeight; _wideAspect = wideAspect;
        _compactFirstWeight = compactFirstWeight ?? firstWeight; _compactSecondWeight = compactSecondWeight ?? secondWeight;
        _splitter = new GridSplitter { Background = Brush.Parse("#2B3445") };
        Children.Add(_first); Children.Add(_splitter); Children.Add(_second);
        SizeChanged += (_, args) => Apply(args.NewSize);
        Apply(default);
    }

    private void Apply(Size size)
    {
        var wide = size.Width > 0 && size.Height > 0 && size.Width / size.Height >= _wideAspect;
        if (_wide == wide) return;
        _wide = wide;
        var first = _firstWeight.ToString("0.###", CultureInfo.InvariantCulture);
        var second = _secondWeight.ToString("0.###", CultureInfo.InvariantCulture);
        if (wide)
        {
            RowDefinitions = new RowDefinitions("*");
            ColumnDefinitions = new ColumnDefinitions($"{first}*,Auto,{second}*");
            _splitter.ResizeDirection = GridResizeDirection.Columns;
            Grid.SetRow(_first, 0); Grid.SetColumn(_first, 0);
            Grid.SetRow(_splitter, 0); Grid.SetColumn(_splitter, 1);
            Grid.SetRow(_second, 0); Grid.SetColumn(_second, 2);
        }
        else
        {
            ColumnDefinitions = new ColumnDefinitions("*");
            var compactFirst = _compactFirstWeight.ToString("0.###", CultureInfo.InvariantCulture);
            var compactSecond = _compactSecondWeight.ToString("0.###", CultureInfo.InvariantCulture);
            RowDefinitions = new RowDefinitions($"{compactFirst}*,Auto,{compactSecond}*");
            _splitter.ResizeDirection = GridResizeDirection.Rows;
            Grid.SetColumn(_first, 0); Grid.SetRow(_first, 0);
            Grid.SetColumn(_splitter, 0); Grid.SetRow(_splitter, 1);
            Grid.SetColumn(_second, 0); Grid.SetRow(_second, 2);
        }
    }
}
