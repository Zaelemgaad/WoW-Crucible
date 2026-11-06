using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop.Controls;

/// <summary>
/// Draws an <see cref="ItemTooltipBuilder"/> tooltip with GameTooltipTemplate's metrics: 10 px padding, 2 px line gap,
/// TOOLTIP_DEFAULT_BACKGROUND_COLOR and the client's own FRIZQT__.TTF when the configured client provides it.
/// Sizes are the client's 14/12 px fonts at a 1.2 UI scale, which is close to a 1080p client at default settings.
/// </summary>
internal sealed partial class ItemTooltipView : UserControl
{
    private const double Scale = 1.2;
    private static readonly FontFamily Fallback = new("Inter");
    private sealed record ClientAssets(FontFamily? Font, IReadOnlyDictionary<int, Bitmap> Sockets);
    private static readonly (int Color, string Path)[] SocketTextures =
        new (int, string)[] { (1, "Meta"), (2, "Red"), (4, "Yellow"), (8, "Blue"), (14, "") }
            .Select(socket => (socket.Item1, $@"Interface\ItemSocketingFrame\UI-EmptySocket{(socket.Item2.Length == 0 ? "" : "-" + socket.Item2)}.blp")).ToArray();
    private static ClientAssets _assets = new(null, new Dictionary<int, Bitmap>());
    private static Task<ClientAssets?>? _assetLoad;
    private static string? _assetSource;
    private static int _registeredFonts;
    private readonly StackPanel _lines = new() { Spacing = 2 * Scale };
    private IReadOnlyList<ItemTooltipLine> _current = [];

    public ItemTooltipView()
    {
        Content = new Border
        {
            // Sampled from in-game 3.3.5 tooltips over dark scenery (#0C0F19) and a reference border (#6A6B6C).
            Background = new SolidColorBrush(Color.Parse("#0B0F1D")),
            BorderBrush = new SolidColorBrush(Color.Parse("#6A6B6C")),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10 * Scale),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _lines
        };
    }

    // Long lines wrap at roughly the client's tooltip width, or the pane width when that is narrower.
    protected override Size MeasureOverride(Size availableSize) =>
        base.MeasureOverride(availableSize.WithWidth(Math.Min(availableSize.Width, 340 * Scale)));

    /// <summary>
    /// Loads the tooltip font and empty-socket textures from the client once per client folder.
    /// The fallback font and tinted socket squares are used until they are ready, or when the client lacks them.
    /// </summary>
    public async void UseClientAssets(string clientDataPath)
    {
        if (string.IsNullOrWhiteSpace(clientDataPath)) return;
        if (_assetSource != clientDataPath || _assetLoad is null)
        {
            _assetSource = clientDataPath;
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWCrucible", "client-assets");
            _assetLoad = LoadAsync(clientDataPath, cache);
        }
        if (await _assetLoad is not { } assets) return;
        _assets = assets;
        Display(_current);
    }

    public void Display(IReadOnlyList<ItemTooltipLine> lines)
    {
        _current = lines;
        _lines.Children.Clear();
        foreach (var line in lines)
        {
            var left = Text(line.Left, line.Color, line.Header);
            Control row = left;
            if (line.SocketColor != 0)
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 * Scale, Children = { SocketIcon(line.SocketColor), left } };
            else if (line.Right is not null)
            {
                var right = Text(line.Right, line.Color, line.Header);
                Grid.SetColumn(right, 1);
                left.TextWrapping = TextWrapping.NoWrap;
                row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 40 * Scale, Children = { left, right } };
            }
            _lines.Children.Add(row);
        }
    }

    private static TextBlock Text(string text, string color, bool header)
    {
        var block = new TextBlock { FontFamily = _assets.Font ?? Fallback, FontSize = (header ? 14 : 12) * Scale, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };
        // Client escapes: |cAARRGGBB starts a color, |r restores the line color, |n breaks the line.
        var lineColor = new SolidColorBrush(Color.Parse(color));
        IBrush current = lineColor;
        foreach (var part in ColorEscape().Split(text))
        {
            if (part.Length == 10 && part.StartsWith("|c", StringComparison.OrdinalIgnoreCase)) current = new SolidColorBrush(Color.Parse("#" + part[4..]));
            else if (part is "|r" or "|R") current = lineColor;
            else if (part is "|n" or "|N") block.Inlines!.Add(new LineBreak());
            else if (part.Length > 0) block.Inlines!.Add(new Run(part) { Foreground = current });
        }
        return block;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\|c[0-9a-fA-F]{8}|\|[rRnN])")]
    private static partial System.Text.RegularExpressions.Regex ColorEscape();

    // The client's Interface\ItemSocketingFrame\UI-EmptySocket-<Color> texture; a tinted square until it loads.
    private static Control SocketIcon(int color)
    {
        var size = 13 * Scale;
        if (_assets.Sockets.TryGetValue(color, out var texture) || _assets.Sockets.TryGetValue(14, out texture))
            return new Image { Source = texture, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.Parse(color switch { 1 => "#9A9AA8", 2 => "#B3262A", 4 => "#C9A227", 8 => "#2D5FB8", _ => "#D8D8D8" }))
        };
    }

    private static async Task<ClientAssets?> LoadAsync(string clientDataPath, string cache)
    {
        try
        {
            // One pass over the archives for every asset; BLPs are decoded once into cached PNGs.
            var files = await Task.Run(() =>
            {
                var found = ClientAssetLocator.FindAll(clientDataPath, cache, [ClientAssetLocator.TooltipFont, .. SocketTextures.Select(socket => socket.Path)]);
                var sockets = new Dictionary<int, string>();
                foreach (var (color, path) in SocketTextures)
                {
                    if (!found.TryGetValue(path, out var blp)) continue;
                    var png = Path.ChangeExtension(blp, ".png");
                    if (!File.Exists(png)) BlpTextureService.WritePng(png, BlpTextureService.Decode(blp), overwrite: true);
                    sockets[color] = png;
                }
                return (Font: found.GetValueOrDefault(ClientAssetLocator.TooltipFont), Sockets: sockets);
            });
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                FontFamily? font = null;
                if (files.Font is not null)
                {
                    var key = new Uri($"fonts:WoWClientTooltip{++_registeredFonts}");
                    var collection = new ClientFontCollection(key);
                    using var stream = File.OpenRead(files.Font);
                    if (collection.TryAddGlyphTypeface(stream, out var typeface))
                    {
                        FontManager.Current.AddFontCollection(collection);
                        font = new FontFamily($"{key}#{typeface.FamilyName}");
                    }
                }
                return new ClientAssets(font, files.Sockets.ToDictionary(pair => pair.Key, pair => new Bitmap(pair.Value)));
            });
        }
        catch (Exception exception)
        {
            DesktopCrashLogger.Debug("ITEM", "client-tooltip-assets-unavailable", ("client", clientDataPath), ("error", exception.Message));
            return null;
        }
    }

    private sealed class ClientFontCollection(Uri key) : FontCollectionBase
    {
        public override Uri Key => key;
    }
}
