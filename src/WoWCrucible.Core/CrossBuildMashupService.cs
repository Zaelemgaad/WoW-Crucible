using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public sealed record CrossBuildMashupRequest(
    int FormatVersion,
    string Name,
    string HostProfileId,
    string DonorProfileId,
    string HostTableRoot,
    string DonorTableRoot,
    string DefinitionsRoot,
    string? HostXmlSchemaPath,
    string? DonorXmlSchemaPath,
    string OutputRoot,
    string PatchFileName,
    string? InstallClientDataRoot = null,
    string? InstallServerTableRoot = null,
    int MaximumRowsPerTable = 0,
    IReadOnlyList<string>? IncludeTables = null,
    string? HostClientDataRoot = null,
    string? DonorClientRoot = null,
    string? DonorFileDataListPath = null);

public sealed record CrossBuildMashupProgress(string Phase, long Completed, long Total, string CurrentPath);

public enum CrossBuildMashupTableStatus
{
    Converted,
    NoHostTable,
    EmptyTable,
    UnsupportedSchema,
    NoSemanticFields,
    StructuralMutationBlocked,
    ProjectedIntoHost,
    NoConvertibleRows,
    Failed
}

public sealed record CrossBuildMashupTableReport(
    string CanonicalTable,
    string HostTable,
    string DonorTable,
    string? HostPath,
    string DonorPath,
    CrossBuildMashupTableStatus Status,
    int HostRows,
    int DonorRows,
    int ConsideredDonorRows,
    int MappedFields,
    IReadOnlyList<string> FieldMappings,
    IReadOnlyList<string> DefaultedHostFields,
    IReadOnlyList<string> DroppedDonorFields,
    int ReusedSameId,
    int ReusedEquivalent,
    int AddedPreservedId,
    int AddedRemappedId,
    int SkippedRows,
    int RewrittenReferences,
    int UnresolvedReferences,
    string? OutputClientTable,
    string? OutputServerTable,
    string? IdMapPath,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Errors,
    int RetainedHostSameId = 0)
{
    public int AddedRows => AddedPreservedId + AddedRemappedId;
}

public sealed record CrossBuildMashupInstallation(
    string? ClientPatchPath,
    IReadOnlyDictionary<string, string> ServerFiles,
    IReadOnlyDictionary<string, string> PreimageFiles,
    string ReceiptPath);

public sealed record CrossBuildMashupResult(
    int FormatVersion,
    string Name,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string RunRoot,
    string HostProfileId,
    string DonorProfileId,
    int SharedTables,
    int ConvertedTables,
    int AddedRows,
    int ReusedRows,
    int RemappedIds,
    int RewrittenReferences,
    int UnresolvedReferences,
    string ClientPayloadRoot,
    string ServerPayloadRoot,
    string PatchManifestPath,
    string PatchPath,
    string IdMapLedgerPath,
    string ReferenceLedgerPath,
    string ReportPath,
    string MarkdownReportPath,
    IReadOnlyList<CrossBuildMashupTableReport> Tables,
    CrossBuildMashupInstallation? Installation,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Errors)
{
    public bool Passed => Errors.Count == 0 && ConvertedTables > 0 && AddedRows > 0 && File.Exists(PatchPath);
}

/// <summary>
/// Performs an explicit, lossy-but-accounted cross-build translation. The host
/// table layout remains authoritative; donor fields are matched semantically,
/// IDs are allocated globally, and DBD-declared references are rewritten before
/// the converted host-format rows are published to client and server payloads.
/// </summary>
public static class CrossBuildMashupService
{
    private const int RequestFormatVersion = 1;
    private const int ResultFormatVersion = 1;
    private const int MaximumSamples = 24;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static CrossBuildMashupRequest LoadRequest(string path)
    {
        path = RequiredFile(path, "Cross-build mashup request");
        var request = JsonSerializer.Deserialize<CrossBuildMashupRequest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Cross-build mashup request is empty.");
        ValidateRequest(request);
        return request;
    }

    public static void SaveRequest(string path, CrossBuildMashupRequest request)
    {
        ValidateRequest(request);
        AtomicWrite(path, JsonSerializer.Serialize(request, JsonOptions));
    }

