using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DesktopSettings
{
    public bool DevbugMode { get; set; }
    public string ServerRootPath { get; set; } = string.Empty;
    public string CoreDbcPath { get; set; } = string.Empty;
    public string ClientDataPath { get; set; } = string.Empty;
    public string SchemaDefinitionPath { get; set; } = string.Empty;
    public string DatabaseHost { get; set; } = "127.0.0.1";
    public uint DatabasePort { get; set; } = 3306;
    public string DatabaseUser { get; set; } = string.Empty;
    public string WorldDatabase { get; set; } = "acore_world";
    public string DatabaseSslMode { get; set; } = "Preferred";

    public static DesktopSettings Load()
    {
        try
        {
            var settings = File.Exists(CruciblePaths.DesktopSettingsFile)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(CruciblePaths.DesktopSettingsFile)) ?? new()
                : new();
            if (string.IsNullOrWhiteSpace(settings.ServerRootPath) && File.Exists(CruciblePaths.SettingsFileForRead))
            {
                using var legacy = JsonDocument.Parse(File.ReadAllText(CruciblePaths.SettingsFileForRead)); var root = legacy.RootElement;
                settings.ServerRootPath = Text(root, "ServerRootPath"); settings.CoreDbcPath = Text(root, "CoreDbcPath"); settings.ClientDataPath = Text(root, "ClientDataPath"); settings.SchemaDefinitionPath = Text(root, "SchemaDefinitionPath");
                settings.DatabaseHost = Text(root, "DatabaseHost", settings.DatabaseHost); settings.DatabaseUser = Text(root, "DatabaseUser"); settings.WorldDatabase = Text(root, "WorldDatabase", settings.WorldDatabase); settings.DatabaseSslMode = Text(root, "DatabaseSslMode", settings.DatabaseSslMode);
                if (root.TryGetProperty("DatabasePort", out var port) && port.TryGetUInt32(out var value)) settings.DatabasePort = value;
            }
            return settings;
        }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(CruciblePaths.SettingsDirectory);
        var temporary = CruciblePaths.DesktopSettingsFile + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, CruciblePaths.DesktopSettingsFile, true);
    }

    private static string Text(JsonElement root, string name, string fallback = "") => root.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
}
