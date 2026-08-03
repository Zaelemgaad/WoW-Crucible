namespace WoWCrucible.Core;

public static class CruciblePaths
{
    private const string ProductFolder = "WoWCrucible";
    public static string ApplicationDirectory { get; } = ResolveApplicationDirectory();
    // Crucible is deliberately portable: every file it owns lives beside the
    // executable. If that directory is not writable, saving must fail clearly
    // instead of silently scattering state through AppData or a workspace.
    public static string DataRoot => ApplicationDirectory;
    public static bool IsPortable => true;
    public static string SettingsDirectory => Path.Combine(DataRoot, "Config");
    public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");
    public static string DesktopSettingsFile => Path.Combine(SettingsDirectory, "desktop.json");
    public static string SqlFavoritesFile => Path.Combine(SettingsDirectory, "sql-favorites.json");
    public static string SqlQueryHistoryFile => Path.Combine(SettingsDirectory, "sql-query-history.json");
    public static string ProfilesDirectory => Path.Combine(DataRoot, "Profiles");
    public static string WorkspaceProfilesDirectory => Path.Combine(SettingsDirectory, "Workspaces");
    public static string TableIdentityDirectory => Path.Combine(SettingsDirectory, "TableIdentities");
    public static string LogDirectory => Path.Combine(DataRoot, "Logs");
    public static string CrashLogDirectory => Path.Combine(LogDirectory, "Crashes");
    public static string DebugLogDirectory => Path.Combine(LogDirectory, "Debug");
    public static string CacheDirectory => Path.Combine(DataRoot, "Cache");
    public static string MpqIndexCacheDirectory => Path.Combine(CacheDirectory, "MPQ");
    public static string BackupDirectory => Path.Combine(DataRoot, "Backups");
    public static string SqlSchemaBackupDirectory => Path.Combine(BackupDirectory, "SqlSchema");
    public static string TransactionDirectory => Path.Combine(CacheDirectory, "Transactions");
    public static string CacheServerPlanDirectory => Path.Combine(DataRoot, "Plans", "CacheServer");
    public static string CacheServerReceiptDirectory => Path.Combine(DataRoot, "Receipts", "CacheServer");
    public static string ReceiptDirectory => Path.Combine(DataRoot, "Receipts");
    public static string LegacySettingsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolder, "settings.json");
    public static string LegacyPortableSettingsFile => Path.Combine(ApplicationDirectory, "Settings", "settings.json");
    public static string LegacyPortableDesktopSettingsFile => Path.Combine(ApplicationDirectory, "Settings", "desktop.json");

    public static string SettingsFileForRead => File.Exists(SettingsFile) ? SettingsFile : File.Exists(LegacyPortableSettingsFile) ? LegacyPortableSettingsFile : LegacySettingsFile;

    private static string ResolveApplicationDirectory()
    {
        var processPath = Environment.ProcessPath;
        var entryName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        if (!string.IsNullOrWhiteSpace(processPath) && !string.IsNullOrWhiteSpace(entryName)
            && Path.GetFileNameWithoutExtension(processPath).Equals(entryName, StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(Path.GetDirectoryName(processPath)!);
        return Path.GetFullPath(AppContext.BaseDirectory);
    }

}
