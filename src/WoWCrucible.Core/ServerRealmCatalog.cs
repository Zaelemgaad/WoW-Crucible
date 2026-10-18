using MySqlConnector;

namespace WoWCrucible.Core;

public sealed record ServerRealmSource(uint Id, string Name, string Profile, ServerWorkspace Server);
public sealed record ServerRealmCatalog(IReadOnlyList<ServerRealmSource> Realms, IReadOnlyList<string> Notices)
{
    // Installed realm roots, not source-tree templates or archived configurations.
    public static async Task<ServerRealmCatalog> ReadAsync(string serverRoot, CancellationToken token = default)
    {
        if (!Directory.Exists(serverRoot)) return new([], []);
        var roots = new List<string> { Path.GetFullPath(serverRoot) };
        var children = Path.Combine(serverRoot, "realms");
        if (Directory.Exists(children)) roots.AddRange(Directory.EnumerateDirectories(children));
        var realms = new List<ServerRealmSource>();
        var notices = new List<string>();
        DatabaseConnectionProfile? auth = null;
        foreach (var root in roots)
        {
            var directory = Directory.Exists(Path.Combine(root, "configs")) ? Path.Combine(root, "configs") : root;
            foreach (var config in Directory.EnumerateFiles(directory, "worldserver*.conf"))
            {
                var text = File.ReadAllText(config);
                var values = ServerWorkspaceDetector.ParseSettings(text);
                if (!values.TryGetValue("RealmID", out var rawId) || !uint.TryParse(rawId, out var id)) continue;
                var server = ServerWorkspaceDetector.Parse(root, config, text);
                var module = Path.Combine(directory, "modules", "mod_ascension_compat.conf");
                var profile = File.Exists(module) ? ServerWorkspaceDetector.ParseSettings(File.ReadAllText(module)).GetValueOrDefault("Ascension.RealmProfile", "") : "";
                realms.Add(new(id, $"Realm {id} ({server.WorldDatabase.Database})", profile, server));
                if (auth is null && values.TryGetValue("LoginDatabaseInfo", out var login))
                    auth = ServerWorkspaceDetector.Parse(root, config, "WorldDatabaseInfo = \"" + login + "\"").WorldDatabase;
            }
        }
        if (realms.GroupBy(r => r.Id).Any(g => g.Count() > 1))
            throw new InvalidDataException("Multiple installed world configurations use the same RealmID. Select the intended server root.");
        if (auth is not null)
        {
            try
            {
                await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(auth));
                await connection.OpenAsync(token);
                await using var command = new MySqlCommand("SELECT id, name FROM realmlist", connection) { CommandTimeout = 5 };
                await using var reader = await command.ExecuteReaderAsync(token);
                var names = new Dictionary<uint, string>();
                while (await reader.ReadAsync(token)) names[reader.GetFieldValue<uint>(0)] = reader.GetString(1);
                realms = realms.Select(r => names.TryGetValue(r.Id, out var name) ? r with { Name = name } : r).ToList();
            }
            catch (MySqlException ex) { notices.Add($"Realm names could not be read from the login database: {ex.Message}"); }
        }
        return new(realms.OrderBy(r => r.Id).ToArray(), notices);
    }
}
