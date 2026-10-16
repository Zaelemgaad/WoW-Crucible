using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Automation;
using Avalonia.Media;
using WoWCrucible.Core;

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
    private SplitPaneLayout _layout = new();
    private readonly List<(string Label, Action Action)> _layoutCommands = [];
    public event EventHandler? LayoutChanged;

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
        _layout = DefaultLayout();
        _splitter = new GridSplitter { Background = Brush.Parse("#39465E"), Focusable = true };
        AutomationProperties.SetName(_splitter, "Resize editor panes");
        ToolTip.SetTip(_splitter, "Drag to resize · right-click for pane layout · double-click to restore");
        Add("Show both panes", () => _layout.FocusedPane = 0);
        Add("Focus first pane", () => _layout.FocusedPane = 1);
        Add("Focus second pane", () => _layout.FocusedPane = 2);
        Add("Swap pane positions", () => _layout.Reversed = !_layout.Reversed);
        Add("Arrange side by side", () => _layout.Arrangement = "wide");
        Add("Arrange top to bottom", () => _layout.Arrangement = "tall");
        Add("Arrange automatically", () => _layout.Arrangement = "auto");
        _layoutCommands.Add(("Restore default split", ResetLayout));
        _splitter.ContextMenu = new ContextMenu { ItemsSource = CreateLayoutMenuItems() };
        _splitter.DoubleTapped += (_, _) => ResetLayout();
        _splitter.AddHandler(PointerReleasedEvent, (_, _) => { RememberFraction(); LayoutChanged?.Invoke(this, EventArgs.Empty); }, RoutingStrategies.Bubble, handledEventsToo: true);
        _splitter.KeyUp += (_, _) => { RememberFraction(); LayoutChanged?.Invoke(this, EventArgs.Empty); };
        void Add(string title, Action change)
        {
            _layoutCommands.Add((title, () => { RememberFraction(); change(); Apply(Bounds.Size, true); LayoutChanged?.Invoke(this, EventArgs.Empty); }));
        }
        Children.Add(_first); Children.Add(_splitter); Children.Add(_second);
        SizeChanged += (_, args) => Apply(args.NewSize);
        Apply(default);
    }

    public IReadOnlyList<MenuItem> CreateLayoutMenuItems() => _layoutCommands.Select(command =>
    {
        var item = new MenuItem { Header = command.Label };
        item.Click += (_, _) => command.Action();
        return item;
    }).ToArray();

    private SplitPaneLayout DefaultLayout() => new()
    {
        WideFraction = _firstWeight / (_firstWeight + _secondWeight),
        TallFraction = _compactFirstWeight / (_compactFirstWeight + _compactSecondWeight)
    };

    public SplitPaneLayout CaptureLayout()
    {
        RememberFraction();
        return new() { WideFraction = _layout.WideFraction, TallFraction = _layout.TallFraction, Reversed = _layout.Reversed, FocusedPane = _layout.FocusedPane, Arrangement = _layout.Arrangement };
    }

    public void RestoreLayout(SplitPaneLayout? layout)
    {
        var defaults = DefaultLayout();
        _layout = layout is null ? defaults : new()
        {
            WideFraction = SplitPaneLayout.ValidFraction(layout.WideFraction, defaults.WideFraction),
            TallFraction = SplitPaneLayout.ValidFraction(layout.TallFraction, defaults.TallFraction),
            Reversed = layout.Reversed, FocusedPane = Math.Clamp(layout.FocusedPane, 0, 2),
            Arrangement = layout.Arrangement is "wide" or "tall" ? layout.Arrangement : "auto"
        };
        Apply(Bounds.Size, true);
    }

    public void ResetLayout()
    {
        RestoreLayout(null);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RememberFraction()
    {
        if (_wide is null || _layout.FocusedPane != 0) return;
        var firstIndex = _layout.Reversed ? 2 : 0;
        var secondIndex = _layout.Reversed ? 0 : 2;
        var firstLength = _wide == true ? ColumnDefinitions[firstIndex].Width : RowDefinitions[firstIndex].Height;
        var secondLength = _wide == true ? ColumnDefinitions[secondIndex].Width : RowDefinitions[secondIndex].Height;
        var first = firstLength.Value;
        var second = secondLength.Value;
        if (firstLength.GridUnitType != secondLength.GridUnitType)
        {
            first = _wide == true ? _first.Bounds.Width : _first.Bounds.Height;
            second = _wide == true ? _second.Bounds.Width : _second.Bounds.Height;
        }
        if (first <= 0 || second <= 0) return;
        var fraction = SplitPaneLayout.ValidFraction(first / (first + second), _wide == true ? _layout.WideFraction : _layout.TallFraction);
        if (_wide == true) _layout.WideFraction = fraction;
        else _layout.TallFraction = fraction;
    }

    private void Apply(Size size, bool force = false)
    {
        var wide = _layout.Arrangement switch { "wide" => true, "tall" => false, _ => ResponsiveLayoutService.UseSideBySide(size.Width, size.Height, _wideAspect) };
        if (_wide == wide && !force) return;
        if (!force) RememberFraction();
        _wide = wide;
        var fraction = wide ? _layout.WideFraction : _layout.TallFraction;
        var first = (_layout.FocusedPane == 2 ? 0 : _layout.FocusedPane == 1 ? 1 : fraction).ToString("0.#####", CultureInfo.InvariantCulture);
        var second = (_layout.FocusedPane == 1 ? 0 : _layout.FocusedPane == 2 ? 1 : 1 - fraction).ToString("0.#####", CultureInfo.InvariantCulture);
        var start = _layout.Reversed ? second : first;
        var end = _layout.Reversed ? first : second;
        var firstPosition = _layout.Reversed ? 2 : 0;
        var secondPosition = _layout.Reversed ? 0 : 2;
        _first.IsVisible = _layout.FocusedPane != 2;
        _second.IsVisible = _layout.FocusedPane != 1;
        if (wide)
        {
            RowDefinitions = new RowDefinitions("*");
            ColumnDefinitions = new ColumnDefinitions($"{start}*,Auto,{end}*");
            _splitter.ResizeDirection = GridResizeDirection.Columns;
            _splitter.Width = 7; _splitter.Height = double.NaN;
            Grid.SetRow(_first, 0); Grid.SetColumn(_first, firstPosition);
            Grid.SetRow(_splitter, 0); Grid.SetColumn(_splitter, 1);
            Grid.SetRow(_second, 0); Grid.SetColumn(_second, secondPosition);
        }
        else
        {
            ColumnDefinitions = new ColumnDefinitions("*");
            RowDefinitions = new RowDefinitions($"{start}*,Auto,{end}*");
            _splitter.ResizeDirection = GridResizeDirection.Rows;
            _splitter.Height = 7; _splitter.Width = double.NaN;
            Grid.SetColumn(_first, 0); Grid.SetRow(_first, firstPosition);
            Grid.SetColumn(_splitter, 0); Grid.SetRow(_splitter, 1);
            Grid.SetColumn(_second, 0); Grid.SetRow(_second, secondPosition);
        }
    }
}
