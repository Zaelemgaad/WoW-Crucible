using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DesktopSettings
{
    public bool DevbugMode { get; set; }
    public string ServerRootPath { get; set; } = string.Empty;
    public string CoreDbcPath { get; set; } = string.Empty;
    public string ClientDataPath { get; set; } = string.Empty;
    public string ClientExecutablePath { get; set; } = string.Empty;
    public string ClientIndexPath { get; set; } = string.Empty;
    public string ProcessedAssetLibraryPath { get; set; } = string.Empty;
    public string CoreSourcePath { get; set; } = string.Empty;
    public string BaseDbcPath { get; set; } = string.Empty;
    public string OverrideDbcPath { get; set; } = string.Empty;
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
            var desktopSettingsExist = File.Exists(CruciblePaths.DesktopSettingsFile);
            var settings = desktopSettingsExist
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(CruciblePaths.DesktopSettingsFile)) ?? new()
                : new();
            if (File.Exists(CruciblePaths.SettingsFileForRead))
            {
                using var legacy = JsonDocument.Parse(File.ReadAllText(CruciblePaths.SettingsFileForRead)); var root = legacy.RootElement;
                settings.ServerRootPath = Fill(settings.ServerRootPath, root, "ServerRootPath"); settings.CoreDbcPath = Fill(settings.CoreDbcPath, root, "CoreDbcPath"); settings.ClientDataPath = Fill(settings.ClientDataPath, root, "ClientDataPath");
                settings.ClientExecutablePath = Fill(settings.ClientExecutablePath, root, "ClientExecutablePath"); settings.ClientIndexPath = Fill(settings.ClientIndexPath, root, "ClientIndexPath"); settings.CoreSourcePath = Fill(settings.CoreSourcePath, root, "CoreSourcePath"); settings.SchemaDefinitionPath = Fill(settings.SchemaDefinitionPath, root, "SchemaDefinitionPath");
                settings.ProcessedAssetLibraryPath = Fill(settings.ProcessedAssetLibraryPath, root, "ProcessedAssetLibraryPath");
                settings.BaseDbcPath = Fill(settings.BaseDbcPath, root, "BaseDbcPath"); settings.OverrideDbcPath = Fill(settings.OverrideDbcPath, root, "OverrideDbcPath");
                if (!desktopSettingsExist)
                {
                    settings.DatabaseHost = Text(root, "DatabaseHost", settings.DatabaseHost); settings.DatabaseUser = Text(root, "DatabaseUser"); settings.WorldDatabase = Text(root, "WorldDatabase", settings.WorldDatabase); settings.DatabaseSslMode = Text(root, "DatabaseSslMode", settings.DatabaseSslMode);
                    if (root.TryGetProperty("DatabasePort", out var port) && port.TryGetUInt32(out var value)) settings.DatabasePort = value;
                }
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
    private static string Fill(string target, JsonElement root, string name) => string.IsNullOrWhiteSpace(target) ? Text(root, name) : target;
}
