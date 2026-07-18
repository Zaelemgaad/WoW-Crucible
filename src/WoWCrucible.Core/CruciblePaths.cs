namespace WoWCrucible.Core;

public static class CruciblePaths
{
    private const string ProductFolder = "WoWCrucible";
    public static string ApplicationDirectory { get; } = ResolveApplicationDirectory();
    public static string DataRoot { get; } = ResolveDataRoot();
    public static bool IsPortable => DataRoot.Equals(ApplicationDirectory, StringComparison.OrdinalIgnoreCase);
    public static string SettingsDirectory => Path.Combine(DataRoot, "Settings");
    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    public static string DesktopSettingsFile => Path.Combine(SettingsDirectory, "desktop.json");
    public static string SqlFavoritesFile => Path.Combine(SettingsDirectory, "sql-favorites.json");
    public static string ProfilesDirectory => Path.Combine(DataRoot, "Profiles");
    public static string LogDirectory => Path.Combine(DataRoot, "Logs");
    public static string CrashLogDirectory => Path.Combine(LogDirectory, "Crashes");
    public static string DebugLogDirectory => Path.Combine(LogDirectory, "Debug");
    public static string LegacySettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder, "settings.json");

    public static string SettingsFileForRead => File.Exists(SettingsFile) ? SettingsFile : LegacySettingsFile;

    private static string ResolveApplicationDirectory()
    {
        var processPath = Environment.ProcessPath;
        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(processPath) && !string.IsNullOrWhiteSpace(entryName)
            && Path.GetFileNameWithoutExtension(processPath).Equals(entryName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.GetDirectoryName(processPath)!);
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

    private static string ResolveDataRoot()
    {
        try
        {
            Directory.CreateDirectory(ApplicationDirectory);
            var probe = Path.Combine(ApplicationDirectory, $".crucible-write-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return ApplicationDirectory;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder);
        }
    }
}
