using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
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
    private readonly List<byte[]> _undo = [];
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _statisticsOperation;
    private WriteableBitmap? _bitmap;
    private BlpTextureInfo? _info;
    private RgbaTexture? _editTexture;
    private RgbaTexture? _editBaseline;
    private int _editMip = -1;
    private long _undoBytes;
    private bool _editDirty;
    private bool _editBusy;
    private bool _changingMip;

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
        var visualPages = new TabControl { Items = { new TabItem { Header = "Inspect decoded mip", Content = previewCard }, new TabItem { Header = "Edit RGBA pixels", Content = BuildEditorPage() } } };
        var body = new ResponsiveSplitGrid(inspector, visualPages, 2, 3);
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
    private void RefreshEditedPixels() { if (_editTexture is null) return; _editorCanvas.SetTexture(_editTexture, CurrentChannelView()); _ = UpdateStatisticsAsync(_editTexture); }
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
        _operation?.Cancel(); _operation?.Dispose(); _statisticsOperation?.Cancel(); _statisticsOperation?.Dispose(); _bitmap?.Dispose(); _editorCanvas.Dispose();
    }
}
