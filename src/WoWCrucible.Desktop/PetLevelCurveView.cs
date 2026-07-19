using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class PetLevelCurveView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly PetLevelCurveService _service = new();
    private readonly NumericUpDown _source = Number(416, uint.MaxValue);
    private readonly NumericUpDown _target = Number(900416, uint.MaxValue);
    private readonly NumericUpDown _start = Number(1, byte.MaxValue);
    private readonly NumericUpDown _end = Number(80, byte.MaxValue);
    private readonly NumericUpDown _health = Factor();
    private readonly NumericUpDown _mana = Factor();
    private readonly NumericUpDown _armor = Factor();
    private readonly NumericUpDown _attributes = Factor();
    private readonly NumericUpDown _damage = Factor();
    private readonly ComboBox _mode = new();
    private readonly TextBlock _summary = Status("Connect a world database, choose an existing source creature curve, and preview a target curve.");
    private readonly TextBox _sql = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBlock _status = Status("No curve has been loaded.");
    private readonly Button _preview = AccentButton("Load source + preview curve");
    private readonly Button _deploy = AccentButton("Deploy reviewed curve");
    private readonly Border _confirmation = new() { IsVisible = false, BorderBrush = Brush.Parse("#6E5426"), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
    private PetLevelCurvePreparedPlan? _prepared;
    private CancellationTokenSource? _operation;

    public event EventHandler? BackRequested;
    public event EventHandler<ReferencePickerRequest>? ReferenceLookupRequested;

    public PetLevelCurveView(DesktopWorkspaceSession session)
    {
        _session = session; _session.Changed += SessionChanged;
        _mode.ItemsSource = new[]
        {
            new WriteChoice(PetLevelCurveWriteMode.InsertMissing, "Insert missing levels only", "Existing target levels are preserved byte-for-byte."),
            new WriteChoice(PetLevelCurveWriteMode.UpdateExactRange, "Update exact range + insert missing", "Every existing target level in the previewed range is explicitly updated; no row is deleted.")
        };
        _mode.SelectedIndex = 0; _mode.SelectionChanged += (_, _) => RefreshSql();
        foreach (var input in Inputs()) input.ValueChanged += (_, _) => Invalidate();

        var back = new Button { Content = "← Pets" }; back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 10, Margin = new Thickness(12, 8), Children = { back, WithColumn(new TextBlock { Text = "PET LEVEL CURVE", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 1) } };
        var sourceFind = new Button { Content = "Find source name…" }; sourceFind.Click += (_, _) => FindCreature(_source, "Pet curve source creature");
        var targetFind = new Button { Content = "Find target name…" }; targetFind.Click += (_, _) => FindCreature(_target, "Pet curve target creature");
        var settings = new StackPanel { Spacing = 10, Margin = new Thickness(12), Children =
        {
            Help("Copy a complete stock/custom pet curve instead of inventing a straight line. Crucible retains every source level and custom column, then scales only the named stat families."),
            Field("Source creature entry", "Must already have every requested pet_levelstats level.", _source, sourceFind),
            Field("Target creature entry", "Receives new composite keys target entry + level.", _target, targetFind),
            Pair("First level", _start, "Last level", _end),
            Pair("Health scale", _health, "Mana scale", _mana),
            Pair("Armor scale", _armor, "All five attributes", _attributes),
            Field("Damage scale", "Applies equally to min_dmg and max_dmg.", _damage),
            Field("Write policy", "Choose whether existing target levels remain untouched or are explicitly replaced after stale-state verification.", _mode),
            _preview
        } };
        _preview.Click += async (_, _) => await PrepareAsync();
        var right = new TabControl { Items = { new TabItem { Header = "Curve summary", Content = new ScrollViewer { Content = new Border { Padding = new Thickness(16), Child = _summary } } }, new TabItem { Header = "Exact SQL preview", Content = _sql } } };
        var workspace = new Grid { ColumnDefinitions = new("2*,Auto,3*"), Children = { new ScrollViewer { Content = settings }, WithColumn(new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Brush.Parse("#2B3445") }, 1), WithColumn(right, 2) } };
        var export = new Button { Content = "Export reviewed SQL…" }; export.Click += async (_, _) => await ExportAsync(); _deploy.Click += (_, _) => PrepareCommit();
        var footer = new WrapPanel { Children = { export, _deploy, _status } };
        Content = new Grid { RowDefinitions = new("Auto,*,Auto,Auto"), Children = { new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading }, WithRow(workspace, 1), WithRow(footer, 2), WithRow(_confirmation, 3) } };
        RefreshConnectionStatus();
    }

    private async Task PrepareAsync()
    {
        if (!_session.DatabaseTested || _session.DatabaseProfile is null) { _status.Text = "Connect and test a world database in Server & SQL first."; return; }
        try
        {
            _operation?.Cancel(); _operation?.Dispose(); _operation = new CancellationTokenSource(); _preview.IsEnabled = false; _status.Text = "Reading the complete source and target level ranges…";
            var request = Request(); _prepared = await _service.PrepareAsync(_session.DatabaseProfile, request, _operation.Token); var existing = _prepared.ExpectedTargetRows.Count(pair => pair.Value is not null); var missing = _prepared.ExpectedTargetRows.Count - existing;
            _summary.Text = $"Source creature {request.SourceCreatureEntry} → target creature {request.TargetCreatureEntry}\nLevels {request.StartLevel}–{request.EndLevel} · {_prepared.Content.Rows.Count:N0} generated complete row(s)\nTarget currently has {existing:N0} matching level(s); {missing:N0} level(s) are missing.\n\nScales\nHealth × {request.Scale.Health}\nMana × {request.Scale.Mana}\nArmor × {request.Scale.Armor}\nStrength/Agility/Stamina/Intellect/Spirit × {request.Scale.Attributes}\nMinimum/maximum damage × {request.Scale.Damage}\n\n{Choice().Description}\n\nSafety: the complete target-row fingerprints and live schema are bound to this preview. Any intervening target or schema change makes deployment fail before a write. Source gaps, overflow, and malformed composite keys are refused. No target row is deleted.";
            RefreshSql(); _status.Text = $"Curve ready · {_prepared.Content.Rows.Count:N0} rows · review the selected policy and exact SQL.";
        }
        catch (OperationCanceledException) { _status.Text = "Curve preview cancelled."; }
        catch (Exception exception) { _prepared = null; _sql.Text = $"-- Curve preview failed: {exception.Message}"; _summary.Text = exception.Message; _status.Text = $"Cannot prepare curve: {exception.Message}"; DesktopCrashLogger.Log("Pet curve preview failed", exception); }
        finally { _preview.IsEnabled = true; }
    }

    private void PrepareCommit()
    {
        if (_prepared is null || _session.DatabaseProfile is null) { _status.Text = "Load and review a current curve before deployment."; return; }
        var choice = Choice(); var existing = _prepared.ExpectedTargetRows.Count(pair => pair.Value is not null); var missing = _prepared.ExpectedTargetRows.Count - existing;
        var cancel = new Button { Content = "Cancel" }; cancel.Click += (_, _) => _confirmation.IsVisible = false; var confirm = AccentButton(choice.Mode == PetLevelCurveWriteMode.InsertMissing ? "Insert missing levels" : "Update exact range"); confirm.Click += async (_, _) => await CommitAsync(confirm);
        _confirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = choice.Mode == PetLevelCurveWriteMode.InsertMissing ? $"Insert {missing:N0} missing level(s) and preserve all {existing:N0} existing level(s)?" : $"Explicitly update {existing:N0} existing level(s) and insert {missing:N0} missing level(s)? The stale preview guard remains mandatory.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _confirmation.IsVisible = true;
    }

    private async Task CommitAsync(Button button)
    {
        if (_prepared is null || _session.DatabaseProfile is null) return;
        try { button.IsEnabled = false; var result = await _service.ApplyAsync(_session.DatabaseProfile, _prepared, Choice().Mode); _status.Text = $"Curve committed transactionally · inserted {result.Inserted:N0} · updated {result.Updated:N0} · preserved {result.Skipped:N0}."; _confirmation.IsVisible = false; _prepared = null; }
        catch (Exception exception) { _status.Text = $"Curve deployment failed: {exception.Message}"; DesktopCrashLogger.Log("Pet curve deployment failed", exception); }
        finally { button.IsEnabled = true; }
    }

    private async Task ExportAsync()
    {
        if (_prepared is null) { _status.Text = "Load and review a curve before exporting SQL."; return; }
        try { var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export pet level curve SQL", SuggestedFileName = $"pet-curve-{_prepared.Request.TargetCreatureEntry}-{_prepared.Request.StartLevel}-{_prepared.Request.EndLevel}.sql", FileTypeChoices = [new FilePickerFileType("SQL") { Patterns = ["*.sql"] }] }); var path = file?.TryGetLocalPath(); if (path is not null) { await File.WriteAllTextAsync(path, _service.PreviewSql(_prepared, Choice().Mode) + Environment.NewLine); _status.Text = $"Exported reviewed curve SQL to {path}"; } }
        catch (Exception exception) { _status.Text = $"Curve export failed: {exception.Message}"; }
    }

    private PetLevelCurveRequest Request() => new(ToUInt(_source), ToUInt(_target), ToByte(_start), ToByte(_end), new(ToDecimal(_health), ToDecimal(_mana), ToDecimal(_armor), ToDecimal(_attributes), ToDecimal(_damage)));
    private WriteChoice Choice() => _mode.SelectedItem as WriteChoice ?? (WriteChoice)_mode.ItemsSource!.Cast<object>().First();
    private IEnumerable<NumericUpDown> Inputs() => new[] { _source, _target, _start, _end, _health, _mana, _armor, _attributes, _damage };
    private void Invalidate() { if (_prepared is null) return; _prepared = null; _confirmation.IsVisible = false; _summary.Text = "Inputs changed. Load the source again so the preview and stale-state fingerprints match these values."; _sql.Text = "-- Reload required after changing curve inputs."; _status.Text = "Curve inputs changed · reload before export or deployment."; }
    private void RefreshSql() { _confirmation.IsVisible = false; if (_prepared is not null) _sql.Text = _service.PreviewSql(_prepared, Choice().Mode); }
    private void SessionChanged(object? sender, EventArgs e) { Invalidate(); RefreshConnectionStatus(); }
    private void RefreshConnectionStatus() { if (_prepared is null) _status.Text = _session.DatabaseTested && _session.DatabaseCapabilities?.FindTable("pet_levelstats") is not null ? $"Live pet_levelstats ready · {_session.DatabaseCapabilities.Database}" : "Connect a world database containing pet_levelstats in Server & SQL."; }
    private void FindCreature(NumericUpDown input, string context) => ReferenceLookupRequested?.Invoke(this, new(ReferenceDomain.Creature, context, ToUInt(input), selected => input.Value = selected));
    public void Dispose() { _session.Changed -= SessionChanged; _operation?.Cancel(); _operation?.Dispose(); }

    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Pet curve workspace is not attached to the main window.");
    private static NumericUpDown Number(decimal value, decimal maximum) => new() { Minimum = 0, Maximum = maximum, Value = value, Increment = 1, FormatString = "0" };
    private static NumericUpDown Factor() => new() { Minimum = 0, Maximum = 1000, Value = 1, Increment = 0.05m, FormatString = "0.###" };
    private static uint ToUInt(NumericUpDown input) => decimal.ToUInt32(input.Value ?? 0);
    private static byte ToByte(NumericUpDown input) => decimal.ToByte(input.Value ?? 0);
    private static decimal ToDecimal(NumericUpDown input) => input.Value ?? 0;
    private static TextBlock Help(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B7C2D5") };
    private static Control Field(string label, string help, Control input, Control? action = null) { var controls = new Grid { ColumnDefinitions = action is null ? new("*") : new("*,Auto"), ColumnSpacing = 8, Children = { input } }; if (action is not null) controls.Children.Add(WithColumn(action, 1)); return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A8") }, controls } }; }
    private static Control Pair(string leftLabel, Control left, string rightLabel, Control right) => new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 10, Children = { new StackPanel { Children = { new TextBlock { Text = leftLabel }, left } }, WithColumn(new StackPanel { Children = { new TextBlock { Text = rightLabel }, right } }, 1) } };
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8"), VerticalAlignment = VerticalAlignment.Center };
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
    private sealed record WriteChoice(PetLevelCurveWriteMode Mode, string Label, string Description) { public override string ToString() => Label; }
}
