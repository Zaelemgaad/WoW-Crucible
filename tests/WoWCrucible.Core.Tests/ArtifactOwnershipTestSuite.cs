using System.Diagnostics;
using System.Text.Json;
using WoWCrucible.Core;

internal static class ArtifactOwnershipTestSuite
{
    public static void Run()
    {
        var tests = new Action[]
        {
            RetainsProtectedFiles, RejectsChangedContent, RejectsExtendedExpiry,
            RejectsForeignManifest, RejectsDuplicateEntries, RejectsLinkedArtifacts,
            RejectsLinkedProject, RejectsChangedParentLink
        };
        var failures = new List<Exception>();
        foreach (var test in tests)
        {
            try { test(); Console.WriteLine($"PASS artifact ownership: {test.Method.Name}"); }
            catch (Exception exception) { failures.Add(exception); Console.Error.WriteLine($"FAIL artifact ownership: {test.Method.Name}: {exception.Message}"); }
        }
        if (failures.Count > 0) throw new AggregateException("Artifact ownership regressions failed.", failures);
    }

    public static void CreateGuiFixture(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root)) throw new IOException("GUI fixture destination already exists; nothing was overwritten.");
        CrucibleContentProjectService.Create(root, "GUI cleanup audit");
        var run = ArtifactOwnershipService.CreateRun(root, "gui-audit");
        Add(root, run, run.ScratchPath, "scratch.bin", ArtifactLifecycleCategory.Scratch);
        Add(root, run, run.CachePath, "cache.bin", ArtifactLifecycleCategory.Cache);
        Add(root, run, run.DiagnosticsPath, "expired.log", ArtifactLifecycleCategory.Diagnostics, DateTimeOffset.UtcNow.AddDays(-1));
        Add(root, run, run.DeliverablePath, "protected-deliverable.bin", ArtifactLifecycleCategory.Deliverable);
        Add(root, run, run.BackupPath, "protected-baseline-marker.bin", ArtifactLifecycleCategory.PreimageBackup);
        Add(root, run, run.ReceiptPath, "protected-receipt.txt", ArtifactLifecycleCategory.Receipt);
        Console.WriteLine($"GUI fixture: {root}\nExpected cleanup preview: 3 files, 9 bytes. Three protected marker files remain after apply. No client data is used.");
    }

    private static string Add(string root, OwnedRunLayout run, string folder, string name, ArtifactLifecycleCategory category, DateTimeOffset? expiry = null)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        ArtifactOwnershipService.RegisterFiles(root, run.OperationId, category, [path], expiresUtc: expiry);
        return path;
    }

    private static void RetainsProtectedFiles()
    {
        using var fixture = new Fixture();
        Add(fixture.Root, fixture.Run, fixture.Run.BackupPath, "baseline.bin", ArtifactLifecycleCategory.PreimageBackup);
        Add(fixture.Root, fixture.Run, fixture.Run.DeliverablePath, "output.bin", ArtifactLifecycleCategory.Deliverable);
        Add(fixture.Root, fixture.Run, fixture.Run.ReceiptPath, "receipt.bin", ArtifactLifecycleCategory.Receipt);
        Add(fixture.Root, fixture.Run, fixture.Run.DiagnosticsPath, "current.log", ArtifactLifecycleCategory.Diagnostics, DateTimeOffset.UtcNow.AddDays(1));
        var plan = ArtifactOwnershipService.PlanCleanup(fixture.Root);
        if (plan.Entries.Count != 1 || plan.ReclaimableBytes != 3) throw new InvalidOperationException("Protected artifacts appeared in cleanup.");
        var result = ArtifactOwnershipService.ApplyCleanup(plan);
        if (result.RemovedFiles != 1 || ArtifactOwnershipService.Load(fixture.Root).Artifacts.Count != 4 || ArtifactOwnershipService.PlanCleanup(fixture.Root).Entries.Count != 0)
            throw new InvalidOperationException("Cleanup did not retain all protected artifacts or was not repeatable.");
    }

    private static void RejectsChangedContent()
    {
        using var fixture = new Fixture();
        var second = Add(fixture.Root, fixture.Run, fixture.Run.ScratchPath, "second.bin", ArtifactLifecycleCategory.Scratch);
        var plan = ArtifactOwnershipService.PlanCleanup(fixture.Root);
        File.WriteAllBytes(second, [4, 5, 6]);
        Reject(() => ArtifactOwnershipService.ApplyCleanup(plan), "changed after preview");
        if (!File.Exists(fixture.Scratch)) throw new InvalidOperationException("Cleanup deleted part of a stale plan.");
    }

    private static void RejectsExtendedExpiry()
    {
        using var fixture = new Fixture();
        var diagnostic = Add(fixture.Root, fixture.Run, fixture.Run.DiagnosticsPath, "diagnostic.log", ArtifactLifecycleCategory.Diagnostics, DateTimeOffset.UtcNow.AddDays(-1));
        var plan = ArtifactOwnershipService.PlanCleanup(fixture.Root);
        ArtifactOwnershipService.RegisterFiles(fixture.Root, fixture.Run.OperationId, ArtifactLifecycleCategory.Diagnostics, [diagnostic], expiresUtc: DateTimeOffset.UtcNow.AddDays(1));
        Reject(() => ArtifactOwnershipService.ApplyCleanup(plan), "changed after preview");
        if (!File.Exists(fixture.Scratch) || !File.Exists(diagnostic)) throw new InvalidOperationException("Unexpired diagnostics or scratch were deleted by a stale plan.");
    }

    private static void RejectsForeignManifest()
    {
        using var fixture = new Fixture();
        var plan = ArtifactOwnershipService.PlanCleanup(fixture.Root);
        var manifest = ArtifactOwnershipService.Load(fixture.Root) with { ProjectId = "another-project" };
        File.WriteAllText(Path.Combine(fixture.Root, "ownership.crucible.json"), JsonSerializer.Serialize(manifest));
        Reject(() => ArtifactOwnershipService.ApplyCleanup(plan), "another project");
        if (!File.Exists(fixture.Scratch)) throw new InvalidOperationException("A foreign ownership manifest authorized deletion.");
    }

    private static void RejectsDuplicateEntries()
    {
        using var fixture = new Fixture();
        var plan = ArtifactOwnershipService.PlanCleanup(fixture.Root);
        Reject(() => ArtifactOwnershipService.ApplyCleanup(plan with { Entries = [plan.Entries[0], plan.Entries[0]] }), "duplicate");
        if (!File.Exists(fixture.Scratch)) throw new InvalidOperationException("Duplicate entries were not rejected before deletion.");
    }

    private static void RejectsLinkedArtifacts()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Run.CachePath, "linked");
        var external = Path.Combine(fixture.Container, "external");
        Directory.CreateDirectory(external);
        File.WriteAllBytes(Path.Combine(external, "baseline.bin"), [1, 2, 3]);
        CreateDirectoryLink(link, external);
        try { Reject(() => ArtifactOwnershipService.RegisterFiles(fixture.Root, fixture.Run.OperationId, ArtifactLifecycleCategory.Cache, [Path.Combine(link, "baseline.bin")]), "link/reparse point"); }
        finally { Directory.Delete(link); }
    }

    private static void RejectsLinkedProject()
    {
        using var fixture = new Fixture();
        var link = Path.Combine(fixture.Container, "alias");
        CreateDirectoryLink(link, fixture.Root);
        try { Reject(() => ArtifactOwnershipService.PlanCleanup(link), "link/reparse point"); }
        finally { Directory.Delete(link); }
    }

    private static void RejectsChangedParentLink()
    {
        using var fixture = new Fixture();
        var plan = ArtifactOwnershipService.PlanCleanup(fixture.Root);
        var moved = Path.Combine(fixture.Container, "baseline");
        Directory.Move(fixture.Run.ScratchPath, moved);
        CreateDirectoryLink(fixture.Run.ScratchPath, moved);
        try
        {
            Reject(() => ArtifactOwnershipService.ApplyCleanup(plan), "link/reparse point");
            if (!File.Exists(Path.Combine(moved, Path.GetFileName(fixture.Scratch)))) throw new InvalidOperationException("Cleanup followed a replaced parent to baseline data.");
        }
        finally { if (Directory.Exists(fixture.Run.ScratchPath)) Directory.Delete(fixture.Run.ScratchPath); }
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // Directory junctions exercise reparse traversal without requiring symlink privileges.
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start) ?? throw new IOException("Could not start directory-junction fixture creation.");
        var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"Could not create test junction: {output} {error}");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException exception) when (exception.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new InvalidOperationException($"Unsafe cleanup was accepted; expected '{message}'.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Container { get; } = Path.Combine(Path.GetTempPath(), $"crucible-ownership-{Guid.NewGuid():N}");
        public string Root { get; }
        public OwnedRunLayout Run { get; }
        public string Scratch { get; }
        public Fixture()
        {
            Root = Path.Combine(Container, "project");
            CrucibleContentProjectService.Create(Root, "Ownership regression");
            Run = ArtifactOwnershipService.CreateRun(Root, "test");
            Scratch = Add(Root, Run, Run.ScratchPath, "scratch.bin", ArtifactLifecycleCategory.Scratch);
        }
        public void Dispose() => Directory.Delete(Container, true);
    }
}
