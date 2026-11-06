using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using WoWCrucible.Core;
using WoWCrucible.Desktop;

/// <summary>Covers workflows that lived only in the retired WinForms shell. Fixture-only: no database or server access.</summary>
internal static class PortedWorkflowsTestSuite
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run()
    {
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
        var root = Path.Combine(Path.GetTempPath(), "Crucible-Ported-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var session = new DesktopWorkspaceSession(new DesktopSettings());
            TestPatchReviewAndValidation(session, root);
            TestIndexedExtractionAndPlans(session, root);
            TestDbcSqlAudit(session, root);
            TestCoreDataSync(root);
            TestFileDrop(root);
            Console.WriteLine("PASS ported workflows: MPQ validation and build review, resumable indexed extraction, fusion and client/server plan export, remembered target, DBC/SQL row audit and migration export, core data sync with backup, window file drop.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestPatchReviewAndValidation(DesktopWorkspaceSession session, string root)
    {
        var payload = Path.Combine(root, "payload.txt"); File.WriteAllText(payload, "payload");
        var archive = Path.Combine(root, "validate.mpq");
        new PatchArchiveService().Create(archive, [new(payload, @"Interface\Test\payload.txt")]);
        using var view = new MpqWorkspaceView(session);
        var entries = (System.Collections.IList)Field<object>(view, "_entries");
        var rowType = entries.GetType().GetGenericArguments()[0];
        entries.Add(Activator.CreateInstance(rowType, payload, @"Interface\Test\payload.txt", (ushort)0)!);
        var status = Field<TextBlock>(view, "_builderStatus");
        view.ValidateArchive(archive);
        Require(status.Text!.StartsWith("Validation passed", StringComparison.Ordinal), "A matching archive failed validation: " + status.Text);
        entries.Add(Activator.CreateInstance(rowType, payload, @"Interface\Test\absent.txt", (ushort)0)!);
        view.ValidateArchive(archive);
        Require(status.Text!.StartsWith("Validation failed", StringComparison.Ordinal), "An archive missing a manifest path passed validation.");

        PatchEntry[] glue = [new(payload, @"Interface\GlueXML\GlueStrings.lua")];
        Require(view.ManifestReviewMessages(glue).Count == 1 && view.BuildReviewMessages(glue).Count == 1, "Unbound protected GlueXML was not raised for review.");
        Set(view, "_requiredClientExecutableSha256", new string('a', 64));
        Require(view.ManifestReviewMessages(glue).Count == 0, "A bound GlueXML manifest still asked for review.");
        Set(view, "_requiredClientExecutableSha256", null);
        var odd = Enumerable.Range(0, 8).Select(index => new PatchEntry(payload, $"NotAClientFolder{index}.xyz")).ToArray();
        var messages = view.BuildReviewMessages(odd);

        Require(odd.All(entry => PatchInputMapper.AssessArchivePath(entry.ArchivePath).HasWarning), "The review fixture no longer raises path warnings.");
        Require(messages.Count == 7 && messages[^1].Contains("2 more", StringComparison.Ordinal), "Build review did not summarize path warnings.");
        var confirmation = Field<Border>(view, "_builderReview");
        var review = (bool)view.GetType().GetMethod("ReviewEntries", Private)!.Invoke(view, [new[] { "needs review" }, "Build anyway", (Func<Task>)(() => Task.CompletedTask)])!;
        Require(review && confirmation.IsVisible, "A warning did not stop the build for confirmation.");
    }

    private static void TestIndexedExtractionAndPlans(DesktopWorkspaceSession session, string root)
    {
        var client = Path.Combine(root, "client"); var data = Path.Combine(client, "Data"); Directory.CreateDirectory(data);
        var payload = Path.Combine(root, "indexed.txt"); File.WriteAllText(payload, "indexed payload");
        new PatchArchiveService().Create(Path.Combine(data, "patch-1.MPQ"), [new(payload, @"Interface\Test\indexed.txt")]);
        var index = Path.Combine(root, "index");
        var built = new ClientArchiveIndexService().Build(client, index, hashArchives: false);
        using var view = new ClientWorkspaceView(session);
        Field<TextBox>(view, "_indexRoot").Text = index;
        view.GetType().GetMethod("LoadIndex", Private)!.Invoke(view, null);
        var status = Field<TextBlock>(view, "_operationStatus");
        var destination = Path.Combine(root, "extracted");
        Pump(view.ExtractArchiveAsync(built.Archives.Single(), destination));
        Require(File.ReadAllText(Path.Combine(destination, "Interface", "Test", "indexed.txt")) == "indexed payload", "Indexed extraction lost the internal path or bytes: " + status.Text);
        Require(status.Text!.StartsWith("Extracted 1;", StringComparison.Ordinal), "Extraction did not report its result: " + status.Text);
        Pump(view.ExtractArchiveAsync(built.Archives.Single(), destination));
        Require(status.Text!.StartsWith("Extracted 0; resumed past 1;", StringComparison.Ordinal), "A repeated extraction did not resume: " + status.Text);

        var baseRoot = Path.Combine(root, "fusion-base"); var source = Path.Combine(root, "fusion-source");
        Directory.CreateDirectory(baseRoot); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "added.txt"), "added");
        var target = TargetProfileCatalog.Find(TargetProfileCatalog.Load(), TargetProfileCatalog.DefaultProfileId);
        Set(view, "_fusionPlan", ClientFusionPlanner.Analyze(baseRoot, [new ClientFusionSource("source", source)], target));
        var fusion = Path.Combine(root, "client-fusion-plan.json");
        view.SaveFusionPlan(fusion);
        Require(File.Exists(fusion) && Field<TextBlock>(view, "_fusionSummary").Text!.StartsWith("Fusion plan saved", StringComparison.Ordinal), "The fusion plan was not saved.");

        var picker = Field<ComboBox>(view, "_serverTarget");
        var other = picker.Items.OfType<TargetProfile>().First(profile => profile.Id != TargetProfileCatalog.DefaultProfileId);
        var original = session.Settings.SelectedTargetProfileId;
        picker.SelectedItem = other;
        Require(session.Settings.SelectedTargetProfileId == other.Id, "The chosen client target was not remembered.");
        using (var reopened = new ClientWorkspaceView(session))
            Require((Field<ComboBox>(reopened, "_fusionTarget").SelectedItem as TargetProfile)?.Id == other.Id, "A reopened workshop forgot the client target.");
        session.Settings.SelectedTargetProfileId = original; session.Settings.Save();
    }

    private static void TestDbcSqlAudit(DesktopWorkspaceSession session, string root)
    {
        var binding = ServerTableBindingCatalog.Resolve(ServerCoreFamily.AzerothCore, clientBuild: 12340).First(candidate => candidate.SqlTableName is not null);
        var dbc = new Dictionary<string, object?> { ["Name"] = "From DBC" }; var sql = new Dictionary<string, object?> { ["Name"] = "From SQL" };
        var audit = new DbcSqlAuditResult(binding, Path.Combine(root, binding.DbcFileName), "ID",
        [
            new(1, "row 1", DbcSqlRowStatus.Same, dbc, dbc),
            new(2, "row 2", DbcSqlRowStatus.SqlOverridesDbc, dbc, sql),
            new(3, "row 3", DbcSqlRowStatus.DbcOnly, dbc, new Dictionary<string, object?>())
        ]);
        var view = new ServerSqlWorkspaceView(session);
        view.ShowSyncAudit(audit);
        var rows = Field<ListBox>(view, "_syncAuditRows");
        Require(rows.IsVisible && rows.ItemsSource is not null && Field<Button>(view, "_syncExportMigration").IsEnabled, "The row audit was not shown or could not be exported.");
        Require(Field<TextBlock>(view, "_syncStatus").Text!.Contains("1 SQL same · 1 SQL overrides · 1 DBC-only", StringComparison.Ordinal), "The audit summary miscounted rows.");
        var formatted = ServerSqlWorkspaceView.FormatAuditRow(audit.Rows[2]);
        Require(formatted.Contains("DbcOnly", StringComparison.Ordinal) && formatted.Contains("Name=From DBC", StringComparison.Ordinal) && formatted.Contains("<missing>", StringComparison.Ordinal), "A row lost its DBC or SQL values.");
        var migration = Path.Combine(root, "migration.sql");
        view.ExportSyncMigration(migration);
        Require(File.ReadAllText(migration).Contains(binding.SqlTableName!, StringComparison.Ordinal), "The migration does not target the overlay table.");
        view.ShowSyncAudit(audit with { Rows = [audit.Rows[0]] });
        Require(!Field<Button>(view, "_syncExportMigration").IsEnabled, "A matching audit offered a migration.");
    }

    private static void TestCoreDataSync(string root)
    {
        var source = Path.Combine(root, "Sync.dbc"); File.WriteAllText(source, "edited");
        var core = Path.Combine(root, "core-dbc"); Directory.CreateDirectory(core);
        var destination = Path.Combine(core, "Sync.dbc");
        var first = MainWindow.SyncDbcToDirectory(source, core);
        Require(File.ReadAllText(destination) == "edited" && first.Contains("no retained backup", StringComparison.Ordinal), "The first sync did not publish the file.");
        File.WriteAllText(source, "edited again");
        MainWindow.SyncDbcToDirectory(source, core);
        Require(File.ReadAllText(destination) == "edited again" && !File.Exists(destination + ".tmp"), "A repeat sync did not replace the file atomically.");
        Require(MainWindow.SyncDbcToDirectory(source, root).Contains("already in that core data directory", StringComparison.Ordinal), "A sync onto itself was not refused.");
    }

    private static void TestFileDrop(string root)
    {
        var dropped = Path.Combine(root, "dropped"); Directory.CreateDirectory(dropped);
        Program.WriteSpellFixture(dropped);
        var payload = Path.Combine(dropped, "note.blp"); File.WriteAllText(payload, "payload");
        var archive = Path.Combine(dropped, "dropped.mpq"); new PatchArchiveService().Create(archive, [new(payload, @"Interface\Test\note.blp")]);
        var window = new MainWindow(new DesktopSettings { BackupChoiceRemembered = true });
        try
        {
            Pump(window.RouteDroppedPathsAsync([Path.Combine(dropped, "Spell.dbc")]));
            Require(Field<List<DbcDocumentSession>>(window, "_documents").Count == 1, "A dropped DBC did not open as a document.");
            Pump(window.RouteDroppedPathsAsync([archive]));
            Require(Field<string>(Field<MpqWorkspaceView>(window, "_mpqWorkspaceView"), "_openedArchivePath") == archive, "A dropped MPQ did not open in the archive browser.");
            Pump(window.RouteDroppedPathsAsync([payload]));
            Require(((System.Collections.IList)Field<object>(Field<MpqWorkspaceView>(window, "_mpqWorkspaceView"), "_entries")).Count == 1, "Other dropped files did not reach the patch builder.");
        }
        finally { window.Close(); }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Set(object target, string name, object? value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pump(Task task, int seconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for the UI.");
            Dispatcher.UIThread.RunJobs(); Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        task.GetAwaiter().GetResult();
    }
}
