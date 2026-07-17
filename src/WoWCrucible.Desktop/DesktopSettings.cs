using System.Text.Json;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DesktopSettings
{
    public bool DevbugMode { get; set; }

    public static DesktopSettings Load()
    {
        try
        {
            return File.Exists(CruciblePaths.DesktopSettingsFile)
                ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(CruciblePaths.DesktopSettingsFile)) ?? new()
                : new();
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
}
