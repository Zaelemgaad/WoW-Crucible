using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Selection;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class ClientWorkspaceView : UserControl, IDisposable
{
    private readonly DesktopWorkspaceSession _session;
    private readonly TextBox _clientRoot = new();
    private readonly TextBox _indexRoot = new();
    private readonly TextBox _externalListfile = new() { PlaceholderText = "Optional path listfile for name recovery" };
    private readonly ListBox _archives = new() { SelectionMode = SelectionMode.Single };
    private readonly ListBox _looseFiles = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBlock _clientSummary = Status("Choose a client or load an existing resumable index.");
    private readonly TextBlock _clientGuidance = Status("Archive scope, content counts, unresolved names, and compatibility guidance appear here.");
    private readonly TextBlock _operationStatus = Status("Ready");
    private ClientArchiveIndex? _loadedIndex;

    private readonly TextBox _clientDbcRoot = new() { PlaceholderText = "Extracted effective DBFilesClient folder" };
    private readonly TextBox _coreSourceRoot = new() { PlaceholderText = "Optional current AzerothCore/TrinityCore source" };
    private readonly ListBox _serverPlanItems = new();
    private readonly TextBlock _serverPlanSummary = Status("Analyze extracted client DBCs against the detected server before staging anything.");
    private ClientServerDeploymentPlan? _serverPlan;

    private readonly TextBox _fusionBase = new() { PlaceholderText = "Extracted stock/effective base folder" };
    private readonly TextBox _fusionSources = new() { AcceptsReturn = true, PlaceholderText = "One extracted override folder per line" };
    private readonly ListBox _fusionItems = new();
    private readonly TextBlock _fusionSummary = Status("Fusion is additive-first: base-identical files are omitted and path conflicts remain blocked for review.");
    private ClientFusionPlan? _fusionPlan;
    private ClientFusionDbcPlan? _fusionDbcPlan;
    private ClientFusionDbcResult? _fusionDbcResult;
    private ClientFusionDbcRemapPlan? _fusionDbcRemapPlan;
    private ClientFusionDbcRemapResult? _fusionDbcRemapResult;
    private readonly TextBox _releaseSourceRoot = new() { PlaceholderText = "Folder containing the exact player-facing files" };
    private readonly TextBox _releaseBundleRoot = new() { PlaceholderText = "New portable release-bundle folder" };
    private readonly TextBox _releaseName = new() { Text = "Crucible Player Release", PlaceholderText = "Release name" };
    private readonly TextBox _releaseChannel = new() { Text = "public", PlaceholderText = "Channel identity" };
    private readonly TextBox _releaseChangelog = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Player-visible changes and compatibility notes" };
    private readonly TextBox _releaseOptionalRules = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, PlaceholderText = "Optional group mappings, one per line: HD Textures|Data\\patch-HD.MPQ" };
    private readonly TextBox _releaseTargetRoot = new() { PlaceholderText = "Target WoW client root containing Data" };
    private readonly TextBox _releaseSelectedGroups = new() { PlaceholderText = "Optional groups to install, comma separated" };
    private readonly ListBox _releaseActions = new();
    private readonly TextBlock _releaseSummary = Status("Create or select a release bundle, choose optional groups, then review an exact install plan.");
    private readonly Border _releaseConfirmation = new() { IsVisible = false, Padding = new Thickness(10), BorderBrush = Brush.Parse("#755A2B"), BorderThickness = new Thickness(1) };
    private ClientReleasePlan? _releasePlan;
    private CancellationTokenSource? _operation;
    private readonly List<Button> _operationButtons = [];

    public event EventHandler? BackRequested;
    public event EventHandler<string>? OpenArchiveRequested;

    public ClientWorkspaceView(DesktopWorkspaceSession session)
    {
        _session = session;
        LoadDefaults();
        ConfigureTemplates();

        var back = new Button { Content = "← Editor" };
        back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
        var heading = new Grid
        {
            ColumnDefinitions = new("Auto,*"),
            Margin = new Thickness(12, 8),
            Children =
            {
                back,
                WithColumn(new TextBlock { Text = "CLIENT WORKSHOP", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0) }, 1)
            }
        };
        var tabs = new TabControl
        {
            Margin = new Thickness(10),
            Items =
            {
                new TabItem { Header = "Inspect / index / extract", Content = InspectorPage() },
                new TabItem { Header = "Client → server DBC plan", Content = ServerPlanPage() },
                new TabItem { Header = "Additive client fusion", Content = FusionPage() },
                new TabItem { Header = "Player release & rollback", Content = ReleasePage() }
            }
        };
        Content = new Grid
        {
            RowDefinitions = new("Auto,*,Auto"),
            Children =
            {
                new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 0, 0, 1), Child = heading },
                WithRow(tabs, 1),
                WithRow(new Border { BorderBrush = Brush.Parse("#2B3445"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(12, 7), Child = _operationStatus }, 2)
            }
        };
        if (File.Exists(Path.Combine(_indexRoot.Text ?? string.Empty, "client-index.json"))) LoadIndex();
    }

    private void ConfigureTemplates()
    {
        _archives.ItemTemplate = new FuncDataTemplate<ClientArchiveSummary>((archive, _) => archive is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("Auto,2*,Auto,Auto,Auto,*"),
            ColumnSpacing = 10,
            Margin = new Thickness(3, 2),
            Children =
            {
                new TextBlock { Text = archive.Scope.ToString(), Foreground = ScopeBrush(archive.Scope) },
                WithColumn(new TextBlock { Text = archive.RelativePath, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                WithColumn(new TextBlock { Text = $"{archive.PayloadFiles:N0} files" }, 2),
                WithColumn(new TextBlock { Text = $"{archive.AnonymousFiles:N0} unnamed" }, 3),
                WithColumn(new TextBlock { Text = FormatBytes(archive.Length) }, 4),
                WithColumn(new TextBlock { Text = archive.Error ?? (archive.Sha256 is null ? "indexed" : "SHA-256 ready"), TextTrimming = TextTrimming.CharacterEllipsis }, 5)
            }
        });
        _looseFiles.ItemTemplate = new FuncDataTemplate<ClientLooseFileSummary>((file, _) => file is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("Auto,2*,Auto,*"),
            ColumnSpacing = 10,
            Margin = new Thickness(3, 2),
            Children =
            {
                new TextBlock { Text = file.Scope.ToString() },
                WithColumn(new TextBlock { Text = file.RelativePath, TextTrimming = TextTrimming.CharacterEllipsis }, 1),
                WithColumn(new TextBlock { Text = FormatBytes(file.Length) }, 2),
                WithColumn(new TextBlock { Text = file.Sha256 ?? "metadata only", TextTrimming = TextTrimming.CharacterEllipsis }, 3)
            }
        });
        _serverPlanItems.ItemTemplate = new FuncDataTemplate<ClientServerPlanEntry>((entry, _) => entry is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("2*,Auto,Auto,3*"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = entry.DbcFileName }, WithColumn(new TextBlock { Text = entry.Status.ToString(), Foreground = PlanBrush(entry.Status) }, 1),
                WithColumn(new TextBlock { Text = entry.SqlTableName ?? entry.Consumption.ToString() }, 2), WithColumn(new TextBlock { Text = entry.Guidance, TextTrimming = TextTrimming.CharacterEllipsis }, 3)
            }
        });
        _fusionItems.ItemTemplate = new FuncDataTemplate<ClientFusionEntry>((entry, _) => entry is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("2*,Auto,Auto,3*"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = entry.ArchivePath, TextTrimming = TextTrimming.CharacterEllipsis }, WithColumn(new TextBlock { Text = entry.Status.ToString(), Foreground = FusionBrush(entry.Status) }, 1),
                WithColumn(new TextBlock { Text = $"{entry.Candidates.Count:N0} source(s)" }, 2), WithColumn(new TextBlock { Text = entry.Guidance, TextTrimming = TextTrimming.CharacterEllipsis }, 3)
            }
        });
        _releaseActions.ItemTemplate = new FuncDataTemplate<ClientReleaseAction>((action, _) => action is null ? new Grid() : new Grid
        {
            ColumnDefinitions = new("Auto,2*,Auto,3*"), ColumnSpacing = 10, Margin = new Thickness(3, 2), Children =
            {
                new TextBlock { Text = action.Kind.ToString(), Foreground = ReleaseBrush(action.Kind) },
                WithColumn(new TextBlock { Text = action.RelativePath, TextWrapping = TextWrapping.Wrap }, 1),
                WithColumn(new TextBlock { Text = action.OptionalGroup ?? "required" }, 2),
                WithColumn(new TextBlock { Text = action.Detail, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#9AA5B7") }, 3)
            }
        });
        _archives.SelectionChanged += async (_, _) => await ExplainArchiveAsync();
        _looseFiles.SelectionChanged += (_, _) => ExplainLooseFile();
    }

    private Control InspectorPage()
    {
        var chooseClient = Button("Client…", async () => { var path = await PickFolderAsync("Select the WoW client root containing Data and Wow.exe"); if (path is not null) { _clientRoot.Text = path; if (string.IsNullOrWhiteSpace(_indexRoot.Text)) _indexRoot.Text = Path.Combine(path, ".crucible-index"); } });
        var chooseIndex = Button("Index folder…", async () => { var path = await PickFolderAsync("Select the resumable client-index folder"); if (path is not null) _indexRoot.Text = path; });
        var chooseList = Button("Listfile…", async () => { var path = await PickFileAsync("Select an optional path listfile", ["*.txt", "*"]); if (path is not null) _externalListfile.Text = path; });
        var load = Button("Load existing index", () => { LoadIndex(); return Task.CompletedTask; });
        var index = AccentButton("Index or resume"); index.Click += async (_, _) => await BuildIndexAsync(); Register(index);
        var cancel = new Button { Content = "Cancel operation" }; cancel.Click += (_, _) => _operation?.Cancel();
        var open = Button("Open selected archive", () => { OpenSelectedArchive(); return Task.CompletedTask; });
        var extract = AccentButton("Extract selected archive"); extract.Click += async (_, _) => await ExtractSelectedAsync(); Register(extract);
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 7 };
        AddPath(paths, 0, "Client root", _clientRoot, chooseClient, null); AddPath(paths, 1, "Index folder", _indexRoot, chooseIndex, load); AddPath(paths, 2, "Path corpus", _externalListfile, chooseList, null);
        var inventory = new TabControl { Items = { new TabItem { Header = "MPQ archives", Content = _archives }, new TabItem { Header = "Loose runtime / AddOns / config", Content = _looseFiles } } };
        return new Grid
        {
            RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(8), Children =
            {
                paths,
                WithRow(new WrapPanel { Children = { index, cancel, open, extract } }, 1),
                WithRow(inventory, 2),
                WithRow(new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { Card(_clientSummary), WithColumn(Card(_clientGuidance), 1) } }, 3)
            }
        };
    }

    private Control ServerPlanPage()
    {
        var clientDbc = Button("DBCs…", async () => { var path = await PickFolderAsync("Select extracted effective client DBCs"); if (path is not null) _clientDbcRoot.Text = path; });
        var core = Button("Core source…", async () => { var path = await PickFolderAsync("Select optional current core source"); if (path is not null) { _coreSourceRoot.Text = path; _session.Settings.CoreSourcePath = path; _session.Settings.Save(); } });
        var analyze = AccentButton("Analyze client against server"); analyze.Click += async (_, _) => await AnalyzeServerPlanAsync(); Register(analyze);
        var stage = AccentButton("Stage reviewed plan…"); stage.Click += async (_, _) => await StageServerPlanAsync(); Register(stage);
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 7 };
        AddPath(paths, 0, "Client DBC root", _clientDbcRoot, clientDbc, null); AddPath(paths, 1, "Core source", _coreSourceRoot, core, null);
        return new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(8), Children = { paths, WithRow(new WrapPanel { Children = { analyze, stage } }, 1), WithRow(_serverPlanItems, 2), WithRow(Card(_serverPlanSummary), 3) } };
    }

    private Control FusionPage()
    {
        var chooseBase = Button("Base…", async () => { var path = await PickFolderAsync("Select the extracted stock/effective client base"); if (path is not null) _fusionBase.Text = path; });
        var addSource = Button("Add source…", async () => { var path = await PickFolderAsync("Add an extracted override source"); if (path is not null) _fusionSources.Text = string.Join(Environment.NewLine, Lines(_fusionSources.Text).Append(path).Distinct(StringComparer.OrdinalIgnoreCase)); });
        var analyze = AccentButton("Analyze additive fusion"); analyze.Click += async (_, _) => await AnalyzeFusionAsync(); Register(analyze);
        var analyzeDbcs = new Button { Content = "Quick DBC equality / additions" }; analyzeDbcs.Click += async (_, _) => await AnalyzeFusionDbcsAsync(); Register(analyzeDbcs);
        var writeDbcs = new Button { Content = "Write quick additive DBCs…" }; writeDbcs.Click += async (_, _) => await ApplyFusionDbcsAsync(); Register(writeDbcs);
        var planRemap = AccentButton("Plan ID + reference remap"); planRemap.Click += async (_, _) => await AnalyzeFusionDbcRemapAsync(); Register(planRemap);
        var writeRemap = AccentButton("Write dependency-safe DBCs…"); writeRemap.Click += async (_, _) => await ApplyFusionDbcRemapAsync(); Register(writeRemap);
        var stage = AccentButton("Stage reviewed patch…"); stage.Click += async (_, _) => await StageFusionAsync(); Register(stage);
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto"), RowDefinitions = new("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 7 };
        AddPath(paths, 0, "Effective base", _fusionBase, chooseBase, null); AddPath(paths, 1, "Override sources", _fusionSources, addSource, null);
        return new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(8), Children = { paths, WithRow(new WrapPanel { Children = { analyze, analyzeDbcs, writeDbcs, planRemap, writeRemap, stage } }, 1), WithRow(_fusionItems, 2), WithRow(Card(_fusionSummary), 3) } };
    }

    private Control ReleasePage()
    {
        var source = Button("Source…", async () => { var path = await PickFolderAsync("Choose the exact player release source"); if (path is not null) _releaseSourceRoot.Text = path; });
        var bundle = Button("Bundle parent…", async () => { var path = await PickFolderAsync("Choose a parent for a new portable release bundle"); if (path is not null) { var stem = string.Concat((_releaseName.Text ?? "release").Where(char.IsLetterOrDigit)); if (stem.Length == 0) stem = "Release"; _releaseBundleRoot.Text = Path.Combine(path, $"Crucible-{stem}-{DateTime.Now:yyyyMMdd-HHmmss}"); } });
        var target = Button("Client…", async () => { var path = await PickFolderAsync("Choose the target WoW client root containing Data"); if (path is not null) _releaseTargetRoot.Text = path; });
        var create = AccentButton("Build immutable release bundle"); create.Click += async (_, _) => await CreateReleaseBundleAsync(); Register(create);
        var plan = AccentButton("Review client install plan"); plan.Click += async (_, _) => await PlanReleaseAsync(); Register(plan);
        var export = new Button { Content = "Export reviewed plan…" }; export.Click += async (_, _) => await ExportReleasePlanAsync();
        var install = AccentButton("Install reviewed release…"); install.Click += async (_, _) => await PrepareReleaseInstallAsync(); Register(install);
        var rollback = new Button { Content = "Validate rollback receipt…" }; rollback.Click += async (_, _) => await PrepareReleaseRollbackAsync(); Register(rollback);
        var paths = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), RowDefinitions = new("Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 7 };
        AddPath(paths, 0, "Release source", _releaseSourceRoot, source, null); AddPath(paths, 1, "Bundle / manifest", _releaseBundleRoot, bundle, null); AddPath(paths, 2, "Target client", _releaseTargetRoot, target, null);
        var identity = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 8, Children = { new StackPanel { Children = { new TextBlock { Text = "Release name" }, _releaseName } }, WithColumn(new StackPanel { Children = { new TextBlock { Text = "Channel (stable ownership identity)" }, _releaseChannel } }, 1) } };
        var configuration = new StackPanel { Spacing = 7, Children = { paths, identity, new TextBlock { Text = "Changelog" }, _releaseChangelog, new TextBlock { Text = "Optional groups · group|client-relative prefix · a group may own multiple prefixes" }, _releaseOptionalRules, new TextBlock { Text = "Optional groups selected for this target" }, _releaseSelectedGroups, new WrapPanel { Children = { create, plan, export, install, rollback } }, _releaseConfirmation, Card(_releaseSummary), new TextBlock { Text = "Safety contract: bundles and plans bind every payload by SHA-256. Updates back up every replaced or removed file. Only unchanged files proven owned by the previous Crucible release can be pruned. Installation closes Wow.exe only when it belongs to the selected client and always clears that client's Cache. This is an offline/local distribution foundation; network downloads and cryptographic channel signing are not claimed here.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#8995A9") } } };
        return new Grid { RowDefinitions = new("*,*"), RowSpacing = 8, Margin = new Thickness(8), Children = { new ScrollViewer { Content = configuration }, WithRow(_releaseActions, 1) } };
    }

    private async Task CreateReleaseBundleAsync()
    {
        Begin("Hashing and copying the exact release payload into a new immutable bundle…"); _releaseConfirmation.IsVisible = false;
        try
        {
            var source = _releaseSourceRoot.Text ?? string.Empty; var output = _releaseBundleRoot.Text ?? string.Empty; var name = _releaseName.Text ?? string.Empty; var channel = _releaseChannel.Text ?? string.Empty; var changelog = _releaseChangelog.Text;
            var rules = ParseReleaseRules(_releaseOptionalRules.Text); var progress = new Progress<ClientReleaseProgress>(value => _operationStatus.Text = $"{value.Completed:N0}/{value.Total:N0} · {value.Stage} · {value.RelativePath}");
            var result = await Task.Run(() => ClientReleaseService.CreateBundle(source, output, name, channel, changelog, rules, progress), _operation!.Token);
            _releaseBundleRoot.Text = result.BundleRoot; _releasePlan = null; _releaseActions.ItemsSource = null;
            var groups = result.Manifest.Files.Where(file => file.OptionalGroup is not null).GroupBy(file => file.OptionalGroup, StringComparer.OrdinalIgnoreCase).Select(group => $"{group.Key}: {group.Count():N0}");
            _releaseSummary.Text = $"Bundle ready · {result.Manifest.Files.Count:N0} file(s) · {FormatBytes(result.PayloadBytes)} · content {result.Manifest.ContentId}\nOptional groups: {(groups.Any() ? string.Join(" · ", groups) : "none")}\n{result.ManifestPath}";
            _operationStatus.Text = "Release bundle built without changing any client.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Release bundle creation cancelled before publication."; }
        catch (Exception exception) { Fail("Release bundle creation failed", exception); }
        finally { End(); }
    }

    private async Task PlanReleaseAsync()
    {
        Begin("Verifying every release payload and comparing the exact target client preimages…"); _releaseConfirmation.IsVisible = false;
        try
        {
            var progress = new Progress<ClientReleaseProgress>(value => _operationStatus.Text = $"{value.Completed:N0}/{value.Total:N0} · {value.Stage} · {value.RelativePath}");
            var plan = await Task.Run(() => ClientReleaseService.CreatePlan(_releaseBundleRoot.Text ?? string.Empty, _releaseTargetRoot.Text ?? string.Empty, ParseGroups(_releaseSelectedGroups.Text), progress), _operation!.Token);
            _releasePlan = plan; _releaseActions.ItemsSource = plan.Actions;
            _releaseSummary.Text = $"{(plan.Ready ? "READY" : "BLOCKED")} · add {plan.Adds:N0} · replace {plan.Replacements:N0} · remove owned {plan.Removals:N0} · unchanged {plan.Unchanged:N0}\nSelected optional groups: {(plan.SelectedOptionalGroups.Count == 0 ? "none" : string.Join(", ", plan.SelectedOptionalGroups))}\n{string.Join(Environment.NewLine, plan.Blockers.Select(blocker => "• " + blocker))}";
            _operationStatus.Text = plan.Ready ? "Exact release plan is ready for export or confirmed installation." : $"Release plan has {plan.Blockers.Count:N0} blocker(s); no installation is available.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Release planning cancelled."; }
        catch (Exception exception) { _releasePlan = null; _releaseActions.ItemsSource = null; Fail("Release planning failed", exception); }
        finally { End(); }
    }

    private async Task ExportReleasePlanAsync()
    {
        if (_releasePlan is null) { _operationStatus.Text = "Review a release plan first."; return; }
        var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Export hash-bound client release plan", SuggestedFileName = "client-release-plan.crucible.json", FileTypeChoices = [new FilePickerFileType("Crucible JSON") { Patterns = ["*.json"] }] }); var path = file?.TryGetLocalPath(); if (path is null) return;
        try { ClientReleaseService.SavePlan(path, _releasePlan); _operationStatus.Text = $"Release plan exported: {path}"; } catch (Exception exception) { Fail("Release plan export failed", exception); }
    }

    private async Task PrepareReleaseInstallAsync()
    {
        if (_releasePlan is not { Ready: true } plan) { _operationStatus.Text = "Create a blocker-free release plan first."; return; }
        var file = await Storage().SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save exact client release receipt", SuggestedFileName = $"client-release-{DateTime.Now:yyyyMMdd-HHmmss}.receipt.json", FileTypeChoices = [new FilePickerFileType("Crucible receipt") { Patterns = ["*.json"] }] }); var receipt = file?.TryGetLocalPath(); if (receipt is null) return;
        var cancel = new Button { Content = "Cancel" }; var confirm = AccentButton("Confirm client update"); cancel.Click += (_, _) => _releaseConfirmation.IsVisible = false; confirm.Click += async (_, _) => await ApplyReleaseAsync(confirm, plan, receipt);
        _releaseConfirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Install this exact reviewed release into {plan.TargetClientRoot}?\n{plan.Adds:N0} add · {plan.Replacements:N0} replace · {plan.Removals:N0} owned removal. Wow processes from this client will be force-closed, every preimage will be backed up, ownership recorded, and Cache deleted.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _releaseConfirmation.IsVisible = true;
    }

    private async Task ApplyReleaseAsync(Button button, ClientReleasePlan plan, string receipt)
    {
        Begin("Rechecking the plan, closing the selected client, and preparing exact backups…"); button.IsEnabled = false;
        try { var result = await Task.Run(() => ClientReleaseService.Apply(plan, receipt), _operation!.Token); _releaseConfirmation.IsVisible = false; _releaseSummary.Text = $"INSTALLED · {result.ChangedFiles:N0} changed · {result.RemovedFiles:N0} removed · {result.Cache.DeletedFiles:N0} cache file(s) cleared\nReceipt: {result.ReceiptPath}\nOwnership: {result.InstalledStatePath}"; _operationStatus.Text = "Player release installed and rollback evidence retained."; await PlanReleaseAsync(); }
        catch (Exception exception) { Fail("Player release installation failed safely", exception); }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task PrepareReleaseRollbackAsync()
    {
        var files = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Choose a committed client release receipt", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("Crucible receipt") { Patterns = ["*.json"] }] }); var receiptPath = files.FirstOrDefault()?.TryGetLocalPath(); if (receiptPath is null) return;
        try
        {
            var receipt = await Task.Run(() => ClientReleaseService.ValidateRollback(receiptPath)); _releaseActions.ItemsSource = receipt.Actions;
            var cancel = new Button { Content = "Cancel" }; var confirm = new Button { Content = "Confirm exact rollback" }; cancel.Click += (_, _) => _releaseConfirmation.IsVisible = false; confirm.Click += async (_, _) => await ApplyReleaseRollbackAsync(confirm, receiptPath);
            _releaseConfirmation.Child = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 8, Children = { new TextBlock { Text = $"Rollback the committed release at {receipt.TargetClientRoot}? Every current postimage and backup passed validation. The previous files and ownership state will be restored, and Cache will be cleared again.", TextWrapping = TextWrapping.Wrap }, WithColumn(cancel, 1), WithColumn(confirm, 2) } }; _releaseConfirmation.IsVisible = true; _releaseSummary.Text = $"Rollback validated · {receipt.Actions.Count:N0} reviewed action(s) · backup {receipt.BackupRoot}";
        }
        catch (Exception exception) { Fail("Release rollback validation failed", exception); }
    }

    private async Task ApplyReleaseRollbackAsync(Button button, string receiptPath)
    {
        Begin("Rechecking postimages and restoring the exact release preimages…"); button.IsEnabled = false;
        try { var result = await Task.Run(() => ClientReleaseService.Rollback(receiptPath), _operation!.Token); _releaseConfirmation.IsVisible = false; _releaseSummary.Text = $"ROLLED BACK · {result.RestoredFiles:N0} restored · {result.RemovedFiles:N0} release-only files removed · {result.Cache.DeletedFiles:N0} cache file(s) cleared\n{result.ReceiptPath}"; _operationStatus.Text = "Exact client rollback completed."; _releasePlan = null; }
        catch (Exception exception) { Fail("Client release rollback failed", exception); }
        finally { button.IsEnabled = true; End(); }
    }

    private async Task BuildIndexAsync()
    {
        Begin("Indexing or resuming the client…");
        try
        {
            var client = _clientRoot.Text ?? string.Empty; var index = _indexRoot.Text ?? string.Empty;
            var executable = File.Exists(_session.Settings.ClientExecutablePath) ? _session.Settings.ClientExecutablePath : null;
            var listfile = File.Exists(_externalListfile.Text) ? _externalListfile.Text : null;
            var progress = new Progress<ClientIndexProgress>(value => _operationStatus.Text = $"{value.CompletedArchives:N0}/{value.TotalArchives:N0} · {value.Stage}{(value.Cached ? " (cached)" : string.Empty)} · {value.ArchivePath}");
            _loadedIndex = await Task.Run(() => new ClientArchiveIndexService().Build(client, index, true, progress, _operation!.Token, listfile, executable), _operation!.Token);
            SaveClientSettings(_loadedIndex); DisplayIndex();
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Client indexing cancelled safely; Index or resume continues from the partial catalog."; }
        catch (Exception exception) { Fail("Client indexing failed", exception); }
        finally { End(); }
    }

    private void LoadIndex()
    {
        try { _loadedIndex = ClientArchiveIndexService.Load(_indexRoot.Text ?? string.Empty); _clientRoot.Text = _loadedIndex.ClientRoot; SaveClientSettings(_loadedIndex); DisplayIndex(); }
        catch (Exception exception) { Fail("Could not load client index", exception); }
    }

    private void DisplayIndex()
    {
        if (_loadedIndex is null) return;
        _archives.ItemsSource = _loadedIndex.Archives.OrderBy(archive => archive.Scope).ThenBy(archive => archive.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        _looseFiles.ItemsSource = (_loadedIndex.LooseFiles ?? []).OrderBy(file => file.Scope).ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        var active = _loadedIndex.Archives.Count(archive => archive.Scope is not ClientArchiveScope.InactiveLocale and not ClientArchiveScope.Backup);
        _clientSummary.Text = $"{_loadedIndex.Name} · {(_loadedIndex.Complete ? "complete" : "resumable partial")}\n{_loadedIndex.Archives.Count:N0} MPQs · {active:N0} effective · {_loadedIndex.Archives.Sum(archive => archive.PayloadFiles):N0} payloads\n{_loadedIndex.Archives.Sum(archive => archive.AnonymousFiles):N0} unresolved names · {_loadedIndex.LooseFiles?.Count ?? 0:N0} loose files\nLocale {_loadedIndex.ActiveLocale ?? "not proven"} · executable {_loadedIndex.Executable?.Path ?? "not detected"}";
        _operationStatus.Text = $"Loaded client index {_indexRoot.Text}";
    }

    private async Task ExplainArchiveAsync()
    {
        if (_loadedIndex is null || _archives.SelectedItem is not ClientArchiveSummary archive) return;
        try
        {
            var path = Path.Combine(_indexRoot.Text ?? string.Empty, archive.ContentIndexFile);
            var content = await Task.Run(() => JsonSerializer.Deserialize<ArchiveContentIndex>(File.ReadAllText(path)) ?? throw new InvalidDataException("Content index is invalid."));
            var files = content.Files.Where(file => !file.IsMetadata).ToArray();
            int CountPrefix(string prefix) => files.Count(file => file.ArchivePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            int CountExtensions(params string[] extensions) => files.Count(file => extensions.Contains(Path.GetExtension(file.ArchivePath), StringComparer.OrdinalIgnoreCase));
            _clientGuidance.Text = $"{ScopeGuidance(archive.Scope)}\n\n{CountPrefix("DBFilesClient\\"):N0} DBCs · {CountPrefix("Interface\\"):N0} UI files · {CountExtensions(".blp"):N0} textures · {CountExtensions(".m2", ".wmo", ".skin"):N0} models · {CountExtensions(".adt", ".wdt", ".wdl"):N0} map files\n{archive.AnonymousFiles:N0} unresolved names remain quarantined by archive/hash.";
        }
        catch (Exception exception) { _clientGuidance.Text = $"{ScopeGuidance(archive.Scope)}\n\nContent details unavailable: {exception.Message}"; }
    }

    private void ExplainLooseFile()
    {
        if (_looseFiles.SelectedItem is not ClientLooseFileSummary file) return;
        _clientGuidance.Text = file.Scope switch
        {
            ClientLooseFileScope.Runtime => $"Runtime dependency: {file.RelativePath}. Preserve its exact identity with the client profile. SHA-256 {file.Sha256 ?? "not recorded"}.",
            ClientLooseFileScope.AddOn => $"Loose AddOn: {file.RelativePath}. It is active outside the MPQ stack and must be compared separately from archived Interface files.",
            ClientLooseFileScope.Configuration => $"Configuration: {file.RelativePath}. Useful for explaining locale/loader behavior but normally installation-specific.",
            ClientLooseFileScope.Volatile => $"Generated/volatile: {file.RelativePath}. Inventoried for completeness but excluded from reusable content by default.",
            _ => $"Loose file: {file.RelativePath}. Review its role before treating the client as reproducible."
        };
    }

    private void OpenSelectedArchive()
    {
        if (_loadedIndex is null || _archives.SelectedItem is not ClientArchiveSummary archive) { _operationStatus.Text = "Select an indexed MPQ first."; return; }
        OpenArchiveRequested?.Invoke(this, Path.Combine(_loadedIndex.ClientRoot, archive.RelativePath));
    }

    private async Task ExtractSelectedAsync()
    {
        if (_loadedIndex is null || _archives.SelectedItem is not ClientArchiveSummary archive) { _operationStatus.Text = "Select an indexed MPQ first."; return; }
        var destination = await PickFolderAsync("Select an empty or resumable extraction folder"); if (destination is null) return;
        Begin("Extracting resolved paths from the indexed archive…");
        try
        {
            var indexRoot = _indexRoot.Text!;
            var progress = new Progress<(int Done, int Total, string Path)>(value => _operationStatus.Text = $"{value.Done:N0}/{value.Total:N0} · {value.Path}");
            var result = await Task.Run(() => ClientArchiveIndexService.ExtractIndexed(indexRoot, archive.RelativePath, destination, resolvedOnly: true, progress: progress, cancellationToken: _operation!.Token), _operation!.Token);
            _operationStatus.Text = $"Extracted {result.ExtractedFiles:N0}; resumed past {result.SkippedExistingFiles:N0}; selected {result.SelectedFiles:N0}.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Extraction cancelled safely and can be resumed."; }
        catch (Exception exception) { Fail("Indexed extraction failed", exception); }
        finally { End(); }
    }

    private async Task AnalyzeServerPlanAsync()
    {
        if (_session.Server is null) { _serverPlanSummary.Text = "Detect Server & SQL first."; return; }
        Begin("Analyzing client DBC consumers and SQL overlays…");
        try
        {
            var clientDbcRoot = _clientDbcRoot.Text ?? string.Empty;
            var coreSourceRoot = _coreSourceRoot.Text;
            _serverPlan = await Task.Run(() => ClientServerDeploymentPlanner.Analyze(clientDbcRoot, _session.Server, coreSourceRoot, _operation!.Token), _operation!.Token);
            _serverPlanItems.ItemsSource = _serverPlan.Entries;
            var grouped = _serverPlan.Entries.GroupBy(entry => entry.Status).OrderBy(group => group.Key).Select(group => $"{group.Key}: {group.Count():N0}");
            _serverPlanSummary.Text = $"{_serverPlan.Entries.Count:N0} DBCs analyzed against {_serverPlan.CoreFamily}.\n{string.Join(" · ", grouped)}\nSQL-overlay entries require an audit before deployment; conflicting client layers remain blocked.";
            _operationStatus.Text = "Client → server plan ready for review.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Client → server analysis cancelled."; }
        catch (Exception exception) { Fail("Client → server analysis failed", exception); }
        finally { End(); }
    }

    private async Task StageServerPlanAsync()
    {
        if (_serverPlan is null) { _serverPlanSummary.Text = "Analyze a plan first."; return; }
        var root = await PickFolderAsync("Select a non-live staging folder for the client/server plan"); if (root is null) return;
        var plan = _serverPlan;
        try { var result = await Task.Run(() => ClientServerDeploymentPlanner.Stage(root, plan)); _serverPlanSummary.Text = $"Staged {result.ClientFiles:N0} client DBCs and {result.ServerFiles:N0} server candidates. {result.BlockedFiles:N0} blocked entries remain.\nPlan: {result.PlanPath}\nManifest: {result.PatchManifestPath ?? "none"}"; }
        catch (Exception exception) { Fail("Client/server staging failed", exception); }
    }

    private async Task AnalyzeFusionAsync()
    {
        Begin("Hashing and comparing additive client candidates…");
        try
        {
            var sources = Lines(_fusionSources.Text).Select((path, index) => new ClientFusionSource($"{index + 1}: {Path.GetFileName(Path.TrimEndingDirectorySeparator(path))}", path)).ToArray();
            var baseRoot = _fusionBase.Text ?? string.Empty;
            var progress = new Progress<(int Done, int Total, string Path)>(value => _operationStatus.Text = $"{value.Done:N0}/{value.Total:N0} · {value.Path}");
            _fusionPlan = await Task.Run(() => ClientFusionPlanner.Analyze(baseRoot, sources, progress, _operation!.Token), _operation!.Token); _fusionDbcPlan = null; _fusionDbcResult = null; _fusionDbcRemapPlan = null; _fusionDbcRemapResult = null;
            _fusionItems.ItemsSource = _fusionPlan.Entries;
            var grouped = _fusionPlan.Entries.GroupBy(entry => entry.Status).OrderBy(group => group.Key).Select(group => $"{group.Key}: {group.Count():N0}");
            _fusionSummary.Text = $"{_fusionPlan.Entries.Count:N0} logical paths analyzed.\n{string.Join(" · ", grouped)}\nConflicts are not silently resolved; DBC conflicts should be merged by record/ID instead of choosing a whole-file winner.";
            _operationStatus.Text = "Additive fusion plan ready for review.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Fusion analysis cancelled."; }
        catch (Exception exception) { Fail("Fusion analysis failed", exception); }
        finally { End(); }
    }

    private async Task AnalyzeFusionDbcsAsync()
    {
        if (_fusionPlan is null) { _fusionSummary.Text = "Analyze the client fusion paths first."; return; } var schema = _session.Settings.SchemaDefinitionPath;
        if (string.IsNullOrWhiteSpace(schema) || !File.Exists(schema)) { _fusionSummary.Text = "Configure the exact WotLK schema XML in Server & SQL before semantic DBC fusion."; return; }
        Begin("Comparing every colliding DBC by record ID and decoded field semantics…");
        try
        {
            var plan = _fusionPlan; _fusionDbcPlan = await Task.Run(() => ClientFusionDbcService.CreatePlan(plan, schema, _operation!.Token), _operation!.Token); _fusionDbcResult = null; _fusionDbcRemapPlan = null; _fusionDbcRemapResult = null;
            var details = _fusionDbcPlan.Tables.Select(table => $"{(table.Ready ? table.RequiresOutput ? "MERGE" : "OMIT EQUAL" : "BLOCKED")} {table.Table}: +{table.Additions.Count:N0}, reuse {table.ReusedRows:N0}, conflicts {table.Conflicts.Count:N0}{(table.Conflicts.Count == 0 ? string.Empty : $" [{string.Join(", ", table.Conflicts.Take(8).Select(conflict => $"ID {conflict.Id}: {string.Join('/', conflict.DifferingColumns.Take(4))}"))}]")}");
            _fusionSummary.Text = $"Semantic DBC review: {_fusionDbcPlan.ResolvableTables:N0} resolvable, {_fusionDbcPlan.BlockedTables:N0} blocked.\n{string.Join(Environment.NewLine, details)}\nDifferent content at an occupied ID remains blocked; Crucible will not discard it or guess cross-table reference rewrites.";
            _operationStatus.Text = _fusionDbcPlan.BlockedTables == 0 ? "Every colliding DBC is additive or semantically equal." : "DBC review complete with explicit same-ID/layout/schema blockers.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "Semantic DBC fusion analysis cancelled."; }
        catch (Exception exception) { Fail("Semantic DBC fusion analysis failed", exception); }
        finally { End(); }
    }

    private async Task AnalyzeFusionDbcRemapAsync()
    {
        if (_fusionPlan is null) { _fusionSummary.Text = "Analyze the client fusion paths first."; return; }
        var schema = _session.Settings.SchemaDefinitionPath; var definitions = _session.Settings.DbdDefinitionsPath;
        if (string.IsNullOrWhiteSpace(schema) || !File.Exists(schema)) { _fusionSummary.Text = "Configure the exact WotLK schema XML in Server & SQL before dependency-aware DBC remapping."; return; }
        if (string.IsNullOrWhiteSpace(definitions) || !Directory.Exists(definitions)) { _fusionSummary.Text = "Configure the WoWDBDefs definitions folder in Server & SQL. Reference remapping will not guess relationships from column names."; return; }
        Begin("Allocating collision-free IDs and propagating DBD-declared DBC references to a fixed point…");
        try
        {
            var fusion = _fusionPlan; _fusionDbcRemapPlan = await Task.Run(() => ClientFusionDbcRemapService.CreatePlan(fusion, schema, definitions, _operation!.Token), _operation!.Token); _fusionDbcRemapResult = null; _fusionDbcPlan = null; _fusionDbcResult = null;
            var tables = _fusionDbcRemapPlan.Tables.Where(table => table.Operations.Count > 0).Select(table => $"{table.Table}: add {table.AddedRows:N0}, reuse {table.ReusedMappings:N0}, mappings {table.Operations.Count:N0}");
            _fusionSummary.Text = $"Dependency-aware DBC plan: {_fusionDbcRemapPlan.Tables.Count:N0} table(s), {_fusionDbcRemapPlan.AddedRows:N0} additive row(s), {_fusionDbcRemapPlan.ReusedMappings:N0} semantic reuse(s), {_fusionDbcRemapPlan.Blockers.Count:N0} blocker(s).\n{string.Join(Environment.NewLine, tables)}\nOnly explicit WoWDBDefs DBC references are rewritten; matching SQL/script changes remain separately reviewable.";
            _operationStatus.Text = _fusionDbcRemapPlan.Ready ? "Collision-safe DBC ID/reference plan is ready to write." : "DBC remap review found blockers; no output can be written.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "DBC ID/reference planning cancelled."; }
        catch (Exception exception) { Fail("DBC ID/reference planning failed", exception); }
        finally { End(); }
    }

    private async Task ApplyFusionDbcRemapAsync()
    {
        if (_fusionDbcRemapPlan is null) { _fusionSummary.Text = "Plan ID + reference remap first."; return; }
        if (!_fusionDbcRemapPlan.Ready) { _fusionSummary.Text = $"The dependency-remap plan has {_fusionDbcRemapPlan.Blockers.Count:N0} blocker(s); no output will be written.\n{string.Join(Environment.NewLine, _fusionDbcRemapPlan.Blockers)}"; return; }
        var parent = await PickFolderAsync("Choose a parent folder for dependency-safe DBC outputs"); if (parent is null) return; var output = Path.Combine(parent, "Crucible-Fusion-DBC-Remap"); for (var suffix = 2; Directory.Exists(output) || File.Exists(output); suffix++) output = Path.Combine(parent, $"Crucible-Fusion-DBC-Remap-{suffix}");
        Begin("Writing hash-bound DBC additions and propagated references…");
        try
        {
            var plan = _fusionDbcRemapPlan; _fusionDbcRemapResult = await Task.Run(() => ClientFusionDbcRemapService.Apply(plan, output, _operation!.Token), _operation!.Token);
            _fusionSummary.Text = $"Wrote {_fusionDbcRemapResult.OutputFiles.Count:N0} dependency-safe DBC(s), adding {_fusionDbcRemapResult.Plan.AddedRows:N0} row(s) and reusing {_fusionDbcRemapResult.Plan.ReusedMappings:N0} semantically equal mapping(s).\nReceipt: {_fusionDbcRemapResult.ReceiptPath}\nStage reviewed patch will substitute these exact verified outputs.";
            _operationStatus.Text = "Dependency-safe DBC outputs are ready for fusion staging.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "DBC remap output cancelled safely."; }
        catch (Exception exception) { Fail("DBC remap output failed", exception); }
        finally { End(); }
    }

    private async Task ApplyFusionDbcsAsync()
    {
        if (_fusionDbcPlan is null) { _fusionSummary.Text = "Analyze colliding DBC records first."; return; }
        var parent = await PickFolderAsync("Choose a parent folder for new merged DBC outputs"); if (parent is null) return; var output = Path.Combine(parent, "Crucible-Fusion-DBCs"); for (var suffix = 2; Directory.Exists(output) || File.Exists(output); suffix++) output = Path.Combine(parent, $"Crucible-Fusion-DBCs-{suffix}");
        Begin("Writing only hash-verified additive DBC unions…");
        try
        {
            var plan = _fusionDbcPlan; _fusionDbcResult = await Task.Run(() => ClientFusionDbcService.Apply(plan, output, _operation!.Token), _operation!.Token);
            _fusionSummary.Text = $"Wrote {_fusionDbcResult.OutputFiles.Count:N0} merged DBC(s); omitted {_fusionDbcResult.OmittedArchivePaths.Count:N0} semantically base-equal DBC(s); retained {_fusionDbcResult.BlockedArchivePaths.Count:N0} unresolved DBC conflict(s).\nReceipt: {_fusionDbcResult.ReceiptPath}\nStage reviewed patch will substitute these outputs and refuse the unresolved DBC paths.";
            _operationStatus.Text = "Additive DBC outputs are ready for fusion staging.";
        }
        catch (OperationCanceledException) { _operationStatus.Text = "DBC fusion output cancelled safely."; }
        catch (Exception exception) { Fail("DBC fusion output failed", exception); }
        finally { End(); }
    }

    private async Task StageFusionAsync()
    {
        if (_fusionPlan is null) { _fusionSummary.Text = "Analyze a fusion plan first."; return; }
        var unresolvedDbcPaths = _fusionPlan.Entries.Where(entry => entry.BaseFilePath is not null && entry.ArchivePath.StartsWith("DBFilesClient\\", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(entry.ArchivePath).Equals(".dbc", StringComparison.OrdinalIgnoreCase) && entry.Status != ClientFusionStatus.IdenticalToBase).ToArray();
        if (unresolvedDbcPaths.Length > 0 && _fusionDbcResult is null && _fusionDbcRemapResult is null) { _fusionSummary.Text = $"{unresolvedDbcPaths.Length:N0} byte-different DBC path(s) require either quick additive review or the preferred ID + reference remap before staging. Crucible will not copy a whole source DBC over the effective base without record-level review."; return; }
        var root = await PickFolderAsync("Select a staging folder for the small fusion patch"); if (root is null) return;
        var plan = _fusionPlan;
        try { var result = await Task.Run(() => ClientFusionPlanner.Stage(root, plan, dbcResult: _fusionDbcResult, dbcRemapResult: _fusionDbcRemapResult)); _fusionSummary.Text = $"Staged {result.StagedFiles:N0} resolved changes; skipped {result.SkippedBaseFiles:N0} base-identical/semantically-equal files; left {result.UnresolvedConflicts:N0} conflicts unresolved.\nManifest: {result.ManifestPath}"; }
        catch (Exception exception) { Fail("Fusion staging failed", exception); }
    }

    private void SaveClientSettings(ClientArchiveIndex index)
    {
        _session.Settings.ClientDataPath = Path.Combine(index.ClientRoot, "Data");
        _session.Settings.ClientIndexPath = _indexRoot.Text ?? string.Empty;
        if (index.Executable is not null) _session.Settings.ClientExecutablePath = Path.Combine(index.ClientRoot, index.Executable.Path);
        _session.Settings.Save();
    }

    private void LoadDefaults()
    {
        var data = _session.Settings.ClientDataPath;
        _clientRoot.Text = Directory.Exists(data) && Path.GetFileName(Path.TrimEndingDirectorySeparator(data)).Equals("Data", StringComparison.OrdinalIgnoreCase) ? Directory.GetParent(data)?.FullName : string.Empty;
        _indexRoot.Text = _session.Settings.ClientIndexPath;
        _coreSourceRoot.Text = _session.Settings.CoreSourcePath;
        _releaseTargetRoot.Text = _clientRoot.Text;
    }

    private void Begin(string text) { _operation?.Cancel(); _operation?.Dispose(); _operation = new(); foreach (var button in _operationButtons) button.IsEnabled = false; _operationStatus.Text = text; }
    private void End() { foreach (var button in _operationButtons) button.IsEnabled = true; _operation?.Dispose(); _operation = null; }
    private void Register(Button button) => _operationButtons.Add(button);
    private void Fail(string title, Exception exception) { _operationStatus.Text = $"{title}: {exception.Message}"; DesktopCrashLogger.Log(title, exception); }
    private IStorageProvider Storage() => TopLevel.GetTopLevel(this)?.StorageProvider ?? throw new InvalidOperationException("Client Workshop is not attached to the main window.");
    private async Task<string?> PickFolderAsync(string title) { var values = await Storage().OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false }); return values.FirstOrDefault()?.TryGetLocalPath(); }
    private async Task<string?> PickFileAsync(string title, IReadOnlyList<string> patterns) { var values = await Storage().OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false, FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }] }); return values.FirstOrDefault()?.TryGetLocalPath(); }
    public void Dispose() { _operation?.Cancel(); _operation?.Dispose(); }

    private static Button Button(string text, Func<Task> action) { var button = new Button { Content = text }; button.Click += async (_, _) => await action(); return button; }
    private static Button AccentButton(string text) { var button = new Button { Content = text }; button.Classes.Add("accent"); return button; }
    private static Border Card(Control child) => new() { Padding = new Thickness(10), BorderBrush = Brush.Parse("#293347"), BorderThickness = new Thickness(1), Child = child };
    private static TextBlock Status(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#99A5B8") };
    private static string[] Lines(string? text) => (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string[] ParseGroups(string? text) => (text ?? string.Empty).Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static IReadOnlyList<ClientReleaseGroupRule> ParseReleaseRules(string? text)
    {
        var result = new List<ClientReleaseGroupRule>();
        foreach (var line in Lines(text)) { var separator = line.IndexOf('|'); if (separator < 1 || separator == line.Length - 1) throw new FormatException($"Optional release rule must use group|relative-prefix: {line}"); result.Add(new(line[..separator], line[(separator + 1)..])); }
        return result;
    }
    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes:N0} B" : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.#} KiB" : bytes < 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024):0.#} MiB" : $"{bytes / (1024d * 1024 * 1024):0.##} GiB";
    private static string ScopeGuidance(ClientArchiveScope scope) => scope switch { ClientArchiveScope.RootData => "Root Data layer; usually effective, but filename precedence still matters.", ClientArchiveScope.ActiveLocale => "Active locale layer; reuse only for the matching locale unless content is proven language-neutral.", ClientArchiveScope.InactiveLocale => "Inactive locale layer; excluded from the effective view by default.", ClientArchiveScope.Cache => "Cache layer; treat as generated or launcher-managed until proven otherwise.", ClientArchiveScope.CustomSubdirectory => "Custom loader subdirectory; preserve separately and expect executable/launcher coupling.", ClientArchiveScope.Backup => "Backup archive; excluded from effective content and patch inputs by default.", _ => "Unknown archive scope." };
    private static IBrush ScopeBrush(ClientArchiveScope scope) => scope is ClientArchiveScope.Backup or ClientArchiveScope.InactiveLocale ? Brush.Parse("#78859A") : scope == ClientArchiveScope.CustomSubdirectory ? Brush.Parse("#F0A34A") : Brush.Parse("#B8C4D8");
    private static IBrush PlanBrush(ClientServerPlanStatus status) => status is ClientServerPlanStatus.Identical ? Brush.Parse("#67C587") : status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.InvalidDbc or ClientServerPlanStatus.UnknownConsumer ? Brush.Parse("#E36B6B") : Brush.Parse("#E5B75A");
    private static IBrush FusionBrush(ClientFusionStatus status) => status is ClientFusionStatus.IdenticalToBase ? Brush.Parse("#78859A") : status is ClientFusionStatus.Conflict ? Brush.Parse("#E36B6B") : Brush.Parse("#67C587");
    private static IBrush ReleaseBrush(ClientReleaseActionKind kind) => kind switch { ClientReleaseActionKind.Add => Brush.Parse("#67C587"), ClientReleaseActionKind.Replace => Brush.Parse("#E5B75A"), ClientReleaseActionKind.RemoveManaged => Brush.Parse("#E36B6B"), _ => Brush.Parse("#78859A") };
    private static void AddPath(Grid grid, int row, string label, Control field, Control firstButton, Control? secondButton) { var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(text, row); grid.Children.Add(text); Grid.SetRow(field, row); Grid.SetColumn(field, 1); grid.Children.Add(field); Grid.SetRow(firstButton, row); Grid.SetColumn(firstButton, 2); grid.Children.Add(firstButton); if (secondButton is not null) { Grid.SetRow(secondButton, row); Grid.SetColumn(secondButton, 3); grid.Children.Add(secondButton); } }
    private static T WithColumn<T>(T control, int column) where T : Control { Grid.SetColumn(control, column); return control; }
    private static T WithRow<T>(T control, int row) where T : Control { Grid.SetRow(control, row); return control; }
}
