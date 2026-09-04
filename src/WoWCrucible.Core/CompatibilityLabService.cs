using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record CompatibilityLabClonePair(
    string Name,
    string SourceRoot,
    string CloneRoot,
    IReadOnlyList<string>? ExcludedDirectoryNames = null,
    IReadOnlyList<string>? ExcludedFilePaths = null);

public sealed record CompatibilityLabLane(
    string Name,
    string ProfileId,
    string TableRoot,
    string ServerCloneRoot,
    string? CoreSourceRoot,
    string DefinitionsRoot,
    string? XmlSchemaPath,
    IReadOnlyList<CompatibilityLabClonePair> ClonePairs,
    bool RecursiveTableDiscovery = true);

public sealed record CompatibilityLabRequest(
    int FormatVersion,
    string OutputRoot,
    IReadOnlyList<CompatibilityLabLane> Lanes,
    int HashWorkers = 0);

public sealed record CompatibilityLabProgress(string Phase, long Completed, long Total, string CurrentPath);

public sealed record CompatibilityLabIntegrityIssue(string Kind, string RelativePath, string SourceDetail, string CloneDetail);

public sealed record CompatibilityLabCloneAudit(
    string Name,
    string SourceRoot,
    string CloneRoot,
    IReadOnlyList<string> ExcludedDirectoryNames,
    IReadOnlyList<string> ExcludedFilePaths,
    int SourceFiles,
    int CloneFiles,
    long SourceBytes,
    long CloneBytes,
    int HashedPairs,
    int MissingFiles,
    int ExtraFiles,
    int LengthMismatches,
    int HashMismatches,
    IReadOnlyList<CompatibilityLabIntegrityIssue> IssueSamples,
    IReadOnlyList<string> Errors)
{
    public bool Passed => Errors.Count == 0 && MissingFiles == 0 && ExtraFiles == 0 && LengthMismatches == 0 && HashMismatches == 0;
}

public sealed record CompatibilityLabDeploymentAudit(
    string SourceLane,
    string TargetLane,
    string TargetProfileId,
    int Entries,
    IReadOnlyDictionary<ClientServerPlanStatus, int> StatusCounts,
    int StagedClientFiles,
    int StagedServerFiles,
    int BlockedFiles,
    string PlanPath,
    string StageRoot,
    bool ExpectedCompatible,
    bool Passed,
    IReadOnlyList<string> Findings);

public sealed record CompatibilityLabLaneAudit(
    string Name,
    string ProfileId,
    int Build,
    ArchiveFormat ArchiveFormat,
    ServerCoreFamily CoreFamily,
    string TableRoot,
    bool RecursiveTableDiscovery,
    int TableFiles,
    IReadOnlyDictionary<ClientTableCompatibilityState, int> CompatibilityCounts,
    DbdSchemaAuditSummary SchemaAudit,
    FixedTableMutationAuditSummary? FixedMutationAudit,
    Wdc1MutationAuditSummary? Wdc1MutationAudit,
    IReadOnlyDictionary<ServerTableConsumption, int> BindingCounts,
    CompatibilityLabDeploymentAudit NativeDeployment,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Errors)
{
    public bool Passed => Errors.Count == 0 &&
        CompatibilityCounts.Where(pair => pair.Key is not ClientTableCompatibilityState.Supported and not ClientTableCompatibilityState.EmptyUnverifiable).Sum(pair => pair.Value) == 0 &&
        SchemaAudit.Failures == 0 && (FixedMutationAudit?.Passed ?? true) && (Wdc1MutationAudit?.Passed ?? true) && NativeDeployment.Passed;
}

public sealed record CompatibilityLabCollisionSample(
    string Table,
    string LeftPath,
    ClientTableContainerKind? LeftContainer,
    string RightPath,
    ClientTableContainerKind? RightContainer,
    bool SameLayout,
    bool LeftAcceptedByRightTarget,
    bool RightAcceptedByLeftTarget);

public sealed record CompatibilityLabCollisionAudit(
    string LeftLane,
    string RightLane,
    int SharedLogicalTables,
    int SameLayoutTables,
    int LeftAcceptedByRightTarget,
    int RightAcceptedByLeftTarget,
    IReadOnlyList<CompatibilityLabCollisionSample> Samples,
    bool Passed);

