using System.Text.Json;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientTableFormat { None = 0, Wdbc = 1, Db2 = 2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArchiveFormat { Mpq }

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
    public bool SupportsWdbc => TableFormats.HasFlag(ClientTableFormat.Wdbc);
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
            "Cata uses a mixture of WDBC and DB2-era tables. WDBC files may be opened; DB2 editing is not implemented yet.")
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
    }

    private static JsonSerializerOptions JsonOptions(bool indented = false) => new()
    {
        WriteIndented = indented,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
