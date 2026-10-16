using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace WoWCrucible.Desktop.Controls;

internal sealed class WorkspaceStartPage : UserControl
{
    private readonly UniformGrid _groups = new() { Columns = 2 };
    public event EventHandler<string>? CommandRequested;

    public WorkspaceStartPage()
    {
        var setup = new Button { Content = "Choose client & server folders", HorizontalAlignment = HorizontalAlignment.Left };
        setup.Click += (_, _) => CommandRequested?.Invoke(this, "workspace.setup");
        var header = new StackPanel { Spacing = 10, Margin = new Thickness(10, 8, 10, 18), Children =
        {
            new TextBlock { Text = "What do you want to work on?", FontSize = 24, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Choose an editor. Open work stays in tabs above; the tool browser stays beside you.", Foreground = Brush.Parse("#AEB8C9"), TextWrapping = TextWrapping.Wrap },
            setup
        } };
        foreach (var group in WorkspaceNavigation.Groups.Take(4))
        {
            var list = new StackPanel { Spacing = 3, Margin = new Thickness(14) };
            list.Children.Add(new TextBlock { Text = group.Title, FontSize = 17, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 0, 4, 10) });
            foreach (var id in group.Commands)
            {
                var command = WorkspaceNavigation.Command(id);
                var button = new Button { Content = new TextBlock { Text = WorkspaceNavigation.Label(id), TextWrapping = TextWrapping.Wrap },
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Left };
                ToolTip.SetTip(button, command.Description);
                button.Click += (_, _) => CommandRequested?.Invoke(this, id);
                list.Children.Add(button);
            }
            _groups.Children.Add(new Border { Margin = new Thickness(8), Child = list, Background = Brush.Parse("#121A27"),
                BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) });
        }
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Margin = new Thickness(14), Children = { header, _groups } } };
        SizeChanged += (_, args) => _groups.Columns = args.NewSize.Width >= 750 ? 2 : 1;
    }
}