public sealed record CompatibilityLabReport(
    int FormatVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string RunRoot,
    IReadOnlyList<CompatibilityLabCloneAudit> CloneAudits,
    IReadOnlyList<CompatibilityLabLaneAudit> LaneAudits,
    IReadOnlyList<CompatibilityLabDeploymentAudit> CrossTargetDeployments,
    IReadOnlyList<CompatibilityLabCollisionAudit> CollisionAudits,
    IReadOnlyList<string> Errors,
    string JsonReportPath,
    string MarkdownReportPath)
{
    public bool Passed => Errors.Count == 0 && CloneAudits.Count > 0 && CloneAudits.All(audit => audit.Passed) &&
        LaneAudits.Count > 1 && LaneAudits.All(audit => audit.Passed) &&
        CrossTargetDeployments.Count > 0 && CrossTargetDeployments.All(audit => audit.Passed) &&
        CollisionAudits.All(audit => audit.Passed);
}

/// <summary>
/// Runs destructive-format exercises only against explicit isolated worktrees.
/// Source installations are opened read-only; every mutation and deployment
/// artifact is written under a unique lab run directory.
/// </summary>
public static partial class CompatibilityLabService
{
    private const int RequestFormatVersion = 1;
    private const int ReportFormatVersion = 1;
    private const int MaximumIssueSamples = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CompatibilityLabRequest LoadRequest(string path)
    {
        path = RequiredFile(path, "Compatibility-lab request");
        var request = JsonSerializer.Deserialize<CompatibilityLabRequest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Compatibility-lab request is empty.");
        ValidateRequest(request);
        return request;
    }

    public static void SaveRequest(string path, CompatibilityLabRequest request)
    {
        ValidateRequest(request);
        AtomicWrite(path, JsonSerializer.Serialize(request, JsonOptions));
    }

