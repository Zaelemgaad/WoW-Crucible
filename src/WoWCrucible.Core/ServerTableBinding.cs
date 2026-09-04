using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum ServerTableConsumption { Unknown, ClientOnly, DbcLoaded, SqlOverlayed, Unused }
[Flags]
public enum DeploymentDestination { None = 0, ClientPatch = 1, ServerDbc = 2, WorldDatabase = 4 }
public enum RestartRequirement { None, ClientRestart, WorldServerRestart }
public enum RowDimensionKind { None, ClassAndLevel100 }

public sealed record ServerTableBinding(
    ServerCoreFamily CoreFamily,
    string Profile,
    string DbcFileName,
    string ClientTableName,
    ServerTableConsumption Consumption,
    string? SqlTableName,
    DbcRecordKeyStrategy KeyStrategy,
    RowDimensionKind Dimensions,
    DeploymentDestination Destinations,
    RestartRequirement Restart,
    bool SourceBacked = false,
    string SupportedRevision = "Unspecified")
{
    public string DescribeRow(uint key) => Dimensions switch
    {
        RowDimensionKind.ClassAndLevel100 => $"class {key / 100 + 1}, level {key % 100 + 1}",
        _ => $"row {key}"
    };
}
public sealed record InspectedServerTableBinding(ServerTableBinding Binding, DatabaseTableCapability? LiveSqlTable)
{
    public bool ExpectedSqlTablePresent => Binding.Consumption != ServerTableConsumption.SqlOverlayed || LiveSqlTable is not null;
}

public static class ServerTableBindingCatalog
{
    private const int WrathClientBuild = 12340;
    private const string AzerothProfile = "AzerothCore 3.3.5 profile";
    private const string TrinityProfile = "TrinityCore 3.3.5 profile";

