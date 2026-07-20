using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DesktopWorkspaceSession : IDisposable
{
    private ServerDatabaseConnectionSession? _serverDatabaseConnection;
    public DesktopSettings Settings { get; }
    public ServerWorkspace? Server { get; private set; }
    public DatabaseConnectionProfile? DatabaseProfile { get; private set; }
    public DatabaseCapabilities? DatabaseCapabilities { get; private set; }
    public bool DatabaseTested => DatabaseCapabilities is not null;
    public string DatabaseTransportDescription { get; private set; } = "Direct database connection";
    public string? LastError { get; private set; }
    public event EventHandler? Changed;

    public DesktopWorkspaceSession(DesktopSettings settings) => Settings = settings;

    public async Task DetectServerAndConnectAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        LastError = null;
        try
        {
            var server = await ServerWorkspaceDetector.DetectAsync(rootPath, cancellationToken);
            DisposeDatabaseTransport(); _serverDatabaseConnection = await ServerDatabaseConnectionSession.ConnectAsync(server, cancellationToken); var capabilities = _serverDatabaseConnection.Capabilities; DatabaseTransportDescription = _serverDatabaseConnection.TransportDescription;
            if (_serverDatabaseConnection.DirectFailure is { } directFailure) DesktopCrashLogger.Debug("SERVER", "wsl-database-bridge-ready", ("distribution", server.WslDistribution), ("transport", DatabaseTransportDescription), ("configured_host", server.WorldDatabase.Host), ("configured_port", server.WorldDatabase.Port), ("direct_error", directFailure));
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
            if (_serverDatabaseConnection is not null && (Server is null || !SameDatabaseEndpoint(profile, Server.WorldDatabase))) DisposeDatabaseTransport();
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

    public async Task RefreshDatabaseAsync(CancellationToken cancellationToken = default)
    {
        if (DatabaseProfile is null) throw new InvalidOperationException("No database profile is connected.");
        LastError = null;
        try
        {
            DatabaseCapabilities = await new DatabaseCapabilityService().InspectAsync(DatabaseProfile, cancellationToken);
            DesktopCrashLogger.Debug("SQL", "database-capabilities-refreshed", ("database", DatabaseCapabilities.Database), ("tables", DatabaseCapabilities.Tables.Count));
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            DesktopCrashLogger.Log($"Database capability refresh failed: {DatabaseProfile.Database}", exception);
            throw;
        }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    public DatabaseConnectionProfile SuggestedProfile(string password = "") => DatabaseProfile is { } active
        ? active with { Password = string.IsNullOrEmpty(password) ? active.Password : password }
        : new(Settings.DatabaseHost, Settings.DatabasePort, Settings.DatabaseUser, password, Settings.WorldDatabase,
            Enum.TryParse<MySqlSslMode>(Settings.DatabaseSslMode, true, out var ssl) ? ssl : MySqlSslMode.Preferred);

    public void Dispose() => DisposeDatabaseTransport();

    private void DisposeDatabaseTransport()
    {
        _serverDatabaseConnection?.Dispose(); _serverDatabaseConnection = null;
        DatabaseTransportDescription = "Direct database connection";
    }

    private static bool SameDatabaseEndpoint(DatabaseConnectionProfile left, DatabaseConnectionProfile right)
        => left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;
}
