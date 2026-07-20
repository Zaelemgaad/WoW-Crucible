namespace WoWCrucible.Core;

public sealed record DatabaseConnectionEndpoint(string Host, uint Port, string Description);

/// <summary>
/// Keeps an installed server's declared database identity separate from a
/// process-local transport endpoint. This lets every existing SQL provider use
/// an app-owned WSL loopback bridge without serializing a volatile WSL address
/// into plans, receipts, favorites, or settings.
/// </summary>
public static class DatabaseConnectionTransportRegistry
{
    private sealed record Entry(Guid Token, DatabaseConnectionEndpoint Endpoint);
    private sealed class Registration(string key, Guid token) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (Gate) if (Entries.TryGetValue(key, out var entry) && entry.Token == token) Entries.Remove(key);
        }
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static IDisposable Register(DatabaseConnectionProfile profile, DatabaseConnectionEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(profile); ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is 0 or > 65535) throw new ArgumentException("A transport endpoint requires a host and valid TCP port.", nameof(endpoint));
        var key = Key(profile); var token = Guid.NewGuid(); lock (Gate) Entries[key] = new(token, endpoint); return new Registration(key, token);
    }

    public static DatabaseConnectionEndpoint Resolve(DatabaseConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile); lock (Gate) return Entries.TryGetValue(Key(profile), out var entry)
            ? entry.Endpoint
            : new(profile.Host, profile.Port, "Direct database connection");
    }

    // Transport belongs to the configured server endpoint, not one credential or
    // schema. SQL Studio may switch from world to auth/characters or use another
    // permitted login while it remains on the same WSL loopback listener.
    private static string Key(DatabaseConnectionProfile profile)
        => $"{profile.Host.Trim()}|{profile.Port}";
}
