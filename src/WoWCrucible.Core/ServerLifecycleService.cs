using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;

namespace WoWCrucible.Core;

public sealed record ServerRuntimeStatus(bool WorldServerRunning, bool AuthServerRunning, bool DatabaseRunning, string Detail);
public sealed record ServerLifecycleResult(string Action, ServerRuntimeStatus Status, string Detail);

/// <summary>
/// Owns server processes started through Crucible and performs graceful worldserver shutdowns. WSL servers are
/// controlled directly through process signals; no workspace PowerShell wrappers are executed or required.
/// </summary>
public sealed class ServerLifecycleService : IDisposable
{
    private readonly ConcurrentDictionary<string, Process> _managed = new(StringComparer.OrdinalIgnoreCase);
    private string? _managedDatabaseService;

    public Task<ServerRuntimeStatus> GetStatusAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default) =>
        workspace.UsesWsl ? GetWslStatusAsync(workspace, cancellationToken) : GetLocalStatusAsync(workspace, cancellationToken);

    public async Task<ServerLifecycleResult> StartDatabaseAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.UsesWsl) await RunWslAsync(workspace, ["service", "mysql", "start"], cancellationToken, allowNonZero: false);
        else await StartLocalDatabaseAsync(workspace, cancellationToken);
        var status = await WaitForDatabaseStateAsync(workspace, true, TimeSpan.FromSeconds(20), cancellationToken);
        return new("Start database", status, "Database start request completed; authserver and worldserver were left unchanged.");
    }

    public async Task<ServerLifecycleResult> StopDatabaseAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var before = await GetStatusAsync(workspace, cancellationToken);
        if (before.WorldServerRunning || before.AuthServerRunning)
            throw new InvalidOperationException("Stop worldserver and authserver before stopping SQL. Crucible will not cut their database connection underneath them.");
        if (workspace.UsesWsl) await RunWslAsync(workspace, ["service", "mysql", "stop"], cancellationToken, allowNonZero: false);
        else
        {
            if (string.IsNullOrWhiteSpace(_managedDatabaseService))
                throw new InvalidOperationException("This local database service was not started by this Crucible session, so Crucible will not stop it blindly.");
            await RunLocalCommandAsync("sc.exe", ["stop", _managedDatabaseService], cancellationToken, allowNonZero: false);
        }
        var status = await WaitForDatabaseStateAsync(workspace, false, TimeSpan.FromSeconds(20), cancellationToken);
        _managedDatabaseService = null;
        return new("Stop database", status, "Database stopped after confirming authserver and worldserver were offline.");
    }

    public async Task<ServerLifecycleResult> StartAuthAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var before = await GetStatusAsync(workspace, cancellationToken);
        if (!before.DatabaseRunning) throw new InvalidOperationException("SQL is stopped. Start SQL first so authserver does not enter a failed startup loop.");
        if (workspace.UsesWsl)
        {
            await StartWslServerAsync(workspace, "authserver", workspace.WslAuthExecutable, workspace.WslAuthConfigPath, TimeSpan.FromSeconds(20), cancellationToken);
        }
        else { StartLocal(workspace, "authserver"); await Task.Delay(750, cancellationToken); }
        var status = await GetStatusAsync(workspace, cancellationToken);
        if (!status.AuthServerRunning) throw new InvalidOperationException("authserver exited during startup. Review its Crucible startup log for the exact core error.");
        return new("Start authserver", status, "Authserver is running; SQL and worldserver were left unchanged.");
    }

    public async Task<ServerLifecycleResult> StopAuthAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.UsesWsl)
        {
            if (await WslProcessExistsAsync(workspace, "authserver", cancellationToken))
            {
                await RunWslAsync(workspace, ["pkill", "-INT", "-x", "authserver"], cancellationToken, allowNonZero: true);
                await WaitForWslStateAsync(workspace, "authserver", false, TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        else await StopManagedAsync("authserver", cancellationToken);
        return new("Stop authserver", await GetStatusAsync(workspace, cancellationToken), "Authserver stopped; SQL and worldserver were left unchanged.");
    }

    public async Task<ServerLifecycleResult> StartAllAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var status = await GetStatusAsync(workspace, cancellationToken);
        if (!status.DatabaseRunning) await StartDatabaseAsync(workspace, cancellationToken);
        status = await GetStatusAsync(workspace, cancellationToken);
        if (!status.AuthServerRunning) await StartAuthAsync(workspace, cancellationToken);
        status = await GetStatusAsync(workspace, cancellationToken);
        if (!status.WorldServerRunning) await StartWorldAsync(workspace, cancellationToken);
        status = await GetStatusAsync(workspace, cancellationToken);
        return new("Start all", status, "Database, authserver, and worldserver start requests completed.");
    }

    public async Task<ServerLifecycleResult> StartWorldAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var before = await GetStatusAsync(workspace, cancellationToken);
        if (!before.DatabaseRunning) throw new InvalidOperationException("SQL is stopped. Start SQL first so worldserver does not enter a failed startup loop.");
        if (workspace.UsesWsl)
        {
            await StartWslServerAsync(workspace, "worldserver", workspace.WslWorldExecutable, workspace.WslConfigPath, TimeSpan.FromSeconds(30), cancellationToken);
        }
        else
        {
            StartLocal(workspace, "worldserver");
            await Task.Delay(750, cancellationToken);
        }
        var status = await GetStatusAsync(workspace, cancellationToken);
        if (!status.WorldServerRunning) throw new InvalidOperationException("worldserver exited during startup. Review its Crucible startup log for the exact core error.");
        return new("Start worldserver", status, "Worldserver is running; authserver was left unchanged.");
    }

    public async Task<ServerLifecycleResult> StopWorldAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.UsesWsl)
        {
            if (await WslProcessExistsAsync(workspace, "worldserver", cancellationToken))
            {
                await RunWslAsync(workspace, ["pkill", "-INT", "-x", "worldserver"], cancellationToken, allowNonZero: true);
                await WaitForWslStateAsync(workspace, "worldserver", false, TimeSpan.FromSeconds(60), cancellationToken);
            }
        }
        else await StopManagedWorldAsync(cancellationToken);
        return new("Graceful worldserver stop", await GetStatusAsync(workspace, cancellationToken), "Worldserver exited after its graceful save/shutdown path; authserver and database were left unchanged.");
    }

    public async Task<ServerLifecycleResult> StopAllAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        await StopWorldAsync(workspace, cancellationToken);
        if (workspace.UsesWsl)
        {
            if (await WslProcessExistsAsync(workspace, "authserver", cancellationToken))
            {
                await RunWslAsync(workspace, ["pkill", "-INT", "-x", "authserver"], cancellationToken, allowNonZero: true);
                await WaitForWslStateAsync(workspace, "authserver", false, TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        else await StopManagedAsync("authserver", cancellationToken);
        return new("Graceful stop all", await GetStatusAsync(workspace, cancellationToken), "Worldserver completed its save/shutdown path before authserver was stopped. The database remains available for editing.");
    }

    public async Task<ServerLifecycleResult> RestartWorldAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        await StopWorldAsync(workspace, cancellationToken);
        var started = await StartWorldAsync(workspace, cancellationToken);
        return started with { Action = "Graceful worldserver restart", Detail = "Worldserver saved and exited before a new process was started; authserver remained online." };
    }

    private async Task<ServerRuntimeStatus> GetWslStatusAsync(ServerWorkspace workspace, CancellationToken cancellationToken)
    {
        var world = await WslProcessExistsAsync(workspace, "worldserver", cancellationToken);
        var auth = await WslProcessExistsAsync(workspace, "authserver", cancellationToken);
        var mysql = (await RunWslAsync(workspace, ["service", "mysql", "status"], cancellationToken, allowNonZero: true)).ExitCode == 0;
        return new(world, auth, mysql, $"WSL {workspace.WslDistribution} · worldserver {(world ? "running" : "stopped")} · authserver {(auth ? "running" : "stopped")} · MySQL {(mysql ? "running" : "stopped")}");
    }

    private async Task<ServerRuntimeStatus> GetLocalStatusAsync(ServerWorkspace workspace, CancellationToken cancellationToken)
    {
        static bool Running(string name) => Process.GetProcessesByName(name).Any(process => { using (process) return !process.HasExited; });
        var world = Running("worldserver");
        var auth = Running("authserver");
        var database = await CanConnectAsync(workspace.WorldDatabase.Host, workspace.WorldDatabase.Port, cancellationToken);
        return new(world, auth, database, $"Local · worldserver {(world ? "running" : "stopped")} · authserver {(auth ? "running" : "stopped")} · SQL {(database ? "reachable" : "stopped/unreachable")}");
    }

    private async Task StartLocalDatabaseAsync(ServerWorkspace workspace, CancellationToken cancellationToken)
    {
        if (await CanConnectAsync(workspace.WorldDatabase.Host, workspace.WorldDatabase.Port, cancellationToken)) return;
        var query = await RunLocalCommandAsync("sc.exe", ["query", "type=", "service", "state=", "all"], cancellationToken, allowNonZero: false);
        var candidates = query.Output.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.Trim()).Where(line => line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[(line.IndexOf(':') + 1)..].Trim())
            .Where(name => name.Contains("mysql", StringComparison.OrdinalIgnoreCase) || name.Contains("maria", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("No installed Windows MySQL/MariaDB service was found. Start the custom database host manually, or use the WSL server layout.");
        Exception? failure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                await RunLocalCommandAsync("sc.exe", ["start", candidate], cancellationToken, allowNonZero: false);
                _managedDatabaseService = candidate;
                return;
            }
            catch (Exception exception) { failure = exception; }
        }
        throw new InvalidOperationException("Windows found MySQL/MariaDB services, but none could be started.", failure);
    }

    private async Task<ServerRuntimeStatus> WaitForDatabaseStateAsync(ServerWorkspace workspace, bool running, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            var status = await GetStatusAsync(workspace, cancellationToken);
            if (status.DatabaseRunning == running) return status;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"SQL did not become {(running ? "reachable" : "stopped")} within {timeout.TotalSeconds:0} seconds.");
    }

    private static async Task<bool> CanConnectAsync(string host, uint port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
        using var client = new TcpClient();
        try { await client.ConnectAsync(host, (int)port, timeout.Token); return true; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    private void StartLocal(ServerWorkspace workspace, string processName)
    {
        if (Process.GetProcessesByName(processName).Length > 0) return;
        var executable = FindExecutable(workspace.RootPath, processName + ".exe") ?? throw new FileNotFoundException($"Could not find {processName}.exe below {workspace.RootPath}.");
        var config = processName.Equals("worldserver", StringComparison.OrdinalIgnoreCase)
            ? workspace.ConfigLocation
            : FindFile(workspace.RootPath, "authserver.conf") ?? throw new FileNotFoundException("Could not find authserver.conf.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add(config);
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) => { _managed.TryRemove(processName, out _); process.Dispose(); };
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        _managed[processName] = process;
    }

    private async Task StopManagedWorldAsync(CancellationToken cancellationToken)
    {
        if (!_managed.TryGetValue("worldserver", out var process) || process.HasExited)
        {
            if (Process.GetProcessesByName("worldserver").Length > 0)
                throw new InvalidOperationException("The local worldserver is running but was not launched by this Crucible session. Refusing to force-kill it because that could lose character/world state.");
            return;
        }
        await process.StandardInput.WriteLineAsync("saveall".AsMemory(), cancellationToken);
        await process.StandardInput.WriteLineAsync("server shutdown 1".AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("worldserver did not exit within 60 seconds. It was not force-killed."); }
    }

    private async Task StopManagedAsync(string name, CancellationToken cancellationToken)
    {
        if (!_managed.TryGetValue(name, out var process) || process.HasExited)
        {
            if (Process.GetProcessesByName(name).Length > 0)
                throw new InvalidOperationException($"The local {name} is running but was not launched by this Crucible session. Refusing to force-kill an unowned process.");
            return;
        }
        process.Kill();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException($"{name} did not exit within 10 seconds."); }
    }

    private static async Task StartWslServerAsync(ServerWorkspace workspace, string name, string? executable, string? config,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(config)) throw new InvalidOperationException("The WSL server executable/config paths could not be detected.");
        if (await WslProcessExistsAsync(workspace, name, cancellationToken)) return;
        var log = $"/tmp/wow-crucible-{name}-startup.log";
        // Keep the WSL relay alive briefly after forking. Without this, WSL can tear down a slower-starting
        // worldserver child as bash exits even though nohup returned success.
        var launch = $"rm -f {ShellQuote(log)}; nohup {ShellQuote(executable)} --config {ShellQuote(config)} </dev/null >>{ShellQuote(log)} 2>&1 & sleep 2";
        await RunWslScriptAsync(workspace, launch, cancellationToken);
        try { await WaitForWslStableStateAsync(workspace, name, TimeSpan.FromSeconds(4), timeout, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var tail = await RunWslAsync(workspace, ["tail", "-n", "40", log], cancellationToken, allowNonZero: true);
            var detail = string.IsNullOrWhiteSpace(tail.Output) ? "No startup output was captured." : tail.Output;
            throw new InvalidOperationException($"{name} did not remain running. Startup log {log}:\n{detail}", exception);
        }
    }

    private static async Task WaitForWslStableStateAsync(ServerWorkspace workspace, string name, TimeSpan stableFor,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew(); TimeSpan? stableSince = null;
        while (stopwatch.Elapsed < timeout)
        {
            if (await WslProcessExistsAsync(workspace, name, cancellationToken))
            {
                stableSince ??= stopwatch.Elapsed;
                if (stopwatch.Elapsed - stableSince >= stableFor) return;
            }
            else stableSince = null;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"{name} did not remain running for {stableFor.TotalSeconds:0} continuous seconds within {timeout.TotalSeconds:0} seconds.");
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static async Task<bool> WslProcessExistsAsync(ServerWorkspace workspace, string name, CancellationToken cancellationToken) =>
        (await RunWslAsync(workspace, ["pgrep", "-x", name], cancellationToken, allowNonZero: true)).ExitCode == 0;

    private static async Task WaitForWslStateAsync(ServerWorkspace workspace, string name, bool running, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (await WslProcessExistsAsync(workspace, name, cancellationToken) == running) return;
            await Task.Delay(500, cancellationToken);
        }
        throw new TimeoutException($"{name} did not become {(running ? "running" : "stopped")} within {timeout.TotalSeconds:0} seconds. It was not force-killed.");
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunWslAsync(ServerWorkspace workspace, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowNonZero)
    {
        if (!workspace.UsesWsl || string.IsNullOrWhiteSpace(workspace.WslDistribution)) throw new InvalidOperationException("This server workspace has no WSL distribution.");
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "wsl.exe", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var value in new[] { "-d", workspace.WslDistribution, "-u", "root", "--" }.Concat(arguments)) process.StartInfo.ArgumentList.Add(value);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask; var error = await errorTask;
        if (!allowNonZero && process.ExitCode != 0) throw new InvalidOperationException($"WSL command failed ({process.ExitCode}): {error.Trim()}");
        return (process.ExitCode, output.Trim(), error.Trim());
    }

    private static async Task RunWslScriptAsync(ServerWorkspace workspace, string script, CancellationToken cancellationToken)
    {
        if (!workspace.UsesWsl || string.IsNullOrWhiteSpace(workspace.WslDistribution)) throw new InvalidOperationException("This server workspace has no WSL distribution.");
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        } };
        foreach (var value in new[] { "-d", workspace.WslDistribution, "-u", "root", "--", "bash", "-s" }) process.StartInfo.ArgumentList.Add(value);
        process.Start();
        process.StandardInput.NewLine = "\n";
        await process.StandardInput.WriteLineAsync("set -e".AsMemory(), cancellationToken);
        await process.StandardInput.WriteLineAsync(script.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"WSL launch script failed ({process.ExitCode}): {error.Trim()} {output.Trim()}".Trim());
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunLocalCommandAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, bool allowNonZero)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var value in arguments) process.StartInfo.ArgumentList.Add(value);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask; var error = await errorTask;
        if (!allowNonZero && process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed ({process.ExitCode}): {error.Trim()} {output.Trim()}".Trim());
        return (process.ExitCode, output.Trim(), error.Trim());
    }

    private static string? FindExecutable(string root, string name) => FindFile(root, name);
    private static string? FindFile(string root, string name) => Directory.EnumerateFiles(root, name, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, MatchCasing = MatchCasing.CaseInsensitive }).OrderBy(path => path.Count(character => character is '\\' or '/')).FirstOrDefault();

    public void Dispose()
    {
        foreach (var process in _managed.Values) process.Dispose();
        _managed.Clear();
    }
}
