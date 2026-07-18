using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

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
    private CancellationTokenSource? _operation;
    private WriteableBitmap? _bitmap;
    private BlpTextureInfo? _info;
    private bool _changingMip;

    public event EventHandler? BackRequested;

    public TextureWorkspaceView()
    {
        var back = new Button { Content = "← Editor" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var open = AccentButton("Open BLP…"); open.Click += async (_, _) => { var path = await PickFileAsync("Open a BLP texture", ["*.blp"]); if (path is not null) await OpenAsync(path); };
        var reload = new Button { Content = "Reload" }; reload.Click += async (_, _) => { if (File.Exists(_sourcePath.Text)) await OpenAsync(_sourcePath.Text!); };
        var decode = new Button { Content = "Save selected mip as PNG…" }; decode.Click += async (_, _) => await SavePngAsync();
        _mip.SelectionChanged += async (_, _) => { if (!_changingMip && _info is not null) await DecodePreviewAsync(); };

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
        var body = new Grid
        {
            ColumnDefinitions = new("2*,Auto,3*"),
            Children = { inspector, Column(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), Column(previewCard, 2) }
        };
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
        var format = _format.SelectedIndex switch { 1 => BlpOutputFormat.Dxt1, 2 => BlpOutputFormat.Dxt1Alpha, 3 => BlpOutputFormat.Dxt3, 4 => BlpOutputFormat.Dxt5, _ => BlpOutputFormat.Auto };
        var quality = _quality.SelectedIndex switch { 1 => BlpOutputQuality.Balanced, 2 => BlpOutputQuality.Fast, _ => BlpOutputQuality.Best };
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

    public void Dispose()
    {
        _operation?.Cancel(); _operation?.Dispose(); _bitmap?.Dispose();
    }
}
