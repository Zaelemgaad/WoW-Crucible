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

    public static IReadOnlyList<ServerTableBinding> BuiltIn(ServerCoreFamily family)
    {
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

    public static IReadOnlyList<ServerTableBinding> Resolve(ServerCoreFamily family, string? sourceRoot = null)
    {
        var builtIn = BuiltIn(family).ToDictionary(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(sourceRoot)) return builtIn.Values.OrderBy(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var sourceFile = FindDbcStores(sourceRoot);
        if (sourceFile is null) throw new FileNotFoundException("Could not find src/server/game/DataStores/DBCStores.cpp in the selected core source folder.", sourceRoot);
        var revision = ReadGitRevision(sourceRoot);
        return ParseSource(family, sourceFile, builtIn).Select(binding => binding with { SupportedRevision = revision }).OrderBy(binding => binding.DbcFileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static ServerTableBinding ResolveFile(ServerCoreFamily family, string dbcFileName, string? sourceRoot = null)
        => Resolve(family, sourceRoot).FirstOrDefault(binding => binding.DbcFileName.Equals(Path.GetFileName(dbcFileName), StringComparison.OrdinalIgnoreCase))
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
        var results = new List<ServerTableBinding>();
        foreach (var line in File.ReadLines(sourceFile))
        {
            var match = Regex.Match(line, "^(?<comment>\\s*//)?\\s*LOAD_DBC\\([^,]+,\\s*\"(?<file>[^\"]+\\.dbc)\"(?:,\\s*\"(?<table>[^\"]+)\")?");
            if (!match.Success) continue;
            var file = match.Groups["file"].Value;
            ServerTableBinding? known = null;
            metadata?.TryGetValue(file, out known);
            var key = known?.KeyStrategy ?? DbcRecordKeyStrategy.None;
            var dimensions = known?.Dimensions ?? RowDimensionKind.None;
            var unused = match.Groups["comment"].Success;
            var sqlTable = match.Groups["table"].Success ? match.Groups["table"].Value : null;
            results.Add(unused ? Unused(family, "Current core source", file, key, dimensions, true)
                : sqlTable is not null ? Overlay(family, "Current core source", file, sqlTable, key, dimensions, true)
                : DbcLoaded(family, "Current core source", file, key, dimensions, true));
        }
        return results;
    }

    private static string? FindDbcStores(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "DBCStores.cpp", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 }).FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}DataStores{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        : null;

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
