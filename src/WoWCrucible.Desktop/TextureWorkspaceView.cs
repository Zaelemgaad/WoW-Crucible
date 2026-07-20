using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed class TextureWorkspaceView : UserControl, IDisposable
{
    private readonly TextBox _sourcePath = new() { PlaceholderText = "Open a BLP1 or BLP2 texture…" };
    private readonly TextBlock _metadata = Text("No texture loaded.");
    private readonly TextBlock _warnings = Text("Strict validation results appear here.");
    private readonly TextBlock _status = Text("Native palette, JPEG, DXT1, DXT3 and DXT5 handling is ready.");
    private readonly ComboBox _mip = new();
    private readonly Image _preview = new() { Stretch = Stretch.Uniform };
    private readonly TextBox _encodeSource = new() { PlaceholderText = "Choose a PNG, JPEG, BMP or TGA source…" };
    private readonly ComboBox _format = new() { ItemsSource = new[] { "Auto (inspect alpha)", "DXT1 · opaque", "DXT1A · binary alpha", "DXT3 · explicit alpha", "DXT5 · smooth alpha" }, SelectedIndex = 0 };
    private readonly ComboBox _quality = new() { ItemsSource = new[] { "Best", "Balanced", "Fast" }, SelectedIndex = 0 };
    private readonly CheckBox _mipmaps = new() { Content = "Generate complete mip chain", IsChecked = true };
    private readonly ListBox _validationResults = new();
    private readonly TexturePixelCanvas _editorCanvas = new();
    private readonly ComboBox _paintMode = new() { ItemsSource = new[] { "Color + alpha", "RGB only", "Alpha only", "Erase alpha" }, SelectedIndex = 0 };
    private readonly ComboBox _falloff = new() { ItemsSource = new[] { "Smooth", "Linear", "Hard" }, SelectedIndex = 0 };
    private readonly TextBox _brushRadius = new() { Text = "8", PlaceholderText = "Brush radius in pixels" };
    private readonly TextBox _brushOpacity = new() { Text = "1", PlaceholderText = "Opacity 0..1" };
    private readonly TextBox _red = new() { Text = "255", PlaceholderText = "R 0..255" };
    private readonly TextBox _green = new() { Text = "255", PlaceholderText = "G 0..255" };
    private readonly TextBox _blue = new() { Text = "255", PlaceholderText = "B 0..255" };
    private readonly TextBox _alpha = new() { Text = "255", PlaceholderText = "A 0..255" };
    private readonly CheckBox _showRed = new() { Content = "R", IsChecked = true };
    private readonly CheckBox _showGreen = new() { Content = "G", IsChecked = true };
    private readonly CheckBox _showBlue = new() { Content = "B", IsChecked = true };
    private readonly CheckBox _showAlpha = new() { Content = "A / transparency", IsChecked = true };
    private readonly CheckBox _alphaGrayscale = new() { Content = "Alpha as grayscale" };
    private readonly TextBlock _pixelStatus = Text("Hover the image to inspect an exact RGBA pixel.");
    private readonly TextBlock _channelStatistics = Text("Channel statistics appear after a mip is decoded.");
    private readonly Image _proofPreview = new() { Stretch = Stretch.Uniform };
    private readonly Image _proofDifference = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _proofSummary = Text("Analyze the current decoded or edited mip to prove exactly what the selected BLP encoding changes.");
    private readonly TextBox _proofAmplification = new() { Text = "4", PlaceholderText = "Difference amplification (0..255)" };
    private readonly TabControl _visualPages = new();
    private readonly ListBox _compositionLayers = new();
    private readonly List<CompositionLayerDraft> _compositionLayerDrafts = [];
    private readonly Image _compositionPreview = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _compositionSummary = Text("Add one or more BLP/image layers, then render the ordered material stack.");
    private readonly TextBox _compositionWidth = new() { PlaceholderText = "Canvas width" };
    private readonly TextBox _compositionHeight = new() { PlaceholderText = "Canvas height" };
    private readonly TextBox _compositionOpacity = new() { Text = "1", PlaceholderText = "Layer opacity 0..1" };
    private readonly TextBox _compositionOffsetX = new() { Text = "0", PlaceholderText = "Layer X offset" };
    private readonly TextBox _compositionOffsetY = new() { Text = "0", PlaceholderText = "Layer Y offset" };
    private readonly ComboBox _compositionBlend = new() { ItemsSource = Enum.GetValues<TextureBlendMode>().Select(TextureLayerCompositionService.BlendModeName).ToArray(), SelectedIndex = 0 };
    private readonly CheckBox _compositionVisible = new() { Content = "Selected layer visible", IsChecked = true };
    private readonly TextBox _compositionBackgroundR = new() { Text = "0", PlaceholderText = "Background R" };
    private readonly TextBox _compositionBackgroundG = new() { Text = "0", PlaceholderText = "Background G" };
    private readonly TextBox _compositionBackgroundB = new() { Text = "0", PlaceholderText = "Background B" };
    private readonly TextBox _compositionBackgroundA = new() { Text = "0", PlaceholderText = "Background A" };
    private readonly List<byte[]> _undo = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _statisticsOperation;
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _proofPreviewBitmap;
    private WriteableBitmap? _proofDifferenceBitmap;
    private WriteableBitmap? _compositionBitmap;
    private BlpTextureInfo? _info;
    private TextureEncodingProof? _proof;
    private TextureCompositionResult? _composition;
    private RgbaTexture? _editTexture;
    private RgbaTexture? _editBaseline;
    private int _editMip = -1;
    private long _undoBytes;
    private bool _editDirty;
    private bool _editBusy;
    private bool _changingMip;
    private bool _changingCompositionSelection;

    private sealed class CompositionLayerDraft
    {
        public required string Name { get; init; }
        public string? SourcePath { get; init; }
        public required RgbaTexture Texture { get; init; }
        public bool Visible { get; set; } = true;
        public double Opacity { get; set; } = 1;
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public TextureBlendMode BlendMode { get; set; }
        public string Detail => $"{Texture.Width:N0}×{Texture.Height:N0} · {TextureLayerCompositionService.BlendModeName(BlendMode)} · opacity {Opacity:0.###} · offset {OffsetX},{OffsetY} · {(Visible ? "visible" : "hidden")}";
    }

    public event EventHandler? BackRequested;

    public TextureWorkspaceView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var open = AccentButton("Open BLP…"); open.Click += async (_, _) => { var path = await PickFileAsync("Open a BLP texture", ["*.blp"]); if (path is not null) await OpenAsync(path); };
        var reload = new Button { Content = "Reload" }; reload.Click += async (_, _) => { if (File.Exists(_sourcePath.Text)) await OpenAsync(_sourcePath.Text!); };
        var decode = new Button { Content = "Save selected mip as PNG…" }; decode.Click += async (_, _) => await SavePngAsync();
        _mip.SelectionChanged += async (_, _) =>
        {
            if (_changingMip || _info is null) return;
            if (_editDirty && _editMip >= 0 && _mip.SelectedIndex != _editMip)
            {
                _changingMip = true; _mip.SelectedIndex = _editMip; _changingMip = false; _status.Text = "The current mip has unsaved pixel edits. Save, undo, or reset them before switching mip levels."; return;
            }
            await DecodePreviewAsync();
        };
        _editorCanvas.StrokeCompleted += async (_, points) => await ApplyStrokeAsync(points);
        _editorCanvas.HoverChanged += (_, pixel) => _pixelStatus.Text = pixel is null ? "Pointer is outside the texture." : $"Pixel {pixel.X:N0},{pixel.Y:N0} · R {pixel.R} · G {pixel.G} · B {pixel.B} · A {pixel.A}";
        foreach (var channel in new[] { _showRed, _showGreen, _showBlue, _showAlpha, _alphaGrayscale }) channel.IsCheckedChanged += (_, _) => RefreshChannelView();
        _brushRadius.TextChanged += (_, _) => { if (double.TryParse(_brushRadius.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) && radius > 0) _editorCanvas.BrushRadius = radius; };
        _format.SelectionChanged += (_, _) => InvalidateProof(); _quality.SelectionChanged += (_, _) => InvalidateProof(); _mipmaps.IsCheckedChanged += (_, _) => InvalidateProof(); _proofAmplification.TextChanged += (_, _) => InvalidateProof();
        _compositionLayers.ItemTemplate = new FuncDataTemplate<CompositionLayerDraft>((layer, _) => layer is null ? new Border() : new Border
        {
            BorderBrush = Brush.Parse("#293247"), BorderThickness = new Thickness(1), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 5),
            Child = new StackPanel { Spacing = 3, Children = { new TextBlock { Text = layer.Name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, new TextBlock { Text = layer.Detail, Foreground = Brush.Parse("#8E99AD"), FontSize = 10, TextWrapping = TextWrapping.Wrap } } }
        });
        _compositionLayers.SelectionChanged += (_, _) => LoadSelectedCompositionLayer();
        foreach (var field in new[] { _compositionWidth, _compositionHeight, _compositionBackgroundR, _compositionBackgroundG, _compositionBackgroundB, _compositionBackgroundA }) field.TextChanged += (_, _) => InvalidateComposition();

        var heading = new Grid
        {
            ColumnDefinitions = new("Auto,*,Auto"), Margin = new Thickness(12, 8), ColumnSpacing = 10,
            Children =
            {
                back,
                Column(new TextBlock { Text = "TEXTURE LAB · NATIVE BLP", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 1),
                Column(new WrapPanel { Children = { open, reload, decode } }, 2)
            }
        };

        var source = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, Children = { new TextBlock { Text = "BLP", VerticalAlignment = VerticalAlignment.Center }, Column(_sourcePath, 1) } };
        var inspector = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 10,
                Children =
                {
                    source,
                    Label("MIP LEVEL"), _mip,
                    Card(_metadata), Card(_warnings),
                    Label("ENCODE IMAGE → BLP2"),
                    EncodePanel(),
                    Label("VALIDATE A TEXTURE LIBRARY"),
                    ValidationPanel()
                }
            }
        };
        var previewCard = new Grid
        {
            RowDefinitions = new("Auto,*"),
            Children =
            {
                new Border { Padding = new Thickness(10, 7), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = new TextBlock { Text = "LIVE DECODED PIXELS · selected mip · checkerboard-free alpha", Foreground = Brush.Parse("#8E99AD"), FontSize = 11 } },
                Row(new Border { Background = Brush.Parse("#080B10"), Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _preview } }, 1)
            }
        };
        _visualPages.Items.Add(new TabItem { Header = "Inspect decoded mip", Content = previewCard });
        _visualPages.Items.Add(new TabItem { Header = "Edit RGBA pixels", Content = BuildEditorPage() });
        _visualPages.Items.Add(new TabItem { Header = "Compression proof", Content = BuildProofPage() });
        _visualPages.Items.Add(new TabItem { Header = "Compose layers", Content = BuildCompositionPage() });
        var body = new ResponsiveSplitGrid(inspector, _visualPages, 2, 3);
        Content = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Children =
            {
                new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading },
                Row(body, 1),
                Row(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7), Child = _status }, 2)
            }
        };
    }

    public async Task OpenAsync(string path)
    {
        if (_editDirty) { _status.Text = "The current texture has unsaved pixel edits. Save or reset that draft before reloading or opening another BLP."; return; }
        Begin("Inspecting and decoding BLP…");
        try
        {
            path = Path.GetFullPath(path);
            var info = await Task.Run(() => BlpTextureService.Inspect(path), _operation!.Token);
            _operation.Token.ThrowIfCancellationRequested();
            _info = info; _sourcePath.Text = path;
            _changingMip = true;
            _mip.ItemsSource = info.MipLevels.Select(level => $"{level.Index} · {level.Width} × {level.Height} · {FormatBytes(level.Size)}").ToArray();
            _mip.SelectedIndex = 0; _changingMip = false;
            _metadata.Text = $"{info.Version} · {info.Width:N0} × {info.Height:N0}\n{info.Encoding} · alpha depth {info.AlphaDepth} · alpha encoding {info.AlphaEncoding}\n{info.MipLevels.Count:N0} mip level(s) · declared {info.DeclaresMipmaps}\n{new FileInfo(path).Length:N0} bytes";
            _warnings.Text = info.Warnings.Count == 0 ? "Validation passed with no structural warnings." : string.Join(Environment.NewLine, info.Warnings.Select(warning => $"• {warning}"));
            await DecodePreviewAsync(reuseOperation: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("Texture open failed", exception); }
        finally { End(); }
    }

    private Control BuildEditorPage()
    {
        var undo = new Button { Content = "Undo stroke / operation" }; undo.Click += (_, _) => UndoEdit();
        var reset = new Button { Content = "Reset decoded mip" }; reset.Click += async (_, _) => await ResetEditAsync();
        var fit = new Button { Content = "Fit view" }; fit.Click += (_, _) => _editorCanvas.ResetView();
        var fill = new Button { Content = "Fill using current tool" }; fill.Click += async (_, _) => await FillAsync();
        var invertAlpha = new Button { Content = "Invert alpha" }; invertAlpha.Click += async (_, _) => await InvertAlphaAsync();
        var savePng = AccentButton("Save edited PNG…"); savePng.Click += async (_, _) => await SaveEditedPngAsync();
        var saveBlp = AccentButton("Save edited BLP2…"); saveBlp.Click += async (_, _) => await SaveEditedBlpAsync();
        var controls = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 7,
            Children =
            {
                new WrapPanel { Children = { Label("TOOL"), _paintMode, Label("FALLOFF"), _falloff, _brushRadius, _brushOpacity } },
                new WrapPanel { Children = { Label("COLOR RGBA"), _red, _green, _blue, _alpha, fill, invertAlpha } },
                new WrapPanel { Children = { Label("VISIBLE CHANNELS"), _showRed, _showGreen, _showBlue, _showAlpha, _alphaGrayscale, fit, undo, reset, savePng, saveBlp } },
                _pixelStatus,
                _channelStatistics,
                new TextBlock { Text = "Left-drag paints · mouse wheel zooms · middle/right-drag pans. Edits affect only the decoded mip draft. Saving always writes a separately selected PNG or Wrath-compatible BLP2; the loaded source is never overwritten silently.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8E99AD"), FontSize = 11 }
            }
        };
        return new Grid { RowDefinitions = new("Auto,*"), Children = { new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = controls }, Row(_editorCanvas, 1) } };
    }

    private Control BuildProofPage()
    {
        var analyze = AccentButton("Analyze current draft with selected encoding"); analyze.Click += async (_, _) => await AnalyzeProofAsync();
        var savePreview = new Button { Content = "Save decoded compression preview…" }; savePreview.Click += async (_, _) => await SaveProofImageAsync(difference: false);
        var saveDifference = new Button { Content = "Save amplified difference map…" }; saveDifference.Click += async (_, _) => await SaveProofImageAsync(difference: true);
        var controls = new StackPanel
        {
            Margin = new Thickness(10), Spacing = 7,
            Children =
            {
                new WrapPanel { Children = { analyze, Label("DIFFERENCE AMPLIFICATION"), _proofAmplification, savePreview, saveDifference } },
                new TextBlock { Text = "This encodes the current in-memory pixels with the Format, Quality, and mip settings on the left, decodes that exact temporary BLP, then compares every byte. The temporary archive is always deleted; neither the loaded source nor the edited draft is changed.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8E99AD"), FontSize = 11 },
                _proofSummary
            }
        };
        var preview = ProofImageCard("ACTUAL DECODED BLP OUTPUT", _proofPreview);
        var difference = ProofImageCard("AMPLIFIED RGBA DIFFERENCE · alpha appears magenta", _proofDifference);
        return new Grid { RowDefinitions = new("Auto,*"), Children = { new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = controls }, Row(new ResponsiveSplitGrid(preview, difference, 1, 1, 1.7), 1) } };
    }

    private static Control ProofImageCard(string title, Image image)
        => new Grid { RowDefinitions = new("Auto,*"), Children = { new Border { Padding = new Thickness(9, 6), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = new TextBlock { Text = title, Foreground = Brush.Parse("#8E99AD"), FontSize = 10 } }, Row(new Border { Background = Brush.Parse("#080B10"), Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = image } }, 1) } };

    private Control BuildCompositionPage()
    {
        var add = AccentButton("Add BLP / image layers…"); add.Click += async (_, _) => await AddCompositionLayersAsync();
        var addDraft = new Button { Content = "Add current RGBA draft" }; addDraft.Click += (_, _) => AddCurrentDraftLayer();
        var remove = new Button { Content = "Remove" }; remove.Click += (_, _) => RemoveCompositionLayer();
        var moveUp = new Button { Content = "Move toward top" }; moveUp.Click += (_, _) => MoveCompositionLayer(-1);
        var moveDown = new Button { Content = "Move toward bottom" }; moveDown.Click += (_, _) => MoveCompositionLayer(1);
        var apply = AccentButton("Apply selected layer settings"); apply.Click += (_, _) => ApplySelectedCompositionLayer();
        var render = AccentButton("Render material stack"); render.Click += async (_, _) => await RenderCompositionAsync();
        var use = new Button { Content = "Use result in RGBA editor" }; use.Click += (_, _) => UseCompositionAsDraft();
        var proof = new Button { Content = "Run compression proof on result" }; proof.Click += async (_, _) => await ProofCompositionAsync();
        var savePng = new Button { Content = "Save composition PNG…" }; savePng.Click += async (_, _) => await SaveCompositionAsync(blp: false);
        var saveBlp = new Button { Content = "Save composition BLP2…" }; saveBlp.Click += async (_, _) => await SaveCompositionAsync(blp: true);
        var layerSettings = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Label("SELECTED LAYER"),
                new WrapPanel { Children = { _compositionVisible, _compositionBlend, _compositionOpacity, _compositionOffsetX, _compositionOffsetY, apply } },
                Label("CANVAS"),
                new WrapPanel { Children = { _compositionWidth, _compositionHeight, Label("BACKGROUND RGBA"), _compositionBackgroundR, _compositionBackgroundG, _compositionBackgroundB, _compositionBackgroundA, render } },
                new WrapPanel { Children = { use, proof, savePng, saveBlp } },
                new TextBlock { Text = "The list is top-layer first. Every render uses exact bottom-to-top source-over alpha; offsets clip rather than resize inputs. Layers and sources remain immutable, and the result can continue through painting or actual-codec compression proof.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8E99AD"), FontSize = 11 },
                _compositionSummary
            }
        };
        var stack = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Children =
            {
                new StackPanel { Margin = new Thickness(8), Spacing = 6, Children = { Label("TOP LAYER FIRST"), new WrapPanel { Children = { add, addDraft, remove, moveUp, moveDown } } } },
                Row(_compositionLayers, 1),
                Row(new ScrollViewer { VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new Border { Padding = new Thickness(8), Child = layerSettings } }, 2)
            }
        };
        var preview = new Grid { RowDefinitions = new("Auto,*"), Children = { new Border { Padding = new Thickness(9, 6), BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = new TextBlock { Text = "RENDERED MATERIAL STACK", Foreground = Brush.Parse("#8E99AD"), FontSize = 10 } }, Row(new Border { Background = Brush.Parse("#080B10"), Child = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = _compositionPreview } }, 1) } };
        return new ResponsiveSplitGrid(stack, preview, 2, 3, 1.65);
    }

    private async Task AddCompositionLayersAsync()
    {
        var paths = await PickFilesAsync("Add BLP or image layers", ["*.blp", "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga"]); if (paths.Count == 0) return;
        Begin($"Decoding {paths.Count:N0} composition layer(s)…");
        try
        {
            var token = _operation!.Token; var loaded = await Task.Run(() => paths.Select(path =>
            {
                token.ThrowIfCancellationRequested(); var full = Path.GetFullPath(path); var texture = Path.GetExtension(full).Equals(".blp", StringComparison.OrdinalIgnoreCase) ? BlpTextureService.Decode(full) : BlpTextureService.DecodeImage(full); return new CompositionLayerDraft { Name = Path.GetFileName(full), SourcePath = full, Texture = texture };
            }).ToArray(), token);
            foreach (var layer in loaded.Reverse()) _compositionLayerDrafts.Insert(0, layer);
            if (string.IsNullOrWhiteSpace(_compositionWidth.Text) || string.IsNullOrWhiteSpace(_compositionHeight.Text)) { _compositionWidth.Text = loaded[0].Texture.Width.ToString(); _compositionHeight.Text = loaded[0].Texture.Height.ToString(); }
            RefreshCompositionLayers(loaded[0]); _status.Text = $"Added {loaded.Length:N0} immutable composition layer(s). Render when ordering and settings are ready.";
        }
        catch (OperationCanceledException) { } catch (Exception exception) { Fail("Composition layer load failed", exception); }
    }

    private void AddCurrentDraftLayer()
    {
        if (_editTexture is null) { _status.Text = "Decode or create an RGBA draft before adding it as a composition layer."; return; }
        var layer = new CompositionLayerDraft { Name = $"Current RGBA draft · {DateTime.Now:HH:mm:ss}", Texture = CloneEditTexture() }; _compositionLayerDrafts.Insert(0, layer);
        if (string.IsNullOrWhiteSpace(_compositionWidth.Text) || string.IsNullOrWhiteSpace(_compositionHeight.Text)) { _compositionWidth.Text = layer.Texture.Width.ToString(); _compositionHeight.Text = layer.Texture.Height.ToString(); }
        RefreshCompositionLayers(layer); _status.Text = "Added an immutable snapshot of the current RGBA draft as the top layer.";
    }

    private void LoadSelectedCompositionLayer()
    {
        if (_compositionLayers.SelectedItem is not CompositionLayerDraft layer) return; _changingCompositionSelection = true;
        _compositionVisible.IsChecked = layer.Visible; _compositionBlend.SelectedIndex = (int)layer.BlendMode; _compositionOpacity.Text = layer.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); _compositionOffsetX.Text = layer.OffsetX.ToString(System.Globalization.CultureInfo.InvariantCulture); _compositionOffsetY.Text = layer.OffsetY.ToString(System.Globalization.CultureInfo.InvariantCulture); _changingCompositionSelection = false;
    }

    private void ApplySelectedCompositionLayer()
    {
        if (_changingCompositionSelection || _compositionLayers.SelectedItem is not CompositionLayerDraft layer) { _status.Text = "Select a composition layer first."; return; }
        try
        {
            var opacity = ParseFinite(_compositionOpacity.Text, "Layer opacity"); if (opacity < 0 || opacity > 1) throw new FormatException("Layer opacity must be from 0 through 1.");
            if (!int.TryParse(_compositionOffsetX.Text, out var x) || !int.TryParse(_compositionOffsetY.Text, out var y)) throw new FormatException("Layer offsets must be whole pixel values.");
            layer.Visible = _compositionVisible.IsChecked == true; layer.BlendMode = _compositionBlend.SelectedIndex >= 0 ? (TextureBlendMode)_compositionBlend.SelectedIndex : TextureBlendMode.Normal; layer.Opacity = opacity; layer.OffsetX = x; layer.OffsetY = y;
            InvalidateComposition(); RefreshCompositionLayers(layer); _status.Text = $"Updated {layer.Name}; render the material stack to apply it.";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void RemoveCompositionLayer()
    {
        if (_compositionLayers.SelectedItem is not CompositionLayerDraft layer) return; var index = _compositionLayerDrafts.IndexOf(layer); _compositionLayerDrafts.Remove(layer); InvalidateComposition();
        var nextSelection = _compositionLayerDrafts.Count == 0 ? null : _compositionLayerDrafts[Math.Min(index, _compositionLayerDrafts.Count - 1)];
        RefreshCompositionLayers(nextSelection); _status.Text = $"Removed layer {layer.Name}; its source file was untouched.";
    }

    private void MoveCompositionLayer(int direction)
    {
        if (_compositionLayers.SelectedItem is not CompositionLayerDraft layer) return; var index = _compositionLayerDrafts.IndexOf(layer); var target = index + direction; if (target < 0 || target >= _compositionLayerDrafts.Count) return;
        (_compositionLayerDrafts[index], _compositionLayerDrafts[target]) = (_compositionLayerDrafts[target], _compositionLayerDrafts[index]); InvalidateComposition(); RefreshCompositionLayers(layer);
    }

    private void RefreshCompositionLayers(CompositionLayerDraft? selected = null)
    {
        selected ??= _compositionLayers.SelectedItem as CompositionLayerDraft; _compositionLayers.ItemsSource = _compositionLayerDrafts.ToArray(); _compositionLayers.SelectedItem = selected is not null && _compositionLayerDrafts.Contains(selected) ? selected : _compositionLayerDrafts.FirstOrDefault();
    }

    private async Task RenderCompositionAsync()
    {
        if (_compositionLayerDrafts.Count == 0) { _status.Text = "Add at least one BLP, image, or current draft layer before rendering."; return; }
        try
        {
            var width = ParsePositiveInt(_compositionWidth.Text, "Canvas width"); var height = ParsePositiveInt(_compositionHeight.Text, "Canvas height"); var background = new[] { ParseByte(_compositionBackgroundR.Text, "Background red"), ParseByte(_compositionBackgroundG.Text, "Background green"), ParseByte(_compositionBackgroundB.Text, "Background blue"), ParseByte(_compositionBackgroundA.Text, "Background alpha") };
            var layers = _compositionLayerDrafts.AsEnumerable().Reverse().Select(layer => new TextureCompositionLayer(layer.Name, layer.Texture, layer.Visible, layer.Opacity, layer.OffsetX, layer.OffsetY, layer.BlendMode)).ToArray();
            Begin($"Compositing {layers.Length:N0} ordered material layer(s)…"); var result = await Task.Run(() => TextureLayerCompositionService.Compose(width, height, layers, background[0], background[1], background[2], background[3], _operation!.Token), _operation!.Token);
            var bitmap = CreateBitmap(result.Texture); var previous = _compositionBitmap; _composition = result; _compositionBitmap = bitmap; _compositionPreview.Source = bitmap; previous?.Dispose();
            _compositionSummary.Text = $"Rendered {width:N0}×{height:N0} · {result.Layers.Count(layer => layer.Visible):N0}/{result.Layers.Count:N0} visible layer(s)\n" + string.Join("\n", result.Layers.Select(layer => $"• {layer.Name} · {(layer.Visible ? $"{layer.ChangedPixels:N0} changed · {layer.ContributingPixels:N0} contributing · {layer.ClippedPixels:N0} clipped" : "hidden")}"));
            _status.Text = $"Material composition rendered · {result.Texture.Pixels.Length:N0} RGBA bytes · sources unchanged.";
        }
        catch (OperationCanceledException) { } catch (Exception exception) { Fail("Material composition failed", exception); }
    }

    private void UseCompositionAsDraft()
    {
        if (_composition is null) { _status.Text = "Render the material stack before handing it to the editor."; return; }
        CaptureUndo(); _editTexture = new(_composition.Texture.Width, _composition.Texture.Height, _composition.Texture.Pixels.ToArray()); _editDirty = true; if (_editMip < 0) _editMip = 0; RefreshEditedPixels(); _visualPages.SelectedIndex = 1;
        _status.Text = "Composition copied into the RGBA editor as a mutable draft. The rendered composition and every layer source remain unchanged.";
    }

    private async Task ProofCompositionAsync()
    {
        if (_composition is null) { _status.Text = "Render the material stack before running compression proof."; return; }
        UseCompositionAsDraft(); _visualPages.SelectedIndex = 2; await AnalyzeProofAsync();
    }

    private async Task SaveCompositionAsync(bool blp)
    {
        if (_composition is null) { _status.Text = "Render the material stack before saving it."; return; }
        var output = await PickSaveFileAsync(blp ? "Save material composition as Wrath-compatible BLP2" : "Save material composition as PNG", blp ? "texture-composition.blp" : "texture-composition.png", blp ? "blp" : "png"); if (output is null) return;
        output = Path.GetFullPath(output); if (_compositionLayerDrafts.Any(layer => layer.SourcePath?.Equals(output, StringComparison.OrdinalIgnoreCase) == true)) { _status.Text = "Composition output must not overwrite a layer source."; return; }
        Begin(blp ? "Encoding material composition BLP2 atomically…" : "Writing material composition PNG atomically…");
        try
        {
            var texture = _composition.Texture; if (blp) await Task.Run(() => BlpTextureService.EncodeBlp2(texture, output, new(SelectedFormat(), _mipmaps.IsChecked == true, SelectedQuality()), overwrite: true), _operation!.Token); else await Task.Run(() => BlpTextureService.WritePng(output, texture, overwrite: true), _operation!.Token);
            _status.Text = $"Material composition saved: {output}";
        }
        catch (OperationCanceledException) { } catch (Exception exception) { Fail("Material composition save failed", exception); }
    }

    private void InvalidateComposition()
    {
        _composition = null; var bitmap = _compositionBitmap; _compositionBitmap = null; _compositionPreview.Source = null; bitmap?.Dispose(); _compositionSummary.Text = _compositionLayerDrafts.Count == 0 ? "Add one or more BLP/image layers, then render the ordered material stack." : "Layer stack or canvas changed · render again before saving or handing off.";
    }

    private async Task AnalyzeProofAsync()
    {
        if (_editTexture is null || _editBusy) { _status.Text = "Decode a BLP mip before analyzing compression loss."; return; }
        double amplification;
        try { amplification = ParseFinite(_proofAmplification.Text, "Difference amplification"); if (amplification <= 0 || amplification > 255) throw new FormatException("Difference amplification must be from 0 exclusive through 255."); }
        catch (Exception exception) { _status.Text = exception.Message; return; }
        var source = CloneEditTexture(); var options = new BlpEncodeOptions(SelectedFormat(), _mipmaps.IsChecked == true, SelectedQuality());
        Begin("Encoding, decoding, and measuring the current texture draft…"); _editBusy = true; _editorCanvas.IsHitTestVisible = false;
        try
        {
            var proof = await Task.Run(() => TextureComparisonService.AnalyzeEncoding(source, options, amplification, _operation!.Token), _operation!.Token); _operation.Token.ThrowIfCancellationRequested();
            var preview = CreateBitmap(proof.DecodedPreview); var difference = CreateBitmap(proof.DifferenceMap); var previousPreview = _proofPreviewBitmap; var previousDifference = _proofDifferenceBitmap;
            _proof = proof; _proofPreviewBitmap = preview; _proofDifferenceBitmap = difference; _proofPreview.Source = preview; _proofDifference.Source = difference; previousPreview?.Dispose(); previousDifference?.Dispose();
            _proofSummary.Text = FormatProof(proof, amplification); var changed = proof.Comparison.ChangedPixels * 100d / proof.Comparison.PixelCount;
            _status.Text = $"Compression proof complete · {proof.ActualEncoding} · {proof.Comparison.ChangedPixels:N0}/{proof.Comparison.PixelCount:N0} pixel(s) changed ({changed:0.####}%) · source and draft unchanged.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("Texture compression proof failed", exception); }
        finally { _editBusy = false; _editorCanvas.IsHitTestVisible = true; }
    }

    private async Task SaveProofImageAsync(bool difference)
    {
        if (_proof is null) { _status.Text = "Analyze the current texture encoding before saving proof images."; return; }
        var output = await PickSaveFileAsync(difference ? "Save amplified texture difference" : "Save decoded BLP compression preview", difference ? "texture-compression-difference.png" : "texture-compression-preview.png", "png"); if (output is null) return;
        Begin("Writing texture proof PNG atomically…");
        try { var texture = difference ? _proof.DifferenceMap : _proof.DecodedPreview; await Task.Run(() => BlpTextureService.WritePng(output, texture, overwrite: true), _operation!.Token); _status.Text = $"Texture proof PNG saved: {output}"; }
        catch (OperationCanceledException) { } catch (Exception exception) { Fail("Texture proof export failed", exception); }
    }

    private static string FormatProof(TextureEncodingProof proof, double amplification)
    {
        var report = proof.Comparison; var changed = report.ChangedPixels * 100d / report.PixelCount;
        return $"{proof.ActualEncoding} · {proof.Quality} · {proof.EncodedBytes:N0} bytes · {proof.MipLevels:N0} mip(s)\n" +
               $"Changed pixels: {report.ChangedPixels:N0}/{report.PixelCount:N0} ({changed:0.####}%) · exact: {report.ExactPixels:N0}\n" +
               $"RGB  MAE {report.RgbCombined.MeanAbsoluteError:0.######} · RMSE {report.RgbCombined.RootMeanSquareError:0.######} · max {report.RgbCombined.MaximumAbsoluteError} · PSNR {Psnr(report.RgbCombined)}\n" +
               $"Alpha MAE {report.Alpha.MeanAbsoluteError:0.######} · RMSE {report.Alpha.RootMeanSquareError:0.######} · max {report.Alpha.MaximumAbsoluteError} · PSNR {Psnr(report.Alpha)}\n" +
               $"Alpha boundary changes: {report.TransparentBoundaryChanges:N0} transparent · {report.OpaqueBoundaryChanges:N0} opaque · {report.AlphaThresholdCrossings:N0} threshold-128 crossing(s) · {report.BinaryAlphaBecameTranslucent:N0} binary→translucent\n" +
               $"Difference map: {amplification:0.###}× amplification; exact pixels are black and alpha damage is added to red + blue.";
        static string Psnr(TextureChannelError value) => value.PeakSignalToNoiseDb is { } psnr ? $"{psnr:0.###} dB" : "exact";
    }

    private void InvalidateProof()
    {
        _proof = null; var preview = _proofPreviewBitmap; var difference = _proofDifferenceBitmap; _proofPreviewBitmap = null; _proofDifferenceBitmap = null; _proofPreview.Source = null; _proofDifference.Source = null; preview?.Dispose(); difference?.Dispose();
        _proofSummary.Text = "Analyze the current decoded or edited mip to prove exactly what the selected BLP encoding changes.";
    }

    private async Task ApplyStrokeAsync(IReadOnlyList<TexturePoint> points)
    {
        if (_editTexture is null || _editBusy) return; TextureBrushSettings settings;
        try { settings = ReadBrush(); }
        catch (Exception exception) { _status.Text = exception.Message; return; }
        CaptureUndo(); _editBusy = true; _editorCanvas.IsHitTestVisible = false; _status.Text = $"Applying {points.Count:N0}-point texture stroke…";
        try
        {
            var texture = _editTexture; var result = await Task.Run(() => TexturePixelEditService.ApplyStroke(texture, points, settings));
            if (result.Changed) { _editDirty = true; RefreshEditedPixels(); _status.Text = $"Painted {result.ChangedPixels:N0} pixel(s) · bounds {result.MinimumX},{result.MinimumY} → {result.MaximumX},{result.MaximumY}. Draft only; source unchanged."; }
            else { DiscardUnusedUndo(); _status.Text = "The stroke produced no pixel change."; }
        }
        catch (Exception exception) { RestoreLastUndoAfterFailure(); Fail("Texture stroke failed", exception); }
        finally { _editBusy = false; _editorCanvas.IsHitTestVisible = true; }
    }

    private async Task FillAsync()
    {
        if (_editTexture is null || _editBusy) { _status.Text = "Decode a BLP mip before filling it."; return; } TextureBrushSettings settings;
        try { settings = ReadBrush(); } catch (Exception exception) { _status.Text = exception.Message; return; }
        CaptureUndo(); _editBusy = true; _status.Text = "Applying a complete texture fill…";
        try { var texture = _editTexture; var result = await Task.Run(() => TexturePixelEditService.Fill(texture, settings)); if (result.Changed) { _editDirty = true; RefreshEditedPixels(); _status.Text = $"Filled {result.ChangedPixels:N0} changed pixel(s). Draft only; source unchanged."; } else { DiscardUnusedUndo(); _status.Text = "The fill produced no pixel change."; } }
        catch (Exception exception) { RestoreLastUndoAfterFailure(); Fail("Texture fill failed", exception); }
        finally { _editBusy = false; }
    }

    private async Task InvertAlphaAsync()
    {
        if (_editTexture is null || _editBusy) { _status.Text = "Decode a BLP mip before editing alpha."; return; } CaptureUndo(); _editBusy = true; _status.Text = "Inverting exact alpha bytes…";
        try { var texture = _editTexture; var result = await Task.Run(() => TexturePixelEditService.InvertAlpha(texture)); if (result.Changed) { _editDirty = true; RefreshEditedPixels(); _status.Text = $"Inverted alpha on {result.ChangedPixels:N0} pixel(s). Draft only; source unchanged."; } else { DiscardUnusedUndo(); _status.Text = "Alpha inversion produced no change."; } }
        catch (Exception exception) { RestoreLastUndoAfterFailure(); Fail("Alpha inversion failed", exception); }
        finally { _editBusy = false; }
    }

    private void UndoEdit()
    {
        if (_editTexture is null || _undo.Count == 0) { _status.Text = "There is no retained texture operation to undo."; return; }
        var pixels = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _undoBytes -= pixels.LongLength; _editTexture = new(_editTexture.Width, _editTexture.Height, pixels); _editDirty = _editBaseline is null || !pixels.AsSpan().SequenceEqual(_editBaseline.Pixels); RefreshEditedPixels(); _status.Text = $"Undid the last texture operation · {_undo.Count:N0} retained undo state(s).";
    }

    private async Task ResetEditAsync()
    {
        if (_info is null || _editMip < 0 || _editBusy) return; _editDirty = false; ClearUndo(); await DecodePreviewAsync(); _status.Text = $"Reset mip {_editMip} from the immutable loaded BLP source.";
    }

    private async Task SaveEditedPngAsync()
    {
        if (_editTexture is null || _info is null) { _status.Text = "Decode a BLP mip before saving edited pixels."; return; }
        var output = await PickSaveFileAsync("Save edited RGBA pixels as PNG", $"{Path.GetFileNameWithoutExtension(_info.Path)}-edited-mip{_editMip}.png", "png"); if (output is null) return;
        Begin("Writing edited PNG atomically…"); try { var texture = CloneEditTexture(); await Task.Run(() => BlpTextureService.WritePng(output, texture, overwrite: true), _operation!.Token); MarkEditSaved(); _status.Text = $"Edited PNG saved: {output}"; }
        catch (OperationCanceledException) { } catch (Exception exception) { Fail("Edited PNG save failed", exception); } finally { End(); }
    }

    private async Task SaveEditedBlpAsync()
    {
        if (_editTexture is null || _info is null) { _status.Text = "Decode a BLP mip before saving edited pixels."; return; }
        var output = await PickSaveFileAsync("Save edited pixels as Wrath-compatible BLP2", $"{Path.GetFileNameWithoutExtension(_info.Path)}-edited.blp", "blp"); if (output is null) return;
        if (Path.GetFullPath(output).Equals(Path.GetFullPath(_info.Path), StringComparison.OrdinalIgnoreCase)) { _status.Text = "The loaded source is immutable in Texture Lab. Choose a new BLP output path."; return; }
        var format = SelectedFormat(); var quality = SelectedQuality(); Begin("Encoding edited BLP2 atomically…");
        try { var texture = CloneEditTexture(); await Task.Run(() => BlpTextureService.EncodeBlp2(texture, output, new(format, _mipmaps.IsChecked == true, quality), overwrite: true), _operation!.Token); var verified = BlpTextureService.Inspect(output); MarkEditSaved(); _status.Text = $"Edited BLP2 saved and reopened structurally · {verified.Width:N0}×{verified.Height:N0} · {verified.Encoding} · {verified.MipLevels.Count:N0} mip(s): {output}"; }
        catch (OperationCanceledException) { } catch (Exception exception) { Fail("Edited BLP2 save failed", exception); } finally { End(); }
    }

    private TextureBrushSettings ReadBrush()
    {
        var radius = ParseFinite(_brushRadius.Text, "Brush radius"); var opacity = ParseFinite(_brushOpacity.Text, "Brush opacity");
        var mode = _paintMode.SelectedIndex switch { 1 => TexturePaintMode.RgbOnly, 2 => TexturePaintMode.AlphaOnly, 3 => TexturePaintMode.EraseAlpha, _ => TexturePaintMode.ColorAndAlpha };
        var falloff = _falloff.SelectedIndex switch { 1 => TextureBrushFalloff.Linear, 2 => TextureBrushFalloff.Hard, _ => TextureBrushFalloff.Smooth };
        return new(radius, opacity, ParseByte(_red.Text, "Red"), ParseByte(_green.Text, "Green"), ParseByte(_blue.Text, "Blue"), ParseByte(_alpha.Text, "Alpha"), mode, falloff);
    }

    private void MarkEditSaved()
    {
        if (_editTexture is null) return;
        _editDirty = false;
        _editBaseline = _editTexture.Pixels.LongLength <= 64L * 1024 * 1024
            ? new(_editTexture.Width, _editTexture.Height, _editTexture.Pixels.ToArray())
            : null;
        ClearUndo();
    }

    private void RefreshChannelView() { _editorCanvas.SetChannelView(CurrentChannelView()); }
    private TextureChannelView CurrentChannelView() => new(_showRed.IsChecked == true, _showGreen.IsChecked == true, _showBlue.IsChecked == true, _showAlpha.IsChecked == true, _alphaGrayscale.IsChecked == true);
    private void RefreshEditedPixels() { if (_editTexture is null) return; InvalidateProof(); _editorCanvas.SetTexture(_editTexture, CurrentChannelView()); _ = UpdateStatisticsAsync(_editTexture); }
    private async Task UpdateStatisticsAsync(RgbaTexture texture)
    {
        _statisticsOperation?.Cancel(); _statisticsOperation?.Dispose(); var operation = _statisticsOperation = new();
        try
        {
            var stats = await Task.Run(() => TexturePixelEditService.Analyze(texture), operation.Token); if (operation.IsCancellationRequested || !ReferenceEquals(texture, _editTexture)) return;
            var total = checked(texture.Width * texture.Height); _channelStatistics.Text = $"R {stats.MinimumR}..{stats.MaximumR} avg {stats.AverageR:0.##} · G {stats.MinimumG}..{stats.MaximumG} avg {stats.AverageG:0.##} · B {stats.MinimumB}..{stats.MaximumB} avg {stats.AverageB:0.##} · A {stats.MinimumA}..{stats.MaximumA} avg {stats.AverageA:0.##}\nAlpha: {stats.TransparentPixels:N0} transparent · {stats.TranslucentPixels:N0} translucent · {stats.OpaquePixels:N0} opaque · {total:N0} total pixels";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _channelStatistics.Text = $"Channel statistics failed: {exception.Message}"; }
    }

    private void CaptureUndo()
    {
        if (_editTexture is null) return; const long budget = 128L * 1024 * 1024; var bytes = _editTexture.Pixels.LongLength;
        if (bytes > budget) { ClearUndo(); _status.Text = "This texture exceeds the 128 MiB undo budget; Reset remains available from the immutable source."; return; }
        while (_undo.Count >= 16 || _undoBytes + bytes > budget) { _undoBytes -= _undo[0].LongLength; _undo.RemoveAt(0); }
        var snapshot = _editTexture.Pixels.ToArray(); _undo.Add(snapshot); _undoBytes += snapshot.LongLength;
    }
    private void DiscardUnusedUndo() { if (_undo.Count == 0) return; _undoBytes -= _undo[^1].LongLength; _undo.RemoveAt(_undo.Count - 1); }
    private void RestoreLastUndoAfterFailure() { if (_editTexture is null || _undo.Count == 0) return; var pixels = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); _undoBytes -= pixels.LongLength; _editTexture = new(_editTexture.Width, _editTexture.Height, pixels); RefreshEditedPixels(); }
    private void ClearUndo() { _undo.Clear(); _undoBytes = 0; }
    private RgbaTexture CloneEditTexture() => _editTexture is null ? throw new InvalidOperationException("No edited texture is loaded.") : new(_editTexture.Width, _editTexture.Height, _editTexture.Pixels.ToArray());
    private static double ParseFinite(string? text, string name) { if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) throw new FormatException($"{name} must be a finite number."); return value; }
    private static int ParsePositiveInt(string? text, string name) { if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value) || value <= 0) throw new FormatException($"{name} must be a positive whole number."); return value; }
    private static byte ParseByte(string? text, string name) { if (!byte.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)) throw new FormatException($"{name} must be a whole byte value from 0 through 255."); return value; }

    private Control EncodePanel()
    {
        var choose = new Button { Content = "Source image…" }; choose.Click += async (_, _) => { var path = await PickFileAsync("Choose an image to encode", ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.tga"]); if (path is not null) _encodeSource.Text = path; };
        var encode = AccentButton("Encode BLP2…"); encode.Click += async (_, _) => await EncodeAsync();
        return Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _encodeSource,
                new WrapPanel { Children = { choose, encode } },
                new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { _format, Column(_quality, 1) } },
                _mipmaps,
                new TextBlock { Text = "Auto chooses DXT1 for opaque pixels, DXT1A for binary transparency, and DXT5 for smooth alpha. Raw dimensions and every generated mip remain inspectable.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8E99AD"), FontSize = 11 }
            }
        });
    }

    private Control ValidationPanel()
    {
        var validateFile = new Button { Content = "Validate BLP…" }; validateFile.Click += async (_, _) => { var path = await PickFileAsync("Validate a BLP texture", ["*.blp"]); if (path is not null) await ValidateAsync(path, false); };
        var validateFolder = new Button { Content = "Validate folder recursively…" }; validateFolder.Click += async (_, _) => { var path = await PickFolderAsync("Validate every BLP in a folder"); if (path is not null) await ValidateAsync(path, true); };
        return Card(new StackPanel { Spacing = 8, Children = { new WrapPanel { Children = { validateFile, validateFolder } }, _validationResults } });
    }

    private async Task DecodePreviewAsync(bool reuseOperation = false)
    {
        if (_info is null || _mip.SelectedIndex < 0) return;
        if (!reuseOperation) Begin("Decoding selected mip…");
        try
        {
            var token = _operation?.Token ?? CancellationToken.None; var path = _info.Path; var mip = _mip.SelectedIndex;
            var texture = await Task.Run(() => BlpTextureService.Decode(path, mip), token); token.ThrowIfCancellationRequested();
            var bitmap = CreateBitmap(texture); var previous = _bitmap; _bitmap = bitmap; _preview.Source = bitmap; previous?.Dispose();
            _editTexture = texture; _editBaseline = texture.Pixels.LongLength <= 64L * 1024 * 1024 ? new(texture.Width, texture.Height, texture.Pixels.ToArray()) : null; _editMip = mip; _editDirty = false; ClearUndo(); _editorCanvas.BrushRadius = double.TryParse(_brushRadius.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var radius) && radius > 0 ? radius : 8; RefreshEditedPixels();
            _status.Text = $"Decoded mip {mip} · {texture.Width:N0} × {texture.Height:N0} · {texture.Pixels.Length:N0} RGBA bytes";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("Texture decode failed", exception); }
        finally { if (!reuseOperation) End(); }
    }

    private async Task SavePngAsync()
    {
        if (_info is null || _mip.SelectedIndex < 0) { _status.Text = "Open a BLP and choose a mip first."; return; }
        var output = await PickSaveFileAsync("Save decoded PNG", $"{Path.GetFileNameWithoutExtension(_info.Path)}-mip{_mip.SelectedIndex}.png", "png");
        if (output is null) return;
        Begin("Writing PNG…");
        try { await Task.Run(() => BlpTextureService.DecodeToPng(_info.Path, output, _mip.SelectedIndex, overwrite: true), _operation!.Token); _status.Text = $"PNG saved: {output}"; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("PNG export failed", exception); }
        finally { End(); }
    }

    private async Task EncodeAsync()
    {
        if (!File.Exists(_encodeSource.Text)) { _status.Text = "Choose an existing source image first."; return; }
        var output = await PickSaveFileAsync("Save Wrath-compatible BLP2", $"{Path.GetFileNameWithoutExtension(_encodeSource.Text)}.blp", "blp");
        if (output is null) return;
        var format = SelectedFormat();
        var quality = SelectedQuality();
        Begin("Encoding native BLP2…");
        try
        {
            var input = _encodeSource.Text!; await Task.Run(() => BlpTextureService.EncodeFromImage(input, output, new(format, _mipmaps.IsChecked == true, quality), overwrite: true), _operation!.Token);
            _status.Text = $"BLP2 saved: {output}"; await OpenAsync(output);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("BLP encode failed", exception); }
        finally { End(); }
    }

    private async Task ValidateAsync(string path, bool recursive)
    {
        Begin("Validating BLP structure…");
        try
        {
            var shown = new List<BlpValidationResult>(500);
            var summary = await Task.Run(() => BlpTextureService.ValidateEach(path, recursive, result => { if (shown.Count < 500) shown.Add(result); }, _operation!.Token), _operation!.Token);
            _validationResults.ItemsSource = shown.Select(result => result.Valid
                ? $"PASS · {result.Info!.Width}×{result.Info.Height} · {result.Info.Encoding} · {Path.GetFileName(result.Path)}"
                : $"FAIL · {Path.GetFileName(result.Path)} · {result.Error}").ToArray();
            _status.Text = $"Validated {summary.Total:N0} BLP(s) · {summary.Total - summary.Failures:N0} decodable · {summary.Warnings:N0} warning(s) · {summary.Failures:N0} failed" + (summary.Total > 500 ? " · first 500 shown" : string.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Fail("Texture validation failed", exception); }
        finally { End(); }
    }

    private static WriteableBitmap CreateBitmap(RgbaTexture texture)
    {
        var bitmap = new WriteableBitmap(new PixelSize(texture.Width, texture.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using var frame = bitmap.Lock(); var rowBytes = checked(texture.Width * 4);
        for (var row = 0; row < texture.Height; row++) Marshal.Copy(texture.Pixels, row * rowBytes, IntPtr.Add(frame.Address, row * frame.RowBytes), rowBytes);
        return bitmap;
    }

    private void Begin(string message) { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); _status.Text = message; }
    private void End() { }
    private void Fail(string context, Exception exception) { _status.Text = $"{context}: {exception.Message}"; DesktopCrashLogger.Log(context, exception); }

    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Supported files") { Patterns = patterns }] });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<IReadOnlyList<string>> PickFilesAsync(string title, IReadOnlyList<string> patterns)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return [];
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true, FileTypeFilter = [new FilePickerFileType("Supported files") { Patterns = patterns }] });
        return files.Select(file => file.TryGetLocalPath()).Where(path => path is not null).Cast<string>().ToArray();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return null;
        return (await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false })).FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider; if (storage is null) return null;
        return (await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = suggestedName, DefaultExtension = extension, FileTypeChoices = [new FilePickerFileType(extension.ToUpperInvariant()) { Patterns = [$"*.{extension}"] }] }))?.TryGetLocalPath();
    }

    private static Border Card(Control child) => new() { Padding = new Thickness(10), CornerRadius = new CornerRadius(6), Background = Brush.Parse("#0E131C"), BorderBrush = Brush.Parse("#293247"), BorderThickness = new Thickness(1), Child = child };
    private static TextBlock Text(string value) => new() { Text = value, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#98A2B4") };
    private static TextBlock Label(string value) => new() { Text = value, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#7F8A9F") };
    private static Button AccentButton(string text) => new() { Content = text, Classes = { "accent" } };
    private static T Column<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T Row<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.##} KiB" : $"{bytes} B";
    private BlpOutputFormat SelectedFormat() => _format.SelectedIndex switch { 1 => BlpOutputFormat.Dxt1, 2 => BlpOutputFormat.Dxt1Alpha, 3 => BlpOutputFormat.Dxt3, 4 => BlpOutputFormat.Dxt5, _ => BlpOutputFormat.Auto };
    private BlpOutputQuality SelectedQuality() => _quality.SelectedIndex switch { 1 => BlpOutputQuality.Balanced, 2 => BlpOutputQuality.Fast, _ => BlpOutputQuality.Best };

    public void Dispose()
    {
        _operation?.Cancel(); _operation?.Dispose(); _statisticsOperation?.Cancel(); _statisticsOperation?.Dispose(); _bitmap?.Dispose(); _proofPreviewBitmap?.Dispose(); _proofDifferenceBitmap?.Dispose(); _compositionBitmap?.Dispose(); _editorCanvas.Dispose();
    }
}
