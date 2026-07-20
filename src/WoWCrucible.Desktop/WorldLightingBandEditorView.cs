using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;
using WoWCrucible.Desktop.Controls;

namespace WoWCrucible.Desktop;

internal sealed record WorldLightBandDraftKey(WorldLightingBandKind Kind, int Time, uint RawValue)
{
    public WorldLightColor Color => WorldLightColor.FromPacked(RawValue);
    public float FloatValue => BitConverter.UInt32BitsToSingle(RawValue);
    public string Clock { get { var minutes = (int)Math.Round(Time / (double)WorldLightingService.DayUnits * 24 * 60) % (24 * 60); return $"{minutes / 60:00}:{minutes % 60:00}"; } }
    public override string ToString() => Kind == WorldLightingBandKind.Color
        ? $"{Time:N0} · {Clock} · {WorldLightColor.FromPacked(RawValue).Hex}"
        : $"{Time:N0} · {Clock} · {BitConverter.UInt32BitsToSingle(RawValue):0.######}";
}

internal sealed class WorldLightingBandEditorView : UserControl
{
    private readonly TextBlock _title = Info("Select a color or float band to edit its time keys.");
    private readonly TextBlock _source = Info("No band selected.");
    private readonly ListBox _keys = new();
    private readonly NumericUpDown _keyTime = new() { Minimum = 0, Maximum = WorldLightingService.DayUnits, Increment = 10 };
    private readonly TextBox _hex = new() { PlaceholderText = "#RRGGBB" };
    private readonly TextBox _floatValue = new() { PlaceholderText = "Finite decimal value" };
    private readonly Border _swatch = new() { BorderBrush = Brush.Parse("#596579"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = new TextBlock { Text = "COLOR", Padding = new Thickness(8, 4), HorizontalAlignment = HorizontalAlignment.Center } };
    private readonly StackPanel _colorFields;
    private readonly StackPanel _floatFields;
    private readonly CheckBox _replaceStaged = new() { Content = "Replace an existing staged output and retain its .bak" };
    private readonly TextBlock _status = Info("Changes remain a draft until you choose a write action.");
    private readonly WorldLightBandCurveControl _curve = new();
    private readonly Dictionary<string, List<WorldLightBandDraftKey>> _drafts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorldLightingBandEditPlan> _baselines = new(StringComparer.OrdinalIgnoreCase);
    private List<WorldLightBandDraftKey> _draft = [];
    private List<WorldLightBandDraftKey> _original = [];
    private string? _draftIdentity;
    private string? _inputPath;
    private uint _bandId;
    private WorldLightingBandKind _kind;
    private int _previewTime = 1440;
    private bool _refreshing;

    public event EventHandler<WorldLightingBandEditResult>? SourceApplied;

    public WorldLightingBandEditorView()
    {
        _keys.ItemTemplate = new FuncDataTemplate<WorldLightBandDraftKey>((key, _) => key is null ? new Border() : new Border
        {
            BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 6),
            Child = new TextBlock { Text = key.ToString(), FontFamily = new FontFamily("Cascadia Mono,Consolas"), TextWrapping = TextWrapping.Wrap }
        });
        _keys.SelectionChanged += (_, _) => ShowSelected(); _curve.KeySelected += (_, index) => { _keys.SelectedIndex = index; if (index >= 0) _keys.ScrollIntoView(_keys.SelectedItem!); };
        _hex.TextChanged += (_, _) => RefreshSwatch();

        var update = Accent("Update selected key"); update.Click += (_, _) => UpdateSelected();
        var add = new Button { Content = "Add key at preview time" }; add.Click += (_, _) => AddAtPreview();
        var remove = new Button { Content = "Remove selected key" }; remove.Click += (_, _) => RemoveSelected();
        var reset = new Button { Content = "Reset band draft" }; reset.Click += (_, _) => ResetDraft();
        var stage = new Button { Content = "Write staged DBC…" }; stage.Click += async (_, _) => await WriteStagedAsync();
        var apply = new Button { Content = "Apply to loaded DBC · keep .bak", Background = Brush.Parse("#5F331F"), Foreground = Brush.Parse("#FFD7B0") }; apply.Click += async (_, _) => await ApplySourceAsync();

        _colorFields = new StackPanel { Spacing = 5, Children = { Label("RGB HEX"), _hex, _swatch } };
        _floatFields = new StackPanel { Spacing = 5, IsVisible = false, Children = { Label("FLOAT VALUE"), _floatValue } };
        var fields = new StackPanel { Spacing = 8, Margin = new Thickness(10), Children = { Label("KEY TIME · CLIENT 0–2880"), _keyTime, _colorFields, _floatFields, new WrapPanel { Children = { update, add, remove, reset } }, Info("Keys are stored in chronological order. Times 0 and 2880 are both valid endpoints; duplicate exact times and non-finite values are refused."), _replaceStaged, new WrapPanel { Children = { stage, apply } } } };
        var visual = new Grid { RowDefinitions = new("*,Auto"), Children = { _curve, WithRow(new TextBlock { Text = "Click a curve handle to select its key · vertical line is the current lighting preview time", Margin = new Thickness(8), Foreground = Brush.Parse("#8E99AD"), TextWrapping = TextWrapping.Wrap }, 1) } };
        var right = new TabControl { Items = { new TabItem { Header = "Curve preview", Content = visual }, new TabItem { Header = "Selected key", Content = new ScrollViewer { Content = fields } } } };
        var body = new ResponsiveSplitGrid(_keys, right, 2, 3);
        Content = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Children =
            {
                new StackPanel { Margin = new Thickness(10, 8), Spacing = 4, Children = { _title, _source } },
                WithRow(body, 1),
                WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(10, 7), Child = _status }, 2)
            }
        };
    }

    public void LoadColor(string dbcRoot, WorldLightColorBand band, int previewTime)
    {
        Load(Path.Combine(dbcRoot, "LightIntBand.dbc"), WorldLightingBandKind.Color, band.Id, band.Name,
            band.Keys.Select(key => new WorldLightBandDraftKey(WorldLightingBandKind.Color, key.Time, key.Packed)), previewTime);
    }

    public void LoadFloat(string dbcRoot, WorldLightFloatBand band, int previewTime)
    {
        Load(Path.Combine(dbcRoot, "LightFloatBand.dbc"), WorldLightingBandKind.Float, band.Id, band.Name,
            band.Keys.Select(key => new WorldLightBandDraftKey(WorldLightingBandKind.Float, key.Time, BitConverter.SingleToUInt32Bits(key.Value))), previewTime);
    }

    public void SetPreviewTime(int time) { _previewTime = Math.Clamp(time, 0, WorldLightingService.DayUnits); _curve.SetPreviewTime(_previewTime); }

    public void ResetCatalog()
    {
        _drafts.Clear(); _baselines.Clear(); _draft = []; _original = []; _draftIdentity = null; _inputPath = null; _bandId = 0; _keys.ItemsSource = null; _curve.SetDraft(WorldLightingBandKind.Color, [], -1, _previewTime); _title.Text = "Select a color or float band to edit its time keys."; _source.Text = "No band selected."; _status.Text = "Changes remain a draft until you choose a write action.";
    }

    private void Load(string inputPath, WorldLightingBandKind kind, uint bandId, string name, IEnumerable<WorldLightBandDraftKey> original, int previewTime)
    {
        try
        {
            PersistActive(); _inputPath = Path.GetFullPath(inputPath); _kind = kind; _bandId = bandId; _previewTime = previewTime; _draftIdentity = $"{_inputPath}|{kind}|{bandId}"; var sourceKeys = original.ToList();
            var seed = new WorldLightBandDraftKey(kind, previewTime, kind == WorldLightingBandKind.Color ? WorldLightingEditService.Pack(default) : BitConverter.SingleToUInt32Bits(0)); var planKeys = sourceKeys.Count == 0 ? [seed] : sourceKeys;
            if (!_baselines.TryGetValue(_draftIdentity, out var baseline))
            {
                baseline = kind == WorldLightingBandKind.Color
                    ? WorldLightingEditService.PlanColor(_inputPath, bandId, planKeys.Select(key => (key.Time, key.Color)))
                    : WorldLightingEditService.PlanFloat(_inputPath, bandId, planKeys.Select(key => (key.Time, key.FloatValue)));
                if (baseline.OriginalFields[1] != sourceKeys.Count || sourceKeys.Where((key, index) => baseline.OriginalFields[2 + index] != key.Time || baseline.OriginalFields[18 + index] != key.RawValue).Any()) throw new InvalidDataException("The lighting DBC changed after the graph was loaded. Refresh the five-table graph before drafting this band.");
                _baselines[_draftIdentity] = baseline;
            }
            _original = sourceKeys.OrderBy(key => key.Time).ToList(); _draft = _drafts.TryGetValue(_draftIdentity, out var saved) ? saved.Select(key => key with { }).ToList() : (_original.Count == 0 ? [seed] : _original.Select(key => key with { }).ToList());
            _title.Text = $"{kind.ToString().ToUpperInvariant()} BAND {bandId:N0} · {name}"; _source.Text = _inputPath; _colorFields.IsVisible = kind == WorldLightingBandKind.Color; _floatFields.IsVisible = kind == WorldLightingBandKind.Float;
            RefreshDraft(0); _status.Text = _original.Count == 0 ? $"The source band contains no keys. Started a black/zero draft key at preview time {previewTime:N0}; writing it will make the band usable." : _draft.SequenceEqual(_original) ? "Loaded the exact source keys and retained their source preimage. Changes remain a draft until you choose a write action." : "Restored this band’s unsaved source-bound draft.";
        }
        catch (Exception exception) { _inputPath = null; _bandId = 0; _status.Text = exception.Message; DesktopCrashLogger.Log("World lighting band draft load failed", exception); }
    }

    private void PersistActive() { if (_draftIdentity is not null) _drafts[_draftIdentity] = _draft.Select(key => key with { }).ToList(); }

    private void RefreshDraft(int selectedIndex)
    {
        _refreshing = true; _draft = _draft.OrderBy(key => key.Time).ToList(); _keys.ItemsSource = _draft.ToArray(); _keys.SelectedIndex = _draft.Count == 0 ? -1 : Math.Clamp(selectedIndex, 0, _draft.Count - 1); _curve.SetDraft(_kind, _draft, _keys.SelectedIndex, _previewTime); _refreshing = false; ShowSelected(); PersistActive();
    }

    private void ShowSelected()
    {
        if (_refreshing || _keys.SelectedItem is not WorldLightBandDraftKey key) return; _keyTime.Value = key.Time;
        if (_kind == WorldLightingBandKind.Color) _hex.Text = WorldLightColor.FromPacked(key.RawValue).Hex; else _floatValue.Text = key.FloatValue.ToString("R", CultureInfo.InvariantCulture);
        _curve.SetSelected(_keys.SelectedIndex);
    }

    private void UpdateSelected()
    {
        try
        {
            if (_keys.SelectedIndex < 0) throw new InvalidOperationException("Select a key first."); var replacement = ReadFields();
            if (_draft.Where((_, index) => index != _keys.SelectedIndex).Any(key => key.Time == replacement.Time)) throw new InvalidOperationException($"Another key already uses time {replacement.Time:N0}.");
            var previous = _keys.SelectedIndex; _draft[previous] = replacement; RefreshDraft(_draft.OrderBy(key => key.Time).ToList().FindIndex(key => key == replacement)); _status.Text = "Draft key updated. No file has been changed.";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void AddAtPreview()
    {
        try
        {
            if (_inputPath is null) throw new InvalidOperationException("Select a lighting band first."); if (_draft.Count >= 16) throw new InvalidOperationException("This band already uses all 16 physical key slots."); if (_draft.Any(key => key.Time == _previewTime)) throw new InvalidOperationException($"A key already exists at preview time {_previewTime:N0}.");
            uint raw = _kind == WorldLightingBandKind.Color ? WorldLightingEditService.Pack(SampleColor(_previewTime)) : BitConverter.SingleToUInt32Bits(SampleFloat(_previewTime)); var added = new WorldLightBandDraftKey(_kind, _previewTime, raw); _draft.Add(added); RefreshDraft(_draft.OrderBy(key => key.Time).ToList().FindIndex(key => key == added)); _status.Text = "Added an interpolated draft key at the current preview time.";
        }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void RemoveSelected()
    {
        try { if (_keys.SelectedIndex < 0) throw new InvalidOperationException("Select a key first."); if (_draft.Count == 1) throw new InvalidOperationException("A lighting band must retain at least one key."); var index = _keys.SelectedIndex; _draft.RemoveAt(index); RefreshDraft(Math.Min(index, _draft.Count - 1)); _status.Text = "Removed the draft key. No file has been changed."; }
        catch (Exception exception) { _status.Text = exception.Message; }
    }

    private void ResetDraft() { if (_inputPath is null) return; _draft = _original.Count == 0 ? [new(_kind, _previewTime, _kind == WorldLightingBandKind.Color ? WorldLightingEditService.Pack(default) : BitConverter.SingleToUInt32Bits(0))] : _original.Select(key => key with { }).ToList(); RefreshDraft(0); _status.Text = _original.Count == 0 ? "Restored the empty source state as one safe draft key; no file has been changed." : "Restored every key from the loaded DBC. No file has been changed."; }

    private WorldLightBandDraftKey ReadFields()
    {
        var time = decimal.ToInt32(_keyTime.Value ?? -1); if (time is < 0 or > WorldLightingService.DayUnits) throw new InvalidOperationException($"Key time must stay within 0..{WorldLightingService.DayUnits:N0}.");
        if (_kind == WorldLightingBandKind.Color) return new(_kind, time, WorldLightingEditService.Pack(ParseColor(_hex.Text)));
        if (!float.TryParse(_floatValue.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value)) throw new InvalidOperationException("Enter a finite float value using a period as the decimal separator.");
        return new(_kind, time, BitConverter.SingleToUInt32Bits(value));
    }

    private async Task WriteStagedAsync()
    {
        try
        {
            var plan = BuildPlan(); var storage = TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Folder picker is unavailable."); var folders = await storage.OpenFolderPickerAsync(new() { Title = "Choose a staging folder for the edited lighting DBC", AllowMultiple = false }); var folder = folders.FirstOrDefault()?.TryGetLocalPath(); if (folder is null) return;
            var output = Path.Combine(folder, Path.GetFileName(plan.InputPath)); _status.Text = $"Writing and re-validating {Path.GetFileName(output)}…"; var result = await Task.Run(() => WorldLightingEditService.Apply(plan, output, overwrite: _replaceStaged.IsChecked == true)); _status.Text = $"Wrote {result.Keys:N0} verified key(s) to {result.OutputPath} · receipt {Path.GetFileName(result.ReceiptPath)}";
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("World lighting staged band write failed", exception); }
    }

    private async Task ApplySourceAsync()
    {
        try
        {
            var plan = BuildPlan(); _status.Text = $"Applying band {plan.BandId:N0} atomically and retaining the source as .bak…"; var result = await Task.Run(() => WorldLightingEditService.Apply(plan, plan.InputPath, overwrite: true, allowSourceReplacement: true)); _drafts.Remove(_draftIdentity!); _status.Text = $"Applied {result.Keys:N0} key(s) · backup {result.BackupPath} · reloading complete graph…"; SourceApplied?.Invoke(this, result);
        }
        catch (Exception exception) { _status.Text = exception.Message; DesktopCrashLogger.Log("World lighting source band write failed", exception); }
    }

    private WorldLightingBandEditPlan BuildPlan()
    {
        if (_inputPath is null || _bandId == 0) throw new InvalidOperationException("Select a lighting band first.");
        if (_draft.SequenceEqual(_original)) throw new InvalidOperationException("This band has no draft changes to write.");
        if (_draftIdentity is null || !_baselines.TryGetValue(_draftIdentity, out var baseline)) throw new InvalidOperationException("This band has no retained source preimage. Refresh the lighting graph before writing it.");
        return baseline with { CreatedUtc = DateTimeOffset.UtcNow, Keys = _draft.Select(key => new WorldLightingBandKeyEdit(key.Time, key.RawValue)).ToArray() };
    }

    private WorldLightColor SampleColor(int time) => WorldLightingService.Sample(new WorldLightColorBand(_bandId, 0, "draft", _draft.Select(key => new WorldLightColorKey(key.Time, key.Color, key.RawValue)).ToArray()), time);
    private float SampleFloat(int time) => WorldLightingService.Sample(new WorldLightFloatBand(_bandId, 0, "draft", _draft.Select(key => new WorldLightFloatKey(key.Time, key.FloatValue)).ToArray()), time);
    private static WorldLightColor ParseColor(string? text)
    {
        text = text?.Trim().TrimStart('#') ?? string.Empty; if (text.Length != 6 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)) throw new InvalidOperationException("Enter a six-digit RGB color such as #C6995E.");
        return new((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
    }
    private void RefreshSwatch() { try { var color = ParseColor(_hex.Text); _swatch.Background = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)); } catch { _swatch.Background = Brushes.Transparent; } }
    private static Button Accent(string text) => new() { Content = text, Background = Brush.Parse("#C58A2B"), Foreground = Brushes.Black, FontWeight = FontWeight.SemiBold };
    private static TextBlock Info(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") };
    private static TextBlock Label(string text) => new() { Text = text, FontSize = 10, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#C58A2B") };
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}

internal sealed class WorldLightBandCurveControl : Control
{
    private static readonly IBrush Background = Brush.Parse("#090D14"); private static readonly IBrush GridBrush = Brush.Parse("#202A39"); private static readonly IBrush SelectedBrush = Brush.Parse("#67C6E3"); private static readonly Typeface Typeface = new("Inter");
    private IReadOnlyList<WorldLightBandDraftKey> _keys = []; private WorldLightingBandKind _kind; private int _selected = -1; private int _previewTime;
    public event EventHandler<int>? KeySelected;
    public WorldLightBandCurveControl() { ClipToBounds = true; Focusable = true; }
    public void SetDraft(WorldLightingBandKind kind, IReadOnlyList<WorldLightBandDraftKey> keys, int selected, int previewTime) { _kind = kind; _keys = keys; _selected = selected; _previewTime = previewTime; InvalidateVisual(); }
    public void SetSelected(int selected) { _selected = selected; InvalidateVisual(); }
    public void SetPreviewTime(int time) { _previewTime = time; InvalidateVisual(); }
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.FillRectangle(Background, Bounds); var left = 22d; var right = Math.Max(left + 1, Bounds.Width - 12); var top = 14d; var bottom = Math.Max(top + 1, Bounds.Height - 24);
        for (var index = 0; index <= 4; index++) { var x = left + (right - left) * index / 4; var y = top + (bottom - top) * index / 4; context.DrawLine(new Pen(GridBrush, 1), new(x, top), new(x, bottom)); context.DrawLine(new Pen(GridBrush, 1), new(left, y), new(right, y)); }
        if (_keys.Count == 0) { Draw(context, "Select a band", left, top, Brush.Parse("#8E99AD")); return; }
        var previewX = X(_previewTime, left, right); context.DrawLine(new Pen(Brush.Parse("#D6A75A"), 1), new(previewX, top), new(previewX, bottom));
        if (_kind == WorldLightingBandKind.Color)
        {
            DrawChannel(context, key => key.Color.R / 255d, new Pen(Brush.Parse("#F06464"), 2), left, right, top, bottom);
            DrawChannel(context, key => key.Color.G / 255d, new Pen(Brush.Parse("#61D47A"), 2), left, right, top, bottom);
            DrawChannel(context, key => key.Color.B / 255d, new Pen(Brush.Parse("#649AF0"), 2), left, right, top, bottom);
        }
        else
        {
            var values = _keys.Select(key => (double)key.FloatValue).ToArray(); var min = values.Min(); var max = values.Max(); if (Math.Abs(max - min) < 0.000001) { min -= 0.5; max += 0.5; }
            DrawChannel(context, key => (key.FloatValue - min) / (max - min), new Pen(Brush.Parse("#E0A55B"), 2), left, right, top, bottom); Draw(context, $"{max:0.###}", 2, top, Brush.Parse("#8E99AD")); Draw(context, $"{min:0.###}", 2, bottom - 12, Brush.Parse("#8E99AD"));
        }
        for (var index = 0; index < _keys.Count; index++)
        {
            var key = _keys[index]; var normalized = _kind == WorldLightingBandKind.Color ? (key.Color.R + key.Color.G + key.Color.B) / (255d * 3) : NormalizeFloat(index); var point = new Point(X(key.Time, left, right), bottom - normalized * (bottom - top)); var brush = index == _selected ? SelectedBrush : _kind == WorldLightingBandKind.Color ? new SolidColorBrush(Color.FromRgb(key.Color.R, key.Color.G, key.Color.B)) : Brush.Parse("#E0A55B"); context.DrawEllipse(brush, new Pen(Brushes.White, 1), point, index == _selected ? 6 : 4, index == _selected ? 6 : 4);
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); if (_keys.Count == 0) return; var position = e.GetPosition(this); var left = 22d; var right = Math.Max(left + 1, Bounds.Width - 12); var nearest = _keys.Select((key, index) => (index, distance: Math.Abs(position.X - X(key.Time, left, right)))).OrderBy(value => value.distance).First(); if (nearest.distance <= 18) { KeySelected?.Invoke(this, nearest.index); e.Handled = true; }
    }
    private void DrawChannel(DrawingContext context, Func<WorldLightBandDraftKey, double> value, Pen pen, double left, double right, double top, double bottom)
    {
        Point? prior = null; foreach (var key in _keys) { var point = new Point(X(key.Time, left, right), bottom - Math.Clamp(value(key), 0, 1) * (bottom - top)); if (prior is not null) context.DrawLine(pen, prior.Value, point); prior = point; }
    }
    private double NormalizeFloat(int index) { var values = _keys.Select(key => (double)key.FloatValue).ToArray(); var min = values.Min(); var max = values.Max(); return Math.Abs(max - min) < 0.000001 ? 0.5 : (values[index] - min) / (max - min); }
    private static double X(int time, double left, double right) => left + Math.Clamp(time, 0, WorldLightingService.DayUnits) / (double)WorldLightingService.DayUnits * (right - left);
    private static void Draw(DrawingContext context, string text, double x, double y, IBrush brush) => context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, 10, brush), new Point(x, y));
}
