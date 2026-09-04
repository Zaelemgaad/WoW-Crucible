using System.Security.Cryptography;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum ClientServerPlanStatus
{
    Identical,
    ClientOnly,
    ServerDbcChange,
    SqlOverlayRequiresAudit,
    UnusedByServer,
    UnknownConsumer,
    MissingServerDbc,
    ConflictingClientLayers,
    IncompatibleTarget,
    InvalidDbc
}

public sealed record ClientServerPlanEntry(
    string DbcFileName,
    string? ClientSourcePath,
    string? ClientSha256,
    int? ClientRows,
    int? ClientFields,
    string? ServerDbcPath,
    string? ServerSha256,
    ClientServerPlanStatus Status,
    ServerTableConsumption Consumption,
    DeploymentDestination Destinations,
    string? SqlTableName,
    RestartRequirement Restart,
    string Profile,
    string SupportedRevision,
    string Guidance,
    IReadOnlyList<string>? ConflictingSources = null);

public sealed record ClientServerDeploymentPlan(
    int FormatVersion,
    DateTimeOffset GeneratedUtc,
    string ClientDbcRoot,
    string ServerRoot,
    string ServerDbcRoot,
    ServerCoreFamily CoreFamily,
    string? CoreSourceRoot,
    string TargetProfileId,
    string TargetDisplayName,
    int TargetBuild,
    ClientTableFormat TargetTableFormats,
    ArchiveFormat ClientArchiveFormat,
    IReadOnlyList<string> ServerTableRoots,
    IReadOnlyList<ClientServerPlanEntry> Entries);

public sealed record ClientServerStageResult(
    string RootPath,
    string PlanPath,
    string? PatchManifestPath,
    string ClientPayloadRoot,
    ArchiveFormat ClientArchiveFormat,
    bool RequiresClientPublisher,
    int ClientFiles,
    int ServerFiles,
    int BlockedFiles);

public static class ClientServerDeploymentPlanner
{
    private const int FormatVersion = 3;

    public static ClientServerDeploymentPlan Analyze(string clientDbcRoot, ServerWorkspace workspace, TargetProfile target,
        string? coreSourceRoot = null, CancellationToken cancellationToken = default, bool recursiveClientTables = true)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(target);
        clientDbcRoot = Path.GetFullPath(clientDbcRoot);
        if (!Directory.Exists(clientDbcRoot)) throw new DirectoryNotFoundException($"Extracted client-table folder not found: {clientDbcRoot}");
        var serverTableRoots = workspace.ClientTablePaths.Where(Directory.Exists).ToArray();
        if (serverTableRoots.Length == 0)
            throw new DirectoryNotFoundException($"No detected server DBC/DB2 table folder is available below {workspace.RootPath}.");
        coreSourceRoot = Directory.Exists(coreSourceRoot) ? Path.GetFullPath(coreSourceRoot) : null;

