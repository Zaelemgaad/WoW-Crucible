using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DesktopWorkspaceSession
{
    public DesktopSettings Settings { get; }
    public ServerWorkspace? Server { get; private set; }
    public DatabaseConnectionProfile? DatabaseProfile { get; private set; }
    public DatabaseCapabilities? DatabaseCapabilities { get; private set; }
    public bool DatabaseTested => DatabaseCapabilities is not null;
    public string? LastError { get; private set; }
    public event EventHandler? Changed;

    public DesktopWorkspaceSession(DesktopSettings settings) => Settings = settings;

    public async Task DetectServerAndConnectAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        LastError = null;
        try
        {
            var server = await ServerWorkspaceDetector.DetectAsync(rootPath, cancellationToken);
            var capabilities = await new DatabaseCapabilityService().InspectAsync(server.WorldDatabase, cancellationToken);
            Server = server; DatabaseProfile = server.WorldDatabase; DatabaseCapabilities = capabilities;
            Settings.ServerRootPath = server.RootPath; Settings.CoreDbcPath = server.DbcPath;
            Settings.DatabaseHost = server.WorldDatabase.Host; Settings.DatabasePort = server.WorldDatabase.Port; Settings.DatabaseUser = server.WorldDatabase.User; Settings.WorldDatabase = server.WorldDatabase.Database; Settings.DatabaseSslMode = server.WorldDatabase.SslMode.ToString();
            Settings.Save();
            DesktopCrashLogger.Debug("SERVER", "workspace-connected", ("root", server.RootPath), ("core", server.CoreFamily), ("wsl", server.UsesWsl), ("database", capabilities.Database), ("tables", capabilities.Tables.Count));
        }
        catch (Exception exception)
        {
            LastError = exception.Message; DatabaseCapabilities = null;
            DesktopCrashLogger.Log($"Server workspace connection failed: {rootPath}", exception);
            throw;
        }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task TestManualDatabaseAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default)
    {
        LastError = null;
        try
        {
            var capabilities = await new DatabaseCapabilityService().InspectAsync(profile, cancellationToken);
            DatabaseProfile = profile; DatabaseCapabilities = capabilities;
            Settings.DatabaseHost = profile.Host; Settings.DatabasePort = profile.Port; Settings.DatabaseUser = profile.User; Settings.WorldDatabase = profile.Database; Settings.DatabaseSslMode = profile.SslMode.ToString(); Settings.Save();
            DesktopCrashLogger.Debug("SQL", "manual-connection-tested", ("host", profile.Host), ("port", profile.Port), ("database", profile.Database), ("version", capabilities.ServerVersion), ("tables", capabilities.Tables.Count));
        }
        catch (Exception exception)
        {
            LastError = exception.Message; DatabaseCapabilities = null;
            DesktopCrashLogger.Log($"Manual SQL connection failed: {profile.Host}:{profile.Port}/{profile.Database}", exception);
            throw;
        }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    public DatabaseConnectionProfile SuggestedProfile(string password = "") => DatabaseProfile is { } active
        ? active with { Password = string.IsNullOrEmpty(password) ? active.Password : password }
        : new(Settings.DatabaseHost, Settings.DatabasePort, Settings.DatabaseUser, password, Settings.WorldDatabase,
            Enum.TryParse<MySqlSslMode>(Settings.DatabaseSslMode, true, out var ssl) ? ssl : MySqlSslMode.Preferred);
}
