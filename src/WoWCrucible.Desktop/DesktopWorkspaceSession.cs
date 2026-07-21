using MySqlConnector;
using WoWCrucible.Core;

namespace WoWCrucible.Desktop;

internal sealed class DesktopWorkspaceSession : IDisposable
{
    private ServerDatabaseConnectionSession? _serverDatabaseConnection;
    public DesktopSettings Settings { get; }
    public ServerLifecycleService Lifecycle { get; } = new();
    public CrucibleWorkspaceLayout? WorkspaceLayout { get; private set; }
    public ServerWorkspace? Server { get; private set; }
    public DatabaseConnectionProfile? DatabaseProfile { get; private set; }
    public DatabaseCapabilities? DatabaseCapabilities { get; private set; }
    public bool DatabaseTested => DatabaseCapabilities is not null;
    public string DatabaseTransportDescription { get; private set; } = "Direct database connection";
    public string? LastError { get; private set; }
    public event EventHandler? Changed;

    public DesktopWorkspaceSession(DesktopSettings settings) => Settings = settings;

    public async Task ConfigureWorkspaceAsync(CrucibleWorkspaceLayout layout, CancellationToken cancellationToken = default)
    {
        CrucibleWorkspaceLayoutService.Save(layout);
        WorkspaceLayout = layout;
        Settings.WorkspaceRootPath = layout.RootPath;
        Settings.WorkspaceName = layout.Name;
        SetIfPresent(value => Settings.ServerRootPath = value, layout.ServerRootPath);
        SetIfPresent(value => Settings.CoreSourcePath = value, layout.CoreSourcePath);
        SetIfPresent(value => Settings.ClientDataPath = value, layout.ClientDataPath);
        SetIfPresent(value => Settings.ClientExecutablePath = value, layout.ClientExecutablePath);
        SetIfPresent(value => Settings.CoreDbcPath = value, layout.CoreDbcPath);
        SetIfPresent(value => Settings.SchemaDefinitionPath = value, layout.SchemaDefinitionPath);
        SetIfPresent(value => Settings.DbdDefinitionsPath = value, layout.DbdDefinitionsPath);
        SetIfPresent(value => Settings.ProcessedAssetLibraryPath = value, layout.ProcessedAssetLibraryPath);
        SetIfPresent(value => Settings.WorkspaceProjectsPath = value, layout.ProjectsPath);
        SetIfPresent(value => Settings.WorkspaceStagingPath = value, layout.StagingPath);
        SetIfPresent(value => Settings.ToolsPath = value, layout.ToolsPath);
        SetIfPresent(value => Settings.NoggitExecutablePath = value, layout.NoggitExecutablePath);
        SetIfPresent(value => Settings.MapSourcePath = value, layout.MapSourcePath);
        Settings.Save();

        if (!string.IsNullOrWhiteSpace(layout.ServerRootPath) && Directory.Exists(layout.ServerRootPath))
            await DetectServerAndConnectAsync(layout.ServerRootPath, cancellationToken);
        else Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task DetectServerAndConnectAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        LastError = null;
        try
        {
            var server = await ServerWorkspaceDetector.DetectAsync(rootPath, cancellationToken);
            DisposeDatabaseTransport();
            Server = server; DatabaseProfile = server.WorldDatabase; DatabaseCapabilities = null;
            Settings.ServerRootPath = server.RootPath; Settings.CoreDbcPath = server.DbcPath;
            Settings.DatabaseHost = server.WorldDatabase.Host; Settings.DatabasePort = server.WorldDatabase.Port; Settings.DatabaseUser = server.WorldDatabase.User; Settings.WorldDatabase = server.WorldDatabase.Database; Settings.DatabaseSslMode = server.WorldDatabase.SslMode.ToString();
            Settings.Save();
            Changed?.Invoke(this, EventArgs.Empty);
            _serverDatabaseConnection = await ServerDatabaseConnectionSession.ConnectAsync(server, cancellationToken); var capabilities = _serverDatabaseConnection.Capabilities; DatabaseTransportDescription = _serverDatabaseConnection.TransportDescription;
            if (_serverDatabaseConnection.DirectFailure is { } directFailure) DesktopCrashLogger.Debug("SERVER", "wsl-database-bridge-ready", ("distribution", server.WslDistribution), ("transport", DatabaseTransportDescription), ("configured_host", server.WorldDatabase.Host), ("configured_port", server.WorldDatabase.Port), ("direct_error", directFailure));
            DatabaseCapabilities = capabilities;
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

    public void Dispose() { DisposeDatabaseTransport(); Lifecycle.Dispose(); }

    private void DisposeDatabaseTransport()
    {
        _serverDatabaseConnection?.Dispose(); _serverDatabaseConnection = null;
        DatabaseTransportDescription = "Direct database connection";
    }

    private static bool SameDatabaseEndpoint(DatabaseConnectionProfile left, DatabaseConnectionProfile right)
        => left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static void SetIfPresent(Action<string> setter, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) setter(value);
    }
}
