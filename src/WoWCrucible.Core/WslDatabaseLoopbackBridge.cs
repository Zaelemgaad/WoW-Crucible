using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace WoWCrucible.Core;

/// <summary>
/// Provides one app-owned, ephemeral TCP route from the Windows process to a
/// database that deliberately listens only on 127.0.0.1 inside WSL. It does not
/// alter mysqld configuration, Windows portproxy state, firewall rules, or the
/// installed server's worldserver.conf.
/// </summary>
public sealed class WslDatabaseLoopbackBridge : IDisposable
{
    private const string RelayProgram = """
        import socket, sys, threading

        token = sys.argv[1]
        listen_host = sys.argv[2]
        target_host = sys.argv[3]
        target_port = int(sys.argv[4])

        def pump(source, target):
            try:
                while True:
                    data = source.recv(65536)
                    if not data:
                        break
                    target.sendall(data)
            except OSError:
                pass
            finally:
                try:
                    target.shutdown(socket.SHUT_WR)
                except OSError:
                    pass

        def handle(client):
            try:
                server = socket.create_connection((target_host, target_port))
            except OSError:
                client.close()
                return
            threading.Thread(target=pump, args=(client, server), daemon=True).start()
            threading.Thread(target=pump, args=(server, client), daemon=True).start()

        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind((listen_host, 0))
        listener.listen(32)
        print("READY " + str(listener.getsockname()[1]), flush=True)
        while True:
            client, _ = listener.accept()
            handle(client)
        """;

    private readonly Process _process;
    private readonly string _distribution;
    private readonly string _token;
    private int _disposed;

    private WslDatabaseLoopbackBridge(Process process, string distribution, string token, string host, uint port)
    {
        _process = process; _distribution = distribution; _token = token; Host = host; Port = port;
    }

    public string Host { get; }
    public uint Port { get; }
    public string Description => $"Ephemeral WSL loopback bridge · {Host}:{Port} → 127.0.0.1";

    public static bool IsRequired(ServerWorkspace workspace)
        => workspace.UsesWsl && !string.IsNullOrWhiteSpace(workspace.WslDistribution) && IsLoopback(workspace.WorldDatabase.Host);

    public static async Task<WslDatabaseLoopbackBridge> StartAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A WSL database bridge can only be created by the Windows desktop application.");
        if (!IsRequired(workspace)) throw new InvalidOperationException("This workspace does not declare a WSL loopback-only database target.");
        var distribution = workspace.WslDistribution!; var host = await ResolveDistributionAddressAsync(distribution, cancellationToken); var token = $"crucible-mysql-bridge-{Guid.NewGuid():N}";
        var start = new ProcessStartInfo
        {
            FileName = "wsl.exe", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-d", distribution, "-u", "root", "--", "python3", "-u", "-c", RelayProgram, token, host, "127.0.0.1", workspace.WorldDatabase.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("WSL did not start the database bridge process.");
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); readiness.CancelAfter(TimeSpan.FromSeconds(10));
            var lineTask = process.StandardOutput.ReadLineAsync(readiness.Token).AsTask(); var exitTask = process.WaitForExitAsync(readiness.Token);
            var completed = await Task.WhenAny(lineTask, exitTask);
            if (completed == exitTask || process.HasExited)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken); throw new InvalidOperationException($"WSL database bridge exited before it became ready: {error.Trim()}");
            }
            var line = await lineTask; if (line is null || !line.StartsWith("READY ", StringComparison.Ordinal) || !uint.TryParse(line[6..], out var port) || port is 0 or > 65535) throw new InvalidOperationException($"WSL database bridge returned an invalid readiness response: {line ?? "<none>"}");
            await VerifyAsync(host, port, readiness.Token);
            return new(process, distribution, token, host, port);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose(); throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        try { _process.WaitForExit(2_000); } catch { }
        _process.Dispose();
        try
        {
            using var cleanup = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl.exe", UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "-d", _distribution, "-u", "root", "--", "pkill", "-f", _token }
            });
            cleanup?.WaitForExit(2_000);
        }
        catch { }
    }

    private static async Task<string> ResolveDistributionAddressAsync(string distribution, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "wsl.exe", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var value in new[] { "-d", distribution, "--", "hostname", "-I" }) process.StartInfo.ArgumentList.Add(value);
        process.Start(); var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken); var errorTask = process.StandardError.ReadToEndAsync(cancellationToken); await process.WaitForExitAsync(cancellationToken); var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"WSL could not report its current network address: {error.Trim()}");
        var address = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(value => IPAddress.TryParse(value, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(parsed));
        return address ?? throw new InvalidOperationException("WSL reported no host-reachable IPv4 address for its loopback database bridge.");
    }

    private static async Task VerifyAsync(string host, uint port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(); await client.ConnectAsync(host, checked((int)port), cancellationToken);
    }

    private static bool IsLoopback(string host)
    {
        if (host.Trim() is "." or "localhost") return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