    public static CrossBuildMashupResult Run(
        CrossBuildMashupRequest request,
        IProgress<CrossBuildMashupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var started = DateTimeOffset.UtcNow;
        var hostProfile = TargetProfileCatalog.FindRequired(TargetProfileCatalog.Load(), request.HostProfileId);
        var donorProfile = TargetProfileCatalog.FindRequired(TargetProfileCatalog.Load(), request.DonorProfileId);
        if (hostProfile.ClientBuild == donorProfile.ClientBuild)
            throw new InvalidOperationException("Cross-build mashup requires different host and donor client builds.");
        if (hostProfile.ArchiveFormat != ArchiveFormat.Mpq)
            throw new InvalidOperationException($"The current mashup publisher requires an MPQ host; {hostProfile.DisplayName} uses {hostProfile.ArchiveFormat}.");

        var hostRoot = RequiredDirectory(request.HostTableRoot, "Host table root");
        var donorRoot = RequiredDirectory(request.DonorTableRoot, "Donor table root");
        var definitions = RequiredDirectory(request.DefinitionsRoot, "WoWDBDefs definitions root");
        var hostSchema = new ClientTableSchemaProvider(hostProfile.ClientBuild, request.HostXmlSchemaPath, definitions);
        var donorSchema = new ClientTableSchemaProvider(donorProfile.ClientBuild, request.DonorXmlSchemaPath, definitions);
        var outputRoot = Path.GetFullPath(request.OutputRoot);
        Directory.CreateDirectory(outputRoot);
        var slug = Slug(request.Name);
        var runRoot = Path.Combine(outputRoot, $"{slug}-{started:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        var staging = Path.Combine(outputRoot, $".{Path.GetFileName(runRoot)}.crucible-staging");
        if (Directory.Exists(staging)) throw new IOException($"Mashup staging path already exists: {staging}");
        Directory.CreateDirectory(staging);

        try
        {
            var clientPayload = Path.Combine(staging, "client-payload", "DBFilesClient");
            var serverPayload = Path.Combine(staging, "server-files");
            var ledgerRoot = Path.Combine(staging, "ledgers");
            Directory.CreateDirectory(clientPayload);
            Directory.CreateDirectory(serverPayload);
            Directory.CreateDirectory(ledgerRoot);
            var idLedger = Path.Combine(ledgerRoot, "id-map.csv");
            var referenceLedger = Path.Combine(ledgerRoot, "reference-rewrites.csv");
            File.WriteAllText(idLedger, "Table,DonorID,HostID,Action\n", Encoding.UTF8);
            File.WriteAllText(referenceLedger, "Table,DonorRowID,Column,ReferencedTable,DonorReferenceID,HostReferenceID,Action,Count\n", Encoding.UTF8);

            var hostTables = DiscoverTables(hostRoot, preferDb2: true, cancellationToken);
            var donorTables = DiscoverTables(donorRoot, preferDb2: true, cancellationToken);
            var include = NormalizeIncludes(request.IncludeTables);
            var donorSelection = donorTables.Values
                .Where(table => include.Count == 0 || include.Contains(table.CanonicalName))
                .OrderBy(table => table.CanonicalName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            CrossBuildAssetBridge? assetBridge = null;
            if (HasAssetBridgeConfiguration(request))
            {
                progress?.Report(new("Resolve cross-build assets", 0, 1, request.DonorFileDataListPath!));
                var assetRequests = CollectAssetRequests(hostProfile.Id, donorProfile.Id, donorSelection, donorSchema,
                    request.MaximumRowsPerTable, cancellationToken);
                assetBridge = CrossBuildAssetBridge.Create(request.HostClientDataRoot!, request.DonorClientRoot!,
                    request.DonorFileDataListPath!, assetRequests, Path.Combine(staging, "asset-bridge"), cancellationToken);
                progress?.Report(new("Resolve cross-build assets", 1, 1, request.DonorClientRoot!));
            }
            CrossBuildItemVisualProjection? itemVisualProjection = null;
            if (assetBridge is not null && CrossBuildItemVisualProjection.Supports(hostProfile.Id, donorProfile.Id) &&
                hostTables.TryGetValue("ITEMVISUALEFFECTS", out var hostItemVisualEffects) &&
                donorTables.TryGetValue("ITEMVISUALS", out var donorItemVisuals))
                itemVisualProjection = CrossBuildItemVisualProjection.Create(hostProfile.Id, donorProfile.Id,
                    hostItemVisualEffects.Path, donorItemVisuals.Path, hostSchema, assetBridge);
            var blueprints = new List<TableBlueprint>();
            var reports = new List<CrossBuildMashupTableReport>();
            var maps = new Dictionary<string, Dictionary<uint, uint>>(StringComparer.OrdinalIgnoreCase);
            var hostIds = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < donorSelection.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var donor = donorSelection[index];
                progress?.Report(new("Plan semantic conversion", index, donorSelection.Length, donor.Path));
                if (!hostTables.TryGetValue(donor.CanonicalName, out var host))
                {
                    reports.Add(NoHostReport(donor));
                    continue;
                }

                try
                {
                    var blueprint = PlanTable(host, donor, hostProfile.Id, donorProfile.Id, donorTables, hostSchema, donorSchema, definitions,
                        request.MaximumRowsPerTable, idLedger, assetBridge, itemVisualProjection, cancellationToken);
                    reports.Add(blueprint.Report);
                    if (blueprint.Ready)
                    {
                        blueprints.Add(blueprint);
                        if (blueprint.ReferenceAddressable)
                        {
                            maps[blueprint.CanonicalTable] = blueprint.IdMap;
                            hostIds[blueprint.CanonicalTable] = blueprint.OriginalHostIds;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    reports.Add(FailedReport(host, donor, exception.Message));
                }
            }
            progress?.Report(new("Plan semantic conversion", donorSelection.Length, donorSelection.Length, hostRoot));

            var projectedSources = blueprints.Where(blueprint => blueprint.Supplemental is not null)
                .Select(blueprint => blueprint.Supplemental!).GroupBy(projection => projection.SourceCanonicalTable, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            reports = reports.Select(report => report.Status == CrossBuildMashupTableStatus.NoHostTable && projectedSources.TryGetValue(report.CanonicalTable, out var projection)
                ? report with
                {
                    Status = CrossBuildMashupTableStatus.ProjectedIntoHost,
                    DonorRows = projection.SourceRows,
                    Findings = [$"Projected into {projection.TargetTable} because the host stores these fields inline rather than in {projection.SourceTable}." ]
                }
                : report).ToList();

            var convertedReports = new Dictionary<string, CrossBuildMashupTableReport>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < blueprints.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var blueprint = blueprints[index];
                progress?.Report(new("Write host-format tables", index, blueprints.Count, blueprint.Host.Path));
                try
                {
                    convertedReports[blueprint.CanonicalTable] = ConvertTable(blueprint, hostSchema, donorSchema,
                        maps, hostIds, hostRoot, clientPayload, serverPayload, referenceLedger, assetBridge, itemVisualProjection, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    convertedReports[blueprint.CanonicalTable] = blueprint.Report with
                    {
                        Status = CrossBuildMashupTableStatus.Failed,
                        Errors = blueprint.Report.Errors.Append(exception.Message).ToArray()
                    };
                }
            }
            progress?.Report(new("Write host-format tables", blueprints.Count, blueprints.Count, clientPayload));
            reports = reports.Select(report => convertedReports.GetValueOrDefault(report.CanonicalTable, report)).ToList();
            if (itemVisualProjection?.Publish(hostSchema, hostRoot, clientPayload, serverPayload, idLedger, cancellationToken) is { } itemVisualEffectsReport)
                reports.Add(itemVisualEffectsReport);

            var successful = reports.Where(report => report.Status == CrossBuildMashupTableStatus.Converted && report.AddedRows > 0).ToArray();
            if (successful.Length == 0) throw new InvalidOperationException("The mashup produced no converted host table with additive donor rows.");
            var entries = successful.Select(report => new PatchEntry(
                report.OutputClientTable ?? throw new InvalidDataException($"{report.HostTable} has no client payload path."),
                $"DBFilesClient\\{Path.GetFileName(report.OutputClientTable)}"))
                .Concat(assetBridge?.PatchEntries ?? [])
                .OrderBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var patchDirectory = Path.Combine(staging, "patch");
            Directory.CreateDirectory(patchDirectory);
            var manifestPath = Path.Combine(patchDirectory, "mists-of-the-pandaren-legion.patch-manifest.json");
            PatchManifestService.Save(manifestPath, request.Name, request.PatchFileName, entries,
                policy: new PatchManifestPolicy(["DBFilesClient\\*.dbc", "DBFilesClient\\*.db2", "Crucible\\CrossBuild\\**", "**\\*.m2", "**\\*.skin", "**\\*.blp"], ExpectedEntryCount: entries.Length));
            var patchPath = Path.Combine(patchDirectory, request.PatchFileName);
            BuildPatchWithShortNativePaths(patchPath, entries);
            var manifestValidation = PatchManifestService.Validate(PatchManifestService.Load(manifestPath), patchPath);
            if (!manifestValidation.Passed)
                throw new InvalidDataException("Mashup MPQ validation failed: " + string.Join("; ", manifestValidation.Errors.Select(error => error.Message)));
            VerifyArchivePayload(patchPath, entries, cancellationToken);

            var reportPath = Path.Combine(staging, "mashup-report.json");
            var markdownPath = Path.Combine(staging, "mashup-report.md");
            var errors = reports.SelectMany(report => report.Errors.Select(error => $"{report.HostTable}: {error}"))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var findings = BuildFindings(request, hostProfile, donorProfile, reports, assetBridge);
            var provisional = new CrossBuildMashupResult(ResultFormatVersion, request.Name, started, DateTimeOffset.UtcNow,
                runRoot, hostProfile.Id, donorProfile.Id, reports.Count(report => report.HostPath is not null), successful.Length,
                successful.Sum(report => report.AddedRows), successful.Sum(report => report.ReusedSameId + report.ReusedEquivalent),
                successful.Sum(report => report.AddedRemappedId), successful.Sum(report => report.RewrittenReferences),
                successful.Sum(report => report.UnresolvedReferences),
                Path.Combine(runRoot, "client-payload"), Path.Combine(runRoot, "server-files"),
                Path.Combine(runRoot, "patch", Path.GetFileName(manifestPath)), Path.Combine(runRoot, "patch", request.PatchFileName),
                Path.Combine(runRoot, "ledgers", Path.GetFileName(idLedger)), Path.Combine(runRoot, "ledgers", Path.GetFileName(referenceLedger)),
                Path.Combine(runRoot, Path.GetFileName(reportPath)), Path.Combine(runRoot, Path.GetFileName(markdownPath)),
                RebaseReports(reports, staging, runRoot), null, findings, errors);
            AtomicWrite(reportPath, JsonSerializer.Serialize(provisional, JsonOptions));
            AtomicWrite(markdownPath, RenderMarkdown(provisional));
            Directory.Move(staging, runRoot);

            var result = provisional with { CompletedUtc = DateTimeOffset.UtcNow };
            AtomicWrite(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
            AtomicWrite(result.MarkdownReportPath, RenderMarkdown(result));
            if (!string.IsNullOrWhiteSpace(request.InstallClientDataRoot) || !string.IsNullOrWhiteSpace(request.InstallServerTableRoot))
            {
                var installation = Install(result, request, cancellationToken);
                result = result with { Installation = installation, CompletedUtc = DateTimeOffset.UtcNow };
                AtomicWrite(result.ReportPath, JsonSerializer.Serialize(result, JsonOptions));
                AtomicWrite(result.MarkdownReportPath, RenderMarkdown(result));
            }
            progress?.Report(new("Complete", 1, 1, result.RunRoot));
            return result;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    private static TableBlueprint PlanTable(
        TableSource host,
        TableSource donor,
        string hostProfileId,
        string donorProfileId,
        IReadOnlyDictionary<string, TableSource> donorTables,
        ClientTableSchemaProvider hostSchema,
        ClientTableSchemaProvider donorSchema,
        string definitionsRoot,
        int maximumRows,
        string idLedgerPath,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(host.Path).Length == 0 || new FileInfo(donor.Path).Length == 0)
            return InactiveBlueprint(host, donor, CrossBuildMashupTableStatus.EmptyTable, "A host or donor table is an empty placeholder.");

        var hostFile = WdbcFile.Load(host.Path);
        var donorFile = WdbcFile.Load(donor.Path);
        var hostResolved = hostSchema.ResolveWithSource(hostFile);
        var donorResolved = donorSchema.ResolveWithSource(donorFile);
        var hostIdentity = CrossBuildTableIdentityCatalog.Resolve(hostProfileId, host.CanonicalName, hostResolved.Resolution.Columns, hostResolved.Resolution.KeyStrategy);
        var donorIdentity = CrossBuildTableIdentityCatalog.Resolve(donorProfileId, donor.CanonicalName, donorResolved.Resolution.Columns, donorResolved.Resolution.KeyStrategy);
        var hostKey = DbcRecordIdentity.PhysicalColumn(hostResolved.Resolution.Columns, hostIdentity.Strategy);
        var donorKey = DbcRecordIdentity.PhysicalColumn(donorResolved.Resolution.Columns, donorIdentity.Strategy);
        if (hostIdentity.Strategy.Kind == DbcRecordKeyKind.NoStableKey || donorIdentity.Strategy.Kind == DbcRecordKeyKind.NoStableKey)
            return InactiveBlueprint(host, donor, CrossBuildMashupTableStatus.UnsupportedSchema,
                $"A proven cross-build identity is required; host={hostIdentity.Strategy.Kind}, donor={donorIdentity.Strategy.Kind}.");
        if (!hostFile.AllowsStructuralMutation)
            return InactiveBlueprint(host, donor, CrossBuildMashupTableStatus.StructuralMutationBlocked,
                $"{hostFile.ContainerKind} side tables currently block structural row insertion.", hostFile.RowCount, donorFile.RowCount);

        var hostDefinition = LoadDefinition(definitionsRoot, host.TableName);
        var donorDefinition = LoadDefinition(definitionsRoot, donor.TableName);
        var mappings = MatchFields(hostProfileId, donorProfileId, host.CanonicalName, hostResolved.Resolution.Columns, donorResolved.Resolution.Columns,
            hostKey, donorKey, hostDefinition, donorDefinition, assetBridge, itemVisualProjection);
        var supplemental = BuildSupplementalProjection(hostProfileId, donorProfileId, host, hostResolved.Resolution.Columns, donorTables, donorSchema);
        var identityOnly = mappings.Count == 0 && supplemental is null &&
            hostResolved.Resolution.Columns.All(column => hostKey is not null && column.Index == hostKey.Index);
        if (mappings.Count == 0 && supplemental is null && !identityOnly)
            return InactiveBlueprint(host, donor, CrossBuildMashupTableStatus.NoSemanticFields,
                "The layouts share no type-compatible named field beyond their IDs.", hostFile.RowCount, donorFile.RowCount);

        var mappedHost = mappings.Select(mapping => mapping.Host.Name).Concat(supplemental?.Fields.Select(field => field.Host.Name) ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedDonor = mappings.Select(mapping => mapping.Donor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaulted = hostResolved.Resolution.Columns.Where(column => (hostKey is null || column.Index != hostKey.Index) && !mappedHost.Contains(column.Name)).Select(column => column.Name).ToArray();
        var dropped = donorResolved.Resolution.Columns.Where(column => (donorKey is null || column.Index != donorKey.Index) && !mappedDonor.Contains(column.Name)).Select(column => column.Name).ToArray();
        var hostRows = DbcRecordIdentity.IndexRows(hostFile, hostResolved.Resolution.Columns, hostIdentity.Strategy);
        var donorRows = DbcRecordIdentity.IndexRows(donorFile, donorResolved.Resolution.Columns, donorIdentity.Strategy)
            .OrderBy(pair => pair.Key).ToArray();
        if (maximumRows > 0) donorRows = donorRows.Take(maximumRows).ToArray();
        var originalHostIds = hostRows.Keys.ToHashSet();
        var occupied = originalHostIds.ToHashSet();
        var donorIds = donorRows.Select(pair => pair.Key).ToHashSet();
        var hostUsesVirtualRows = hostIdentity.Strategy.Kind == DbcRecordKeyKind.VirtualRowIndex;
        var keyMaximum = hostUsesVirtualRows ? uint.MaxValue : MaximumPositiveId(hostKey ?? throw new InvalidDataException($"{host.TableName} physical identity has no column."));
        var maximumObserved = occupied.Concat(donorIds.Where(id => id <= keyMaximum)).DefaultIfEmpty().Max();
        var nextId = hostUsesVirtualRows
            ? checked((uint)hostFile.RowCount + hostIdentity.Strategy.VirtualStart)
            : maximumObserved < keyMaximum ? maximumObserved + 1 : 1;
        var catalog = new Dictionary<ulong, List<SemanticCandidate>>();
        if (!identityOnly)
            foreach (var pair in hostRows)
                AddCandidate(catalog, SemanticHash(hostFile, pair.Value, pair.Key, mappings, supplemental, assetBridge, itemVisualProjection, donorSide: false), new(false, pair.Value, pair.Key, 0));

        var idMap = new Dictionary<uint, uint>();
        var reusedSame = 0;
        var reusedEquivalent = 0;
        var preserved = 0;
        var remapped = 0;
        var retainedHost = 0;
        var skipped = 0;
        var findings = new List<string>();
        if (hostIdentity.Finding is not null) findings.Add(hostIdentity.Finding);
        if (donorIdentity.Finding is not null && !donorIdentity.Finding.Equals(hostIdentity.Finding, StringComparison.Ordinal)) findings.Add(donorIdentity.Finding);
        using var ledger = new StreamWriter(idLedgerPath, append: true, new UTF8Encoding(false));
        foreach (var pair in donorRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryValidateRow(donorFile, pair.Value, pair.Key, mappings, supplemental, assetBridge, itemVisualProjection, out var conversionError))
            {
                skipped++;
                if (findings.Count < MaximumSamples) findings.Add($"Skipped donor ID {pair.Key:N0}: {conversionError}");
                continue;
            }

            uint targetId;
            string action;
            if (!hostUsesVirtualRows && hostRows.TryGetValue(pair.Key, out var sameIdRow) &&
                (identityOnly || RowsEquivalent(hostFile, sameIdRow, donorFile, pair.Value, pair.Key, mappings, supplemental, assetBridge, itemVisualProjection)))
            {
                targetId = pair.Key;
                action = "reuse-same-id";
                reusedSame++;
            }
            else if (!hostUsesVirtualRows && hostIdentity.RetainHostOnSameKeyConflict && hostRows.ContainsKey(pair.Key))
            {
                targetId = pair.Key;
                action = "retain-host-same-id";
                retainedHost++;
                if (findings.Count < MaximumSamples)
                    findings.Add($"Retained host ID {pair.Key:N0} because this table uses fixed gameplay identities and the donor row differs.");
            }
            else
            {
                var hash = identityOnly ? 0 : SemanticHash(donorFile, pair.Value, pair.Key, mappings, supplemental, assetBridge, itemVisualProjection, donorSide: true);
                var equivalent = identityOnly ? null : FindEquivalent(catalog, hash, hostFile, donorFile, pair.Value, pair.Key, mappings, supplemental, assetBridge, itemVisualProjection);
                if (equivalent is not null)
                {
                    targetId = equivalent.HostId;
                    action = "reuse-equivalent";
                    reusedEquivalent++;
                }
                else if (hostUsesVirtualRows)
                {
                    targetId = AllocateVirtualRow(occupied, ref nextId);
                    action = "add-row-index";
                    remapped++;
                    AddCandidate(catalog, hash, new(true, pair.Value, targetId, pair.Key));
                }
                else if (pair.Key <= keyMaximum && !occupied.Contains(pair.Key))
                {
                    targetId = pair.Key;
                    action = "add-preserve-id";
                    occupied.Add(targetId);
                    preserved++;
                    AddCandidate(catalog, hash, new(true, pair.Value, targetId, pair.Key));
                }
                else
                {
                    targetId = AllocateId(occupied, donorIds, keyMaximum, ref nextId);
                    action = "add-remap-id";
                    remapped++;
                    AddCandidate(catalog, hash, new(true, pair.Value, targetId, pair.Key));
                }
            }
            idMap[pair.Key] = targetId;
            ledger.WriteLine($"{Csv(host.CanonicalName)},{pair.Key.ToString(CultureInfo.InvariantCulture)},{targetId.ToString(CultureInfo.InvariantCulture)},{action}");
        }

        var mappingDescriptions = mappings.Select(mapping =>
            $"{mapping.Donor.Name} -> {mapping.Host.Name} ({mapping.MatchKind}{(mapping.ReferenceTable is null ? string.Empty : $", ref {mapping.ReferenceTable}")})")
            .Concat(supplemental?.Fields.Select(field => $"{supplemental.SourceTable}.{field.SourceField} -> {field.Host.Name} (supplemental-table{(field.ReferenceTable is null ? string.Empty : $", ref {field.ReferenceTable}")})") ?? []).ToArray();
        var status = preserved + remapped > 0 ? CrossBuildMashupTableStatus.Converted : CrossBuildMashupTableStatus.NoConvertibleRows;
        if (identityOnly)
            findings.Add("The host table is an ID-only domain. Donor IDs are preserved or remapped without pretending donor-only payload fields exist in the host format.");
        var report = new CrossBuildMashupTableReport(host.CanonicalName, host.TableName, donor.TableName, host.Path, donor.Path,
            status, hostFile.RowCount, donorFile.RowCount, donorRows.Length, mappings.Count + (supplemental?.Fields.Count ?? 0), mappingDescriptions,
            defaulted, dropped, reusedSame, reusedEquivalent, preserved, remapped, skipped, 0, 0,
            null, null, idLedgerPath, findings.Concat(supplemental?.Findings ?? []).ToArray(), [], retainedHost);
        return new(host.CanonicalName, host, donor, mappings, supplemental, hostKey, donorKey, hostIdentity.Strategy, donorIdentity.Strategy,
            hostIdentity.ReferenceAddressable, idMap, originalHostIds, report, true);
    }

    private static CrossBuildMashupTableReport ConvertTable(
        TableBlueprint blueprint,
        ClientTableSchemaProvider hostSchema,
        ClientTableSchemaProvider donorSchema,
        IReadOnlyDictionary<string, Dictionary<uint, uint>> maps,
        IReadOnlyDictionary<string, HashSet<uint>> hostIds,
        string hostRoot,
        string clientPayloadRoot,
        string serverPayloadRoot,
        string referenceLedgerPath,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection,
        CancellationToken cancellationToken)
    {
        if (blueprint.Report.AddedRows == 0) return blueprint.Report;
        var hostFile = WdbcFile.Load(blueprint.Host.Path);
        var donorFile = WdbcFile.Load(blueprint.Donor.Path);
        var hostResolution = hostSchema.Resolve(hostFile);
        var donorResolution = donorSchema.Resolve(donorFile);
        var donorRows = DbcRecordIdentity.IndexRows(donorFile, donorResolution.Columns, blueprint.DonorKeyStrategy);
        var emitted = blueprint.OriginalHostIds.ToHashSet();
        var representatives = new Dictionary<uint, (uint DonorId, int Row)>();
        var events = new Dictionary<ReferenceEventKey, ReferenceEventValue>();
        var rewritten = 0;
        var unresolved = 0;

        foreach (var pair in blueprint.IdMap.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (emitted.Contains(pair.Value)) continue;
            if (!donorRows.TryGetValue(pair.Key, out var donorRow))
                throw new InvalidDataException($"{blueprint.Donor.TableName} donor ID {pair.Key:N0} disappeared after planning.");
            var targetRow = hostFile.AddBlankRow();
            foreach (var mapping in blueprint.Mappings)
            {
                if (!TryConvertMappingCell(donorFile, donorRow, mapping, assetBridge, itemVisualProjection, false, out var cell, out var error))
                    throw new InvalidDataException($"{blueprint.Donor.TableName} donor ID {pair.Key:N0}, field {mapping.Donor.Name}: {error}");
                if (mapping.ReferenceTable is not null && TryReferenceId(cell, mapping.Donor, out var donorReferenceId) && !IsReferenceSentinel(donorReferenceId, mapping.Donor))
                {
                    var hostReferenceId = donorReferenceId;
                    var action = "retain-existing";
                    if (maps.TryGetValue(mapping.ReferenceTable, out var referenceMap) && referenceMap.TryGetValue(donorReferenceId, out var mappedReference))
                    {
                        hostReferenceId = mappedReference;
                        action = mappedReference == donorReferenceId ? "mapped-same" : "rewritten";
                        if (mappedReference != donorReferenceId) rewritten++;
                    }
                    else if (!hostIds.TryGetValue(mapping.ReferenceTable, out var existingIds) || !existingIds.Contains(donorReferenceId))
                    {
                        action = "unresolved-retained";
                        unresolved++;
                    }
                    if (hostReferenceId != donorReferenceId) cell = CellFromReference(hostReferenceId, mapping.Host);
                    AddReferenceEvent(events, new(blueprint.CanonicalTable, mapping.Host.Name, mapping.ReferenceTable,
                        donorReferenceId, hostReferenceId, action), pair.Key);
                }
                SetCell(hostFile, targetRow, mapping.Host, cell);
            }
            if (blueprint.Supplemental is not null)
            {
                var supplementalCells = blueprint.Supplemental.CellsFor(pair.Key);
                for (var fieldIndex = 0; fieldIndex < blueprint.Supplemental.Fields.Count; fieldIndex++)
                {
                    var field = blueprint.Supplemental.Fields[fieldIndex];
                    var cell = supplementalCells[fieldIndex];
                    if (field.ReferenceTable is not null && TryReferenceId(cell, field.Source, out var donorReferenceId) && !IsReferenceSentinel(donorReferenceId, field.Source))
                    {
                        var hostReferenceId = donorReferenceId;
                        var action = "retain-existing";
                        if (maps.TryGetValue(field.ReferenceTable, out var referenceMap) && referenceMap.TryGetValue(donorReferenceId, out var mappedReference))
                        {
                            hostReferenceId = mappedReference;
                            action = mappedReference == donorReferenceId ? "mapped-same" : "rewritten";
                            if (mappedReference != donorReferenceId) rewritten++;
                        }
                        else if (!hostIds.TryGetValue(field.ReferenceTable, out var existingIds) || !existingIds.Contains(donorReferenceId))
                        {
                            action = "unresolved-retained";
                            unresolved++;
                        }
                        if (hostReferenceId != donorReferenceId) cell = CellFromReference(hostReferenceId, field.Host);
                        AddReferenceEvent(events, new(blueprint.CanonicalTable, field.Host.Name, field.ReferenceTable,
                            donorReferenceId, hostReferenceId, action), pair.Key);
                    }
                    SetCell(hostFile, targetRow, field.Host, cell);
                }
            }
            if (blueprint.HostKey is not null) hostFile.SetRaw64(targetRow, blueprint.HostKey, pair.Value);
            else
            {
                var actualVirtualKey = DbcRecordIdentity.GetKey(hostFile, targetRow, hostResolution.Columns, blueprint.HostKeyStrategy);
                if (actualVirtualKey != pair.Value)
                    throw new InvalidDataException($"{blueprint.Host.TableName} appended row has virtual key {actualVirtualKey:N0}; planned {pair.Value:N0}.");
            }
            emitted.Add(pair.Value);
            representatives[pair.Value] = (pair.Key, donorRow);
        }

        var outputClient = Path.Combine(clientPayloadRoot, Path.GetFileName(blueprint.Host.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(outputClient)!);
        hostFile.Save(outputClient, createBackup: false);
        var relativeServer = Path.GetRelativePath(hostRoot, blueprint.Host.Path);
        if (relativeServer.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Host table escaped its declared table root: {blueprint.Host.Path}");
        var outputServer = Path.Combine(serverPayloadRoot, relativeServer);
        Directory.CreateDirectory(Path.GetDirectoryName(outputServer)!);
        File.Copy(outputClient, outputServer, overwrite: true);
        VerifyConvertedTable(outputClient, blueprint, representatives, maps, hostSchema, donorSchema, assetBridge, itemVisualProjection);
        MarkConvertedDependencies(blueprint, donorFile, representatives, assetBridge, itemVisualProjection);
        AppendReferenceEvents(referenceLedgerPath, events);
        return blueprint.Report with
        {
            Status = CrossBuildMashupTableStatus.Converted,
            RewrittenReferences = rewritten,
            UnresolvedReferences = unresolved,
            OutputClientTable = outputClient,
            OutputServerTable = outputServer
        };
    }

    private static void VerifyConvertedTable(
        string outputPath,
        TableBlueprint blueprint,
        IReadOnlyDictionary<uint, (uint DonorId, int Row)> representatives,
        IReadOnlyDictionary<string, Dictionary<uint, uint>> maps,
        ClientTableSchemaProvider hostSchema,
        ClientTableSchemaProvider donorSchema,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection)
    {
        var output = WdbcFile.Load(outputPath);
        var outputResolution = hostSchema.Resolve(output);
        var rows = DbcRecordIdentity.IndexRows(output, outputResolution.Columns, blueprint.HostKeyStrategy);
        if (output.RowCount != blueprint.Report.HostRows + blueprint.Report.AddedRows)
            throw new InvalidDataException($"{blueprint.Host.TableName} output has {output.RowCount:N0} rows; expected {blueprint.Report.HostRows + blueprint.Report.AddedRows:N0}.");
        var donor = WdbcFile.Load(blueprint.Donor.Path);
        _ = donorSchema.Resolve(donor);
        foreach (var pair in representatives)
        {
            if (!rows.TryGetValue(pair.Key, out var outputRow)) throw new InvalidDataException($"Converted host ID {pair.Key:N0} is missing from {blueprint.Host.TableName}.");
            foreach (var mapping in blueprint.Mappings)
            {
                if (!TryConvertMappingCell(donor, pair.Value.Row, mapping, assetBridge, itemVisualProjection, false, out var expected, out var error))
                    throw new InvalidDataException($"Verification conversion failed for {blueprint.Donor.TableName} ID {pair.Value.DonorId:N0}: {error}");
                if (mapping.ReferenceTable is not null && TryReferenceId(expected, mapping.Donor, out var reference) && !IsReferenceSentinel(reference, mapping.Donor) &&
                    maps.TryGetValue(mapping.ReferenceTable, out var referenceMap) && referenceMap.TryGetValue(reference, out var rewritten))
                    expected = CellFromReference(rewritten, mapping.Host);
                var actual = CellFromTarget(output, outputRow, mapping.Host);
                if (!actual.Equals(expected)) throw new InvalidDataException($"Converted value mismatch in {blueprint.Host.TableName} ID {pair.Key:N0}, field {mapping.Host.Name}.");
            }
            if (blueprint.Supplemental is not null)
            {
                var supplementalCells = blueprint.Supplemental.CellsFor(pair.Value.DonorId);
                for (var fieldIndex = 0; fieldIndex < blueprint.Supplemental.Fields.Count; fieldIndex++)
                {
                    var field = blueprint.Supplemental.Fields[fieldIndex];
                    var expected = supplementalCells[fieldIndex];
                    if (field.ReferenceTable is not null && TryReferenceId(expected, field.Source, out var reference) && !IsReferenceSentinel(reference, field.Source) &&
                        maps.TryGetValue(field.ReferenceTable, out var referenceMap) && referenceMap.TryGetValue(reference, out var rewritten))
                        expected = CellFromReference(rewritten, field.Host);
                    var actual = CellFromTarget(output, outputRow, field.Host);
                    if (!actual.Equals(expected)) throw new InvalidDataException($"Converted supplemental value mismatch in {blueprint.Host.TableName} ID {pair.Key:N0}, field {field.Host.Name}.");
                }
            }
        }
        var stablePath = outputPath + ".stable-test";
        try
        {
            output.Save(stablePath, createBackup: false);
            if (!Hash(outputPath).Equals(Hash(stablePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{blueprint.Host.TableName} did not produce a stable second serialization.");
        }
        finally { if (File.Exists(stablePath)) File.Delete(stablePath); }
    }

    private static void MarkConvertedDependencies(
        TableBlueprint blueprint,
        WdbcFile donor,
        IReadOnlyDictionary<uint, (uint DonorId, int Row)> representatives,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection)
    {
        if (blueprint.Mappings.All(mapping => mapping.AssetRule is null && mapping.ItemVisualRule is null)) return;
        foreach (var representative in representatives.Values)
            foreach (var mapping in blueprint.Mappings.Where(mapping => mapping.AssetRule is not null || mapping.ItemVisualRule is not null))
                if (!TryConvertMappingCell(donor, representative.Row, mapping, assetBridge, itemVisualProjection, true, out _, out var error))
                    throw new InvalidDataException($"Could not retain converted asset for {blueprint.Donor.TableName} donor ID {representative.DonorId:N0}: {error}");
    }

    private static SupplementalProjection? BuildSupplementalProjection(
        string hostProfileId,
        string donorProfileId,
        TableSource host,
        IReadOnlyList<DbcColumn> hostColumns,
        IReadOnlyDictionary<string, TableSource> donorTables,
        ClientTableSchemaProvider donorSchema)
    {
        if (!hostProfileId.Equals("mop-18414", StringComparison.OrdinalIgnoreCase) ||
            !donorProfileId.Equals("legion-26972", StringComparison.OrdinalIgnoreCase)) return null;

        return host.CanonicalName.ToUpperInvariant() switch
        {
            "ITEMSPARSE" => BuildItemEffectProjection(host, hostColumns, donorTables, donorSchema),
            "ITEMSET" => BuildItemSetSpellProjection(host, hostColumns, donorTables, donorSchema),
            _ => null
        };
    }

    private static SupplementalProjection BuildItemEffectProjection(
        TableSource host,
        IReadOnlyList<DbcColumn> hostColumns,
        IReadOnlyDictionary<string, TableSource> donorTables,
        ClientTableSchemaProvider donorSchema)
    {
        if (!donorTables.TryGetValue("ITEMEFFECT", out var source))
            throw new InvalidDataException("Legion ItemSparse conversion requires ItemEffect.db2 so embedded MoP item spells are not silently discarded.");

        var effectFile = WdbcFile.Load(source.Path);
        var resolution = donorSchema.Resolve(effectFile);
        var sourceColumns = resolution.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var itemId = RequiredColumn(sourceColumns, "ParentItemID", source.TableName);
        var slot = RequiredColumn(sourceColumns, "LegacySlotIndex", source.TableName);
        var specialization = RequiredColumn(sourceColumns, "ChrSpecializationID", source.TableName);
        var descriptors = new[]
        {
            new SupplementalDescriptor("SpellID", "SpellID", "SPELL"),
            new SupplementalDescriptor("TriggerType", "SpellTrigger", null),
            new SupplementalDescriptor("Charges", "SpellCharges", null),
            new SupplementalDescriptor("CoolDownMSec", "SpellCooldown", null),
            new SupplementalDescriptor("SpellCategoryID", "SpellCategory", "SPELLCATEGORY"),
            new SupplementalDescriptor("CategoryCoolDownMSec", "SpellCategoryCooldown", null)
        };
        var fields = new List<SupplementalField>(descriptors.Length * 5);
        for (var legacySlot = 0; legacySlot < 5; legacySlot++)
            foreach (var descriptor in descriptors)
                fields.Add(new(RequiredColumn(hostColumns, $"{descriptor.HostBaseName}[{legacySlot}]", host.TableName),
                    RequiredColumn(sourceColumns, descriptor.SourceName, source.TableName), descriptor.SourceName, descriptor.ReferenceTable));

        var empty = fields.Select(field => ZeroCell(field.Host)).ToArray();
        var cells = new Dictionary<uint, CanonicalCell[]>();
        var occupiedSlots = new Dictionary<uint, HashSet<int>>();
        var invalid = new Dictionary<uint, string>();
        for (var row = 0; row < effectFile.RowCount; row++)
        {
            if (!TryPositiveUInt(effectFile, row, itemId, out var donorItemId)) continue;
            if (ReadUnsigned(effectFile, row, specialization) != 0)
            {
                invalid.TryAdd(donorItemId, "ItemEffect contains a specialization-specific effect that MoP Item-sparse cannot represent.");
                continue;
            }
            var legacySlot = ReadUnsigned(effectFile, row, slot);
            if (legacySlot >= 5)
            {
                invalid.TryAdd(donorItemId, $"ItemEffect legacy slot {legacySlot:N0} exceeds MoP's five embedded item-spell slots.");
                continue;
            }
            var itemSlots = occupiedSlots.GetValueOrDefault(donorItemId);
            if (itemSlots is null) occupiedSlots[donorItemId] = itemSlots = [];
            if (!itemSlots.Add((int)legacySlot))
            {
                invalid.TryAdd(donorItemId, $"ItemEffect contains more than one effect for legacy slot {legacySlot:N0}.");
                continue;
            }
            if (!cells.TryGetValue(donorItemId, out var itemCells)) cells[donorItemId] = itemCells = empty.ToArray();
            for (var descriptorIndex = 0; descriptorIndex < descriptors.Length; descriptorIndex++)
            {
                var fieldIndex = checked((int)legacySlot * descriptors.Length + descriptorIndex);
                var field = fields[fieldIndex];
                if (!TryConvertCell(effectFile, row, field.Source, field.Host, out var value, out var error))
                {
                    invalid.TryAdd(donorItemId, $"ItemEffect.{field.SourceField} cannot enter {field.Host.Name}: {error}");
                    break;
                }
                itemCells[fieldIndex] = value;
            }
        }

        return new(source.CanonicalName, source.TableName, host.TableName, effectFile.RowCount, fields, cells, empty, invalid,
        [
            $"Projected {cells.Count:N0} Legion item-effect group(s) from {source.TableName} into MoP's five embedded spell slots.",
            invalid.Count == 0
                ? "Every projected ItemEffect group fits the MoP embedded-slot model."
                : $"{invalid.Count:N0} item-effect group(s) cannot be represented faithfully and their owning donor item rows are skipped with explicit reasons."
        ]);
    }

    private static SupplementalProjection BuildItemSetSpellProjection(
        TableSource host,
        IReadOnlyList<DbcColumn> hostColumns,
        IReadOnlyDictionary<string, TableSource> donorTables,
        ClientTableSchemaProvider donorSchema)
    {
        if (!donorTables.TryGetValue("ITEMSETSPELL", out var source))
            throw new InvalidDataException("Legion ItemSet conversion requires ItemSetSpell.db2 so embedded MoP set bonuses are not silently discarded.");

        var spellFile = WdbcFile.Load(source.Path);
        var resolution = donorSchema.Resolve(spellFile);
        var sourceColumns = resolution.Columns.ToDictionary(column => column.Name, StringComparer.OrdinalIgnoreCase);
        var itemSetId = RequiredColumn(sourceColumns, "ItemSetID", source.TableName);
        var spellId = RequiredColumn(sourceColumns, "SpellID", source.TableName);
        var threshold = RequiredColumn(sourceColumns, "Threshold", source.TableName);
        var specialization = RequiredColumn(sourceColumns, "ChrSpecID", source.TableName);
        var fields = new List<SupplementalField>(16);
        for (var slot = 0; slot < 8; slot++)
        {
            fields.Add(new(RequiredColumn(hostColumns, $"SetSpellID[{slot}]", host.TableName), spellId, "SpellID", "SPELL"));
            fields.Add(new(RequiredColumn(hostColumns, $"SetThreshold[{slot}]", host.TableName), threshold, "Threshold", null));
        }

        var empty = fields.Select(field => ZeroCell(field.Host)).ToArray();
        var groupedRows = new Dictionary<uint, List<int>>();
        for (var row = 0; row < spellFile.RowCount; row++)
        {
            if (!TryPositiveUInt(spellFile, row, itemSetId, out var donorItemSetId)) continue;
            if (!groupedRows.TryGetValue(donorItemSetId, out var rows)) groupedRows[donorItemSetId] = rows = [];
            rows.Add(row);
        }

        var cells = new Dictionary<uint, CanonicalCell[]>();
        var invalid = new Dictionary<uint, string>();
        foreach (var group in groupedRows.OrderBy(pair => pair.Key))
        {
            if (group.Value.Any(row => ReadUnsigned(spellFile, row, specialization) != 0))
            {
                invalid[group.Key] = "ItemSetSpell contains specialization-specific bonuses that MoP's embedded ItemSet layout cannot represent.";
                continue;
            }
            if (group.Value.Count > 8)
            {
                invalid[group.Key] = $"ItemSetSpell contains {group.Value.Count:N0} bonuses; MoP ItemSet has eight embedded bonus slots.";
                continue;
            }

            var setCells = empty.ToArray();
            for (var slot = 0; slot < group.Value.Count; slot++)
            {
                var row = group.Value[slot];
                var spellField = fields[slot * 2];
                var thresholdField = fields[(slot * 2) + 1];
                if (!TryConvertCell(spellFile, row, spellId, spellField.Host, out var spellCell, out var spellError))
                {
                    invalid[group.Key] = $"ItemSetSpell.SpellID cannot enter {spellField.Host.Name}: {spellError}";
                    break;
                }
                if (!TryConvertCell(spellFile, row, threshold, thresholdField.Host, out var thresholdCell, out var thresholdError))
                {
                    invalid[group.Key] = $"ItemSetSpell.Threshold cannot enter {thresholdField.Host.Name}: {thresholdError}";
                    break;
                }
                setCells[slot * 2] = spellCell;
                setCells[(slot * 2) + 1] = thresholdCell;
            }
            if (!invalid.ContainsKey(group.Key)) cells[group.Key] = setCells;
        }

        return new(source.CanonicalName, source.TableName, host.TableName, spellFile.RowCount, fields, cells, empty, invalid,
        [
            $"Projected {cells.Count:N0} Legion item-set bonus group(s) from {source.TableName} into MoP's eight embedded set-bonus slots.",
            invalid.Count == 0
                ? "Every projected ItemSetSpell group fits the MoP embedded-slot model."
                : $"{invalid.Count:N0} item-set bonus group(s) require specialization-aware or larger storage and their owning donor set rows are skipped explicitly."
        ]);
    }

    private static DbcColumn RequiredColumn(IReadOnlyDictionary<string, DbcColumn> columns, string name, string table)
        => columns.TryGetValue(name, out var column) ? column : throw new InvalidDataException($"{table} schema is missing required field '{name}'.");

    private static DbcColumn RequiredColumn(IReadOnlyList<DbcColumn> columns, string name, string table)
        => columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"{table} schema is missing required field '{name}'.");

    private static bool TryPositiveUInt(WdbcFile file, int row, DbcColumn column, out uint value)
    {
        if (IsSigned(column.Type))
        {
            var signed = SignedValue(file, row, column);
            if (signed is <= 0 or > uint.MaxValue) { value = 0; return false; }
            value = (uint)signed;
            return true;
        }
        var raw = file.GetRaw64(row, column);
        if (raw is 0 or > uint.MaxValue) { value = 0; return false; }
        value = (uint)raw;
        return true;
    }

    private static ulong ReadUnsigned(WdbcFile file, int row, DbcColumn column)
    {
        if (!IsSigned(column.Type)) return file.GetRaw64(row, column);
        var value = SignedValue(file, row, column);
        return value < 0 ? ulong.MaxValue : (ulong)value;
    }

    private static CanonicalCell ZeroCell(DbcColumn target) => target.Type switch
    {
        DbcValueType.StringOffset => new(CellKind.String, string.Empty, 0, 0, 0),
        DbcValueType.Float32 => new(CellKind.Float, null, 0, 0, 0),
        DbcValueType.Int32 or DbcValueType.Int64 => new(CellKind.Signed, null, 0, 0, 0),
        _ => new(CellKind.Unsigned, null, 0, 0, 0)
    };

    private static IReadOnlyList<FieldMapping> MatchFields(
        string hostProfileId,
        string donorProfileId,
        string canonicalTable,
        IReadOnlyList<DbcColumn> hostColumns,
        IReadOnlyList<DbcColumn> donorColumns,
        DbcColumn? hostKey,
        DbcColumn? donorKey,
        DbdDefinition hostDefinition,
        DbdDefinition donorDefinition,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection)
    {
        var hostReferences = References(hostColumns, hostDefinition);
        var donorReferences = References(donorColumns, donorDefinition);
        var remaining = donorColumns.Where(column => donorKey is null || column.Index != donorKey.Index).ToList();
        var result = new List<FieldMapping>();
        foreach (var host in hostColumns.Where(column => hostKey is null || column.Index != hostKey.Index))
        {
            var exact = remaining.Where(column => column.Name.Equals(host.Name, StringComparison.OrdinalIgnoreCase) && CompatibleTypes(host, column)).ToArray();
            var match = exact.Length == 1 ? exact[0] : null;
            var kind = "exact-name";
            if (match is null)
            {
                var normalized = remaining.Where(column => CanonicalName(column.Name).Equals(CanonicalName(host.Name), StringComparison.OrdinalIgnoreCase) && CompatibleTypes(host, column)).ToArray();
                if (normalized.Length == 1) { match = normalized[0]; kind = "normalized-name"; }
            }
            if (match is null && CrossBuildFieldAliasCatalog.DonorField(hostProfileId, donorProfileId, canonicalTable, host.Name) is { } alias)
            {
                var aliased = remaining.Where(column => column.Name.Equals(alias, StringComparison.OrdinalIgnoreCase) && CompatibleTypes(host, column)).ToArray();
                if (aliased.Length == 1) { match = aliased[0]; kind = "profile-alias"; }
            }
            CrossBuildAssetFieldRule? assetRule = null;
            if (match is null && assetBridge is not null && host.Type == DbcValueType.StringOffset &&
                CrossBuildAssetBridgeCatalog.Find(hostProfileId, donorProfileId, canonicalTable, host.Name) is { } configuredAssetRule)
            {
                var assetFields = remaining.Where(column => column.Name.Equals(configuredAssetRule.DonorField, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (assetFields.Length == 1)
                {
                    match = assetFields[0];
                    kind = $"filedata-{configuredAssetRule.Kind.ToString().ToLowerInvariant()}";
                    assetRule = configuredAssetRule;
                }
            }
            CrossBuildItemVisualFieldRule? itemVisualRule = null;
            if (match is null && itemVisualProjection is not null &&
                CrossBuildItemVisualProjection.FindRule(canonicalTable, host.Name) is { } configuredItemVisualRule)
            {
                var itemVisualFields = remaining.Where(column => column.Name.Equals(configuredItemVisualRule.DonorField, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (itemVisualFields.Length == 1)
                {
                    match = itemVisualFields[0];
                    kind = "itemvisual-effect-projection";
                    itemVisualRule = configuredItemVisualRule;
                }
            }
            if (match is null && hostReferences.TryGetValue(host.Name, out var hostReference))
            {
                var byReference = remaining.Where(column => donorReferences.TryGetValue(column.Name, out var donorReference) &&
                    donorReference.Equals(hostReference, StringComparison.OrdinalIgnoreCase) && column.ArrayIndex == host.ArrayIndex && CompatibleTypes(host, column)).ToArray();
                if (byReference.Length == 1) { match = byReference[0]; kind = "reference-target"; }
            }
            if (match is null) continue;
            remaining.Remove(match);
            donorReferences.TryGetValue(match.Name, out var reference);
            result.Add(new(host, match, kind, assetRule is null && itemVisualRule is null ? reference : null, assetRule, itemVisualRule));
        }
        return result;
    }

    private static Dictionary<string, string> References(IReadOnlyList<DbcColumn> columns, DbdDefinition definition)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in columns)
        {
            var logical = FieldBaseName(column.Name);
            if (!definition.Columns.TryGetValue(logical, out var definitionColumn) || string.IsNullOrWhiteSpace(definitionColumn.Reference)) continue;
            var separator = definitionColumn.Reference.IndexOf("::", StringComparison.Ordinal);
            var table = (separator < 0 ? definitionColumn.Reference : definitionColumn.Reference[..separator]).Trim();
            if (table.Length > 0) result[column.Name] = CanonicalName(table);
        }
        return result;
    }

    private static bool CompatibleTypes(DbcColumn host, DbcColumn donor)
    {
        var hostString = host.Type == DbcValueType.StringOffset;
        var donorString = donor.Type == DbcValueType.StringOffset;
        return hostString == donorString;
    }

    private static bool TryValidateRow(
        WdbcFile donor,
        int donorRow,
        uint donorId,
        IReadOnlyList<FieldMapping> mappings,
        SupplementalProjection? supplemental,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection,
        out string error)
    {
        if (supplemental is not null && supplemental.InvalidRows.TryGetValue(donorId, out var supplementalError))
        {
            error = supplementalError;
            return false;
        }
        foreach (var mapping in mappings)
            if (!TryConvertMappingCell(donor, donorRow, mapping, assetBridge, itemVisualProjection, false, out _, out error))
            {
                error = $"{mapping.Donor.Name} -> {mapping.Host.Name}: {error}";
                return false;
            }
        error = string.Empty;
        return true;
    }

    private static bool TryConvertMappingCell(
        WdbcFile source,
        int row,
        FieldMapping mapping,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection,
        bool markUsed,
        out CanonicalCell cell,
        out string error)
    {
        if (mapping.ItemVisualRule is not null)
        {
            if (itemVisualProjection is null)
                return ConversionFailure(out cell, out error, "the ItemVisualEffects projection is not configured");
            if (!TryConvertCell(source, row, mapping.Donor, DummyColumn, out var visualIdCell, out var visualConversionError))
                return ConversionFailure(out cell, out error, visualConversionError);
            if (visualIdCell.Kind != CellKind.Unsigned || visualIdCell.Unsigned > uint.MaxValue)
                return ConversionFailure(out cell, out error, "ItemVisual model FileDataID is outside the unsigned 32-bit range");
            if (!itemVisualProjection.TryResolve(mapping.ItemVisualRule, (uint)visualIdCell.Unsigned, markUsed, out var effectId, out error))
            {
                cell = default;
                return false;
            }
            cell = CellFromReference(effectId, mapping.Host);
            return true;
        }
        if (mapping.AssetRule is null)
            return TryConvertCell(source, row, mapping.Donor, mapping.Host, out cell, out error);
        if (assetBridge is null)
            return ConversionFailure(out cell, out error, "the FileData asset bridge is not configured");
        if (!TryConvertCell(source, row, mapping.Donor, DummyColumn, out var idCell, out var conversionError))
            return ConversionFailure(out cell, out error, conversionError);
        if (idCell.Kind != CellKind.Unsigned || idCell.Unsigned > uint.MaxValue)
            return ConversionFailure(out cell, out error, "FileDataID is outside the unsigned 32-bit range");
        if (!assetBridge.TryResolve(mapping.AssetRule, (uint)idCell.Unsigned, markUsed, out var clientPath, out error))
        {
            cell = default;
            return false;
        }
        cell = new(CellKind.String, clientPath, 0, 0, 0);
        return true;
    }

    private static bool TryConvertCell(WdbcFile source, int row, DbcColumn sourceColumn, DbcColumn targetColumn, out CanonicalCell cell, out string error)
    {
        try
        {
            if (targetColumn.Type == DbcValueType.StringOffset)
            {
                if (sourceColumn.Type != DbcValueType.StringOffset) return ConversionFailure(out cell, out error, "string/numeric type mismatch");
                cell = new(CellKind.String, source.GetString(source.GetRaw64(row, sourceColumn)), 0, 0, 0);
                error = string.Empty;
                return true;
            }

            if (targetColumn.Type == DbcValueType.Float32)
            {
                float value;
                if (sourceColumn.Type == DbcValueType.Float32) value = BitConverter.UInt32BitsToSingle(checked((uint)source.GetRaw64(row, sourceColumn)));
                else if (IsSigned(sourceColumn.Type)) value = Convert.ToSingle(SignedValue(source, row, sourceColumn), CultureInfo.InvariantCulture);
                else value = Convert.ToSingle(source.GetRaw64(row, sourceColumn), CultureInfo.InvariantCulture);
                if (!float.IsFinite(value)) return ConversionFailure(out cell, out error, "non-finite float value");
                cell = new(CellKind.Float, null, 0, 0, BitConverter.SingleToUInt32Bits(value));
                error = string.Empty;
                return true;
            }

            if (IsSigned(targetColumn.Type))
            {
                long value;
                if (sourceColumn.Type == DbcValueType.Float32)
                {
                    var floating = BitConverter.UInt32BitsToSingle(checked((uint)source.GetRaw64(row, sourceColumn)));
                    if (!float.IsFinite(floating) || floating != MathF.Truncate(floating))
                        return ConversionFailure(out cell, out error, $"float {floating:R} is not an integer");
                    value = checked((long)floating);
                }
                else if (IsSigned(sourceColumn.Type)) value = SignedValue(source, row, sourceColumn);
                else
                {
                    var raw = source.GetRaw64(row, sourceColumn);
                    var sourceWidth = Math.Clamp(sourceColumn.EffectiveBitWidth, 1, 64);
                    var targetWidth = Math.Clamp(targetColumn.EffectiveBitWidth, 1, 64);
                    var signedMaximum = targetWidth == 64 ? (ulong)long.MaxValue : (1UL << (targetWidth - 1)) - 1;
                    if (raw <= signedMaximum) value = (long)raw;
                    else if (sourceWidth == targetWidth) value = SignExtend(raw, targetWidth);
                    else return ConversionFailure(out cell, out error, $"unsigned value {raw} exceeds signed {targetWidth}-bit range");
                }
                var width = Math.Clamp(targetColumn.EffectiveBitWidth, 1, 64);
                var minimum = width == 64 ? long.MinValue : -(1L << (width - 1));
                var maximum = width == 64 ? long.MaxValue : (1L << (width - 1)) - 1;
                if (value < minimum || value > maximum) return ConversionFailure(out cell, out error, $"value {value} exceeds signed {width}-bit range");
                cell = new(CellKind.Signed, null, value, 0, 0);
                error = string.Empty;
                return true;
            }

            ulong unsigned;
            if (IsSigned(sourceColumn.Type))
            {
                var signed = SignedValue(source, row, sourceColumn);
                if (signed < 0)
                {
                    var sourceWidth = Math.Clamp(sourceColumn.EffectiveBitWidth, 1, 64);
                    var targetWidth = Math.Clamp(targetColumn.EffectiveBitWidth, 1, 64);
                    if (sourceWidth != targetWidth)
                        return ConversionFailure(out cell, out error, $"negative value {signed} cannot enter an unsigned {targetWidth}-bit field from a signed {sourceWidth}-bit field");
                    unsigned = source.GetRaw64(row, sourceColumn) & WidthMask(targetWidth);
                }
                else unsigned = (ulong)signed;
            }
            else if (sourceColumn.Type == DbcValueType.Float32)
            {
                var floating = BitConverter.UInt32BitsToSingle(checked((uint)source.GetRaw64(row, sourceColumn)));
                if (!float.IsFinite(floating) || floating < 0 || floating != MathF.Truncate(floating)) return ConversionFailure(out cell, out error, $"float {floating:R} is not a nonnegative integer");
                unsigned = checked((ulong)floating);
            }
            else unsigned = source.GetRaw64(row, sourceColumn);
            var bits = Math.Clamp(targetColumn.EffectiveBitWidth, 1, 64);
            var maximumUnsigned = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
            if (unsigned > maximumUnsigned) return ConversionFailure(out cell, out error, $"value {unsigned} exceeds unsigned {bits}-bit range");
            cell = new(CellKind.Unsigned, null, 0, unsigned, 0);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            cell = default;
            error = exception.Message;
            return false;
        }
    }

    private static bool ConversionFailure(out CanonicalCell cell, out string error, string message)
    {
        cell = default;
        error = message;
        return false;
    }

    private static long SignedValue(WdbcFile file, int row, DbcColumn column)
    {
        var raw = file.GetRaw64(row, column);
        var width = Math.Clamp(column.EffectiveBitWidth, 1, 64);
        return SignExtend(raw, width);
    }

    private static long SignExtend(ulong raw, int width)
    {
        if (width == 64) return unchecked((long)raw);
        var mask = WidthMask(width);
        var sign = 1UL << (width - 1);
        raw &= mask;
        return (raw & sign) == 0 ? (long)raw : unchecked((long)(raw | ~mask));
    }

    private static ulong WidthMask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1;

    private static bool IsSigned(DbcValueType type) => type is DbcValueType.Int32 or DbcValueType.Int64;

    private static void SetCell(WdbcFile file, int row, DbcColumn column, CanonicalCell cell)
    {
        switch (cell.Kind)
        {
            case CellKind.String: file.SetDisplayValue(row, column, cell.Text ?? string.Empty); break;
            case CellKind.Float: file.SetRaw64(row, column, cell.FloatBits); break;
            case CellKind.Signed: file.SetDisplayValue(row, column, cell.Signed.ToString(CultureInfo.InvariantCulture)); break;
            case CellKind.Unsigned: file.SetRaw64(row, column, cell.Unsigned); break;
            default: throw new ArgumentOutOfRangeException(nameof(cell));
        }
    }

    private static CanonicalCell CellFromTarget(WdbcFile file, int row, DbcColumn column)
    {
        if (!TryConvertCell(file, row, column, column, out var cell, out var error))
            throw new InvalidDataException($"Could not read target field {column.Name}: {error}");
        return cell;
    }

    private static CanonicalCell CellFromReference(uint value, DbcColumn target) => IsSigned(target.Type)
        ? new(CellKind.Signed, null, value, 0, 0)
        : new(CellKind.Unsigned, null, 0, value, 0);

    private static bool TryReferenceId(CanonicalCell cell, DbcColumn sourceColumn, out uint value)
    {
        if (cell.Kind == CellKind.Signed)
        {
            if (cell.Signed < 0 || cell.Signed > uint.MaxValue) { value = 0; return false; }
            value = (uint)cell.Signed;
            return true;
        }
        if (cell.Kind == CellKind.Unsigned && cell.Unsigned <= uint.MaxValue)
        {
            value = (uint)cell.Unsigned;
            return true;
        }
        value = 0;
        return false;
    }

    private static bool IsReferenceSentinel(uint value, DbcColumn sourceColumn)
    {
        if (value == 0) return true;
        var width = Math.Clamp(sourceColumn.EffectiveBitWidth, 1, 32);
        var maximum = width == 32 ? uint.MaxValue : (1u << width) - 1;
        return value == maximum;
    }

    private static ulong SemanticHash(
        WdbcFile file,
        int row,
        uint donorId,
        IReadOnlyList<FieldMapping> mappings,
        SupplementalProjection? supplemental,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection,
        bool donorSide)
    {
        const ulong basis = 14695981039346656037UL;
        var hash = basis;
        foreach (var mapping in mappings)
        {
            CanonicalCell cell;
            if (donorSide)
            {
                if (!TryConvertMappingCell(file, row, mapping, assetBridge, itemVisualProjection, false, out cell, out var error))
                    throw new InvalidDataException($"Cannot hash {mapping.Donor.Name}: {error}");
            }
            else cell = CellFromTarget(file, row, mapping.Host);
            MixCell(ref hash, cell);
        }
        if (supplemental is not null)
        {
            if (donorSide)
            {
                foreach (var cell in supplemental.CellsFor(donorId)) MixCell(ref hash, cell);
            }
            else
            {
                foreach (var field in supplemental.Fields) MixCell(ref hash, CellFromTarget(file, row, field.Host));
            }
        }
        return hash;
    }

    private static void MixCell(ref ulong hash, CanonicalCell cell)
    {
        MixByte(ref hash, (byte)cell.Kind);
        switch (cell.Kind)
        {
            case CellKind.String:
                foreach (var value in Encoding.UTF8.GetBytes(cell.Text ?? string.Empty)) MixByte(ref hash, value);
                break;
            case CellKind.Float:
                MixUInt32(ref hash, cell.FloatBits);
                break;
            case CellKind.Signed:
                MixUInt64(ref hash, unchecked((ulong)cell.Signed));
                break;
            case CellKind.Unsigned:
                MixUInt64(ref hash, cell.Unsigned);
                break;
        }
        MixByte(ref hash, 0xFF);
    }

    private static void MixByte(ref ulong state, byte value) { state ^= value; state *= 1099511628211UL; }
    private static void MixUInt32(ref ulong state, uint value) { for (var shift = 0; shift < 32; shift += 8) MixByte(ref state, (byte)(value >> shift)); }
    private static void MixUInt64(ref ulong state, ulong value) { for (var shift = 0; shift < 64; shift += 8) MixByte(ref state, (byte)(value >> shift)); }

    private static bool RowsEquivalent(
        WdbcFile host,
        int hostRow,
        WdbcFile donor,
        int donorRow,
        uint donorId,
        IReadOnlyList<FieldMapping> mappings,
        SupplementalProjection? supplemental,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection)
    {
        foreach (var mapping in mappings)
        {
            var hostValue = CellFromTarget(host, hostRow, mapping.Host);
            if (!TryConvertMappingCell(donor, donorRow, mapping, assetBridge, itemVisualProjection, false, out var donorValue, out _)) return false;
            if (!hostValue.Equals(donorValue)) return false;
        }
        if (supplemental is not null)
        {
            var cells = supplemental.CellsFor(donorId);
            for (var index = 0; index < supplemental.Fields.Count; index++)
                if (!CellFromTarget(host, hostRow, supplemental.Fields[index].Host).Equals(cells[index])) return false;
        }
        return true;
    }

    private static bool DonorRowsEquivalent(
        WdbcFile donor,
        int leftRow,
        uint leftId,
        int rightRow,
        uint rightId,
        IReadOnlyList<FieldMapping> mappings,
        SupplementalProjection? supplemental,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection)
    {
        foreach (var mapping in mappings)
        {
            if (!TryConvertMappingCell(donor, leftRow, mapping, assetBridge, itemVisualProjection, false, out var left, out _) ||
                !TryConvertMappingCell(donor, rightRow, mapping, assetBridge, itemVisualProjection, false, out var right, out _) || !left.Equals(right)) return false;
        }
        if (supplemental is not null && !supplemental.CellsFor(leftId).SequenceEqual(supplemental.CellsFor(rightId))) return false;
        return true;
    }

    private static SemanticCandidate? FindEquivalent(
        IReadOnlyDictionary<ulong, List<SemanticCandidate>> catalog,
        ulong hash,
        WdbcFile host,
        WdbcFile donor,
        int donorRow,
        uint donorId,
        IReadOnlyList<FieldMapping> mappings,
        SupplementalProjection? supplemental,
        CrossBuildAssetBridge? assetBridge,
        CrossBuildItemVisualProjection? itemVisualProjection)
    {
        if (!catalog.TryGetValue(hash, out var candidates)) return null;
        return candidates.FirstOrDefault(candidate => candidate.DonorSide
            ? DonorRowsEquivalent(donor, candidate.Row, candidate.DonorId, donorRow, donorId, mappings, supplemental, assetBridge, itemVisualProjection)
            : RowsEquivalent(host, candidate.Row, donor, donorRow, donorId, mappings, supplemental, assetBridge, itemVisualProjection));
    }

    private static void AddCandidate(IDictionary<ulong, List<SemanticCandidate>> catalog, ulong hash, SemanticCandidate candidate)
    {
        if (!catalog.TryGetValue(hash, out var candidates)) catalog[hash] = candidates = [];
        candidates.Add(candidate);
    }

    private static uint AllocateId(HashSet<uint> occupied, IReadOnlySet<uint> donorIds, uint maximum, ref uint next)
    {
        for (ulong attempts = 0; attempts <= maximum; attempts++)
        {
            if (next == 0 || next > maximum) next = 1;
            var candidate = next;
            next = candidate == maximum ? 1 : candidate + 1;
            if (donorIds.Contains(candidate) || !occupied.Add(candidate)) continue;
            return candidate;
        }
        throw new InvalidOperationException($"No collision-free ID remains in the target field's 1..{maximum:N0} range.");
    }

    private static uint AllocateVirtualRow(HashSet<uint> occupied, ref uint next)
    {
        if (next == uint.MaxValue) throw new InvalidOperationException("The target table exhausted its virtual row-index range.");
        var candidate = next++;
        if (!occupied.Add(candidate)) throw new InvalidDataException($"Virtual row-index allocation collided at {candidate:N0}.");
        return candidate;
    }

    private static uint MaximumPositiveId(DbcColumn key)
    {
        var width = Math.Clamp(key.EffectiveBitWidth, 1, 32);
        if (IsSigned(key.Type)) return width == 32 ? int.MaxValue : (1u << (width - 1)) - 1;
        return width == 32 ? uint.MaxValue : (1u << width) - 1;
    }

    private static Dictionary<string, TableSource> DiscoverTables(string root, bool preferDb2, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, List<TableSource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(path => Path.GetExtension(path) is ".dbc" or ".DBC" or ".db2" or ".DB2")
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = Path.GetFileNameWithoutExtension(path);
            var canonical = CanonicalName(table);
            if (!candidates.TryGetValue(canonical, out var values)) candidates[canonical] = values = [];
            values.Add(new(canonical, table, Path.GetFullPath(path)));
        }
        return candidates.ToDictionary(pair => pair.Key, pair => pair.Value
            .OrderByDescending(table => TablePreference(table, preferDb2))
            .ThenBy(table => table.Path, StringComparer.OrdinalIgnoreCase).First(), StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<CrossBuildAssetRequest> CollectAssetRequests(
        string hostProfileId,
        string donorProfileId,
        IReadOnlyList<TableSource> donorTables,
        ClientTableSchemaProvider donorSchema,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        var requests = new HashSet<CrossBuildAssetRequest>();
        foreach (var donor in donorTables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rules = CrossBuildAssetBridgeCatalog.ForTable(hostProfileId, donorProfileId, donor.CanonicalName);
            if (rules.Count == 0) continue;
            var file = WdbcFile.Load(donor.Path);
            var resolved = donorSchema.Resolve(file);
            var identity = CrossBuildTableIdentityCatalog.Resolve(donorProfileId, donor.CanonicalName, resolved.Columns, resolved.KeyStrategy);
            var rows = DbcRecordIdentity.IndexRows(file, resolved.Columns, identity.Strategy).OrderBy(pair => pair.Key);
            if (maximumRows > 0) rows = rows.Take(maximumRows).OrderBy(pair => pair.Key);
            var selectedRows = rows.Select(pair => pair.Value).ToArray();
            foreach (var rule in rules)
            {
                var columns = resolved.Columns.Where(column => column.Name.Equals(rule.DonorField, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (columns.Length != 1)
                    throw new InvalidDataException($"Asset bridge contract requires exactly one {donor.TableName}.{rule.DonorField} field; found {columns.Length:N0}.");
                foreach (var row in selectedRows)
                {
                    if (!TryConvertCell(file, row, columns[0], DummyColumn, out var cell, out var error))
                        throw new InvalidDataException($"Could not read {donor.TableName}.{rule.DonorField} as a FileDataID: {error}");
                    if (cell.Kind != CellKind.Unsigned || cell.Unsigned > uint.MaxValue)
                        throw new InvalidDataException($"{donor.TableName}.{rule.DonorField} contains a FileDataID outside the unsigned 32-bit range.");
                    if (cell.Unsigned != 0) requests.Add(new(rule, (uint)cell.Unsigned));
                }
            }
        }
        return requests.OrderBy(value => value.FileDataId).ThenBy(value => value.Rule.CanonicalTable, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int TablePreference(TableSource table, bool preferDb2)
    {
        var info = new FileInfo(table.Path);
        var score = info.Length > 0 ? 10 : 0;
        if (preferDb2 && Path.GetExtension(table.Path).Equals(".db2", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (Path.GetFileName(Path.GetDirectoryName(table.Path) ?? string.Empty).Equals("db2", StringComparison.OrdinalIgnoreCase)) score += 5;
        return score;
    }

    private static DbdDefinition LoadDefinition(string root, string table)
    {
        var exact = Path.Combine(root, table + ".dbd");
        if (File.Exists(exact)) return DbdSchemaService.Load(exact);
        var canonical = CanonicalName(table);
        var matches = Directory.EnumerateFiles(root, "*.dbd", SearchOption.TopDirectoryOnly)
            .Where(path => CanonicalName(Path.GetFileNameWithoutExtension(path)).Equals(canonical, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => DbdSchemaService.Load(matches[0]),
            0 => throw new FileNotFoundException($"No WoWDBDefs definition exists for {table}.", exact),
            _ => throw new InvalidDataException($"Multiple WoWDBDefs definitions normalize to {canonical}: {string.Join(", ", matches.Select(Path.GetFileName))}")
        };
    }

    private static HashSet<string> NormalizeIncludes(IReadOnlyList<string>? tables) => (tables ?? [])
        .Where(table => !string.IsNullOrWhiteSpace(table)).Select(CanonicalName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string CanonicalName(string value)
    {
        var normalized = new string(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return normalized.Length > 0 ? normalized : throw new InvalidDataException($"'{value}' has no usable alphanumeric table identity.");
    }

    private static string FieldBaseName(string value)
    {
        var bracket = value.IndexOf('[', StringComparison.Ordinal);
        return bracket < 0 ? value : value[..bracket];
    }

    private static TableBlueprint InactiveBlueprint(TableSource host, TableSource donor, CrossBuildMashupTableStatus status,
        string finding, int hostRows = 0, int donorRows = 0)
    {
        var report = new CrossBuildMashupTableReport(host.CanonicalName, host.TableName, donor.TableName, host.Path, donor.Path,
            status, hostRows, donorRows, 0, 0, [], [], [], 0, 0, 0, 0, 0, 0, 0, null, null, null, [finding], []);
        return new(host.CanonicalName, host, donor, [], null, DummyColumn, DummyColumn,
            DbcRecordKeyStrategy.None, DbcRecordKeyStrategy.None, false, [], [], report, false);
    }

    private static CrossBuildMashupTableReport NoHostReport(TableSource donor) => new(
        donor.CanonicalName, string.Empty, donor.TableName, null, donor.Path, CrossBuildMashupTableStatus.NoHostTable,
        0, 0, 0, 0, [], [], [], 0, 0, 0, 0, 0, 0, 0, null, null, null,
        ["The donor table has no MoP host layout and cannot be made runtime-visible by adding an unknown table file."], []);

    private static CrossBuildMashupTableReport FailedReport(TableSource host, TableSource donor, string error) => new(
        host.CanonicalName, host.TableName, donor.TableName, host.Path, donor.Path, CrossBuildMashupTableStatus.Failed,
        0, 0, 0, 0, [], [], [], 0, 0, 0, 0, 0, 0, 0, null, null, null, [], [error]);

    private static void AddReferenceEvent(IDictionary<ReferenceEventKey, ReferenceEventValue> events, ReferenceEventKey key, uint donorRowId)
    {
        if (events.TryGetValue(key, out var current)) events[key] = current with { Count = current.Count + 1 };
        else events[key] = new(donorRowId, 1);
    }

    private static void AppendReferenceEvents(string path, IReadOnlyDictionary<ReferenceEventKey, ReferenceEventValue> events)
    {
        using var writer = new StreamWriter(path, append: true, new UTF8Encoding(false));
        foreach (var pair in events.OrderBy(pair => pair.Key.Table, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key.Column, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(pair => pair.Key.DonorReferenceId))
            writer.WriteLine(string.Join(',', Csv(pair.Key.Table), pair.Value.SampleDonorRowId.ToString(CultureInfo.InvariantCulture),
                Csv(pair.Key.Column), Csv(pair.Key.ReferenceTable), pair.Key.DonorReferenceId.ToString(CultureInfo.InvariantCulture),
                pair.Key.HostReferenceId.ToString(CultureInfo.InvariantCulture), Csv(pair.Key.Action), pair.Value.Count.ToString(CultureInfo.InvariantCulture)));
    }

    private static void VerifyArchivePayload(string patchPath, IReadOnlyList<PatchEntry> entries, CancellationToken cancellationToken)
    {
        var service = new PatchArchiveService();
        var listed = service.ListFiles(patchPath).Where(entry => !entry.IsMetadata).ToArray();
        if (listed.Length != entries.Count) throw new InvalidDataException($"Published MPQ contains {listed.Length:N0} data entries; expected {entries.Count:N0}.");
        var extraction = Path.Combine(Path.GetTempPath(), $"crucible-mashup-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(extraction);
        try
        {
            var extracted = service.ExtractFlat(patchPath, extraction, listed, cancellationToken);
            var sourceByIdentity = entries.ToDictionary(entry => MpqLocale.Identity(entry.ArchivePath, entry.Locale), StringComparer.Ordinal);
            foreach (var item in extracted)
            {
                var identity = MpqLocale.Identity(item.Entry.ArchivePath, item.Entry.Locale);
                if (!sourceByIdentity.TryGetValue(identity, out var source)) throw new InvalidDataException($"MPQ verification found unexpected entry {item.Entry.ArchivePath}.");
                if (!Hash(source.SourcePath).Equals(Hash(item.FilePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"MPQ entry {item.Entry.ArchivePath} differs from its converted source table.");
            }
        }
        finally { if (Directory.Exists(extraction)) Directory.Delete(extraction, true); }
    }

    private static void BuildPatchWithShortNativePaths(string outputPath, IReadOnlyList<PatchEntry> entries)
    {
        var volume = Path.GetPathRoot(Path.GetFullPath(outputPath)) ?? throw new InvalidOperationException("Mashup patch output has no volume root.");
        var nativeRoot = Path.Combine(volume, $"wc-mp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nativeRoot);
        try
        {
            var nativeEntries = new List<PatchEntry>(entries.Count);
            for (var index = 0; index < entries.Count; index++)
            {
                var source = Path.Combine(nativeRoot, $"{index:X4}{Path.GetExtension(entries[index].SourcePath)}");
                File.Copy(entries[index].SourcePath, source, overwrite: false);
                nativeEntries.Add(entries[index] with { SourcePath = source });
            }
            var nativePatch = Path.Combine(nativeRoot, "mashup.mpq");
            new PatchArchiveService().Create(nativePatch, nativeEntries);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            CopyAtomic(nativePatch, outputPath);
            var nativeList = PatchArchiveService.GetRecoveryListFilePath(nativePatch);
            if (File.Exists(nativeList)) CopyAtomic(nativeList, PatchArchiveService.GetRecoveryListFilePath(outputPath));
        }
        finally
        {
            var resolved = Path.GetFullPath(nativeRoot);
            if (!resolved.StartsWith(Path.Combine(volume, "wc-mp-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Refusing to clean unexpected native MPQ staging path: {resolved}");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static CrossBuildMashupInstallation Install(CrossBuildMashupResult result, CrossBuildMashupRequest request, CancellationToken cancellationToken)
    {
        var installedServer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var preimages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? clientPatch = null;
        var preimageRoot = Path.Combine(result.RunRoot, "install-preimages");
        if (!string.IsNullOrWhiteSpace(request.InstallClientDataRoot))
        {
            var clientData = RequiredDirectory(request.InstallClientDataRoot, "Mashup client Data root");
            clientPatch = ContainedPath(clientData, Path.GetFileName(result.PatchPath));
            BackupAndCopy(result.PatchPath, clientPatch, Path.Combine(preimageRoot, "client"), preimages);
        }
        if (!string.IsNullOrWhiteSpace(request.InstallServerTableRoot))
        {
            var serverRoot = RequiredDirectory(request.InstallServerTableRoot, "Mashup server table root");
            foreach (var table in result.Tables.Where(table => table.Status == CrossBuildMashupTableStatus.Converted && table.OutputServerTable is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(result.ServerPayloadRoot, table.OutputServerTable!);
                if (relative.StartsWith("..", StringComparison.Ordinal)) throw new InvalidDataException($"Server payload escaped its root: {table.OutputServerTable}");
                var destination = ContainedPath(serverRoot, relative);
                BackupAndCopy(table.OutputServerTable!, destination, Path.Combine(preimageRoot, "server"), preimages);
                installedServer[relative] = destination;
            }
        }
        var receipt = Path.Combine(result.RunRoot, "installation-receipt.json");
        var installation = new CrossBuildMashupInstallation(clientPatch, installedServer, preimages, receipt);
        AtomicWrite(receipt, JsonSerializer.Serialize(installation, JsonOptions));
        return installation;
    }

    private static void BackupAndCopy(string source, string destination, string backupRoot, IDictionary<string, string> preimages)
    {
        destination = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            var relative = Path.GetFileName(destination) + "-" + Hash(destination)[..12] + ".preimage";
            var backup = Path.Combine(backupRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(destination, backup, overwrite: false);
            preimages[destination] = backup;
        }
        CopyAtomic(source, destination);
        if (!Hash(source).Equals(Hash(destination), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Installed file hash mismatch: {destination}");
    }

    private static void CopyAtomic(string source, string destination)
    {
        var temporary = destination + $".crucible-{Guid.NewGuid():N}.tmp";
        try { File.Copy(source, temporary, overwrite: false); File.Move(temporary, destination, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ContainedPath(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes the declared installation root: {relative}");
        return path;
    }

    private static IReadOnlyList<CrossBuildMashupTableReport> RebaseReports(
        IReadOnlyList<CrossBuildMashupTableReport> reports, string staging, string runRoot) => reports.Select(report => report with
    {
        OutputClientTable = Rebase(report.OutputClientTable, staging, runRoot),
        OutputServerTable = Rebase(report.OutputServerTable, staging, runRoot),
        IdMapPath = Rebase(report.IdMapPath, staging, runRoot)
    }).ToArray();

    private static string? Rebase(string? path, string from, string to)
    {
        if (path is null) return null;
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from)) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(to, Path.GetRelativePath(from, full))
            : full;
    }

    private static IReadOnlyList<string> BuildFindings(
        CrossBuildMashupRequest request,
        TargetProfile host,
        TargetProfile donor,
        IReadOnlyList<CrossBuildMashupTableReport> reports,
        CrossBuildAssetBridge? assetBridge)
    {
        var converted = reports.Where(report => report.Status == CrossBuildMashupTableStatus.Converted).ToArray();
        var findings = new List<string>
        {
            $"{request.Name} uses {host.DisplayName} build {host.ClientBuild:N0} as the runnable host and {donor.DisplayName} build {donor.ClientBuild:N0} as the semantic donor.",
            $"Converted {converted.Length:N0} shared table(s), adding {converted.Sum(report => report.AddedRows):N0} donor row(s) while preserving the host container layouts.",
            $"Reused {converted.Sum(report => report.ReusedSameId + report.ReusedEquivalent):N0} semantically matching donor row(s), retained {converted.Sum(report => report.RetainedHostSameId):N0} fixed-identity host row(s), and remapped {converted.Sum(report => report.AddedRemappedId):N0} colliding ID(s).",
            $"Rewrote {converted.Sum(report => report.RewrittenReferences):N0} DBD-declared reference occurrence(s); {converted.Sum(report => report.UnresolvedReferences):N0} unresolved occurrence(s) were retained and are enumerated in the reference ledger.",
            "This is a real data hybrid, not proof that SkyFire implements Legion gameplay systems. Donor-only fields, tables, assets, SQL, scripts, and executable behavior remain separately visible work rather than being silently claimed as converted."
        };
        if (assetBridge is not null) findings.AddRange(assetBridge.Findings);
        return findings;
    }

    private static string RenderMarkdown(CrossBuildMashupResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {result.Name}").AppendLine();
        builder.AppendLine($"- Result: **{(result.Passed ? "PASS" : "INCOMPLETE")}**");
        builder.AppendLine($"- Host: `{result.HostProfileId}`");
        builder.AppendLine($"- Donor: `{result.DonorProfileId}`");
        builder.AppendLine($"- Converted tables: {result.ConvertedTables:N0}/{result.SharedTables:N0}");
        builder.AppendLine($"- Added donor rows: {result.AddedRows:N0}");
        builder.AppendLine($"- Reused donor rows: {result.ReusedRows:N0}");
        builder.AppendLine($"- Fixed-identity host rows retained: {result.Tables.Sum(table => table.RetainedHostSameId):N0}");
        builder.AppendLine($"- Remapped IDs: {result.RemappedIds:N0}");
        builder.AppendLine($"- Rewritten references: {result.RewrittenReferences:N0}");
        builder.AppendLine($"- Unresolved retained references: {result.UnresolvedReferences:N0}");
        builder.AppendLine($"- Patch: `{result.PatchPath}`");
        builder.AppendLine($"- ID ledger: `{result.IdMapLedgerPath}`");
        builder.AppendLine($"- Reference ledger: `{result.ReferenceLedgerPath}`").AppendLine();
        builder.AppendLine("## Table Results").AppendLine();
        builder.AppendLine("| Host | Donor | Status | Host rows | Donor rows | Added | Host kept | Remapped | Fields | Dropped | Defaults | Ref rewrites | Unresolved |");
        builder.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var table in result.Tables.OrderBy(table => table.CanonicalTable, StringComparer.OrdinalIgnoreCase))
            builder.AppendLine($"| {Md(table.HostTable)} | {Md(table.DonorTable)} | {table.Status} | {table.HostRows:N0} | {table.DonorRows:N0} | {table.AddedRows:N0} | {table.RetainedHostSameId:N0} | {table.AddedRemappedId:N0} | {table.MappedFields:N0} | {table.DroppedDonorFields.Count:N0} | {table.DefaultedHostFields.Count:N0} | {table.RewrittenReferences:N0} | {table.UnresolvedReferences:N0} |");
        if (result.Errors.Count > 0)
        {
            builder.AppendLine().AppendLine("## Errors").AppendLine();
            foreach (var error in result.Errors) builder.AppendLine($"- {error}");
        }
        builder.AppendLine().AppendLine("## Boundary").AppendLine();
        foreach (var finding in result.Findings) builder.AppendLine($"- {finding}");
        if (result.Installation is not null)
            builder.AppendLine($"- Installed client patch: `{result.Installation.ClientPatchPath ?? "not requested"}`").AppendLine($"- Installed server tables: {result.Installation.ServerFiles.Count:N0}");
        return builder.ToString();
    }

    private static string Md(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static void ValidateRequest(CrossBuildMashupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FormatVersion != RequestFormatVersion) throw new InvalidDataException($"Unsupported mashup request version {request.FormatVersion}.");
        if (string.IsNullOrWhiteSpace(request.Name)) throw new InvalidDataException("Mashup request requires a name.");
        if (string.IsNullOrWhiteSpace(request.HostProfileId) || string.IsNullOrWhiteSpace(request.DonorProfileId)) throw new InvalidDataException("Mashup request requires host and donor profile IDs.");
        if (request.MaximumRowsPerTable < 0) throw new InvalidDataException("MaximumRowsPerTable cannot be negative; zero means all rows.");
        var assetConfiguration = new[] { request.HostClientDataRoot, request.DonorClientRoot, request.DonorFileDataListPath };
        var configuredAssetPaths = assetConfiguration.Count(value => !string.IsNullOrWhiteSpace(value));
        if (configuredAssetPaths is not 0 and not 3)
            throw new InvalidDataException("Cross-build FileData assets require HostClientDataRoot, DonorClientRoot, and DonorFileDataListPath together.");
        if (string.IsNullOrWhiteSpace(request.PatchFileName) || !Path.GetExtension(request.PatchFileName).Equals(".mpq", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(request.PatchFileName) != request.PatchFileName)
            throw new InvalidDataException("PatchFileName must be one MPQ file name without a directory component.");
        _ = Path.GetFullPath(request.HostTableRoot);
        _ = Path.GetFullPath(request.DonorTableRoot);
        _ = Path.GetFullPath(request.DefinitionsRoot);
        _ = Path.GetFullPath(request.OutputRoot);
        foreach (var path in assetConfiguration.Where(value => !string.IsNullOrWhiteSpace(value))) _ = Path.GetFullPath(path!);
    }

    private static bool HasAssetBridgeConfiguration(CrossBuildMashupRequest request) =>
        !string.IsNullOrWhiteSpace(request.HostClientDataRoot) &&
        !string.IsNullOrWhiteSpace(request.DonorClientRoot) &&
        !string.IsNullOrWhiteSpace(request.DonorFileDataListPath);

    private static string Slug(string value)
    {
        var result = new string(value.Trim().ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        while (result.Contains("--", StringComparison.Ordinal)) result = result.Replace("--", "-", StringComparison.Ordinal);
        return result.Trim('-');
    }

    private static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
        : value;

    private static string RequiredFile(string path, string label)
    {
        path = Path.GetFullPath(path);
        return File.Exists(path) ? path : throw new FileNotFoundException($"{label} does not exist.", path);
    }

    private static string RequiredDirectory(string path, string label)
    {
        path = Path.GetFullPath(path);
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"{label} does not exist: {path}");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AtomicWrite(string path, string content)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".crucible-{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, content, new UTF8Encoding(false)); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static readonly DbcColumn DummyColumn = new(0, 0, 4, "ID", DbcValueType.UInt32, true);
    private enum CellKind : byte { None, String, Float, Signed, Unsigned }
    private readonly record struct CanonicalCell(CellKind Kind, string? Text, long Signed, ulong Unsigned, uint FloatBits);
    private sealed record TableSource(string CanonicalName, string TableName, string Path);
    private sealed record FieldMapping(
        DbcColumn Host,
        DbcColumn Donor,
        string MatchKind,
        string? ReferenceTable,
        CrossBuildAssetFieldRule? AssetRule = null,
        CrossBuildItemVisualFieldRule? ItemVisualRule = null);
    private sealed record SupplementalDescriptor(string SourceName, string HostBaseName, string? ReferenceTable);
    private sealed record SupplementalField(DbcColumn Host, DbcColumn Source, string SourceField, string? ReferenceTable);
    private sealed class SupplementalProjection(
        string sourceCanonicalTable,
        string sourceTable,
        string targetTable,
        int sourceRows,
        IReadOnlyList<SupplementalField> fields,
        IReadOnlyDictionary<uint, CanonicalCell[]> cellsByDonorId,
        CanonicalCell[] emptyCells,
        IReadOnlyDictionary<uint, string> invalidRows,
        IReadOnlyList<string> findings)
    {
        public string SourceCanonicalTable { get; } = sourceCanonicalTable;
        public string SourceTable { get; } = sourceTable;
        public string TargetTable { get; } = targetTable;
        public int SourceRows { get; } = sourceRows;
        public IReadOnlyList<SupplementalField> Fields { get; } = fields;
        public IReadOnlyDictionary<uint, string> InvalidRows { get; } = invalidRows;
        public IReadOnlyList<string> Findings { get; } = findings;
        public IReadOnlyList<CanonicalCell> CellsFor(uint donorId) => cellsByDonorId.TryGetValue(donorId, out var cells) ? cells : emptyCells;
    }
    private sealed record SemanticCandidate(bool DonorSide, int Row, uint HostId, uint DonorId);
    private sealed record TableBlueprint(
        string CanonicalTable,
        TableSource Host,
        TableSource Donor,
        IReadOnlyList<FieldMapping> Mappings,
        SupplementalProjection? Supplemental,
        DbcColumn? HostKey,
        DbcColumn? DonorKey,
        DbcRecordKeyStrategy HostKeyStrategy,
        DbcRecordKeyStrategy DonorKeyStrategy,
        bool ReferenceAddressable,
        Dictionary<uint, uint> IdMap,
        HashSet<uint> OriginalHostIds,
        CrossBuildMashupTableReport Report,
        bool Ready);
    private readonly record struct ReferenceEventKey(string Table, string Column, string ReferenceTable, uint DonorReferenceId, uint HostReferenceId, string Action);
    private readonly record struct ReferenceEventValue(uint SampleDonorRowId, int Count);
}
