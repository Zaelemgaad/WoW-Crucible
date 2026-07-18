using System.Collections.Concurrent;
using System.Diagnostics;

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

    public Task<ServerRuntimeStatus> GetStatusAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default) =>
        workspace.UsesWsl ? GetWslStatusAsync(workspace, cancellationToken) : Task.FromResult(GetLocalStatus());

    public async Task<ServerLifecycleResult> StartAllAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.UsesWsl)
        {
            await RunWslAsync(workspace, ["service", "mysql", "start"], cancellationToken, allowNonZero: false);
            if (!await WslProcessExistsAsync(workspace, "authserver", cancellationToken)) StartWslProcess(workspace, workspace.WslAuthExecutable, workspace.WslAuthConfigPath);
            if (!await WslProcessExistsAsync(workspace, "worldserver", cancellationToken)) StartWslProcess(workspace, workspace.WslWorldExecutable, workspace.WslConfigPath);
            await WaitForWslStateAsync(workspace, "authserver", true, TimeSpan.FromSeconds(15), cancellationToken);
            await WaitForWslStateAsync(workspace, "worldserver", true, TimeSpan.FromSeconds(20), cancellationToken);
        }
        else
        {
            StartLocal(workspace, "authserver");
            StartLocal(workspace, "worldserver");
            await Task.Delay(750, cancellationToken);
        }
        var status = await GetStatusAsync(workspace, cancellationToken);
        return new("Start all", status, "Database, authserver, and worldserver start requests completed.");
    }

    public async Task<ServerLifecycleResult> StartWorldAsync(ServerWorkspace workspace, CancellationToken cancellationToken = default)
    {
        if (workspace.UsesWsl)
        {
            await RunWslAsync(workspace, ["service", "mysql", "start"], cancellationToken, allowNonZero: false);
            if (!await WslProcessExistsAsync(workspace, "worldserver", cancellationToken)) StartWslProcess(workspace, workspace.WslWorldExecutable, workspace.WslConfigPath);
            await WaitForWslStateAsync(workspace, "worldserver", true, TimeSpan.FromSeconds(20), cancellationToken);
        }
        else
        {
            StartLocal(workspace, "worldserver");
            await Task.Delay(750, cancellationToken);
        }
        return new("Start worldserver", await GetStatusAsync(workspace, cancellationToken), "Worldserver start request completed; authserver was left unchanged.");
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
        else StopManaged("authserver");
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

    private ServerRuntimeStatus GetLocalStatus()
    {
        static bool Running(string name) => Process.GetProcessesByName(name).Any(process => { using (process) return !process.HasExited; });
        var world = Running("worldserver");
        var auth = Running("authserver");
        return new(world, auth, true, $"Local · worldserver {(world ? "running" : "stopped")} · authserver {(auth ? "running" : "stopped")} · database status is managed externally");
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

    private void StopManaged(string name)
    {
        if (!_managed.TryGetValue(name, out var process) || process.HasExited) return;
        process.Kill();
    }

    private static void StartWslProcess(ServerWorkspace workspace, string? executable, string? config)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(config)) throw new InvalidOperationException("The WSL server executable/config paths could not be detected.");
        var start = new ProcessStartInfo { FileName = "wsl.exe", UseShellExecute = false, CreateNoWindow = true };
        foreach (var value in new[] { "-d", workspace.WslDistribution!, "-u", "root", "--", executable, "--config", config }) start.ArgumentList.Add(value);
        Process.Start(start)?.Dispose();
    }

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

    private static string? FindExecutable(string root, string name) => FindFile(root, name);
    private static string? FindFile(string root, string name) => Directory.EnumerateFiles(root, name, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, MatchCasing = MatchCasing.CaseInsensitive }).OrderBy(path => path.Count(character => character is '\\' or '/')).FirstOrDefault();

    public void Dispose()
    {
        foreach (var process in _managed.Values) process.Dispose();
        _managed.Clear();
    }
}
