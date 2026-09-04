using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientTableFormat { None = 0, Wdbc = 1, Db2 = 2, Wdc1 = 4 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchiveFormat { Mpq, Casc }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetSupportTier { Verified, SchemaReady, Experimental }

public sealed record TargetProfile(
    string Id,
    string DisplayName,
    string Expansion,
    int ClientBuild,
    string SchemaFileName,
    ClientTableFormat TableFormats,
    ArchiveFormat ArchiveFormat,
    TargetSupportTier SupportTier,
    string Notes)
{
    public IReadOnlyList<int> CompatibleEmbeddedTableBuilds { get; init; } = [];
    public bool SupportsWdbc => TableFormats.HasFlag(ClientTableFormat.Wdbc);
    public bool SupportsDb2 => TableFormats.HasFlag(ClientTableFormat.Db2);
    public bool SupportsWdc1 => TableFormats.HasFlag(ClientTableFormat.Wdc1);
    public bool AcceptsEmbeddedTableBuild(int build) =>
        build == ClientBuild || CompatibleEmbeddedTableBuilds.Contains(build);
    public override string ToString() => $"{DisplayName} — {SupportTier}";
}

public static class TargetProfileCatalog
{
    public const string DefaultProfileId = "wotlk-12340";

    private static readonly TargetProfile[] BuiltIns =
    [
        new("classic-5875", "Classic 1.12.1 (5875)", "Classic", 5875, "Classic 1.12.1 (5875).xml",
            ClientTableFormat.Wdbc, ArchiveFormat.Mpq, TargetSupportTier.SchemaReady,
            "Definition support is available; full corpus round-trip validation is still required."),
        new("tbc-8606", "The Burning Crusade 2.4.3 (8606)", "The Burning Crusade", 8606, "TBC 2.4.3 (8606).xml",
            ClientTableFormat.Wdbc, ArchiveFormat.Mpq, TargetSupportTier.SchemaReady,
            "Definition support is available; full corpus round-trip validation is still required."),
        new(DefaultProfileId, "Wrath of the Lich King 3.3.5a (12340)", "Wrath of the Lich King", 12340, "WotLK 3.3.5 (12340).xml",
            ClientTableFormat.Wdbc, ArchiveFormat.Mpq, TargetSupportTier.Verified,
            "Primary target. WDBC editing and patch MPQ workflows are corpus tested."),
        new("cata-15595", "Cataclysm 4.3.4 (15595)", "Cataclysm", 15595, "Cata 4.3.4 (15595).xml",
            ClientTableFormat.Wdbc | ClientTableFormat.Db2, ArchiveFormat.Mpq, TargetSupportTier.Experimental,
            "The real build-15595 cache corpus (20 WDBC + 5 fixed-layout WDB2 tables) is schema- and byte-round-trip-verified. Raw PTCH layers are recognized, but effective base-plus-delta reconstruction and complete base-corpus verification remain pending."),
        new("mop-18414", "Mists of Pandaria 5.4.8 (18414)", "Mists of Pandaria", 18414, "MoP 5.4.8 (18414).xml",
            ClientTableFormat.Wdbc | ClientTableFormat.Db2, ArchiveFormat.Mpq, TargetSupportTier.Experimental,
            "Real SkyFire server corpora are used for WDBC/WDB2 round-trip and deployment testing. The runtime's WDB2 corpus carries its compatible 18273 producer stamp. Tables without an exact build-18414 DBD or WDBX layout remain raw-only instead of being assigned guessed columns.")
            { CompatibleEmbeddedTableBuilds = [18273] },
        new("legion-26972", "Legion 7.3.5 (26972)", "Legion", 26972, "Legion 7.3.5 (26972).xml",
            ClientTableFormat.Wdc1, ArchiveFormat.Casc, TargetSupportTier.Experimental,
            "Real LegionCore and client WDC1 corpora are used for storage-mode, copy-table, relationship, offset-map, and unchanged round-trip validation. CASC publication remains an explicit client-runtime capability rather than being mislabeled as an MPQ patch.")
    ];

    public static IReadOnlyList<TargetProfile> Load(string? userDirectory = null, string? applicationDirectory = null)
    {
        var profiles = BuiltIns.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateDirectories(userDirectory, applicationDirectory).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<TargetProfile>(File.ReadAllText(path), JsonOptions());
                    if (profile is not null) Validate(profile, path);
                    if (profile is not null) profiles[profile.Id] = profile;
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    throw new InvalidDataException($"Invalid target profile '{path}': {ex.Message}", ex);
                }
            }
        }
        return profiles.Values.OrderBy(profile => profile.ClientBuild).ToArray();
    }

    public static TargetProfile Find(IReadOnlyList<TargetProfile> profiles, string? id) =>
        profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? profiles.First(profile => profile.Id == DefaultProfileId);

    public static TargetProfile FindRequired(IReadOnlyList<TargetProfile> profiles, string id) =>
        profiles.FirstOrDefault(profile => profile.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Unknown target profile '{id}'. Available profiles: {string.Join(", ", profiles.Select(profile => profile.Id))}");

    public static void SaveTemplate(string path, TargetProfile profile)
    {
        Validate(profile, path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions(true)));
    }

    private static IEnumerable<string> CandidateDirectories(string? userDirectory, string? applicationDirectory)
    {
        yield return userDirectory ?? CruciblePaths.ProfilesDirectory;
        yield return applicationDirectory ?? Path.Combine(AppContext.BaseDirectory, "profiles");
    }

    private static void Validate(TargetProfile profile, string source)
    {
        if (string.IsNullOrWhiteSpace(profile.Id) || profile.Id.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character == '-')))
            throw new InvalidDataException($"Profile ID must contain only lowercase letters, digits, and hyphens ({source}).");
        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.ClientBuild <= 0 || profile.TableFormats == ClientTableFormat.None)
            throw new InvalidDataException($"Profile name, positive build, and at least one table format are required ({source}).");
        if (profile.CompatibleEmbeddedTableBuilds.Any(build => build <= 0))
            throw new InvalidDataException($"Compatible embedded table builds must be positive ({source}).");
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
