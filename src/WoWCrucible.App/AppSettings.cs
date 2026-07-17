using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.App;

public sealed class AppSettings
{
    private static string SettingsPath => CruciblePaths.SettingsFile;

    public string CoreDbcPath { get; set; } = string.Empty;
    public string ClientDataPath { get; set; } = string.Empty;
    public string ClientExecutablePath { get; set; } = string.Empty;
    public string ClientIndexPath { get; set; } = string.Empty;
    public string SchemaDefinitionPath { get; set; } = string.Empty;
    public string BaseDbcPath { get; set; } = string.Empty;
    public string OverrideDbcPath { get; set; } = string.Empty;
    public string SelectedTargetProfileId { get; set; } = "wotlk-12340";
    public string DatabaseHost { get; set; } = "127.0.0.1";
    public uint DatabasePort { get; set; } = 3306;
    public string DatabaseUser { get; set; } = string.Empty;
    public string WorldDatabase { get; set; } = "acore_world";
    public string DatabaseSslMode { get; set; } = "Preferred";
    public string ServerRootPath { get; set; } = string.Empty;
    public string CoreSourcePath { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try
        {
            var readPath = CruciblePaths.SettingsFileForRead;
            var settings = File.Exists(readPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(readPath)) ?? new() : new();
            if (!readPath.Equals(SettingsPath, StringComparison.OrdinalIgnoreCase) && File.Exists(readPath)) settings.Save();
            return settings;
        }
        catch (Exception ex) { CrashLogger.Log("Could not load settings", ex); return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
