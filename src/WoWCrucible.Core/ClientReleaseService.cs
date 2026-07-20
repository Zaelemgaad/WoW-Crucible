using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record ClientReleaseGroupRule(string Group, string PathPrefix);
public sealed record ClientReleaseFile(string RelativePath, long Length, string Sha256, string? OptionalGroup);
public sealed record ClientReleaseManifest(
    string Format,
    int FormatVersion,
    string Name,
    string Channel,
    string ContentId,
    DateTimeOffset CreatedUtc,
    string Changelog,
    IReadOnlyList<ClientReleaseFile> Files);
public sealed record ClientReleaseBundleResult(string BundleRoot, string ManifestPath, ClientReleaseManifest Manifest, long PayloadBytes);
public sealed record ClientReleaseProgress(string Stage, int Completed, int Total, string RelativePath, long Bytes);

public enum ClientReleaseActionKind { Add, Replace, RemoveManaged, Unchanged }
public sealed record ClientReleaseAction(
    ClientReleaseActionKind Kind,
    string RelativePath,
    string? OptionalGroup,
    long Length,
    string? BeforeSha256,
    string? AfterSha256,
    string Detail);
public sealed record ClientReleasePlan(
    string Format,
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string BundleRoot,
    string ManifestPath,
    string ManifestSha256,
    string TargetClientRoot,
    IReadOnlyList<string> SelectedOptionalGroups,
    string InstalledStatePath,
    string? InstalledStateSha256,
    IReadOnlyList<ClientReleaseAction> Actions,
    IReadOnlyList<string> Blockers,
    string Fingerprint)
{
    public bool Ready => Blockers.Count == 0;
    public int Adds => Actions.Count(action => action.Kind == ClientReleaseActionKind.Add);
    public int Replacements => Actions.Count(action => action.Kind == ClientReleaseActionKind.Replace);
    public int Removals => Actions.Count(action => action.Kind == ClientReleaseActionKind.RemoveManaged && action.BeforeSha256 is not null);
    public int Unchanged => Actions.Count(action => action.Kind == ClientReleaseActionKind.Unchanged);
}

public sealed record ClientReleaseManagedFile(string RelativePath, long Length, string Sha256, string? OptionalGroup);
public sealed record ClientReleaseInstalledState(
    string Format,
    int FormatVersion,
    string Channel,
    string ReleaseName,
    string ContentId,
    DateTimeOffset InstalledUtc,
    IReadOnlyList<string> SelectedOptionalGroups,
    IReadOnlyList<ClientReleaseManagedFile> Files);

public sealed record ClientReleaseReceipt(
    string Format,
    int FormatVersion,
    string Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    DateTimeOffset? RolledBackUtc,
    string PlanFingerprint,
    string ManifestSha256,
    string TargetClientRoot,
    string InstalledStatePath,
    string? PreviousInstalledStateSha256,
    ClientReleaseInstalledState? PreviousInstalledState,
    string? NewInstalledStateSha256,
    string BackupRoot,
    IReadOnlyList<ClientReleaseAction> Actions,
    IReadOnlyList<int> ClosedWowProcessIds,
    ClientCacheInvalidationResult? Cache,
    string? RollbackPostimageRoot,
    string? Finding);
public sealed record ClientReleaseApplyResult(string ReceiptPath, string InstalledStatePath, int ChangedFiles, int RemovedFiles, IReadOnlyList<int> ClosedWowProcessIds, ClientCacheInvalidationResult Cache);
public sealed record ClientReleaseRollbackResult(string ReceiptPath, int RestoredFiles, int RemovedFiles, ClientCacheInvalidationResult Cache);