    public static CompatibilityLabReport Run(
        CompatibilityLabRequest request,
        IProgress<CompatibilityLabProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var started = DateTimeOffset.UtcNow;
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        Directory.CreateDirectory(outputRoot);
        var runRoot = Path.Combine(outputRoot, $"compat-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runRoot);
        var profiles = TargetProfileCatalog.Load();
        var resolved = request.Lanes.Select(lane => new ResolvedLane(lane, TargetProfileCatalog.FindRequired(profiles, lane.ProfileId))).ToArray();
        var cloneAudits = new List<CompatibilityLabCloneAudit>();
        var laneAudits = new List<CompatibilityLabLaneAudit>();
        var crossAudits = new List<CompatibilityLabDeploymentAudit>();
        var collisionAudits = new List<CompatibilityLabCollisionAudit>();
        var errors = new List<string>();

        foreach (var lane in resolved)
        foreach (var pair in lane.Request.ClonePairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { cloneAudits.Add(AuditClone($"{lane.Request.Name}: {pair.Name}", pair, request.HashWorkers, progress, cancellationToken)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"Clone audit {lane.Request.Name}/{pair.Name}: {exception.Message}");
            }
        }

        var workspaces = new Dictionary<string, ServerWorkspace>(StringComparer.OrdinalIgnoreCase);
        foreach (var lane in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detected = ServerWorkspaceDetector.DetectAsync(lane.Request.ServerCloneRoot, cancellationToken).GetAwaiter().GetResult();
                var (workspace, workspaceFinding) = BindWorkspaceToLane(detected, lane);
                workspaces[lane.Request.Name] = workspace;
                laneAudits.Add(AuditLane(lane, workspace, workspaceFinding, runRoot, progress, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"Lane audit {lane.Request.Name}: {exception.Message}");
            }
        }

        foreach (var source in resolved)
        foreach (var target in resolved.Where(candidate => !candidate.Request.Name.Equals(source.Request.Name, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!workspaces.TryGetValue(target.Request.Name, out var workspace)) continue;
            try { crossAudits.Add(AuditDeployment(source, target, workspace, runRoot, expectedCompatible: false, cancellationToken)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"Cross deployment {source.Request.Name} -> {target.Request.Name}: {exception.Message}");
            }
        }

        for (var leftIndex = 0; leftIndex < resolved.Length; leftIndex++)
        for (var rightIndex = leftIndex + 1; rightIndex < resolved.Length; rightIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { collisionAudits.Add(AuditCollisions(resolved[leftIndex], resolved[rightIndex], cancellationToken)); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"Collision audit {resolved[leftIndex].Request.Name}/{resolved[rightIndex].Request.Name}: {exception.Message}");
            }
        }

        var jsonPath = Path.Combine(runRoot, "compatibility-lab-report.json");
        var markdownPath = Path.Combine(runRoot, "compatibility-lab-report.md");
        var report = new CompatibilityLabReport(ReportFormatVersion, started, DateTimeOffset.UtcNow, runRoot,
            cloneAudits, laneAudits, crossAudits, collisionAudits, errors, jsonPath, markdownPath);
        AtomicWrite(jsonPath, JsonSerializer.Serialize(report, JsonOptions));
        AtomicWrite(markdownPath, RenderMarkdown(report));
        progress?.Report(new("Complete", 1, 1, runRoot));
        return report;
    }

    private static CompatibilityLabLaneAudit AuditLane(
        ResolvedLane lane,
        ServerWorkspace workspace,
        string? workspaceFinding,
        string runRoot,
        IProgress<CompatibilityLabProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tableRoot = RequiredDirectory(lane.Request.TableRoot, $"{lane.Request.Name} table root");
        var definitions = RequiredDirectory(lane.Request.DefinitionsRoot, $"{lane.Request.Name} WoWDBDefs root");
        var xml = string.IsNullOrWhiteSpace(lane.Request.XmlSchemaPath) ? null : RequiredFile(lane.Request.XmlSchemaPath, $"{lane.Request.Name} WDBX XML");
        var tables = EnumerateTables(tableRoot, lane.Request.RecursiveTableDiscovery).ToArray();
        progress?.Report(new($"{lane.Request.Name}: compatibility", 0, tables.Length, tableRoot));
        var compatibility = new List<ClientTableCompatibilityAssessment>(tables.Length);
        for (var index = 0; index < tables.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            compatibility.Add(ClientTableCompatibilityPolicy.Assess(tables[index], lane.Profile));
            if ((index + 1) % 128 == 0 || index + 1 == tables.Length)
                progress?.Report(new($"{lane.Request.Name}: compatibility", index + 1, tables.Length, tables[index]));
        }

        progress?.Report(new($"{lane.Request.Name}: schema round-trip", 0, tables.Length, tableRoot));
        var schema = AuditSchemas(definitions, tableRoot, lane.Profile.ClientBuild, xml, lane.Request.RecursiveTableDiscovery);
        FixedTableMutationAuditSummary? fixedMutation = null;
        Wdc1MutationAuditSummary? wdc1Mutation = null;
        var mutationRoot = Path.Combine(runRoot, "mutations", Slug(lane.Request.Name));
        if (lane.Profile.SupportsWdbc || lane.Profile.SupportsDb2)
        {
            progress?.Report(new($"{lane.Request.Name}: fixed mutation", 0, tables.Length, tableRoot));
            fixedMutation = FixedTableMutationAuditService.Audit(definitions, tableRoot, lane.Profile.ClientBuild,
                Path.Combine(mutationRoot, "fixed"), xml, cancellationToken, lane.Request.RecursiveTableDiscovery);
        }
        if (lane.Profile.SupportsWdc1)
        {
            progress?.Report(new($"{lane.Request.Name}: WDC1 mutation", 0, tables.Length, tableRoot));
            wdc1Mutation = AuditWdc1Mutations(definitions, tableRoot, lane.Profile.ClientBuild,
                Path.Combine(mutationRoot, "wdc1"), cancellationToken, lane.Request.RecursiveTableDiscovery);
        }

        var coreSourceRoot = string.IsNullOrWhiteSpace(lane.Request.CoreSourceRoot)
            ? null
            : RequiredDirectory(lane.Request.CoreSourceRoot, $"{lane.Request.Name} core source");
        var bindings = ServerTableBindingCatalog.Resolve(workspace.CoreFamily, coreSourceRoot, lane.Profile.ClientBuild);
        var native = AuditDeployment(lane, lane, workspace, runRoot, expectedCompatible: true, cancellationToken);
        var laneErrors = new List<string>();
        var findings = new List<string>();
        if (workspaceFinding is not null) findings.Add(workspaceFinding);
        if (coreSourceRoot is null)
            findings.Add($"No matching core source checkout was supplied. Table format, schema, mutation, and client/server byte-layout checks were retained, but server consumer ownership remains conservative; built-in bindings for other client builds were not applied to build {lane.Profile.ClientBuild:N0}.");
        if (tables.Length == 0) laneErrors.Add("No client tables were found.");
        if (workspace.CoreFamily == ServerCoreFamily.Unknown) laneErrors.Add("The cloned server family was not detected.");
        return new(lane.Request.Name, lane.Profile.Id, lane.Profile.ClientBuild, lane.Profile.ArchiveFormat,
            workspace.CoreFamily, tableRoot, lane.Request.RecursiveTableDiscovery, tables.Length,
            compatibility.GroupBy(value => value.State).ToDictionary(group => group.Key, group => group.Count()),
            schema, fixedMutation, wdc1Mutation,
            bindings.GroupBy(value => value.Consumption).ToDictionary(group => group.Key, group => group.Count()),
            native, findings, laneErrors);
    }

    private static CompatibilityLabDeploymentAudit AuditDeployment(
        ResolvedLane source,
        ResolvedLane target,
        ServerWorkspace targetWorkspace,
        string runRoot,
        bool expectedCompatible,
        CancellationToken cancellationToken)
    {
        var label = $"{Slug(source.Request.Name)}-to-{Slug(target.Request.Name)}";
        var root = Path.Combine(runRoot, expectedCompatible ? "native-deployments" : "cross-deployments", label);
        var plan = ClientServerDeploymentPlanner.Analyze(source.Request.TableRoot, targetWorkspace, target.Profile,
            target.Request.CoreSourceRoot, cancellationToken, source.Request.RecursiveTableDiscovery);
        Directory.CreateDirectory(root);
        var exportedPlan = Path.Combine(root, "client-server-plan.json");
        ClientServerDeploymentPlanner.Save(exportedPlan, plan);
        var stage = ClientServerDeploymentPlanner.Stage(Path.Combine(root, "stage"), plan);
        var statusCounts = plan.Entries.GroupBy(entry => entry.Status).ToDictionary(group => group.Key, group => group.Count());
        var incompatible = statusCounts.GetValueOrDefault(ClientServerPlanStatus.IncompatibleTarget) +
            statusCounts.GetValueOrDefault(ClientServerPlanStatus.InvalidDbc) +
            statusCounts.GetValueOrDefault(ClientServerPlanStatus.ConflictingClientLayers);
        var findings = new List<string>();
        bool passed;
        if (expectedCompatible)
        {
            passed = incompatible == 0;
            if (!passed) findings.Add($"{incompatible:N0} native table(s) failed target/layout validation.");
        }
        else
        {
            passed = plan.Entries.Count > 0 && incompatible == plan.Entries.Count && stage.ClientFiles == 0 && stage.ServerFiles == 0;
            if (!passed) findings.Add($"Cross-target attack left {plan.Entries.Count - incompatible:N0} non-incompatible entries and staged {stage.ClientFiles:N0}/{stage.ServerFiles:N0} client/server files.");
        }
        return new(source.Request.Name, target.Request.Name, target.Profile.Id, plan.Entries.Count, statusCounts,
            stage.ClientFiles, stage.ServerFiles, stage.BlockedFiles, exportedPlan, stage.RootPath, expectedCompatible, passed, findings);
    }

    private static CompatibilityLabCollisionAudit AuditCollisions(ResolvedLane left, ResolvedLane right, CancellationToken cancellationToken)
    {
        var leftTables = EnumerateTables(left.Request.TableRoot, left.Request.RecursiveTableDiscovery).GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.Order(StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
        var rightTables = EnumerateTables(right.Request.TableRoot, right.Request.RecursiveTableDiscovery).GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key!, group => group.Order(StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
        var shared = leftTables.Keys.Intersect(rightTables.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var samples = new List<CompatibilityLabCollisionSample>();
        var sameLayout = 0; var leftAccepted = 0; var rightAccepted = 0;
        foreach (var table in shared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var leftPath = leftTables[table]; var rightPath = rightTables[table];
            var leftOwn = ClientTableCompatibilityPolicy.Assess(leftPath, left.Profile);
            var rightOwn = ClientTableCompatibilityPolicy.Assess(rightPath, right.Profile);
            var leftCross = ClientTableCompatibilityPolicy.Assess(leftPath, right.Profile);
            var rightCross = ClientTableCompatibilityPolicy.Assess(rightPath, left.Profile);
            var layout = false;
            if (leftOwn.State == ClientTableCompatibilityState.Supported && rightOwn.State == ClientTableCompatibilityState.Supported)
                layout = ClientTableCompatibilityPolicy.HasSameLayout(WdbcFile.Load(leftPath), WdbcFile.Load(rightPath));
            if (layout) sameLayout++;
            if (leftCross.CanTarget) leftAccepted++;
            if (rightCross.CanTarget) rightAccepted++;
            if (samples.Count < MaximumIssueSamples)
                samples.Add(new(table, leftPath, leftOwn.Container, rightPath, rightOwn.Container, layout, leftCross.CanTarget, rightCross.CanTarget));
        }
        return new(left.Request.Name, right.Request.Name, shared.Length, sameLayout, leftAccepted, rightAccepted, samples,
            leftAccepted == 0 && rightAccepted == 0);
    }

    private static CompatibilityLabCloneAudit AuditClone(
        string name,
        CompatibilityLabClonePair pair,
        int requestedWorkers,
        IProgress<CompatibilityLabProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sourceRoot = RequiredDirectory(pair.SourceRoot, $"{name} source");
        var cloneRoot = RequiredDirectory(pair.CloneRoot, $"{name} clone");
        var excludedDirectories = NormalizeExcludedDirectoryNames(pair.ExcludedDirectoryNames);
        var excludedFiles = NormalizeExcludedFilePaths(pair.ExcludedFilePaths);
        var source = FileMap(sourceRoot, excludedDirectories, excludedFiles);
        var clone = FileMap(cloneRoot, excludedDirectories, excludedFiles);
        var issues = new ConcurrentBag<CompatibilityLabIntegrityIssue>();
        var errors = new ConcurrentBag<string>();
        var missing = source.Keys.Except(clone.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var extra = clone.Keys.Except(source.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in missing) issues.Add(new("Missing", path, source[path].Length.ToString("N0"), "absent"));
        foreach (var path in extra) issues.Add(new("Extra", path, "absent", clone[path].Length.ToString("N0")));
        var common = source.Keys.Intersect(clone.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var lengthMismatch = 0; var hashMismatch = 0; var hashed = 0; var completed = 0;
        var workers = requestedWorkers <= 0 ? Math.Clamp(Environment.ProcessorCount, 1, 16) : Math.Clamp(requestedWorkers, 1, 64);
        progress?.Report(new($"{name}: clone SHA-256", 0, common.Length, sourceRoot));
        Parallel.ForEach(common, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = workers }, relative =>
        {
            try
            {
                var sourceFile = source[relative]; var cloneFile = clone[relative];
                if (sourceFile.Length != cloneFile.Length)
                {
                    Interlocked.Increment(ref lengthMismatch);
                    issues.Add(new("Length", relative, sourceFile.Length.ToString("N0"), cloneFile.Length.ToString("N0")));
                }
                else
                {
                    var sourceHash = Hash(sourceFile.FullName); var cloneHash = Hash(cloneFile.FullName);
                    Interlocked.Increment(ref hashed);
                    if (!sourceHash.Equals(cloneHash, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref hashMismatch);
                        issues.Add(new("SHA256", relative, sourceHash, cloneHash));
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                errors.Add($"{relative}: {exception.Message}");
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                if (done % 256 == 0 || done == common.Length) progress?.Report(new($"{name}: clone SHA-256", done, common.Length, relative));
            }
        });
        return new(name, sourceRoot, cloneRoot, excludedDirectories, excludedFiles, source.Count, clone.Count,
            source.Values.Sum(file => file.Length), clone.Values.Sum(file => file.Length), hashed,
            missing.Length, extra.Length, lengthMismatch, hashMismatch,
            issues.OrderBy(issue => issue.RelativePath, StringComparer.OrdinalIgnoreCase).Take(MaximumIssueSamples).ToArray(),
            errors.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static DbdSchemaAuditSummary AuditSchemas(
        string definitionsRoot,
        string tableRoot,
        int build,
        string? xmlSchemaPath,
        bool recursiveTableDiscovery)
    {
        var roots = DirectTableRoots(tableRoot, recursiveTableDiscovery);
        if (roots.Count == 0) throw new InvalidDataException($"No direct client-table corpus exists below {tableRoot}.");
        var summaries = roots.Select(root => DbdSchemaService.Audit(definitionsRoot, root, build, xmlSchemaPath, verifyRoundTrip: true)).ToArray();
        return new(build, definitionsRoot, tableRoot, summaries.SelectMany(summary => summary.Rows)
            .OrderBy(row => row.Table, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Container, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static Wdc1MutationAuditSummary AuditWdc1Mutations(
        string definitionsRoot,
        string tableRoot,
        int build,
        string artifactParent,
        CancellationToken cancellationToken,
        bool recursiveTableDiscovery)
    {
        var roots = DirectTableRoots(tableRoot, recursiveTableDiscovery).Where(root => Directory.EnumerateFiles(root, "*.db2", SearchOption.TopDirectoryOnly)
            .Any(path => new FileInfo(path).Length > 0 && WdbcFile.Load(path).ContainerKind == ClientTableContainerKind.Wdc1)).ToArray();
        if (roots.Length == 0) throw new InvalidDataException($"No direct WDC1 corpus exists below {tableRoot}.");
        var summaries = roots.Select((root, index) => Wdc1MutationAuditService.Audit(definitionsRoot, root, build,
            Path.Combine(artifactParent, $"root-{index + 1}"), cancellationToken)).ToArray();
        return new(build, definitionsRoot, tableRoot, summaries.Sum(summary => summary.ScannedTables),
            summaries.SelectMany(summary => summary.RequiredCapabilities).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            summaries.SelectMany(summary => summary.Cases).OrderBy(result => result.Table, StringComparer.OrdinalIgnoreCase).ToArray(),
            summaries.SelectMany(summary => summary.Errors).Order(StringComparer.OrdinalIgnoreCase).ToArray(), artifactParent);
    }

    private static IReadOnlyList<string> DirectTableRoots(string root, bool recursiveTableDiscovery)
    {
        root = RequiredDirectory(root, "Client-table root");
        if (!recursiveTableDiscovery)
        {
            if (!Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Any(ClientTableCompatibilityPolicy.IsTableExtension))
                throw new InvalidDataException($"No direct client-table corpus exists below {root}; recursive table discovery is disabled.");
            return [root];
        }
        return Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root)
            .Where(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).Any(ClientTableCompatibilityPolicy.IsTableExtension))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, FileInfo> FileMap(
        string root,
        IReadOnlyList<string> excludedDirectoryNames,
        IReadOnlyList<string> excludedFilePaths)
    {
        var excludedDirectories = excludedDirectoryNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedFiles = excludedFilePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return EnumerateCompatibilityTreeFiles(root, excludedDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(root, path)))
            .Where(value => !excludedFiles.Contains(value.Relative))
            .ToDictionary(value => value.Relative, value => new FileInfo(value.Path), StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateCompatibilityTreeFiles(string root, IReadOnlySet<string> excludedDirectoryNames)
    {
        root = Path.GetFullPath(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Compatibility trees cannot use a reparse-point root: {root}");
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory && excludedDirectoryNames.Contains(Path.GetFileName(entry))) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Compatibility trees cannot contain reparse points because they may escape the declared source or clone root: {entry}");
                if (isDirectory) pending.Push(entry);
                else yield return entry;
            }
        }
    }

    private static IEnumerable<string> EnumerateTables(string root, bool recursiveTableDiscovery = true)
    {
        root = RequiredDirectory(root, "Client-table root");
        return Directory.EnumerateFiles(root, "*", recursiveTableDiscovery ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(ClientTableCompatibilityPolicy.IsTableExtension)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    private static string RenderMarkdown(CompatibilityLabReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# WoW Crucible Compatibility Lab").AppendLine();
        text.AppendLine($"- Result: **{(report.Passed ? "PASS" : "FAIL")}**");
        text.AppendLine($"- Started UTC: {report.StartedUtc:O}");
        text.AppendLine($"- Completed UTC: {report.CompletedUtc:O}");
        text.AppendLine($"- Run root: `{report.RunRoot}`").AppendLine();
        text.AppendLine("## Clone Integrity").AppendLine();
        text.AppendLine("| Pair | Result | Source files | Clone files | Hashed | Missing | Extra | Length | SHA-256 |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var audit in report.CloneAudits) text.AppendLine($"| {audit.Name} | {(audit.Passed ? "PASS" : "FAIL")} | {audit.SourceFiles:N0} | {audit.CloneFiles:N0} | {audit.HashedPairs:N0} | {audit.MissingFiles:N0} | {audit.ExtraFiles:N0} | {audit.LengthMismatches:N0} | {audit.HashMismatches:N0} |");
        text.AppendLine().AppendLine("## Native Lanes").AppendLine();
        text.AppendLine("| Lane | Target | Core | Tables | Schema | Mutation | Native deploy | Result |");
        text.AppendLine("|---|---|---|---:|---:|---:|---:|---:|");
        foreach (var lane in report.LaneAudits)
        {
            var mutation = (lane.FixedMutationAudit?.Passed ?? true) && (lane.Wdc1MutationAudit?.Passed ?? true);
            text.AppendLine($"| {lane.Name} | {lane.ProfileId} ({lane.Build:N0}) | {lane.CoreFamily} | {lane.TableFiles:N0} | {(lane.SchemaAudit.Failures == 0 ? "PASS" : "FAIL")} | {(mutation ? "PASS" : "FAIL")} | {(lane.NativeDeployment.Passed ? "PASS" : "FAIL")} | {(lane.Passed ? "PASS" : "FAIL")} |");
        }
        text.AppendLine().AppendLine("## Cross-Target Attacks").AppendLine();
        text.AppendLine("| Source -> target | Entries | Incompatible | Staged client | Staged server | Result |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var audit in report.CrossTargetDeployments) text.AppendLine($"| {audit.SourceLane} -> {audit.TargetLane} | {audit.Entries:N0} | {audit.StatusCounts.GetValueOrDefault(ClientServerPlanStatus.IncompatibleTarget):N0} | {audit.StagedClientFiles:N0} | {audit.StagedServerFiles:N0} | {(audit.Passed ? "PASS" : "FAIL")} |");
        text.AppendLine().AppendLine("## Logical-Table Collisions").AppendLine();
        text.AppendLine("| Lanes | Shared names | Same layout | Left accepted by right | Right accepted by left | Result |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var audit in report.CollisionAudits) text.AppendLine($"| {audit.LeftLane} / {audit.RightLane} | {audit.SharedLogicalTables:N0} | {audit.SameLayoutTables:N0} | {audit.LeftAcceptedByRightTarget:N0} | {audit.RightAcceptedByLeftTarget:N0} | {(audit.Passed ? "PASS" : "FAIL")} |");
        text.AppendLine().AppendLine("## Publication Boundary").AppendLine();
        text.AppendLine("MPQ targets may produce a manifest-backed payload. CASC targets deliberately stop at a hash-verified payload until a matching target-build CASC publisher is configured; the lab never disguises a loose folder as a publishable Legion client.");
        var findings = report.LaneAudits.SelectMany(lane => lane.Findings.Select(finding => $"{lane.Name}: {finding}"))
            .Concat(report.CrossTargetDeployments.SelectMany(audit => audit.Findings.Select(finding => $"{audit.SourceLane} -> {audit.TargetLane}: {finding}"))).ToArray();
        if (findings.Length > 0)
        {
            text.AppendLine().AppendLine("## Findings").AppendLine();
            foreach (var finding in findings) text.AppendLine($"- {finding}");
        }
        var allErrors = report.Errors.Concat(report.CloneAudits.SelectMany(audit => audit.Errors)).Concat(report.LaneAudits.SelectMany(audit => audit.Errors)).ToArray();
        if (allErrors.Length > 0)
        {
            text.AppendLine().AppendLine("## Errors").AppendLine();
            foreach (var error in allErrors) text.AppendLine($"- {error}");
        }
        return text.ToString();
    }

    private static (ServerWorkspace Workspace, string? Finding) BindWorkspaceToLane(ServerWorkspace detected, ResolvedLane lane)
    {
        var serverRoot = RequiredDirectory(lane.Request.ServerCloneRoot, $"{lane.Request.Name} cloned server");
        var tableRoot = RequiredDirectory(lane.Request.TableRoot, $"{lane.Request.Name} table root");
        var relative = Path.GetRelativePath(serverRoot, tableRoot);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidDataException($"{lane.Request.Name} table root is outside its cloned server: {tableRoot}");
        var dbcChild = Path.Combine(tableRoot, "dbc");
        var db2Child = Path.Combine(tableRoot, "db2");
        var dbc = Directory.Exists(dbcChild) ? dbcChild : tableRoot;
        var db2 = Directory.Exists(db2Child) ? db2Child : null;
        var bound = detected with { DbcPath = dbc, Db2Path = db2 };
        var detectedPaths = detected.ClientTablePaths;
        var changed = !detectedPaths.SequenceEqual(bound.ClientTablePaths, StringComparer.OrdinalIgnoreCase);
        return (bound, changed
            ? $"Detected config table path(s) {string.Join(" | ", detectedPaths)} were not used by the lab; analysis was rebound to isolated clone path(s) {string.Join(" | ", bound.ClientTablePaths)}."
            : null);
    }

    private static void ValidateRequest(CompatibilityLabRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FormatVersion != RequestFormatVersion) throw new InvalidDataException($"Unsupported compatibility-lab request version {request.FormatVersion}.");
        if (string.IsNullOrWhiteSpace(request.OutputRoot)) throw new InvalidDataException("Compatibility-lab output root is required.");
        if (request.Lanes.Count < 2) throw new InvalidDataException("Compatibility lab requires at least two target lanes for cross-target stress testing.");
        var duplicate = request.Lanes.GroupBy(lane => lane.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Compatibility-lab lane name is duplicated: {duplicate.Key}");
        foreach (var lane in request.Lanes)
        {
            if (string.IsNullOrWhiteSpace(lane.Name) || string.IsNullOrWhiteSpace(lane.ProfileId) ||
                string.IsNullOrWhiteSpace(lane.TableRoot) || string.IsNullOrWhiteSpace(lane.ServerCloneRoot) ||
                string.IsNullOrWhiteSpace(lane.DefinitionsRoot) || lane.ClonePairs.Count == 0)
                throw new InvalidDataException("Every compatibility-lab lane requires a name, profile ID, table root, server clone root, definitions root, and at least one clone pair.");
            foreach (var pair in lane.ClonePairs)
            {
                if (string.IsNullOrWhiteSpace(pair.Name) || string.IsNullOrWhiteSpace(pair.SourceRoot) || string.IsNullOrWhiteSpace(pair.CloneRoot))
                    throw new InvalidDataException($"Every compatibility clone pair in lane {lane.Name} requires a name, source root, and clone root.");
                _ = NormalizeExcludedDirectoryNames(pair.ExcludedDirectoryNames);
                _ = NormalizeExcludedFilePaths(pair.ExcludedFilePaths);
            }
        }
    }

    private static string RequiredDirectory(string path, string label)
    {
        path = Path.GetFullPath(path ?? string.Empty);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"{label} does not exist: {path}");
    }

    private static string RequiredFile(string path, string label)
    {
        path = Path.GetFullPath(path ?? string.Empty);
        return File.Exists(path) ? path : throw new FileNotFoundException($"{label} does not exist.", path);
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string Slug(string value)
    {
        var result = new string(value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        return result.Length == 0 ? "lane" : result;
    }

    private static void AtomicWrite(string path, string content)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record ResolvedLane(CompatibilityLabLane Request, TargetProfile Profile);
}
