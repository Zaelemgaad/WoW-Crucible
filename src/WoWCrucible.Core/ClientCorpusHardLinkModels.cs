namespace WoWCrucible.Core;

public sealed record ClientCorpusCohort(string Id, int ClientBuild, IReadOnlyList<string> ClientRoots);

public sealed record ClientCorpusHardLinkRequest(
    int FormatVersion,
    string OutputRoot,
    IReadOnlyList<string> DiscoveryRoots,
    IReadOnlyList<ClientCorpusCohort> Cohorts,
    IReadOnlyList<string>? ExcludedDirectoryNames = null,
    IReadOnlyList<string>? ExcludedRelativePaths = null,
    long MinimumFileBytes = 1,
    int HashWorkers = 0,
    long MinimumCompleteClientDataBytes = 1L * 1024 * 1024 * 1024);

public sealed record ClientCorpusHardLinkProgress(string Phase, long Completed, long Total, string CurrentPath);

public sealed record ClientCorpusRootSnapshot(
    string CohortId,
    int ClientIndex,
    string RootPath,
    int Files,
    long Bytes,
    IReadOnlyList<string> VolumeSerials);

public sealed record ClientCorpusFileOccurrence(
    int ClientIndex,
    string ClientRoot,
    string RelativePath,
    string FullPath,
    string VolumeSerial,
    string FileId,
    uint ExistingLinkCount,
    int Attributes,
    long CreationTimeUtcTicks,
    long LastWriteTimeUtcTicks);

public sealed record ClientCorpusHardLinkAction(
    string Id,
    string CohortId,
    string Sha256,
    long Length,
    string CanonicalPath,
    string CanonicalVolumeSerial,
    string CanonicalFileId,
    string TargetPath,
    string TargetVolumeSerial,
    string TargetFileId,
    uint TargetExistingLinkCount,
    int TargetAttributes,
    long TargetCreationTimeUtcTicks,
    long TargetLastWriteTimeUtcTicks,
    bool MetadataMatchesCanonical,
    long EstimatedReclaimableBytes);

public sealed record ClientCorpusConsensusGroup(
    string CohortId,
    string Sha256,
    long Length,
    int ClientCount,
    int Occurrences,
    int ExistingPhysicalFiles,
    int PlannedPhysicalFiles,
    long EstimatedReclaimableBytes,
    IReadOnlyList<ClientCorpusFileOccurrence> Files);

public sealed record ClientCorpusNearConsensus(
    string CohortId,
    string Sha256,
    long Length,
    int PresentClients,
    int RequiredClients,
    int Occurrences,
    IReadOnlyList<string> SamplePaths);

public sealed record ClientCorpusDiscoveredClient(
    string CohortId,
    int ClientBuild,
    string ClientRoot,
    string VersionSource);

public sealed record ClientCorpusCoverageSnapshot(
    IReadOnlyList<string> DiscoveryRoots,
    long MinimumCompleteClientDataBytes,
    IReadOnlyList<ClientCorpusDiscoveredClient> Clients,
    string Fingerprint);

public sealed record ClientCorpusHardLinkPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string OutputRoot,
    string HashCachePath,
    string PlanPath,
    string MarkdownReportPath,
    string Fingerprint,
    IReadOnlyList<ClientCorpusRootSnapshot> Roots,
    IReadOnlyList<string> ExcludedDirectoryNames,
    IReadOnlyList<string> ExcludedRelativePaths,
    long MinimumFileBytes,
    long ScannedFiles,
    long ScannedBytes,
    long SkippedReparsePoints,
    long IgnoredZoneIdentifierFiles,
    long SkippedOtherAlternateStreamFiles,
    long HashedFiles,
    long ReusedHashes,
    IReadOnlyList<ClientCorpusConsensusGroup> ConsensusGroups,
    IReadOnlyList<ClientCorpusNearConsensus> NearConsensusGroups,
    IReadOnlyList<ClientCorpusHardLinkAction> Actions,
    long EstimatedReclaimableBytes,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    ClientCorpusCoverageSnapshot? Coverage = null)
{
    public bool Ready => Errors.Count == 0;
}

public enum ClientCorpusHardLinkApplyState { Linked, AlreadyLinked, Materialized, AlreadyMaterialized, Failed }

public sealed record ClientCorpusHardLinkApplyEntry(
    string ActionId,
    string TargetPath,
    ClientCorpusHardLinkApplyState State,
    long ReclaimableBytes,
    string? Error);

public sealed record ClientCorpusHardLinkApplyReport(
    int FormatVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    string PlanPath,
    string PlanFingerprint,
    bool Materialize,
    string ReportRoot,
    string JournalPath,
    string JsonReportPath,
    string MarkdownReportPath,
    IReadOnlyList<ClientCorpusHardLinkApplyEntry> Entries,
    IReadOnlyList<string> Errors)
{
    public bool Passed => Errors.Count == 0 && Entries.All(entry => entry.State != ClientCorpusHardLinkApplyState.Failed);
}