    private static readonly (string File, string Table, DbcRecordKeyStrategy Key, RowDimensionKind Dimensions)[] AzerothGtOverlays =
    [
        ("gtBarberShopCostBase.dbc", "gtbarbershopcostbase_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.None),
        ("gtCombatRatings.dbc", "gtcombatratings_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.None),
        ("gtChanceToMeleeCritBase.dbc", "gtchancetomeleecritbase_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.None),
        ("gtChanceToMeleeCrit.dbc", "gtchancetomeleecrit_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100),
        ("gtChanceToSpellCritBase.dbc", "gtchancetospellcritbase_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.None),
        ("gtChanceToSpellCrit.dbc", "gtchancetospellcrit_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100),
        ("gtNPCManaCostScaler.dbc", "gtnpcmanacostscaler_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.None),
        ("gtOCTClassCombatRatingScalar.dbc", "gtoctclasscombatratingscalar_dbc", DbcRecordKeyStrategy.Physical(0), RowDimensionKind.None),
        ("gtOCTRegenHP.dbc", "gtoctregenhp_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100),
        ("gtRegenHPPerSpt.dbc", "gtregenhpperspt_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100),
        ("gtRegenMPPerSpt.dbc", "gtregenmpperspt_dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100)
    ];

    public static IReadOnlyList<ServerTableBinding> BuiltIn(ServerCoreFamily family, int? clientBuild = null)
    {
        if (clientBuild is not null && clientBuild != WrathClientBuild) return [];
        if (family == ServerCoreFamily.AzerothCore)
        {
            var bindings = AzerothGtOverlays.Select(value => Overlay(family, AzerothProfile, value.File, value.Table, value.Key, value.Dimensions)).ToList();
            bindings.Add(Unused(family, AzerothProfile, "gtOCTRegenMP.dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100));
            return bindings;
        }
        if (family == ServerCoreFamily.TrinityCore)
        {
            var bindings = AzerothGtOverlays.Select(value => DbcLoaded(family, TrinityProfile, value.File, value.Key, value.Dimensions)).ToList();
            bindings.Add(Unused(family, TrinityProfile, "gtOCTRegenMP.dbc", DbcRecordKeyStrategy.Virtual(), RowDimensionKind.ClassAndLevel100));
            return bindings;
        }
        return [];
    }

    public static IReadOnlyList<ServerTableBinding> Resolve(ServerCoreFamily family, string? sourceRoot = null, int? clientBuild = null)
    {
        var builtIn = BuiltIn(family, clientBuild).ToDictionary(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceRoot)) return builtIn.Values.OrderBy(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var sourceFiles = FindTableStores(sourceRoot);
        if (sourceFiles.Count == 0) throw new FileNotFoundException("Could not find DBCStores.cpp or DB2Stores.cpp in the selected core source folder.", sourceRoot);
        var revision = ReadGitRevision(sourceRoot);
        return sourceFiles.SelectMany(sourceFile => ParseSource(family, sourceFile, builtIn))
            .GroupBy(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(binding => binding.Consumption == ServerTableConsumption.Unused).First() with { SupportedRevision = revision })
            .OrderBy(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ServerTableBinding ResolveFile(ServerCoreFamily family, string dbcFileName, string? sourceRoot = null, int? clientBuild = null)
        => Resolve(family, sourceRoot, clientBuild).FirstOrDefault(binding => binding.DbcFileName.Equals(Path.GetFileName(dbcFileName), StringComparison.OrdinalIgnoreCase))
           ?? (sourceRoot is not null
               ? new(family, "Current core source (absent from DBCStores)", Path.GetFileName(dbcFileName), Path.GetFileNameWithoutExtension(dbcFileName), ServerTableConsumption.ClientOnly, null, DbcRecordKeyStrategy.None, RowDimensionKind.None, DeploymentDestination.ClientPatch, RestartRequirement.ClientRestart, true, "Selected source checkout")
               : new(family, $"{family} profile (mapping unknown)", Path.GetFileName(dbcFileName), Path.GetFileNameWithoutExtension(dbcFileName), ServerTableConsumption.Unknown, null, DbcRecordKeyStrategy.None, RowDimensionKind.None, DeploymentDestination.ClientPatch, RestartRequirement.ClientRestart, false, "No matching built-in binding"));

    public static IReadOnlyList<InspectedServerTableBinding> AttachCapabilities(IEnumerable<ServerTableBinding> bindings, DatabaseCapabilities capabilities)
        => bindings.Select(binding => new InspectedServerTableBinding(binding, binding.SqlTableName is null ? null : capabilities.FindTable(binding.SqlTableName))).ToArray();

    public static ServerTableBinding ApplySchemaKey(ServerTableBinding binding, DbcSchemaResolution schema)
    {
        if (schema.KeyStrategy.Kind == DbcRecordKeyKind.NoStableKey) return binding;
        if (binding.KeyStrategy.Kind != DbcRecordKeyKind.NoStableKey && binding.KeyStrategy != schema.KeyStrategy)
            throw new InvalidDataException($"The {binding.Profile} binding key ({binding.KeyStrategy.Kind}) disagrees with the selected schema ({schema.KeyStrategy.Kind}) for {binding.DbcFileName}.");
        return binding with { KeyStrategy = schema.KeyStrategy };
    }

    public static IReadOnlyList<ServerTableBinding> ParseSource(ServerCoreFamily family, string sourceFile, IReadOnlyDictionary<string, ServerTableBinding>? metadata = null)
    {
        if (Path.GetFileName(sourceFile).Equals("DB2Stores.cpp", StringComparison.OrdinalIgnoreCase))
            return ParseDb2Source(family, sourceFile, metadata);
        var results = new List<ServerTableBinding>();
        foreach (var line in File.ReadLines(sourceFile))
        {
            var call = Regex.Match(line, @"^(?<comment>\s*//)?\s*(?:LOAD_DBC|LoadDBC)\s*\((?<arguments>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!call.Success) continue;
            var quoted = Regex.Matches(call.Groups["arguments"].Value, "\"(?<value>[^\"]+)\"")
                .Select(match => match.Groups["value"].Value).ToArray();
            var fileIndex = Array.FindIndex(quoted, value => value.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase));
            if (fileIndex < 0) continue;
            var file = Path.GetFileName(quoted[fileIndex]);
            ServerTableBinding? known = null;
            metadata?.TryGetValue(file, out known);
            var key = known?.KeyStrategy ?? DbcRecordKeyStrategy.None;
            var dimensions = known?.Dimensions ?? RowDimensionKind.None;
            var unused = call.Groups["comment"].Success;
            var sqlTable = quoted.Skip(fileIndex + 1).FirstOrDefault(value => !value.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase));
            results.Add(unused ? Unused(family, "Current core source", file, key, dimensions, true)
                : sqlTable is not null ? Overlay(family, "Current core source", file, sqlTable, key, dimensions, true)
                : DbcLoaded(family, "Current core source", file, key, dimensions, true));
        }
        return results;
    }

    private static IReadOnlyList<ServerTableBinding> ParseDb2Source(ServerCoreFamily family, string sourceFile, IReadOnlyDictionary<string, ServerTableBinding>? metadata)
    {
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = File.ReadAllLines(sourceFile);
        foreach (var line in lines)
        {
            var declaration = Regex.Match(line, "^\\s*DB2Storage<[^>]+>\\s+(?<store>[A-Za-z_][A-Za-z0-9_]*)\\s*\\((?<arguments>.*)$");
            if (!declaration.Success) continue;
            var file = Regex.Matches(declaration.Groups["arguments"].Value, "\"(?<value>[^\"]+\\.db2)\"", RegexOptions.IgnoreCase)
                .Select(match => match.Groups["value"].Value).FirstOrDefault();
            declarations[declaration.Groups["store"].Value] = file ?? string.Empty;
        }

        var results = new List<ServerTableBinding>();
        foreach (var line in lines)
        {
            var load = Regex.Match(line, @"^(?<comment>\s*//)?\s*(?:LOAD_DB2|LoadDB2)\s*\((?<arguments>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!load.Success) continue;
            var arguments = load.Groups["arguments"].Value;
            var file = Regex.Matches(arguments, "\"(?<value>[^\"]+\\.db2)\"", RegexOptions.IgnoreCase)
                .Select(match => match.Groups["value"].Value).FirstOrDefault();
            if (file is null)
            {
                var store = declarations.Keys.FirstOrDefault(name => Regex.IsMatch(arguments, $@"(?:^|[^A-Za-z0-9_]){Regex.Escape(name)}(?:[^A-Za-z0-9_]|$)"));
                if (store is null || string.IsNullOrWhiteSpace(declarations[store])) continue;
                file = declarations[store];
            }
            file = Path.GetFileName(file);
            ServerTableBinding? known = null;
            metadata?.TryGetValue(file, out known);
            var key = known?.KeyStrategy ?? DbcRecordKeyStrategy.None;
            var dimensions = known?.Dimensions ?? RowDimensionKind.None;
            results.Add(load.Groups["comment"].Success
                ? Unused(family, "Current core source", file, key, dimensions, true)
                : DbcLoaded(family, "Current core source", file, key, dimensions, true));
        }
        return results;
    }

    private static IReadOnlyList<string> FindTableStores(string root)
    {
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 };
        return new[] { "DBCStores.cpp", "DB2Stores.cpp" }
            .SelectMany(name => Directory.EnumerateFiles(root, name, options))
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}DataStores{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string ReadGitRevision(string root)
    {
        try
        {
            var git = Path.Combine(Path.GetFullPath(root), ".git");
            if (!Directory.Exists(git)) return "Selected source checkout (Git revision unavailable)";
            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head.Length >= 12 ? $"detached@{head[..12]}" : $"detached@{head}";
            var reference = head[5..]; var refPath = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
            var hash = File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : File.ReadLines(Path.Combine(git, "packed-refs")).FirstOrDefault(line => line.EndsWith(" " + reference, StringComparison.Ordinal))?.Split(' ')[0];
            var branch = reference[(reference.LastIndexOf('/') + 1)..];
            return string.IsNullOrWhiteSpace(hash) ? $"{branch} (revision unavailable)" : $"{branch}@{hash[..Math.Min(12, hash.Length)]}";
        }
        catch { return "Selected source checkout (Git revision unavailable)"; }
    }

    private static ServerTableBinding Overlay(ServerCoreFamily family, string profile, string file, string table, DbcRecordKeyStrategy key, RowDimensionKind dimensions, bool source = false)
        => new(family, profile, file, Path.GetFileNameWithoutExtension(file), ServerTableConsumption.SqlOverlayed, table, key, dimensions, DeploymentDestination.ClientPatch | DeploymentDestination.ServerDbc | DeploymentDestination.WorldDatabase, RestartRequirement.WorldServerRestart, source, Revision(profile, source));
    private static ServerTableBinding DbcLoaded(ServerCoreFamily family, string profile, string file, DbcRecordKeyStrategy key, RowDimensionKind dimensions, bool source = false)
        => new(family, profile, file, Path.GetFileNameWithoutExtension(file), ServerTableConsumption.DbcLoaded, null, key, dimensions, DeploymentDestination.ClientPatch | DeploymentDestination.ServerDbc, RestartRequirement.WorldServerRestart, source, Revision(profile, source));
    private static ServerTableBinding Unused(ServerCoreFamily family, string profile, string file, DbcRecordKeyStrategy key, RowDimensionKind dimensions, bool source = false)
        => new(family, profile, file, Path.GetFileNameWithoutExtension(file), ServerTableConsumption.Unused, null, key, dimensions, DeploymentDestination.ClientPatch, RestartRequirement.ClientRestart, source, Revision(profile, source));

    private static string Revision(string profile, bool source) => source ? "Selected source checkout" : profile.StartsWith("AzerothCore", StringComparison.Ordinal) ? "AzerothCore 3.3.5 branch profile, snapshot 2026-07" : "TrinityCore 3.3.5 branch profile, snapshot 2026-07";
}
