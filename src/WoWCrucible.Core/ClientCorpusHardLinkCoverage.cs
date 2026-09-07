using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public sealed partial class ClientCorpusHardLinkService
{
    private sealed record DiscoveredClient(int ClientBuild, string ClientRoot, string VersionSource);

    private static ClientCorpusCoverageSnapshot DiscoverAndValidateCoverage(
        ClientCorpusHardLinkRequest request,
        IProgress<ClientCorpusHardLinkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var discoveryRoots = request.DiscoveryRoots.Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var discovered = DiscoverCompleteClients(discoveryRoots, request.OutputRoot,
            request.MinimumCompleteClientDataBytes, progress, cancellationToken);
        var cohortsByBuild = request.Cohorts.ToDictionary(cohort => cohort.ClientBuild);
        var relevant = discovered.Where(client => cohortsByBuild.ContainsKey(client.ClientBuild)).ToArray();

        foreach (var cohort in request.Cohorts)
        {
            var listed = cohort.ClientRoots.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var found = relevant.Where(client => client.ClientBuild == cohort.ClientBuild)
                .Select(client => client.ClientRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var omitted = found.Except(listed, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var unavailable = listed.Except(found, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (omitted.Length == 0 && unavailable.Length == 0) continue;

            var details = new List<string>();
            if (omitted.Length > 0)
                details.Add($"unlisted same-build clients: {string.Join("; ", omitted)}");
            if (unavailable.Length > 0)
                details.Add($"listed roots not discovered as complete build {cohort.ClientBuild}: {string.Join("; ", unavailable)}");
            throw new InvalidDataException(
                $"Cohort {cohort.Id} must equal every complete build-{cohort.ClientBuild} client below the discovery roots; {string.Join(" | ", details)}");
        }

        var clients = relevant.Select(client => new ClientCorpusDiscoveredClient(
                cohortsByBuild[client.ClientBuild].Id, client.ClientBuild, client.ClientRoot, client.VersionSource))
            .OrderBy(client => client.ClientBuild).ThenBy(client => client.ClientRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(discoveryRoots, request.MinimumCompleteClientDataBytes, clients,
            CoverageFingerprint(discoveryRoots, request.MinimumCompleteClientDataBytes, clients));
    }

    private static void VerifyCoverage(
        ClientCorpusCoverageSnapshot coverage,
        string outputRoot,
        IProgress<ClientCorpusHardLinkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var storedFingerprint = CoverageFingerprint(
            coverage.DiscoveryRoots, coverage.MinimumCompleteClientDataBytes, coverage.Clients);
        if (!storedFingerprint.Equals(coverage.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("The reviewed same-build client coverage proof does not match its contents.");

        var cohortByBuild = coverage.Clients.GroupBy(client => client.ClientBuild)
            .ToDictionary(group => group.Key, group => group.First().CohortId);
        var current = DiscoverCompleteClients(coverage.DiscoveryRoots, outputRoot,
                coverage.MinimumCompleteClientDataBytes, progress, cancellationToken)
            .Where(client => cohortByBuild.ContainsKey(client.ClientBuild))
            .Select(client => new ClientCorpusDiscoveredClient(
                cohortByBuild[client.ClientBuild], client.ClientBuild, client.ClientRoot, client.VersionSource))
            .OrderBy(client => client.ClientBuild).ThenBy(client => client.ClientRoot, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentFingerprint = CoverageFingerprint(
            coverage.DiscoveryRoots, coverage.MinimumCompleteClientDataBytes, current);
        if (currentFingerprint.Equals(coverage.Fingerprint, StringComparison.Ordinal)) return;

        var expected = coverage.Clients.Select(client => $"{client.ClientBuild}:{client.ClientRoot}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = current.Select(client => $"{client.ClientBuild}:{client.ClientRoot}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = actual.Except(expected, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = expected.Except(actual, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        throw new InvalidDataException(
            $"Same-version client coverage changed after planning. Added: {(added.Length == 0 ? "none" : string.Join("; ", added))}. Removed or no longer complete: {(removed.Length == 0 ? "none" : string.Join("; ", removed))}. Build a new plan before linking.");
    }

    internal static void VerifyCurrentCoverage(
        ClientCorpusHardLinkPlan plan,
        IProgress<ClientCorpusHardLinkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (plan.FormatVersion != PlanFormatVersion || plan.Coverage is null)
            throw new InvalidDataException(
                "A current fingerprint-bound whole-library same-build coverage plan is required.");
        VerifyCoverage(plan.Coverage, plan.OutputRoot, progress, cancellationToken);
    }

    private static IReadOnlyList<DiscoveredClient> DiscoverCompleteClients(
        IReadOnlyList<string> discoveryRoots,
        string outputRoot,
        long minimumDataBytes,
        IProgress<ClientCorpusHardLinkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var output = Path.GetFullPath(outputRoot);
        var clients = new Dictionary<string, DiscoveredClient>(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive
        };
        foreach (var discoveryRoot in discoveryRoots)
        {
            progress?.Report(new("Discovering client executables", candidates.Count, 0, discoveryRoot));
            foreach (var executable in Directory.EnumerateFiles(discoveryRoot, "*.exe", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = Path.GetDirectoryName(executable)!;
                if (!Directory.Exists(Path.Combine(directory, "Data"))) continue;
                var version = FileVersionInfo.GetVersionInfo(executable);
                if (IsLikelyClientExecutable(executable, version)) candidates.Add(directory);
            }
        }

        var ordered = candidates.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = ordered[index];
            progress?.Report(new("Validating complete clients", index, ordered.Length, directory));
            var version = ReadClientBuild(directory);
            var dataRoot = Path.Combine(directory, "Data");
            if (version is null || !ContainsAtLeast(dataRoot, minimumDataBytes, cancellationToken)) continue;
            if (ContainsPath(output, directory))
                throw new InvalidDataException(
                    $"The hard-link report/cache root contains a complete client and cannot be excluded from whole-library coverage: {directory}");
            clients[directory] = new(version.Value.ClientBuild, directory, version.Value.VersionSource);
        }
        return clients.Values.OrderBy(client => client.ClientBuild)
            .ThenBy(client => client.ClientRoot, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static (int ClientBuild, string VersionSource)? ReadClientBuild(string root)
    {
        var executables = Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Version: FileVersionInfo.GetVersionInfo(path)))
            .Where(item => IsLikelyClientExecutable(item.Path, item.Version))
            .ToArray();
        if (executables.Length == 0) return null;

        var buildInfo = Path.Combine(root, ".build.info");
        var declaredBuild = TryReadActiveBuild(buildInfo);
        if (declaredBuild > 0) return (declaredBuild.Value, buildInfo);

        foreach (var item in executables.Select(item =>
            {
                var build = item.Version.FilePrivatePart > 0
                    ? item.Version.FilePrivatePart
                    : ParseTrailingBuild(item.Version.FileVersion ?? item.Version.ProductVersion);
                return (ClientBuild: build ?? 0, VersionSource: item.Path);
            })
            .Where(item => item.ClientBuild > 0)
            .OrderByDescending(item => item.ClientBuild)
            .ThenBy(item => item.VersionSource, StringComparer.OrdinalIgnoreCase))
            return item;
        return null;
    }

    private static bool IsLikelyClientExecutable(string path, FileVersionInfo version)
    {
        var name = Path.GetFileName(path);
        return name.Contains("wow", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Ascension.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("MovieProxy.exe", StringComparison.OrdinalIgnoreCase) ||
               (version.ProductName?.Contains("World of Warcraft", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static int? TryReadActiveBuild(string path)
    {
        if (!File.Exists(path)) return null;
        var lines = File.ReadLines(path).Take(64).ToArray();
        if (lines.Length < 2) return null;
        var headers = lines[0].Split('|').Select(value => value.Split('!')[0]).ToArray();
        var versionIndex = Array.FindIndex(headers, value => value.Equals("Version", StringComparison.OrdinalIgnoreCase));
        var activeIndex = Array.FindIndex(headers, value => value.Equals("Active", StringComparison.OrdinalIgnoreCase));
        if (versionIndex < 0) return null;
        foreach (var line in lines.Skip(1))
        {
            var values = line.Split('|');
            if (versionIndex >= values.Length || (activeIndex >= 0 && (activeIndex >= values.Length || values[activeIndex] != "1")))
                continue;
            var build = ParseTrailingBuild(values[versionIndex]);
            if (build > 0) return build;
        }
        return null;
    }

    private static int? ParseTrailingBuild(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        foreach (var value in version.Split(['.', ',', ' '], StringSplitOptions.RemoveEmptyEntries).Reverse())
            if (int.TryParse(value, out var build) && build > 0) return build;
        return null;
    }

    private static bool ContainsAtLeast(string root, long minimumBytes, CancellationToken cancellationToken)
    {
        long bytes = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                bytes = checked(bytes + new FileInfo(file).Length);
                if (bytes >= minimumBytes) return true;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
        return false;
    }

    private static string CoverageFingerprint(
        IEnumerable<string> discoveryRoots,
        long minimumCompleteClientDataBytes,
        IEnumerable<ClientCorpusDiscoveredClient> clients)
    {
        var text = new StringBuilder();
        text.Append("minimum-complete-data-bytes").Append('\t')
            .Append(minimumCompleteClientDataBytes).Append('\n');
        foreach (var root in discoveryRoots.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase))
            text.Append("discovery-root").Append('\t').Append(root.ToUpperInvariant()).Append('\n');
        foreach (var client in clients.OrderBy(value => value.ClientBuild)
                     .ThenBy(value => value.ClientRoot, StringComparer.OrdinalIgnoreCase))
            text.Append(client.CohortId).Append('\t').Append(client.ClientBuild).Append('\t')
                .Append(Path.GetFullPath(client.ClientRoot).ToUpperInvariant()).Append('\t')
                .Append(Path.GetFullPath(client.VersionSource).ToUpperInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
