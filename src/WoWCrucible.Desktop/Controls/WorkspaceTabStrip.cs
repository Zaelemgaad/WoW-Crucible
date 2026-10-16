using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace WoWCrucible.Desktop.Controls;

internal sealed class WorkspaceTabStrip : UserControl
{
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Button _overflow = new() { Content = "▾", Padding = new Thickness(9, 5) };
    public event EventHandler<string>? Selected;
    public event EventHandler<string>? Closed;
    public event EventHandler<(string Id, int Delta)>? Moved;

    public WorkspaceTabStrip()
    {
        AutomationProperties.SetName(_overflow, "All open workspaces");
        ToolTip.SetTip(_overflow, "All open workspaces");
        Grid.SetColumn(_overflow, 1);
        Content = new Grid
        {
            ColumnDefinitions = new("*,Auto"),
            Children =
            {
                new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
                _overflow
            }
        };
    }

    public void Refresh(WorkspaceTabState state)
    {
        _tabs.Children.Clear();
        var menu = new MenuFlyout();
        foreach (var tab in state.Tabs)
        {
            var selected = state.SelectedId == tab.Id;
            var button = new Button { Content = tab.Title, Padding = new Thickness(12, 7), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontWeight = selected ? FontWeight.SemiBold : FontWeight.Normal };
            button.Click += (_, _) => Selected?.Invoke(this, tab.Id);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { button } };
            if (!tab.Pinned)
            {
                var close = new Button { Content = "×", Padding = new Thickness(6, 5), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(close, $"Close {tab.Title} tab");
                ToolTip.SetTip(close, "Close tab. Reopen from the tool browser; this session's editor stays intact.");
                close.Click += (_, _) => Closed?.Invoke(this, tab.Id);
                content.Children.Add(close);
                var context = new ContextMenu();
                foreach (var (title, delta) in new[] { ("Move tab left", -1), ("Move tab right", 1) })
                {
                    var move = new MenuItem { Header = title };
                    move.Click += (_, _) => Moved?.Invoke(this, (tab.Id, delta));
                    context.Items.Add(move);
                }
                button.ContextMenu = context;
            }
            _tabs.Children.Add(new Border { Child = content, BorderThickness = new Thickness(0, 0, 0, 2),
                Background = Brush.Parse(selected ? "#1B2535" : "#10151E"), BorderBrush = selected ? Brush.Parse("#E0A33C") : Brushes.Transparent });
            var item = new MenuItem { Header = (selected ? "●  " : "") + tab.Title };
            item.Click += (_, _) => Selected?.Invoke(this, tab.Id);
            menu.Items.Add(item);
        }
        _overflow.Flyout = menu;
    }
}