public static class ClientReleaseService
{
    public const string ManifestFileName = "release.crucible.json";
    public const string ManifestFormat = "wow-crucible-client-release";
    public const string PlanFormat = "wow-crucible-client-release-plan";
    public const string StateFormat = "wow-crucible-client-release-state";
    public const string ReceiptFormat = "wow-crucible-client-release-receipt";
    public const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General) { WriteIndented = true };

    public static ClientReleaseBundleResult CreateBundle(string sourceRoot, string bundleRoot, string name, string channel,
        string? changelog = null, IReadOnlyList<ClientReleaseGroupRule>? optionalGroups = null, IProgress<ClientReleaseProgress>? progress = null)
    {
        sourceRoot = RequireDirectory(sourceRoot, "Release source");
        bundleRoot = Path.GetFullPath(bundleRoot);
        name = RequireText(name, "Release name"); channel = RequireText(channel, "Release channel");
        if (Directory.Exists(bundleRoot) || File.Exists(bundleRoot)) throw new IOException($"Release bundle output already exists: {bundleRoot}");
        EnsureSeparateTrees(sourceRoot, bundleRoot);
        var rules = NormalizeRules(optionalGroups ?? []);
        var sourceFiles = EnumerateSafeFiles(sourceRoot).Select(path => (Path: path, Relative: NormalizeRelative(Path.GetRelativePath(sourceRoot, path)))).ToArray();
        EnsureUniquePaths(sourceFiles.Select(file => file.Relative));
        if (sourceFiles.Length == 0) throw new InvalidDataException("A client release cannot be empty.");

        var parent = Path.GetDirectoryName(bundleRoot) ?? throw new InvalidOperationException("The release output needs a parent folder.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".{Path.GetFileName(bundleRoot)}.{Guid.NewGuid():N}.crucible.tmp");
        try
        {
            var payloadRoot = Path.Combine(staging, "Payload"); Directory.CreateDirectory(payloadRoot);
            var entries = new List<ClientReleaseFile>(sourceFiles.Length); long bytes = 0;
            for (var index = 0; index < sourceFiles.Length; index++)
            {
                var file = sourceFiles[index]; progress?.Report(new("Hashing and copying payload", index, sourceFiles.Length, file.Relative, bytes));
                var sourceHash = Sha256(file.Path); var target = CombineInside(payloadRoot, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file.Path, target, false);
                var targetHash = Sha256(target); if (!sourceHash.Equals(targetHash, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Copied payload failed SHA-256 verification: {file.Relative}");
                var length = new FileInfo(target).Length; bytes += length;
                entries.Add(new(file.Relative, length, sourceHash, ResolveGroup(file.Relative, rules)));
            }
            progress?.Report(new("Publishing manifest", sourceFiles.Length, sourceFiles.Length, ManifestFileName, bytes));
            entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
            var contentId = ComputeContentId(name, channel, entries);
            var manifest = new ClientReleaseManifest(ManifestFormat, FormatVersion, name, channel, contentId, DateTimeOffset.UtcNow, changelog?.Trim() ?? string.Empty, entries);
            WriteNew(Path.Combine(staging, ManifestFileName), manifest); Directory.Move(staging, bundleRoot);
            return new(bundleRoot, Path.Combine(bundleRoot, ManifestFileName), manifest, bytes);
        }
        catch { if (Directory.Exists(staging)) Directory.Delete(staging, true); throw; }
    }

    public static ClientReleasePlan CreatePlan(string manifestOrBundlePath, string targetClientRoot, IEnumerable<string>? selectedOptionalGroups = null, IProgress<ClientReleaseProgress>? progress = null)
    {
        var manifestPath = ResolveManifest(manifestOrBundlePath); var bundleRoot = Path.GetDirectoryName(manifestPath)!;
        var manifest = LoadManifest(manifestPath); VerifyBundle(bundleRoot, manifest, progress);
        targetClientRoot = ValidateClientRoot(targetClientRoot);
        var selected = (selectedOptionalGroups ?? []).Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var available = manifest.Files.Where(file => file.OptionalGroup is not null).Select(file => file.OptionalGroup!).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blockers = selected.Where(group => !available.Contains(group)).Select(group => $"Optional group does not exist in this release: {group}").ToList();
        var statePath = InstalledStatePath(targetClientRoot, manifest.Channel); var state = File.Exists(statePath) ? Read<ClientReleaseInstalledState>(statePath) : null;
        if (state is not null && (!state.Format.Equals(StateFormat, StringComparison.Ordinal) || state.FormatVersion != FormatVersion || !state.Channel.Equals(manifest.Channel, StringComparison.OrdinalIgnoreCase)))
        { blockers.Add($"Installed ownership state is invalid or belongs to another channel: {statePath}"); state = null; }
        if (state is not null) ValidateManagedState(state);
        var stateHash = File.Exists(statePath) ? Sha256(statePath) : null;
        var prior = state?.Files.ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ClientReleaseManagedFile>(StringComparer.OrdinalIgnoreCase);
        var wanted = manifest.Files.Where(file => file.OptionalGroup is null || selected.Contains(file.OptionalGroup, StringComparer.OrdinalIgnoreCase)).ToDictionary(file => file.RelativePath, StringComparer.OrdinalIgnoreCase);
        var actions = new List<ClientReleaseAction>();
        var orderedWanted = wanted.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < orderedWanted.Length; index++)
        {
            var file = orderedWanted[index]; progress?.Report(new("Comparing target client", index, orderedWanted.Length, file.RelativePath, file.Length));
            var target = CombineInside(targetClientRoot, file.RelativePath); var before = File.Exists(target) ? Sha256(target) : null;
            var kind = before is null ? ClientReleaseActionKind.Add : before.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase) ? ClientReleaseActionKind.Unchanged : ClientReleaseActionKind.Replace;
            var owned = prior.ContainsKey(file.RelativePath);
            var detail = kind switch
            {
                ClientReleaseActionKind.Add => "New release-managed file.",
                ClientReleaseActionKind.Unchanged => "Target bytes already match the selected release.",
                _ when owned => "Replace a file owned by the previously installed release; the preimage will be backed up.",
                _ => "Replace a pre-existing unowned file; the complete preimage will be backed up."
            };
            actions.Add(new(kind, file.RelativePath, file.OptionalGroup, file.Length, before, file.Sha256, detail));
        }
        foreach (var old in prior.Values.Where(file => !wanted.ContainsKey(file.RelativePath)).OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var target = CombineInside(targetClientRoot, old.RelativePath); var before = File.Exists(target) ? Sha256(target) : null;
            if (before is not null && !before.Equals(old.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                blockers.Add($"Previously managed file was changed outside Crucible and will not be pruned: {old.RelativePath}");
                actions.Add(new(ClientReleaseActionKind.Unchanged, old.RelativePath, old.OptionalGroup, new FileInfo(target).Length, before, before, "Externally changed managed file retained; resolve it before installation."));
            }
            else actions.Add(new(ClientReleaseActionKind.RemoveManaged, old.RelativePath, old.OptionalGroup, before is null ? 0 : new FileInfo(target).Length, before, null, before is null ? "Previously managed file is already absent; ownership will be released." : "Remove an unchanged file owned by the previous release."));
        }
        var manifestHash = Sha256(manifestPath); var fingerprint = Fingerprint(manifestHash, targetClientRoot, selected, stateHash, actions, blockers);
        return new(PlanFormat, FormatVersion, DateTimeOffset.UtcNow, bundleRoot, manifestPath, manifestHash, targetClientRoot, selected, statePath, stateHash, actions, blockers, fingerprint);
    }

    public static void SavePlan(string path, ClientReleasePlan plan, bool overwrite = false) => Write(path, plan, overwrite);
    public static ClientReleasePlan LoadPlan(string path)
    {
        var plan = Read<ClientReleasePlan>(path);
        if (!plan.Format.Equals(PlanFormat, StringComparison.Ordinal) || plan.FormatVersion != FormatVersion) throw new InvalidDataException("Unsupported client release plan format.");
        return plan;
    }

    public static ClientReleaseApplyResult Apply(ClientReleasePlan reviewedPlan, string receiptPath, bool overwriteReceipt = false)
    {
        if (!reviewedPlan.Ready) throw new InvalidOperationException($"Release plan has {reviewedPlan.Blockers.Count:N0} blocker(s).");
        receiptPath = PrepareOutput(receiptPath, overwriteReceipt);
        var fresh = CreatePlan(reviewedPlan.ManifestPath, reviewedPlan.TargetClientRoot, reviewedPlan.SelectedOptionalGroups);
        if (!fresh.Ready || !fresh.Fingerprint.Equals(reviewedPlan.Fingerprint, StringComparison.Ordinal)) throw new InvalidOperationException("The release bundle, client files, selected groups, or ownership state changed after review. Create a new plan.");
        var manifest = LoadManifest(fresh.ManifestPath); var closed = CloseTargetWowProcesses(fresh.TargetClientRoot);
        var operationId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{manifest.ContentId[..Math.Min(12, manifest.ContentId.Length)]}";
        var operationRoot = Path.Combine(fresh.TargetClientRoot, ".crucible", "operations", operationId);
        var backupRoot = Path.Combine(operationRoot, "Backup"); var stagedRoot = Path.Combine(operationRoot, "Staged");
        Directory.CreateDirectory(backupRoot); Directory.CreateDirectory(stagedRoot);
        var previousState = File.Exists(fresh.InstalledStatePath) ? Read<ClientReleaseInstalledState>(fresh.InstalledStatePath) : null;
        foreach (var action in fresh.Actions.Where(action => action.BeforeSha256 is not null && action.Kind is (ClientReleaseActionKind.Replace or ClientReleaseActionKind.RemoveManaged)))
        {
            var source = CombineInside(fresh.TargetClientRoot, action.RelativePath); var backup = CombineInside(backupRoot, action.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(source, backup, false);
            if (!Sha256(backup).Equals(action.BeforeSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Backup verification failed: {action.RelativePath}");
        }
        foreach (var action in fresh.Actions.Where(action => action.Kind is ClientReleaseActionKind.Add or ClientReleaseActionKind.Replace))
        {
            var source = CombineInside(Path.Combine(fresh.BundleRoot, "Payload"), action.RelativePath); var staged = CombineInside(stagedRoot, action.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!); File.Copy(source, staged, false);
            if (!Sha256(staged).Equals(action.AfterSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Staged payload verification failed: {action.RelativePath}");
        }
        var pending = new ClientReleaseReceipt(ReceiptFormat, FormatVersion, "Pending", DateTimeOffset.UtcNow, null, null, fresh.Fingerprint, fresh.ManifestSha256,
            fresh.TargetClientRoot, fresh.InstalledStatePath, fresh.InstalledStateSha256, previousState, null, backupRoot, fresh.Actions, closed, null, null, "Prepared all backups and staged payloads before changing the client.");
        Write(receiptPath, pending, overwrite: true);
        try
        {
            foreach (var action in fresh.Actions.Where(action => action.Kind is ClientReleaseActionKind.Add or ClientReleaseActionKind.Replace))
            {
                var staged = CombineInside(stagedRoot, action.RelativePath); var target = CombineInside(fresh.TargetClientRoot, action.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Move(staged, target, true);
            }
            foreach (var action in fresh.Actions.Where(action => action.Kind == ClientReleaseActionKind.RemoveManaged && action.BeforeSha256 is not null)) File.Delete(CombineInside(fresh.TargetClientRoot, action.RelativePath));
            var selected = manifest.Files.Where(file => file.OptionalGroup is null || fresh.SelectedOptionalGroups.Contains(file.OptionalGroup, StringComparer.OrdinalIgnoreCase))
                .Select(file => new ClientReleaseManagedFile(file.RelativePath, file.Length, file.Sha256, file.OptionalGroup)).ToArray();
            var newState = new ClientReleaseInstalledState(StateFormat, FormatVersion, manifest.Channel, manifest.Name, manifest.ContentId, DateTimeOffset.UtcNow, fresh.SelectedOptionalGroups, selected);
            WriteAtomic(fresh.InstalledStatePath, newState); var newStateHash = Sha256(fresh.InstalledStatePath);
            var cache = ClientPatchDeploymentService.InvalidateCache(fresh.TargetClientRoot);
            var completed = pending with { Status = "Committed", CompletedUtc = DateTimeOffset.UtcNow, NewInstalledStateSha256 = newStateHash, Cache = cache, Finding = "Release installed; exact managed ownership recorded and the client cache invalidated." };
            Write(receiptPath, completed, overwrite: true);
            return new(receiptPath, fresh.InstalledStatePath, fresh.Actions.Count(action => action.Kind is ClientReleaseActionKind.Add or ClientReleaseActionKind.Replace), fresh.Removals, closed, cache);
        }
        catch (Exception exception)
        {
            try { RestorePreimages(fresh.TargetClientRoot, backupRoot, fresh.Actions); RestoreState(fresh.InstalledStatePath, previousState); }
            catch (Exception restore) { Write(receiptPath, pending with { Status = "RecoveryRequired", Finding = $"Install failed: {exception.Message} Recovery also failed: {restore.Message}" }, true); throw new IOException("Client release failed and automatic recovery was incomplete. Preserve the receipt and operation folder.", new AggregateException(exception, restore)); }
            Write(receiptPath, pending with { Status = "FailedRolledBack", Finding = $"Install failed and all changed files were restored: {exception.Message}" }, true); throw;
        }
    }

    public static ClientReleaseRollbackResult Rollback(string receiptPath)
    {
        receiptPath = Path.GetFullPath(receiptPath); var receipt = ValidateRollback(receiptPath);
        var closed = CloseTargetWowProcesses(receipt.TargetClientRoot); var restored = 0; var removed = 0;
        var currentState = Read<ClientReleaseInstalledState>(receipt.InstalledStatePath);
        var rollbackPostimages = Path.Combine(receipt.BackupRoot, $"RollbackPostimages-{Guid.NewGuid():N}"); Directory.CreateDirectory(rollbackPostimages);
        foreach (var action in receipt.Actions.Where(action => action.Kind is ClientReleaseActionKind.Add or ClientReleaseActionKind.Replace))
        {
            var target = CombineInside(receipt.TargetClientRoot, action.RelativePath); var backup = CombineInside(rollbackPostimages, action.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(backup)!); File.Copy(target, backup, false);
            if (!Sha256(backup).Equals(action.AfterSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException($"Rollback postimage staging failed: {action.RelativePath}");
        }
        var pending = receipt with { Status = "RollbackPending", RollbackPostimageRoot = rollbackPostimages, ClosedWowProcessIds = receipt.ClosedWowProcessIds.Concat(closed).Distinct().ToArray(), Finding = "Rollback postimages and original preimages were verified before mutation." }; Write(receiptPath, pending, true);
        try
        {
            foreach (var action in receipt.Actions.Where(action => action.Kind == ClientReleaseActionKind.Add)) { var target = CombineInside(receipt.TargetClientRoot, action.RelativePath); if (File.Exists(target)) { File.Delete(target); removed++; } }
            foreach (var action in receipt.Actions.Where(action => action.BeforeSha256 is not null && action.Kind is (ClientReleaseActionKind.Replace or ClientReleaseActionKind.RemoveManaged)))
            {
                var backup = CombineInside(receipt.BackupRoot, action.RelativePath); var target = CombineInside(receipt.TargetClientRoot, action.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + $".{Guid.NewGuid():N}.rollback.tmp"; File.Copy(backup, temporary, false); File.Move(temporary, target, true); restored++;
            }
            RestoreState(receipt.InstalledStatePath, receipt.PreviousInstalledState); var cache = ClientPatchDeploymentService.InvalidateCache(receipt.TargetClientRoot);
            var rolled = pending with { Status = "RolledBack", RolledBackUtc = DateTimeOffset.UtcNow, Cache = cache, Finding = "Release rolled back to the exact recorded preimages; client cache invalidated again." };
            Write(receiptPath, rolled, true); return new(receiptPath, restored, removed, cache);
        }
        catch (Exception exception)
        {
            try
            {
                foreach (var action in receipt.Actions.Where(action => action.Kind is ClientReleaseActionKind.Add or ClientReleaseActionKind.Replace))
                {
                    var postimage = CombineInside(rollbackPostimages, action.RelativePath); var target = CombineInside(receipt.TargetClientRoot, action.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(postimage, target, true);
                }
                foreach (var action in receipt.Actions.Where(action => action.Kind == ClientReleaseActionKind.RemoveManaged)) { var target = CombineInside(receipt.TargetClientRoot, action.RelativePath); if (File.Exists(target)) File.Delete(target); }
                WriteAtomic(receipt.InstalledStatePath, currentState); Write(receiptPath, receipt with { Finding = $"Rollback failed and the installed release was restored: {exception.Message}" }, true);
            }
            catch (Exception recovery) { Write(receiptPath, pending with { Status = "RollbackRecoveryRequired", Finding = $"Rollback failed: {exception.Message} Recovery also failed: {recovery.Message}" }, true); throw new IOException("Client rollback failed and automatic release restoration was incomplete. Preserve the receipt and backup roots.", new AggregateException(exception, recovery)); }
            throw;
        }
    }

    public static ClientReleaseReceipt LoadReceipt(string receiptPath)
    {
        var receipt = Read<ClientReleaseReceipt>(Path.GetFullPath(receiptPath));
        if (!receipt.Format.Equals(ReceiptFormat, StringComparison.Ordinal) || receipt.FormatVersion != FormatVersion || !receipt.Status.Equals("Committed", StringComparison.Ordinal)) throw new InvalidOperationException("Only a committed, not-yet-rolled-back client release receipt can be rolled back.");
        return receipt;
    }

    public static ClientReleaseReceipt ValidateRollback(string receiptPath)
    {
        var receipt = LoadReceipt(receiptPath);
        if (!File.Exists(receipt.InstalledStatePath) || !Sha256(receipt.InstalledStatePath).Equals(receipt.NewInstalledStateSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Installed ownership state changed after this release; rollback is stale.");
        foreach (var action in receipt.Actions)
        {
            var target = CombineInside(receipt.TargetClientRoot, action.RelativePath); var current = File.Exists(target) ? Sha256(target) : null;
            if (action.Kind is (ClientReleaseActionKind.Add or ClientReleaseActionKind.Replace) && !string.Equals(current, action.AfterSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Installed file changed after release; rollback refused: {action.RelativePath}");
            if (action.Kind == ClientReleaseActionKind.RemoveManaged && current is not null) throw new InvalidOperationException($"Removed managed path was recreated after release; rollback refused: {action.RelativePath}");
            if (action.BeforeSha256 is not null && action.Kind is (ClientReleaseActionKind.Replace or ClientReleaseActionKind.RemoveManaged))
            {
                var backup = CombineInside(receipt.BackupRoot, action.RelativePath); if (!File.Exists(backup) || !Sha256(backup).Equals(action.BeforeSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Rollback backup is missing or changed: {action.RelativePath}");
            }
        }
        return receipt;
    }

    public static ClientReleaseManifest LoadManifest(string path)
    {
        var manifest = Read<ClientReleaseManifest>(path);
        if (!manifest.Format.Equals(ManifestFormat, StringComparison.Ordinal) || manifest.FormatVersion != FormatVersion) throw new InvalidDataException("Unsupported client release manifest format.");
        if (manifest.Files.Count == 0) throw new InvalidDataException("Client release manifest contains no files.");
        RequireText(manifest.Name, "Release name"); RequireText(manifest.Channel, "Release channel");
        foreach (var file in manifest.Files)
        {
            if (!NormalizeRelative(file.RelativePath).Equals(file.RelativePath, StringComparison.Ordinal)) throw new InvalidDataException($"Release path is not canonical: {file.RelativePath}");
            if (file.Length < 0 || !IsSha256(file.Sha256)) throw new InvalidDataException($"Release entry has an invalid length or SHA-256: {file.RelativePath}");
            if (file.OptionalGroup is not null) RequireText(file.OptionalGroup, "Optional group name");
        }
        EnsureUniquePaths(manifest.Files.Select(file => NormalizeRelative(file.RelativePath)));
        if (!manifest.ContentId.Equals(ComputeContentId(manifest.Name, manifest.Channel, manifest.Files), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Client release content identity does not match its manifest entries.");
        return manifest;
    }

    public static void VerifyBundle(string bundleRoot, ClientReleaseManifest manifest, IProgress<ClientReleaseProgress>? progress = null)
    {
        var payload = Path.Combine(Path.GetFullPath(bundleRoot), "Payload"); if (!Directory.Exists(payload)) throw new DirectoryNotFoundException($"Release payload is missing: {payload}");
        var expected = manifest.Files.Select(file => NormalizeRelative(file.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = EnumerateSafeFiles(payload).Select(path => NormalizeRelative(Path.GetRelativePath(payload, path))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unexpected = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var absent = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unexpected.Length > 0 || absent.Length > 0) throw new InvalidDataException($"Release payload inventory differs from its manifest. Missing: {string.Join(", ", absent)}; unexpected: {string.Join(", ", unexpected)}");
        for (var index = 0; index < manifest.Files.Count; index++)
        {
            var file = manifest.Files[index]; progress?.Report(new("Verifying release payload", index, manifest.Files.Count, file.RelativePath, file.Length));
            var path = CombineInside(payload, file.RelativePath); if (!File.Exists(path)) throw new FileNotFoundException($"Release payload is missing {file.RelativePath}.", path);
            var info = new FileInfo(path); if (info.Length != file.Length || !Sha256(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Release payload changed after publication: {file.RelativePath}");
        }
        progress?.Report(new("Release payload verified", manifest.Files.Count, manifest.Files.Count, ManifestFileName, manifest.Files.Sum(file => file.Length)));
    }

    private static IReadOnlyList<int> CloseTargetWowProcesses(string clientRoot)
    {
        var closed = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string processName; try { processName = process.ProcessName; } catch { continue; }
                if (!processName.Equals("Wow", StringComparison.OrdinalIgnoreCase) && !processName.StartsWith("Wow-", StringComparison.OrdinalIgnoreCase)) continue;
                string? executable; try { executable = process.MainModule?.FileName; } catch { continue; }
                if (executable is null || !IsInside(executable, clientRoot)) continue;
                process.Kill(entireProcessTree: true); if (!process.WaitForExit(15000)) throw new InvalidOperationException($"WoW process {process.Id} did not exit before client update."); closed.Add(process.Id);
            }
        }
        return closed;
    }

    private static void RestorePreimages(string clientRoot, string backupRoot, IReadOnlyList<ClientReleaseAction> actions)
    {
        foreach (var action in actions.Where(action => action.Kind == ClientReleaseActionKind.Add)) { var target = CombineInside(clientRoot, action.RelativePath); if (File.Exists(target)) File.Delete(target); }
        foreach (var action in actions.Where(action => action.BeforeSha256 is not null && action.Kind is (ClientReleaseActionKind.Replace or ClientReleaseActionKind.RemoveManaged)))
        {
            var backup = CombineInside(backupRoot, action.RelativePath); var target = CombineInside(clientRoot, action.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(backup, target, true);
        }
    }
    private static void RestoreState(string path, ClientReleaseInstalledState? state) { if (state is null) { if (File.Exists(path)) File.Delete(path); } else WriteAtomic(path, state); }
    private static string ResolveManifest(string path) { path = Path.GetFullPath(path); return Directory.Exists(path) ? Path.Combine(path, ManifestFileName) : path; }
    private static string InstalledStatePath(string clientRoot, string channel)
    {
        var slug = new string(channel.Where(char.IsLetterOrDigit).Take(32).ToArray()); if (slug.Length == 0) slug = "channel";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(channel.ToUpperInvariant())))[..12];
        return Path.Combine(clientRoot, ".crucible", "channels", $"{slug}-{suffix}.json");
    }
    private static string Fingerprint(string manifestHash, string clientRoot, IReadOnlyList<string> groups, string? stateHash, IReadOnlyList<ClientReleaseAction> actions, IReadOnlyList<string> blockers)
        => HashText(JsonSerializer.Serialize(new { manifestHash, clientRoot = Path.GetFullPath(clientRoot).ToUpperInvariant(), groups, stateHash, actions, blockers }, Json));
    private static string ComputeContentId(string name, string channel, IEnumerable<ClientReleaseFile> files)
        => HashText(JsonSerializer.Serialize(new { name, channel, files = files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).Select(file => new { file.RelativePath, file.Length, Sha256 = file.Sha256.ToUpperInvariant(), file.OptionalGroup }) }, Json));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string RequireDirectory(string path, string label) { path = Path.GetFullPath(path); if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} does not exist: {path}"); return Path.TrimEndingDirectorySeparator(path); }
    private static string ValidateClientRoot(string root) { root = RequireDirectory(root, "Client root"); if (!Directory.Exists(Path.Combine(root, "Data"))) throw new DirectoryNotFoundException($"Selected client has no Data folder: {root}"); return root; }
    private static string RequireText(string? value, string label) { value = value?.Trim(); if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{label} cannot be empty."); return value; }
    private static void EnsureSeparateTrees(string source, string output) { if (IsInside(output, source) || IsInside(source, output)) throw new InvalidOperationException("Release source and bundle output cannot contain one another."); }
    private static bool IsInside(string path, string root) { var full = Path.GetFullPath(path); var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); return full.Equals(parent, StringComparison.OrdinalIgnoreCase) || full.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
    private static string CombineInside(string root, string relative)
    {
        relative = NormalizeRelative(relative); if (relative.Split('\\')[0].Equals(".crucible", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A release cannot manage Crucible's private client metadata folder.");
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar))); if (!IsInside(full, root) || full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Path escaped its root: {relative}");
        EnsureNoReparseTraversal(root, relative); return full;
    }
    private static string NormalizeRelative(string value)
    {
        value = value.Replace('/', '\\').Trim('\\'); if (value.Length == 0 || Path.IsPathRooted(value) || value.Contains(':')) throw new InvalidDataException($"Invalid client-relative path: {value}");
        var pieces = value.Split('\\'); if (pieces.Any(piece => piece.Length == 0 || piece is "." or "..")) throw new InvalidDataException($"Invalid client-relative path: {value}");
        foreach (var piece in pieces) ValidateWindowsSegment(piece, value); return string.Join('\\', pieces);
    }
    private static void ValidateWindowsSegment(string segment, string completePath)
    {
        if (!segment.Equals(segment.TrimEnd(' ', '.'), StringComparison.Ordinal) || segment.Any(character => char.IsControl(character) || "<>\"|?*".Contains(character))) throw new InvalidDataException($"Client path contains a Windows-ambiguous or invalid segment: {completePath}");
        var stem = segment.Split('.')[0]; var reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9';
        if (reserved) throw new InvalidDataException($"Client path uses a reserved Windows device name: {completePath}");
    }
    private static void EnsureNoReparseTraversal(string root, string relative)
    {
        var current = Path.GetFullPath(root);
        foreach (var piece in relative.Split('\\'))
        {
            current = Path.Combine(current, piece); if (!File.Exists(current) && !Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Client release path traverses a link/reparse point: {current}");
        }
    }
    private static void EnsureUniquePaths(IEnumerable<string> paths) { var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var path in paths) if (!seen.Add(path)) throw new InvalidDataException($"Case-insensitive client path collision: {path}"); }
    private static void ValidateManagedState(ClientReleaseInstalledState state)
    {
        EnsureUniquePaths(state.Files.Select(file => NormalizeRelative(file.RelativePath)));
        foreach (var file in state.Files) if (file.Length < 0 || !IsSha256(file.Sha256)) throw new InvalidDataException($"Installed ownership entry is invalid: {file.RelativePath}");
    }
    private static IReadOnlyList<ClientReleaseGroupRule> NormalizeRules(IEnumerable<ClientReleaseGroupRule> rules)
    {
        var result = rules.Select(rule => new ClientReleaseGroupRule(RequireText(rule.Group, "Optional group name"), NormalizeRelative(rule.PathPrefix))).OrderByDescending(rule => rule.PathPrefix.Length).ToArray();
        if (result.GroupBy(rule => rule.PathPrefix, StringComparer.OrdinalIgnoreCase).Any(group => group.Select(rule => rule.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)) throw new InvalidDataException("The same optional path prefix cannot belong to multiple groups.");
        return result;
    }
    private static string? ResolveGroup(string path, IReadOnlyList<ClientReleaseGroupRule> rules) => rules.FirstOrDefault(rule => path.Equals(rule.PathPrefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(rule.PathPrefix + "\\", StringComparison.OrdinalIgnoreCase))?.Group;
    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory)) { if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Release sources cannot contain directory links/reparse points: {child}"); pending.Push(child); }
            foreach (var file in Directory.EnumerateFiles(directory)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Release sources cannot contain linked/reparse-point files: {file}"); yield return file; }
        }
    }
    private static string PrepareOutput(string path, bool overwrite) { path = Path.GetFullPath(path); if (File.Exists(path) && !overwrite) throw new IOException($"Output already exists: {path}"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); return path; }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"JSON artifact is empty: {path}");
    private static void WriteNew<T>(string path, T value) => Write(path, value, false);
    private static void Write<T>(string path, T value, bool overwrite) { path = PrepareOutput(path, overwrite); var temp = path + $".{Guid.NewGuid():N}.tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, path, overwrite); }
    private static void WriteAtomic<T>(string path, T value) => Write(path, value, true);
}
