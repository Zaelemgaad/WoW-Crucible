using System.Security.Cryptography;

namespace WoWCrucible.Core;

public enum MpqMergeConflictPolicy { BlockDifferentEntries, PreferEarlierArchive, PreferLaterArchive }
public sealed record MpqMergeConflict(string ArchivePath, uint Locale, IReadOnlyList<string> Sources, IReadOnlyList<string> Sha256);
public sealed record MpqMergeResult(string OutputPath, int InputArchives, int OutputFiles, int LocaleVariantEntries, int ExactDuplicates, IReadOnlyList<MpqMergeConflict> Conflicts, MpqMergeConflictPolicy Policy);

public sealed class MpqMergeService
{
    public MpqMergeResult Merge(IReadOnlyList<string> archivePaths, string outputPath, MpqMergeConflictPolicy policy = MpqMergeConflictPolicy.BlockDifferentEntries,
        string? externalListFile = null, IProgress<(int Done, int Total, string Path)>? progress = null, CancellationToken cancellationToken = default)
    {
        var inputs = archivePaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inputs.Length < 2) throw new InvalidOperationException("Select at least two MPQ patches to merge.");
        if (inputs.Any(path => !File.Exists(path))) throw new FileNotFoundException("One or more source MPQs do not exist.");
        outputPath = Path.GetFullPath(outputPath);
        if (inputs.Contains(outputPath, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("The merged output cannot overwrite one of its source archives.");
        var parent = Path.GetDirectoryName(outputPath) ?? throw new InvalidOperationException("The output path has no parent folder."); Directory.CreateDirectory(parent);
        var temporary = Path.Combine(parent, $".wcm-{Guid.NewGuid():N}"[..13]); Directory.CreateDirectory(temporary);
        try
        {
            var service = new PatchArchiveService(); var extracted = new List<(string Archive, string InternalPath, uint Locale, string File)>(); var done = 0;
            for (var index = 0; index < inputs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var entries = service.ListFiles(inputs[index], "*", externalListFile).Where(entry => !entry.IsMetadata).ToArray();
                var ambiguous = entries.GroupBy(entry => MpqLocale.Identity(entry.ArchivePath, entry.Locale), StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
                if (ambiguous is not null) { var entry = ambiguous.First(); throw new InvalidDataException($"{Path.GetFileName(inputs[index])} contains multiple physical entries with the same identity '{entry.ArchivePath}' · {MpqLocale.Format(entry.Locale)}. Platform/variant identity is not independently distinguishable, so merge is blocked instead of guessing."); }
                var anonymous = entries.FirstOrDefault(entry => Path.GetFileName(entry.ArchivePath).StartsWith("File000", StringComparison.OrdinalIgnoreCase));
                if (anonymous is not null) throw new InvalidDataException($"{Path.GetFileName(inputs[index])} contains unresolved hash-only paths such as {anonymous.ArchivePath}. Supply a compatible listfile before merging so payloads cannot be assigned the wrong client path.");
                var destination = Path.Combine(temporary, $"s{index:D3}"); var files = service.ExtractFlat(inputs[index], destination, entries, cancellationToken);
                extracted.AddRange(files.Select(file => (inputs[index], PatchInputMapper.NormalizeArchivePath(file.Entry.ArchivePath), file.Entry.Locale, file.FilePath)));
                progress?.Report((++done, inputs.Length, Path.GetFileName(inputs[index])));
            }

            var selected = new List<PatchEntry>(); var conflicts = new List<MpqMergeConflict>(); var exactDuplicates = 0;
            foreach (var group in extracted.GroupBy(entry => MpqLocale.Identity(entry.InternalPath, entry.Locale), StringComparer.Ordinal).OrderBy(group => group.First().InternalPath, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.First().Locale))
            {
                cancellationToken.ThrowIfCancellationRequested(); var candidates = group.ToArray();
                if (candidates.Length == 1) { selected.Add(new(candidates[0].File, candidates[0].InternalPath, candidates[0].Locale)); continue; }
                var hashes = candidates.Select(candidate => Hash(candidate.File)).ToArray();
                var identical = hashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && candidates.Skip(1).All(candidate => FilesEqual(candidates[0].File, candidate.File));
                if (identical) { exactDuplicates += candidates.Length - 1; selected.Add(new(candidates[0].File, candidates[0].InternalPath, candidates[0].Locale)); continue; }
                conflicts.Add(new(candidates[0].InternalPath, candidates[0].Locale, candidates.Select(candidate => candidate.Archive).ToArray(), hashes));
                if (policy == MpqMergeConflictPolicy.PreferEarlierArchive) selected.Add(new(candidates[0].File, candidates[0].InternalPath, candidates[0].Locale));
                else if (policy == MpqMergeConflictPolicy.PreferLaterArchive) selected.Add(new(candidates[^1].File, candidates[^1].InternalPath, candidates[^1].Locale));
            }
            var localeVariantEntries = selected.GroupBy(entry => entry.ArchivePath, StringComparer.OrdinalIgnoreCase).Where(group => group.Select(entry => entry.Locale).Distinct().Count() > 1).Sum(group => group.Count());
            if (conflicts.Count > 0 && policy == MpqMergeConflictPolicy.BlockDifferentEntries)
                return new(outputPath, inputs.Length, 0, localeVariantEntries, exactDuplicates, conflicts, policy);
            service.Create(outputPath, selected);
            return new(outputPath, inputs.Length, selected.Count, localeVariantEntries, exactDuplicates, conflicts, policy);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static bool FilesEqual(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        const int size = 1024 * 1024; var left = new byte[size]; var right = new byte[size]; using var a = File.OpenRead(first); using var b = File.OpenRead(second);
        while (true) { var read = a.Read(left); if (read != b.Read(right)) return false; if (read == 0) return true; if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read))) return false; }
    }
}
