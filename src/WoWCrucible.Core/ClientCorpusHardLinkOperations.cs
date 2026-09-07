using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed partial class ClientCorpusHardLinkService
{
    private const int BufferBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JournalJsonOptions = new(JsonOptions) { WriteIndented = false };

    private static IReadOnlyList<ClientCorpusHardLinkAction> BuildActions(
        string cohortId,
        string sha256,
        long length,
        IReadOnlyList<ScannedFile> files,
        List<string> warnings)
    {
        var actions = new List<ClientCorpusHardLinkAction>();
        foreach (var volume in files.GroupBy(file => file.Identity.VolumeSerialHex, StringComparer.Ordinal))
        {
            var identities = volume.GroupBy(file => file.Identity.Key, StringComparer.Ordinal)
                .Select(group => group.OrderBy(file => file.ClientIndex)
                    .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray())
                .OrderBy(group => group[0].ClientIndex)
                .ThenBy(group => group[0].RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            if (identities.Length < 2) continue;

            var anchor = identities[0][0];
            var capacity = Math.Max(0, (int)MaximumHardLinks - checked((int)anchor.Identity.LinkCount));
            foreach (var identity in identities.Skip(1))
            {
                if (identity.Length > capacity)
                {
                    warnings.Add($"Hard-link limit partition retained another physical identity for {cohortId} SHA-256 {sha256} on volume {volume.Key}.");
                    anchor = identity[0];
                    capacity = Math.Max(0, (int)MaximumHardLinks - checked((int)anchor.Identity.LinkCount));
                    continue;
                }

                var reclaimable = identity[0].Identity.LinkCount == identity.Length ? length : 0;
                for (var index = 0; index < identity.Length; index++)
                {
                    var target = identity[index];
                    actions.Add(new(
                        ActionId(cohortId, sha256, anchor.FullPath, target.FullPath),
                        cohortId,
                        sha256,
                        length,
                        anchor.FullPath,
                        anchor.Identity.VolumeSerialHex,
                        anchor.Identity.FileIdHex,
                        target.FullPath,
                        target.Identity.VolumeSerialHex,
                        target.Identity.FileIdHex,
                        target.Identity.LinkCount,
                        target.Attributes,
                        target.CreationTicks,
                        target.WriteTicks,
                        MetadataMatches(anchor, target),
                        index == identity.Length - 1 ? reclaimable : 0));
                }
                capacity -= identity.Length;
            }
        }
        return actions;
    }

    private static ClientCorpusHardLinkApplyState ApplyAction(
        ClientCorpusHardLinkAction action,
        string journalPath,
        CancellationToken cancellationToken)
    {
        var canonicalIdentity = ValidateCurrent(action.CanonicalPath, action.Length, action.Sha256,
            action.CanonicalFileId, "canonical", cancellationToken);
        var targetIdentity = NativeHardLinks.ReadIdentity(action.TargetPath);
        if (targetIdentity.Key == canonicalIdentity.Key)
        {
            VerifyHash(action.TargetPath, action.Length, action.Sha256, cancellationToken);
            AppendJournal(journalPath, new
            {
                Utc = DateTimeOffset.UtcNow,
                action.Id,
                action.TargetPath,
                State = "AlreadyLinked"
            });
            return ClientCorpusHardLinkApplyState.AlreadyLinked;
        }
        if (canonicalIdentity.VolumeSerialHex != action.CanonicalVolumeSerial ||
            targetIdentity.FileIdHex != action.TargetFileId ||
            targetIdentity.VolumeSerialHex != action.TargetVolumeSerial ||
            targetIdentity.VolumeSerialHex != canonicalIdentity.VolumeSerialHex)
            throw new InvalidDataException("Target identity or volume no longer matches the reviewed plan.");

        VerifyByteIdentity(action.CanonicalPath, action.TargetPath, action.Length, action.Sha256, cancellationToken);
        AppendJournal(journalPath, new
        {
            Utc = DateTimeOffset.UtcNow,
            action.Id,
            action.CanonicalPath,
            action.TargetPath,
            State = "PendingLink"
        });
        NativeHardLinks.ReplaceWithHardLink(action.TargetPath, action.CanonicalPath);
        var linked = NativeHardLinks.ReadIdentity(action.TargetPath);
        if (linked.Key != canonicalIdentity.Key)
            throw new IOException("The replacement path does not share the canonical NTFS file identity.");
        AppendJournal(journalPath, new
        {
            Utc = DateTimeOffset.UtcNow,
            action.Id,
            action.TargetPath,
            State = "Linked",
            FileId = linked.FileIdHex
        });
        return ClientCorpusHardLinkApplyState.Linked;
    }

    private static ClientCorpusHardLinkApplyState MaterializeAction(
        ClientCorpusHardLinkAction action,
        string journalPath,
        CancellationToken cancellationToken)
    {
        var canonicalIdentity = NativeHardLinks.ReadIdentity(action.CanonicalPath);
        var targetIdentity = NativeHardLinks.ReadIdentity(action.TargetPath);
        VerifyHash(action.TargetPath, action.Length, action.Sha256, cancellationToken);
        if (targetIdentity.Key != canonicalIdentity.Key)
        {
            AppendJournal(journalPath, new
            {
                Utc = DateTimeOffset.UtcNow,
                action.Id,
                action.TargetPath,
                State = "AlreadyMaterialized"
            });
            return ClientCorpusHardLinkApplyState.AlreadyMaterialized;
        }

        AppendJournal(journalPath, new
        {
            Utc = DateTimeOffset.UtcNow,
            action.Id,
            action.TargetPath,
            State = "PendingMaterialize"
        });
        CopyIndependent(action.TargetPath, action.TargetAttributes, action.TargetCreationTimeUtcTicks,
            action.TargetLastWriteTimeUtcTicks, cancellationToken);
        var independent = NativeHardLinks.ReadIdentity(action.TargetPath);
        if (independent.Key == canonicalIdentity.Key)
            throw new IOException("Materialization did not produce an independent NTFS file identity.");
        VerifyHash(action.TargetPath, action.Length, action.Sha256, cancellationToken);
        AppendJournal(journalPath, new
        {
            Utc = DateTimeOffset.UtcNow,
            action.Id,
            action.TargetPath,
            State = "Materialized",
            FileId = independent.FileIdHex
        });
        return ClientCorpusHardLinkApplyState.Materialized;
    }

    private static NativeFileIdentity ValidateCurrent(
        string path,
        long length,
        string hash,
        string fileId,
        string role,
        CancellationToken cancellationToken)
    {
        var identity = NativeHardLinks.ReadIdentity(path);
        if (identity.FileIdHex != fileId)
            throw new InvalidDataException($"The {role} file identity no longer matches the reviewed plan.");
        VerifyHash(path, length, hash, cancellationToken);
        return identity;
    }

    private static void VerifyHash(string path, long length, string expectedHash, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != length)
            throw new InvalidDataException($"Reviewed file length changed: {path}");
        var hash = Hash(path, cancellationToken);
        if (!hash.Equals(expectedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"Reviewed file SHA-256 changed: {path}");
    }

    private static void VerifyByteIdentity(
        string canonicalPath,
        string targetPath,
        long length,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        using var canonical = new FileStream(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, FileOptions.SequentialScan);
        using var target = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, FileOptions.SequentialScan);
        if (canonical.Length != length || target.Length != length)
            throw new InvalidDataException("A reviewed file length changed before final byte comparison.");
        using var canonicalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var targetHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var left = ArrayPool<byte>.Shared.Rent(BufferBytes);
        var right = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leftRead = canonical.Read(left, 0, left.Length);
                var rightRead = target.Read(right, 0, right.Length);
                if (leftRead != rightRead)
                    throw new InvalidDataException("Files diverged during final byte comparison.");
                if (leftRead == 0) break;
                canonicalHash.AppendData(left, 0, leftRead);
                targetHash.AppendData(right, 0, rightRead);
                if (!left.AsSpan(0, leftRead).SequenceEqual(right.AsSpan(0, rightRead)))
                    throw new InvalidDataException("Files with the reviewed hash are not byte-for-byte identical.");
            }
            var leftHash = Convert.ToHexString(canonicalHash.GetHashAndReset());
            var rightHash = Convert.ToHexString(targetHash.GetHashAndReset());
            if (leftHash != expectedHash || rightHash != expectedHash)
                throw new InvalidDataException("Final byte comparison did not reproduce the reviewed SHA-256.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(left);
            ArrayPool<byte>.Shared.Return(right);
        }
    }

    private static void CopyIndependent(
        string path,
        int attributes,
        long creationTicks,
        long writeTicks,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.materializing";
        try
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       BufferBytes, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       BufferBytes, FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
                try
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = input.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        output.Write(buffer, 0, read);
                    }
                    output.Flush(true);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            File.Move(temporary, path, true);
            File.SetCreationTimeUtc(path, new DateTime(creationTicks, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(path, new DateTime(writeTicks, DateTimeKind.Utc));
            File.SetAttributes(path, (FileAttributes)attributes);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Hash(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static ClientCorpusFileOccurrence ToOccurrence(ScannedFile file) => new(
        file.ClientIndex,
        file.ClientRoot,
        file.RelativePath,
        file.FullPath,
        file.Identity.VolumeSerialHex,
        file.Identity.FileIdHex,
        file.Identity.LinkCount,
        file.Attributes,
        file.CreationTicks,
        file.WriteTicks);

    private static bool MetadataMatches(ScannedFile left, ScannedFile right) =>
        left.Attributes == right.Attributes && left.CreationTicks == right.CreationTicks &&
        left.WriteTicks == right.WriteTicks && left.HasZoneIdentifier == right.HasZoneIdentifier;

    private static string ActionId(string cohortId, string sha256, string canonical, string target)
    {
        var bytes = Encoding.UTF8.GetBytes($"{cohortId}\n{sha256}\n{canonical}\n{target}");
        return Convert.ToHexString(SHA256.HashData(bytes))[..24];
    }

    internal static string LegacyFingerprint(
        IReadOnlyList<ClientCorpusRootSnapshot> roots,
        IReadOnlyList<ClientCorpusConsensusGroup> groups,
        IReadOnlyList<ClientCorpusHardLinkAction> actions)
    {
        var text = new StringBuilder();
        foreach (var root in roots.OrderBy(value => value.CohortId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.ClientIndex))
            text.Append(root.CohortId).Append('\t').Append(root.ClientIndex).Append('\t')
                .Append(root.RootPath).Append('\n');
        foreach (var group in groups.OrderBy(value => value.CohortId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Sha256, StringComparer.Ordinal))
        {
            text.Append(group.CohortId).Append('\t').Append(group.Length).Append('\t')
                .Append(group.Sha256).Append('\n');
            foreach (var file in group.Files.OrderBy(value => value.FullPath, StringComparer.OrdinalIgnoreCase))
                text.Append(file.FullPath).Append('\t').Append(file.VolumeSerial).Append('\t')
                    .Append(file.FileId).Append('\n');
        }
        foreach (var action in actions.OrderBy(value => value.Id, StringComparer.Ordinal))
            text.Append(action.Id).Append('\t').Append(action.CanonicalPath).Append('\t')
                .Append(action.TargetPath).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Fingerprint(ClientCorpusHardLinkPlan plan)
    {
        var payload = new
        {
            plan.FormatVersion,
            plan.CreatedUtc,
            plan.OutputRoot,
            plan.HashCachePath,
            plan.PlanPath,
            plan.MarkdownReportPath,
            plan.Roots,
            plan.ExcludedDirectoryNames,
            plan.ExcludedRelativePaths,
            plan.MinimumFileBytes,
            plan.ScannedFiles,
            plan.ScannedBytes,
            plan.SkippedReparsePoints,
            plan.IgnoredZoneIdentifierFiles,
            plan.SkippedOtherAlternateStreamFiles,
            plan.HashedFiles,
            plan.ReusedHashes,
            plan.ConsensusGroups,
            plan.NearConsensusGroups,
            plan.Actions,
            plan.EstimatedReclaimableBytes,
            plan.Warnings,
            plan.Errors,
            plan.Coverage
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)));
    }

    private static void ValidateRequest(ClientCorpusHardLinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FormatVersion != RequestFormatVersion)
            throw new InvalidDataException($"Unsupported client corpus hard-link request version {request.FormatVersion}.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Client corpus hard-link storage requires Windows NTFS file identities.");
        if (string.IsNullOrWhiteSpace(request.OutputRoot))
            throw new InvalidDataException("A hard-link output root is required.");
        if (request.MinimumFileBytes < 1)
            throw new InvalidDataException("MinimumFileBytes must be at least 1; empty files provide no storage reduction.");
        if (request.MinimumCompleteClientDataBytes < 1)
            throw new InvalidDataException("MinimumCompleteClientDataBytes must be at least 1.");
        if (request.DiscoveryRoots.Count == 0)
            throw new InvalidDataException("At least one library discovery root is required so omitted same-build clients cannot qualify files by accident.");
        if (request.Cohorts.Count == 0)
            throw new InvalidDataException("At least one same-version client cohort is required.");
        var duplicateId = request.Cohorts.GroupBy(cohort => cohort.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
            throw new InvalidDataException($"Client cohort ID is duplicated: {duplicateId.Key}");
        var duplicateBuild = request.Cohorts.GroupBy(cohort => cohort.ClientBuild)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateBuild is not null)
            throw new InvalidDataException($"Client build is assigned to more than one cohort: {duplicateBuild.Key}");

        var discoveryRoots = request.DiscoveryRoots.Select(path => RequiredDirectory(path, "library discovery root")).ToArray();
        for (var left = 0; left < discoveryRoots.Length; left++)
        for (var right = left + 1; right < discoveryRoots.Length; right++)
            if (ContainsPath(discoveryRoots[left], discoveryRoots[right]) || ContainsPath(discoveryRoots[right], discoveryRoots[left]))
                throw new InvalidDataException($"Library discovery roots cannot overlap: {discoveryRoots[left]} / {discoveryRoots[right]}");
        var allRoots = new List<string>();
        foreach (var cohort in request.Cohorts)
        {
            if (string.IsNullOrWhiteSpace(cohort.Id))
                throw new InvalidDataException("Every client cohort requires a stable version ID.");
            if (cohort.ClientBuild <= 0)
                throw new InvalidDataException($"Client cohort {cohort.Id} requires an exact positive client build.");
            if (cohort.ClientRoots.Count < 2)
                throw new InvalidDataException($"Client cohort {cohort.Id} has only one complete client, so it cannot reduce storage. This is not an eligibility threshold: every discovered client of a represented build is always required.");
            foreach (var root in cohort.ClientRoots)
            {
                var full = RequiredDirectory(root, $"{cohort.Id} client root");
                if (!discoveryRoots.Any(discovery => ContainsPath(discovery, full)))
                    throw new InvalidDataException($"Client root is outside every reviewed library discovery root: {full}");
                allRoots.Add(full);
            }
        }
        var duplicateRoot = allRoots.GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateRoot is not null)
            throw new InvalidDataException($"A client root is assigned more than once: {duplicateRoot.Key}");
        for (var left = 0; left < allRoots.Count; left++)
        for (var right = left + 1; right < allRoots.Count; right++)
            if (ContainsPath(allRoots[left], allRoots[right]) || ContainsPath(allRoots[right], allRoots[left]))
                throw new InvalidDataException($"Client roots cannot overlap: {allRoots[left]} / {allRoots[right]}");
        var output = Path.GetFullPath(request.OutputRoot);
        if (allRoots.Any(root => ContainsPath(root, output) || ContainsPath(output, root)))
            throw new InvalidDataException("The hard-link report/cache root must not contain, or be contained by, a client root.");
        _ = NormalizeDirectoryNames(request.ExcludedDirectoryNames);
        _ = NormalizeRelativePaths(request.ExcludedRelativePaths);
    }

    private static HashSet<string> NormalizeDirectoryNames(IReadOnlyList<string>? values)
    {
        var result = (values ?? []).Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (result.Any(value => value is "." or ".." ||
                                value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
            throw new InvalidDataException("Excluded directories must be individual names.");
        return result;
    }

    private static HashSet<string> NormalizeRelativePaths(IReadOnlyList<string>? values)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0) continue;
            if (Path.IsPathRooted(value))
                throw new InvalidDataException("Excluded files must be client-relative paths.");
            var segments = value.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
                throw new InvalidDataException($"Invalid excluded relative path: {value}");
            result.Add(string.Join(Path.DirectorySeparatorChar, segments));
        }
        return result;
    }

    private static void AppendJournal(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(value, JournalJsonOptions) + Environment.NewLine;
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            4096, FileOptions.WriteThrough);
        var bytes = new UTF8Encoding(false).GetBytes(line);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static string RenderPlan(ClientCorpusHardLinkPlan plan)
    {
        var text = new StringBuilder();
        text.AppendLine("# Client Corpus Hard-Link Plan").AppendLine();
        text.AppendLine($"- Result: **{(plan.Ready ? "READY" : "BLOCKED")}**");
        text.AppendLine("- Rule: content must occur in every complete client of its build below the reviewed discovery roots");
        text.AppendLine($"- Coverage proof: **{(plan.Coverage is null ? "LEGACY / MISSING" : "VALIDATED")}**");
        text.AppendLine($"- Cohorts: {plan.Roots.Select(root => root.CohortId).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0}");
        text.AppendLine($"- Clients: {plan.Roots.Count:N0}");
        text.AppendLine($"- Scanned: {plan.ScannedFiles:N0} files / {FormatBytes(plan.ScannedBytes)}");
        text.AppendLine($"- Ignored Zone.Identifier metadata: {plan.IgnoredZoneIdentifierFiles:N0} files");
        text.AppendLine($"- Skipped other alternate streams: {plan.SkippedOtherAlternateStreamFiles:N0} files");
        text.AppendLine($"- Unanimous groups: {plan.ConsensusGroups.Count:N0}");
        text.AppendLine($"- Planned links: {plan.Actions.Count:N0}");
        text.AppendLine($"- Conservatively reclaimable: {FormatBytes(plan.EstimatedReclaimableBytes)}");
        text.AppendLine($"- Plan fingerprint: `{plan.Fingerprint}`").AppendLine();
        text.AppendLine("| Cohort | SHA-256 | Size | Clients | Occurrences | Physical before | Physical after | Reclaimable |");
        text.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var group in plan.ConsensusGroups.Take(500))
            text.AppendLine($"| {group.CohortId} | `{group.Sha256}` | {FormatBytes(group.Length)} | {group.ClientCount:N0} | {group.Occurrences:N0} | {group.ExistingPhysicalFiles:N0} | {group.PlannedPhysicalFiles:N0} | {FormatBytes(group.EstimatedReclaimableBytes)} |");
        if (plan.Warnings.Count > 0)
        {
            text.AppendLine().AppendLine("## Warnings").AppendLine();
            foreach (var warning in plan.Warnings) text.AppendLine($"- {warning}");
        }
        if (plan.NearConsensusGroups.Count > 0)
        {
            text.AppendLine().AppendLine("## Partial Matches Not Eligible").AppendLine();
            foreach (var value in plan.NearConsensusGroups.Take(100))
                text.AppendLine($"- {value.CohortId}: {value.PresentClients:N0}/{value.RequiredClients:N0} clients, {FormatBytes(value.Length)}, `{value.Sha256}`");
        }
        return text.ToString();
    }

    private static string RenderApply(ClientCorpusHardLinkApplyReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(report.Materialize
            ? "# Client Hard-Link Materialization Report"
            : "# Client Hard-Link Application Report").AppendLine();
        text.AppendLine($"- Result: **{(report.Passed ? "PASS" : "FAIL")}**");
        text.AppendLine($"- Plan: `{report.PlanPath}`");
        text.AppendLine($"- Journal: `{report.JournalPath}`");
        text.AppendLine($"- Entries: {report.Entries.Count:N0}");
        text.AppendLine($"- Reclaimable bytes linked this run: {FormatBytes(report.Entries.Sum(entry => entry.ReclaimableBytes))}");
        if (report.Errors.Count > 0)
        {
            text.AppendLine().AppendLine("## Errors").AppendLine();
            foreach (var error in report.Errors) text.AppendLine($"- {error}");
        }
        return text.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string RequiredDirectory(string path, string label)
    {
        path = Path.GetFullPath(path ?? string.Empty);
        return Directory.Exists(path)
            ? path
            : throw new DirectoryNotFoundException($"{label} does not exist: {path}");
    }

    private static string RequiredFile(string path, string label)
    {
        path = Path.GetFullPath(path ?? string.Empty);
        return File.Exists(path) ? path : throw new FileNotFoundException($"{label} does not exist.", path);
    }

    private static bool ContainsPath(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative) &&
               (relative == "." || (relative != ".." &&
                                    !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));
    }

    private static void AtomicWrite(string path, string content)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
