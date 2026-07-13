using System.Text.Json;

namespace WoWCrucible.App;

public sealed class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WoWCrucible", "settings.json");

    public string CoreDbcPath { get; set; } = string.Empty;
    public string ClientDataPath { get; set; } = string.Empty;
    public string ClientExecutablePath { get; set; } = string.Empty;
    public string SchemaDefinitionPath { get; set; } = string.Empty;
    public string BaseDbcPath { get; set; } = string.Empty;
    public string OverrideDbcPath { get; set; } = string.Empty;

    public static AppSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) { CrashLogger.Log("Could not load settings", ex); return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
