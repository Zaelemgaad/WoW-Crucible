using System.Text.Json;

namespace WoWCrucible.Core;

public enum ContentIdDomain { Item, ItemSet, Spell, CreatureTemplate, CreatureModelData, CreatureDisplayInfo, CreatureDisplayInfoExtra, GameObject, Race, Class, Faction, Mount, Quest, Custom }
public sealed record ContentIdReservation(string Id, ContentIdDomain Domain, IReadOnlyList<uint> Values, string Purpose, DateTimeOffset CreatedUtc);
public sealed record ContentIdRegistry(int FormatVersion, DateTimeOffset UpdatedUtc, IReadOnlyList<ContentIdReservation> Reservations);
public sealed record CrucibleContentProject(int FormatVersion, string Name, string TargetProfile, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc, string IdRegistryFile, string? AssetLibrary);

public static class CrucibleContentProjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static CrucibleContentProject Create(string rootPath, string name, string targetProfile = TargetProfileCatalog.DefaultProfileId, string? assetLibrary = null)
    {
        rootPath = Path.GetFullPath(rootPath); Directory.CreateDirectory(rootPath); var projectPath = Path.Combine(rootPath, "project.crucible.json");
        if (File.Exists(projectPath)) throw new IOException($"A Crucible content project already exists at {projectPath}");
        foreach (var directory in new[] { "Assets", "DBC", "SQL", "Manifests", "Reports", "Staging" }) Directory.CreateDirectory(Path.Combine(rootPath, directory));
        var now = DateTimeOffset.UtcNow; var project = new CrucibleContentProject(1, name.Trim(), targetProfile.Trim(), now, now, "ids.crucible.json", string.IsNullOrWhiteSpace(assetLibrary) ? null : Path.GetFullPath(assetLibrary));
        WriteAtomic(projectPath, project); WriteAtomic(Path.Combine(rootPath, project.IdRegistryFile), new ContentIdRegistry(1, now, [])); return project;
    }

    public static CrucibleContentProject Load(string projectOrRoot)
    {
        var path = ResolveProjectPath(projectOrRoot); var project = JsonSerializer.Deserialize<CrucibleContentProject>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The content project is empty.");
        if (project.FormatVersion != 1) throw new InvalidDataException($"Unsupported content project format: {project.FormatVersion}"); return project;
    }

    public static ContentIdRegistry LoadRegistry(string projectOrRoot)
    {
        var projectPath = ResolveProjectPath(projectOrRoot); var project = Load(projectPath); var path = Path.Combine(Path.GetDirectoryName(projectPath)!, project.IdRegistryFile);
        var registry = JsonSerializer.Deserialize<ContentIdRegistry>(File.ReadAllText(path), JsonOptions) ?? throw new InvalidDataException("The ID registry is empty.");
        if (registry.FormatVersion != 1) throw new InvalidDataException($"Unsupported ID registry format: {registry.FormatVersion}"); return registry;
    }

    public static (ContentIdRegistry Registry, ContentIdReservation Reservation) ReserveIds(string projectOrRoot, ContentIdDomain domain, int count, uint start, IEnumerable<uint> occupiedIds, string purpose)
    {
        if (count is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(count), "Reserve from 1 to 1,000,000 IDs at once.");
        var policy = ContentIdDomainCatalog.Get(domain); if (start == 0) start = policy.RecommendedStart;
        if (start > policy.Maximum) throw new ArgumentOutOfRangeException(nameof(start), $"{domain} IDs cannot begin at {start:N0}; the verified range ends at {policy.Maximum:N0}. {policy.Guidance}");
        var projectPath = ResolveProjectPath(projectOrRoot); var project = Load(projectPath); var registryPath = Path.Combine(Path.GetDirectoryName(projectPath)!, project.IdRegistryFile); var registry = LoadRegistry(projectPath);
        var occupied = occupiedIds.ToHashSet(); foreach (var existing in registry.Reservations.Where(reservation => ContentIdDomainCatalog.RegistryNamespace(reservation.Domain) == policy.RegistryNamespace)) occupied.UnionWith(existing.Values);
        var values = new uint[count]; var candidate = start;
        for (var index = 0; index < values.Length;)
        {
            if (!occupied.Contains(candidate)) { values[index++] = candidate; occupied.Add(candidate); }
            if (index < values.Length) { if (candidate == policy.Maximum) throw new OverflowException($"No room remains to reserve {count:N0} IDs in {domain} from {start:N0} through {policy.Maximum:N0}. {policy.Guidance}"); candidate++; }
        }
        var reservation = new ContentIdReservation(Guid.NewGuid().ToString("N"), domain, values, string.IsNullOrWhiteSpace(purpose) ? "Unspecified content" : purpose.Trim(), DateTimeOffset.UtcNow);
        var updated = registry with { UpdatedUtc = DateTimeOffset.UtcNow, Reservations = registry.Reservations.Append(reservation).ToArray() }; WriteAtomic(registryPath, updated); return (updated, reservation);
    }

    public static (ContentIdRegistry Registry, ContentIdReservation Reservation) ReserveVerifiedIds(string projectOrRoot, ContentIdOccupancyReport occupancy, int count, uint? start, string purpose)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        if (!occupancy.Complete) throw new InvalidOperationException($"The {occupancy.Domain} occupancy scan is incomplete; refusing to reserve potentially colliding IDs. {string.Join(" ", occupancy.Warnings)}");
        var policy = ContentIdDomainCatalog.Get(occupancy.Domain);
        if (occupancy.RegistryNamespace != policy.RegistryNamespace) throw new InvalidDataException($"The occupancy report namespace {occupancy.RegistryNamespace} does not match {policy.RegistryNamespace}.");
        return ReserveIds(projectOrRoot, occupancy.Domain, count, start ?? policy.RecommendedStart, occupancy.OccupiedIds, purpose);
    }

    public static IReadOnlyList<uint> ReadOccupiedIds(string path)
    {
        var result = new HashSet<uint>(); foreach (var line in File.ReadLines(Path.GetFullPath(path))) foreach (var token in line.Split([',', ';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)) if (uint.TryParse(token, out var value)) result.Add(value); return result.Order().ToArray();
    }

    private static string ResolveProjectPath(string path) { path = Path.GetFullPath(path); return Directory.Exists(path) ? Path.Combine(path, "project.crucible.json") : path; }
    private static void WriteAtomic<T>(string path, T value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions)); File.Move(temporary, path, true); }
}