        var clientGroups = EnumerateTables(clientDbcRoot, recursiveClientTables)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (clientGroups.Length == 0) throw new InvalidDataException($"No .dbc or .db2 files were found under {clientDbcRoot}. Select an extracted DBFilesClient table folder first.");
        var serverFiles = serverTableRoots.SelectMany(root => EnumerateTables(root, recursive: true))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key!, group => SelectServerCandidate(group, workspace.RootPath), StringComparer.OrdinalIgnoreCase);
        var bindings = ServerTableBindingCatalog.Resolve(workspace.CoreFamily, coreSourceRoot, target.ClientBuild)
            .ToDictionary(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase);
        var entries = new List<ClientServerPlanEntry>(clientGroups.Length);

        foreach (var group in clientGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = group.Select(Path.GetFullPath).ToArray();
            var incompatible = candidates.Select(path => ClientTableCompatibilityPolicy.Assess(path, target))
                .Where(assessment => !assessment.CanTarget).ToArray();
            if (incompatible.Length > 0)
            {
                entries.Add(new(group.Key!, null, null, null, null, serverFiles.GetValueOrDefault(group.Key!), null,
                    ClientServerPlanStatus.IncompatibleTarget, ServerTableConsumption.Unknown, DeploymentDestination.None, null,
                    RestartRequirement.None, target.DisplayName, $"Build {target.ClientBuild:N0}",
                    string.Join(" | ", incompatible.Select(assessment => assessment.Message)), candidates));
                continue;
            }
            var candidateHashes = candidates.Select(Hash).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (candidateHashes.Length > 1)
            {
                entries.Add(new(group.Key!, null, null, null, null, serverFiles.GetValueOrDefault(group.Key!), null,
                    ClientServerPlanStatus.ConflictingClientLayers, ServerTableConsumption.Unknown, DeploymentDestination.None, null,
                    RestartRequirement.None, "Client extraction", "Unresolved layers",
                    "Multiple extracted layers contain different versions of this DBC. Compare/promote records or select the effective layer before deployment.", candidates));
                continue;
            }

            var clientPath = candidates[0]; var clientHash = candidateHashes[0];
            serverFiles.TryGetValue(group.Key!, out var serverPath);
            var serverHash = serverPath is null ? null : Hash(serverPath);
            var binding = bindings.GetValueOrDefault(group.Key!) ?? (coreSourceRoot is not null
                ? new(workspace.CoreFamily, "Current core source (absent from DBCStores)", group.Key!, Path.GetFileNameWithoutExtension(group.Key!), ServerTableConsumption.ClientOnly, null, DbcRecordKeyStrategy.None, RowDimensionKind.None, DeploymentDestination.ClientPatch, RestartRequirement.ClientRestart, true, "Selected source checkout")
                : new(workspace.CoreFamily, $"{workspace.CoreFamily} profile (mapping unknown)", group.Key!, Path.GetFileNameWithoutExtension(group.Key!), ServerTableConsumption.Unknown, null, DbcRecordKeyStrategy.None, RowDimensionKind.None, DeploymentDestination.ClientPatch, RestartRequirement.ClientRestart, false, "No matching built-in binding"));
            if (new FileInfo(clientPath).Length == 0 && serverHash is not null && clientHash.Equals(serverHash, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new(group.Key!, clientPath, clientHash, 0, 0, serverPath, serverHash, ClientServerPlanStatus.Identical,
                    binding.Consumption, binding.Destinations, binding.SqlTableName, binding.Restart, binding.Profile,
                    binding.SupportedRevision, "Client and server contain the same intentional empty placeholder; no deployment is needed.", candidates.Length > 1 ? candidates : null));
                continue;
            }
            if (new FileInfo(clientPath).Length == 0)
            {
                var emptyStatus = serverPath is not null && new FileInfo(serverPath).Length > 0
                    ? ClientServerPlanStatus.IncompatibleTarget
                    : Classify(binding, serverPath, clientHash, serverHash);
                var guidance = emptyStatus == ClientServerPlanStatus.IncompatibleTarget
                    ? "An empty client placeholder cannot replace a populated server table."
                    : "The client table is an intentional empty placeholder; its extension is target-compatible, but no binary layout is claimed.";
                entries.Add(new(group.Key!, clientPath, clientHash, 0, 0, serverPath, serverHash, emptyStatus,
                    binding.Consumption, emptyStatus == ClientServerPlanStatus.IncompatibleTarget ? DeploymentDestination.None : binding.Destinations,
                    binding.SqlTableName, emptyStatus == ClientServerPlanStatus.IncompatibleTarget ? RestartRequirement.None : binding.Restart,
                    target.DisplayName, $"Build {target.ClientBuild:N0}", guidance, candidates.Length > 1 ? candidates : null));
                continue;
            }
            WdbcFile client;
            try { client = WdbcFile.Load(clientPath); }
            catch (Exception ex)
            {
                entries.Add(new(group.Key!, clientPath, clientHash, null, null, serverFiles.GetValueOrDefault(group.Key!), null,
                    ClientServerPlanStatus.InvalidDbc, ServerTableConsumption.Unknown, DeploymentDestination.None, null,
                    RestartRequirement.None, "Client extraction", "Invalid WDBC", ex.Message, candidates.Length > 1 ? candidates : null));
                continue;
            }

            if (serverPath is not null && new FileInfo(serverPath).Length > 0)
            {
                var serverAssessment = ClientTableCompatibilityPolicy.Assess(serverPath, target);
                if (!serverAssessment.CanTarget)
                {
                    entries.Add(new(group.Key!, clientPath, clientHash, client.RowCount, client.FieldCount, serverPath, serverHash,
                        ClientServerPlanStatus.IncompatibleTarget, binding.Consumption, DeploymentDestination.None, binding.SqlTableName,
                        RestartRequirement.None, target.DisplayName, $"Build {target.ClientBuild:N0}",
                        $"The detected server table is incompatible with the selected target: {serverAssessment.Message}", candidates.Length > 1 ? candidates : null));
                    continue;
                }
                try
                {
                    var server = WdbcFile.Load(serverPath);
                    if (!ClientTableCompatibilityPolicy.HasSameLayout(client, server))
                    {
                        entries.Add(new(group.Key!, clientPath, clientHash, client.RowCount, client.FieldCount, serverPath, serverHash,
                            ClientServerPlanStatus.IncompatibleTarget, binding.Consumption, DeploymentDestination.None, binding.SqlTableName,
                            RestartRequirement.None, target.DisplayName, $"Build {target.ClientBuild:N0}",
                            $"Client {client.ContainerKind} layout {client.FieldCount:N0} fields/{client.RecordSize:N0} bytes does not match server {server.ContainerKind} layout {server.FieldCount:N0}/{server.RecordSize:N0}; table/layout hashes must also match for DB2/WDC1.", candidates.Length > 1 ? candidates : null));
                        continue;
                    }
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
                {
                    entries.Add(new(group.Key!, clientPath, clientHash, client.RowCount, client.FieldCount, serverPath, serverHash,
                        ClientServerPlanStatus.IncompatibleTarget, binding.Consumption, DeploymentDestination.None, binding.SqlTableName,
                        RestartRequirement.None, target.DisplayName, $"Build {target.ClientBuild:N0}",
                        $"The detected server table cannot be validated against the selected target: {exception.Message}", candidates.Length > 1 ? candidates : null));
                    continue;
                }
            }
            var status = Classify(binding, serverPath, clientHash, serverHash);
            entries.Add(new(group.Key!, clientPath, clientHash, client.RowCount, client.FieldCount, serverPath, serverHash, status,
                binding.Consumption, binding.Destinations, binding.SqlTableName, binding.Restart, binding.Profile,
                binding.SupportedRevision, Guidance(status, binding.SqlTableName), candidates.Length > 1 ? candidates : null));
        }

        return new(FormatVersion, DateTimeOffset.UtcNow, clientDbcRoot, workspace.RootPath, serverTableRoots[0],
            workspace.CoreFamily, coreSourceRoot, target.Id, target.DisplayName, target.ClientBuild, target.TableFormats,
            target.ArchiveFormat, serverTableRoots, entries);
    }

    public static void Save(string path, ClientServerDeploymentPlan plan)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static ClientServerStageResult Stage(string rootPath, ClientServerDeploymentPlan plan, string patchName = "Extracted client-table changes", string outputMpqName = "patch-Crucible.MPQ")
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.FormatVersion != FormatVersion) throw new InvalidDataException($"Unsupported client/server deployment plan version {plan.FormatVersion}.");
        rootPath = Path.GetFullPath(rootPath); Directory.CreateDirectory(rootPath);
        var clientContainer = plan.ClientArchiveFormat == ArchiveFormat.Mpq ? "client-mpq-payload" : "client-casc-payload";
        var clientRoot = Path.Combine(rootPath, clientContainer, "DBFilesClient");
        var serverRoot = Path.Combine(rootPath, "server-files");
        Directory.CreateDirectory(clientRoot); Directory.CreateDirectory(serverRoot);
        var clientEntries = new List<PatchEntry>(); var serverFiles = 0; var blocked = 0;

        foreach (var entry in plan.Entries)
        {
            if (entry.ClientSourcePath is null || entry.Status is ClientServerPlanStatus.Identical) continue;
            if (entry.Status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.IncompatibleTarget or ClientServerPlanStatus.InvalidDbc)
            { blocked++; continue; }
            var stagedClient = Path.Combine(clientRoot, entry.DbcFileName);
            File.Copy(entry.ClientSourcePath, stagedClient, true);
            clientEntries.Add(new(stagedClient, $"DBFilesClient\\{entry.DbcFileName}"));
            var stageForServer = entry.Status is ClientServerPlanStatus.ServerDbcChange or ClientServerPlanStatus.SqlOverlayRequiresAudit ||
                entry.Status == ClientServerPlanStatus.MissingServerDbc && (entry.Consumption is ServerTableConsumption.DbcLoaded or ServerTableConsumption.SqlOverlayed);
            if (stageForServer)
            {
                var target = entry.ServerDbcPath ?? Path.Combine(plan.ServerTableRoots.First(), entry.DbcFileName);
                var relative = Path.GetRelativePath(plan.ServerRoot, target);
                if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    blocked++;
                    continue;
                }
                var stagedServer = Path.Combine(serverRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(stagedServer)!);
                File.Copy(entry.ClientSourcePath, stagedServer, true); serverFiles++;
            }
            if (entry.Status == ClientServerPlanStatus.UnknownConsumer) blocked++;
        }

        var planPath = Path.Combine(rootPath, "client-server-plan.json"); Save(planPath, plan);
        string? manifestPath = null;
        if (clientEntries.Count > 0 && plan.ClientArchiveFormat == ArchiveFormat.Mpq)
        {
            manifestPath = Path.Combine(rootPath, clientContainer, "client-tables.crucible-patch.json");
            PatchManifestService.Save(manifestPath, patchName, outputMpqName, clientEntries,
                policy: new PatchManifestPolicy(["DBFilesClient\\*.dbc", "DBFilesClient\\*.db2"], null, clientEntries.Count));
        }
        return new(rootPath, planPath, manifestPath, Path.Combine(rootPath, clientContainer), plan.ClientArchiveFormat,
            plan.ClientArchiveFormat == ArchiveFormat.Casc && clientEntries.Count > 0, clientEntries.Count, serverFiles, blocked);
    }

    private static ClientServerPlanStatus Classify(ServerTableBinding binding, string? serverPath, string clientHash, string? serverHash)
    {
        if (serverHash is not null && clientHash.Equals(serverHash, StringComparison.OrdinalIgnoreCase)) return ClientServerPlanStatus.Identical;
        return binding.Consumption switch
        {
            ServerTableConsumption.ClientOnly => ClientServerPlanStatus.ClientOnly,
            ServerTableConsumption.Unused => ClientServerPlanStatus.UnusedByServer,
            ServerTableConsumption.SqlOverlayed when serverPath is null => ClientServerPlanStatus.MissingServerDbc,
            ServerTableConsumption.SqlOverlayed => ClientServerPlanStatus.SqlOverlayRequiresAudit,
            ServerTableConsumption.DbcLoaded when serverPath is null => ClientServerPlanStatus.MissingServerDbc,
            ServerTableConsumption.DbcLoaded => ClientServerPlanStatus.ServerDbcChange,
            _ => ClientServerPlanStatus.UnknownConsumer
        };
    }

    private static string Guidance(ClientServerPlanStatus status, string? sqlTable) => status switch
    {
        ClientServerPlanStatus.Identical => "Client and server table bytes already match; no deployment is needed.",
        ClientServerPlanStatus.ClientOnly => "Stage this file only in the client table payload.",
        ClientServerPlanStatus.ServerDbcChange => "Stage matching client and server table copies; back up the live file and restart worldserver when applying.",
        ClientServerPlanStatus.SqlOverlayRequiresAudit => $"Stage the client table, then audit SQL table {sqlTable}; SQL rows may override its values.",
        ClientServerPlanStatus.UnusedByServer => "This core does not load the server table; keep it client-side unless core code is changed.",
        ClientServerPlanStatus.MissingServerDbc => "The core consumes this table but no matching server file was found. Review the detected DataDir before applying.",
        ClientServerPlanStatus.UnknownConsumer => "Client patch staging is possible, but server deployment is blocked until current core source maps the table consumer.",
        ClientServerPlanStatus.IncompatibleTarget => "The client table does not match the selected target profile or detected server layout; do not stage it.",
        _ => "Resolve this entry before deployment."
    };

    private static IEnumerable<string> EnumerateTables(string root, bool recursive) =>
        Directory.EnumerateFiles(root, "*.dbc", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(root, "*.db2", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));

    private static string SelectServerCandidate(IEnumerable<string> candidates, string serverRoot) => candidates
        .OrderBy(path => Path.GetRelativePath(serverRoot, path).Count(character => character is '\\' or '/'))
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).First();

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
