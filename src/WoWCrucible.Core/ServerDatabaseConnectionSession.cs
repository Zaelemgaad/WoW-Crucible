namespace WoWCrucible.Core;

/// <summary>
/// Verifies an installed server's declared world database and owns any
/// process-local transport needed for a WSL loopback-only endpoint.
/// </summary>
public sealed class ServerDatabaseConnectionSession : IDisposable
{
    private readonly WslDatabaseLoopbackBridge? _bridge;
    private readonly IDisposable? _transportRegistration;
    private int _disposed;

    private ServerDatabaseConnectionSession(DatabaseCapabilities capabilities, WslDatabaseLoopbackBridge? bridge, IDisposable? transportRegistration, string? directFailure)
    {
        Capabilities = capabilities; _bridge = bridge; _transportRegistration = transportRegistration; DirectFailure = directFailure;
    }

    public DatabaseCapabilities Capabilities { get; }
    public string TransportDescription => _bridge?.Description ?? "Direct database connection";
    public string? DirectFailure { get; }

    public static async Task<ServerDatabaseConnectionSession> ConnectAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        try
        {
            var direct = await new DatabaseCapabilityService().InspectAsync(workspace.WorldDatabase, cancellationToken);
            return new(direct, null, null, null);
        }
        catch (Exception directException) when (directException is not OperationCanceledException && WslDatabaseLoopbackBridge.IsRequired(workspace))
        {
            WslDatabaseLoopbackBridge? bridge = null; IDisposable? registration = null;
            try
            {
                bridge = await WslDatabaseLoopbackBridge.StartAsync(workspace, cancellationToken);
                registration = DatabaseConnectionTransportRegistry.Register(workspace.WorldDatabase, new(bridge.Host, bridge.Port, bridge.Description));
                var capabilities = await new DatabaseCapabilityService().InspectAsync(workspace.WorldDatabase, cancellationToken);
                return new(capabilities, bridge, registration, directException.Message);
            }
            catch (Exception bridgeException) when (bridgeException is not OperationCanceledException)
            {
                registration?.Dispose(); bridge?.Dispose();
                throw new InvalidOperationException($"The WSL server database was unreachable directly ({directException.Message}) and Crucible could not establish its temporary loopback bridge ({bridgeException.Message}).", bridgeException);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _transportRegistration?.Dispose(); _bridge?.Dispose();
    }
}
