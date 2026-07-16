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
    IReadOnlyList<ClientServerPlanEntry> Entries);

public sealed record ClientServerStageResult(string RootPath, string PlanPath, string? PatchManifestPath, int ClientFiles, int ServerFiles, int BlockedFiles);

public static class ClientServerDeploymentPlanner
{
    public static ClientServerDeploymentPlan Analyze(string clientDbcRoot, ServerWorkspace workspace, string? coreSourceRoot = null, CancellationToken cancellationToken = default)
    {
        clientDbcRoot = Path.GetFullPath(clientDbcRoot);
        if (!Directory.Exists(clientDbcRoot)) throw new DirectoryNotFoundException($"Extracted client DBC folder not found: {clientDbcRoot}");
        if (string.IsNullOrWhiteSpace(workspace.DbcPath) || !Directory.Exists(workspace.DbcPath))
            throw new DirectoryNotFoundException($"The detected server DBC folder is unavailable: {workspace.DbcPath}");
        coreSourceRoot = Directory.Exists(coreSourceRoot) ? Path.GetFullPath(coreSourceRoot) : null;

        var clientGroups = Directory.EnumerateFiles(clientDbcRoot, "*.dbc", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        if (clientGroups.Length == 0) throw new InvalidDataException($"No DBC files were found under {clientDbcRoot}. Select DBFilesClient or enable/extract DBC content first.");
        var serverFiles = Directory.EnumerateFiles(workspace.DbcPath, "*.dbc", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var bindings = ServerTableBindingCatalog.Resolve(workspace.CoreFamily, coreSourceRoot)
            .ToDictionary(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase);
        var entries = new List<ClientServerPlanEntry>(clientGroups.Length);

        foreach (var group in clientGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = group.Select(Path.GetFullPath).ToArray();
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
            WdbcFile client;
            try { client = WdbcFile.Load(clientPath); }
            catch (Exception ex)
            {
                entries.Add(new(group.Key!, clientPath, clientHash, null, null, serverFiles.GetValueOrDefault(group.Key!), null,
                    ClientServerPlanStatus.InvalidDbc, ServerTableConsumption.Unknown, DeploymentDestination.None, null,
                    RestartRequirement.None, "Client extraction", "Invalid WDBC", ex.Message, candidates.Length > 1 ? candidates : null));
                continue;
            }

            var status = Classify(binding, serverPath, clientHash, serverHash);
            entries.Add(new(group.Key!, clientPath, clientHash, client.RowCount, client.FieldCount, serverPath, serverHash, status,
                binding.Consumption, binding.Destinations, binding.SqlTableName, binding.Restart, binding.Profile,
                binding.SupportedRevision, Guidance(status, binding.SqlTableName), candidates.Length > 1 ? candidates : null));
        }

        return new(1, DateTimeOffset.UtcNow, clientDbcRoot, workspace.RootPath, Path.GetFullPath(workspace.DbcPath),
            workspace.CoreFamily, coreSourceRoot, entries);
    }

    public static void Save(string path, ClientServerDeploymentPlan plan)
    {
        path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public static ClientServerStageResult Stage(string rootPath, ClientServerDeploymentPlan plan, string patchName = "Extracted client DBC changes", string outputMpqName = "patch-Crucible.MPQ")
    {
        rootPath = Path.GetFullPath(rootPath); Directory.CreateDirectory(rootPath);
        var clientRoot = Path.Combine(rootPath, "client-patch", "DBFilesClient");
        var serverRoot = Path.Combine(rootPath, "server-dbc");
        Directory.CreateDirectory(clientRoot); Directory.CreateDirectory(serverRoot);
        var clientEntries = new List<PatchEntry>(); var serverFiles = 0; var blocked = 0;

        foreach (var entry in plan.Entries)
        {
            if (entry.ClientSourcePath is null || entry.Status is ClientServerPlanStatus.Identical) continue;
            if (entry.Status is ClientServerPlanStatus.ConflictingClientLayers or ClientServerPlanStatus.InvalidDbc)
            { blocked++; continue; }
            var stagedClient = Path.Combine(clientRoot, entry.DbcFileName);
            File.Copy(entry.ClientSourcePath, stagedClient, true);
            clientEntries.Add(new(stagedClient, $"DBFilesClient\\{entry.DbcFileName}"));
            var stageForServer = entry.Status is ClientServerPlanStatus.ServerDbcChange or ClientServerPlanStatus.SqlOverlayRequiresAudit ||
                entry.Status == ClientServerPlanStatus.MissingServerDbc && (entry.Consumption is ServerTableConsumption.DbcLoaded or ServerTableConsumption.SqlOverlayed);
            if (stageForServer)
            {
                File.Copy(entry.ClientSourcePath, Path.Combine(serverRoot, entry.DbcFileName), true); serverFiles++;
            }
            if (entry.Status == ClientServerPlanStatus.UnknownConsumer) blocked++;
        }

        var planPath = Path.Combine(rootPath, "client-server-plan.json"); Save(planPath, plan);
        string? manifestPath = null;
        if (clientEntries.Count > 0)
        {
            manifestPath = Path.Combine(rootPath, "client-patch", "client-dbc.crucible-patch.json");
            PatchManifestService.Save(manifestPath, patchName, outputMpqName, clientEntries,
                policy: new PatchManifestPolicy(["DBFilesClient\\*.dbc"], null, clientEntries.Count));
        }
        return new(rootPath, planPath, manifestPath, clientEntries.Count, serverFiles, blocked);
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
        ClientServerPlanStatus.Identical => "Client and server DBC bytes already match; no deployment is needed.",
        ClientServerPlanStatus.ClientOnly => "Stage this file only in the client patch.",
        ClientServerPlanStatus.ServerDbcChange => "Stage matching client and server DBC copies; back up the live file and restart worldserver when applying.",
        ClientServerPlanStatus.SqlOverlayRequiresAudit => $"Stage the DBC, then audit SQL table {sqlTable}; SQL rows may override its values.",
        ClientServerPlanStatus.UnusedByServer => "This core does not load the server DBC; keep it client-side unless core code is changed.",
        ClientServerPlanStatus.MissingServerDbc => "The core consumes this table but no matching server DBC was found. Review the detected DataDir before applying.",
        ClientServerPlanStatus.UnknownConsumer => "Client patch staging is possible, but server deployment is blocked until current core source maps the table consumer.",
        _ => "Resolve this entry before deployment."
    };

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
