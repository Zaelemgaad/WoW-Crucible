using System.Text.RegularExpressions;
using System.Diagnostics;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum ServerCoreFamily { Unknown, AzerothCore, TrinityCore }

public sealed record ServerWorkspace(
    string RootPath,
    string ConfigLocation,
    string DbcPath,
    ServerCoreFamily CoreFamily,
    DatabaseConnectionProfile WorldDatabase,
    bool UsesWsl = false,
    string? WslDistribution = null,
    string? WslConfigPath = null);

public static partial class ServerWorkspaceDetector
{
    public static async Task<ServerWorkspace> DetectAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        try { return DetectLocal(rootPath); }
        catch (FileNotFoundException)
        {
            var wsl = DetectWslLauncher(rootPath) ?? throw new FileNotFoundException("No live worldserver.conf or supported WSL launcher was found in this folder.");
            var text = await ReadWslFileAsync(wsl.Distribution, wsl.ConfigPath, cancellationToken);
            return Parse(rootPath, $"WSL {wsl.Distribution}:{wsl.ConfigPath}", text, true, wsl.Distribution, wsl.ConfigPath);
        }
    }

    public static ServerWorkspace DetectLocal(string rootPath)
    {
        var root = NormalizeRoot(rootPath);
        var config = FindLiveConfig(root) ?? throw new FileNotFoundException(
            "No live worldserver.conf was found. Template worldserver.conf.dist files are intentionally ignored.", root);
        return Parse(root, config, File.ReadAllText(config));
    }

    public static ServerWorkspace Parse(string rootPath, string configLocation, string configText, bool usesWsl = false, string? wslDistribution = null, string? wslConfigPath = null)
    {
        var root = NormalizeRoot(rootPath);
        var values = ParseSettings(configText);
        if (!values.TryGetValue("WorldDatabaseInfo", out var rawDatabase))
            throw new InvalidDataException("worldserver.conf has no WorldDatabaseInfo setting.");
        var parts = rawDatabase.Split(';');
        if (parts.Length < 5 || !uint.TryParse(parts[1], out var port) || port is 0 or > 65535)
            throw new InvalidDataException("WorldDatabaseInfo must use host;port;user;password;database format.");
        var ssl = parts.Length > 5 && parts[5].Equals("ssl", StringComparison.OrdinalIgnoreCase) ? MySqlSslMode.Required : MySqlSslMode.Preferred;
        var database = new DatabaseConnectionProfile(NormalizeHost(parts[0]), port, parts[2], parts[3], parts[4], ssl);
        var dbcPath = ResolveDbcPath(root, configLocation, values.GetValueOrDefault("DataDir"), usesWsl);
        return new(root, configLocation, dbcPath, DetectFamily(root, configText), database, usesWsl, wslDistribution, wslConfigPath);
    }

    public static (string Distribution, string ConfigPath)? DetectWslLauncher(string rootPath)
    {
        var script = Path.Combine(NormalizeRoot(rootPath), "Start-Server.ps1");
        if (!File.Exists(script)) return null;
        var text = File.ReadAllText(script);
        var distribution = WslDistributionRegex().Match(text);
        var config = WslWorldConfigRegex().Match(text);
        return distribution.Success && config.Success ? (distribution.Groups[1].Value, config.Groups[1].Value) : null;
    }

    private static Dictionary<string, string> ParseSettings(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sourceLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = sourceLine.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            var equals = line.IndexOf('='); if (equals <= 0) continue;
            var key = line[..equals].Trim(); if (!key.All(character => char.IsLetterOrDigit(character) || character is '_' or '.')) continue;
            var value = line[(equals + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                var closingQuote = value.LastIndexOf('"');
                if (closingQuote <= 0) throw new InvalidDataException($"Unterminated quoted value for {key}.");
                value = value[1..closingQuote];
            }
            else
            {
                var comment = value.IndexOf('#'); if (comment >= 0) value = value[..comment].Trim();
            }
            values[key] = value;
        }
        return values;
    }

    private static string? FindLiveConfig(string root)
    {
        string[] directCandidates =
        [
            Path.Combine(root, "worldserver.conf"), Path.Combine(root, "etc", "worldserver.conf"),
            Path.Combine(root, "configs", "worldserver.conf"), Path.Combine(root, "bin", "worldserver.conf")
        ];
        var direct = directCandidates.FirstOrDefault(File.Exists); if (direct is not null) return direct;
        return Directory.EnumerateFiles(root, "worldserver.conf", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 5, MatchCasing = MatchCasing.CaseInsensitive })
            .OrderBy(path => path.Count(character => character is '\\' or '/')).FirstOrDefault();
    }

    private static string ResolveDbcPath(string root, string configLocation, string? dataDir, bool usesWsl)
    {
        var obvious = new[] { Path.Combine(root, "data", "dbc"), Path.Combine(root, "Data", "dbc"), Path.Combine(root, "dbc") }.FirstOrDefault(Directory.Exists);
        if (obvious is not null) return Path.GetFullPath(obvious);
        if (!usesWsl && !string.IsNullOrWhiteSpace(dataDir))
        {
            var expanded = Environment.ExpandEnvironmentVariables(dataDir.Trim('"'));
            var basePath = Path.IsPathRooted(expanded) ? expanded : Path.Combine(root, expanded);
            var candidate = Path.Combine(basePath, "dbc"); if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            var configRelative = Path.Combine(Path.GetDirectoryName(configLocation)!, expanded, "dbc"); if (Directory.Exists(configRelative)) return Path.GetFullPath(configRelative);
        }
        return string.Empty;
    }

    private static ServerCoreFamily DetectFamily(string root, string configText)
    {
        var evidence = root + "\n" + configText;
        if (evidence.Contains("AzerothCore", StringComparison.OrdinalIgnoreCase) || evidence.Contains("acore_", StringComparison.OrdinalIgnoreCase)) return ServerCoreFamily.AzerothCore;
        if (evidence.Contains("TrinityCore", StringComparison.OrdinalIgnoreCase) || evidence.Contains("TDB", StringComparison.OrdinalIgnoreCase)) return ServerCoreFamily.TrinityCore;
        return ServerCoreFamily.Unknown;
    }

    private static string NormalizeHost(string host) => host is "." or "localhost" ? "127.0.0.1" : host;

    private static async Task<string> ReadWslFileAsync(string distribution, string path, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = "wsl.exe", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in new[] { "-d", distribution, "-u", "root", "--", "cat", path }) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken); var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask; var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException($"WSL could not read the configured worldserver.conf: {error.Trim()}");
        return output;
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) throw new DirectoryNotFoundException($"Server folder does not exist: {rootPath}");
        return Path.GetFullPath(rootPath);
    }

    [GeneratedRegex("""(?mi)^\s*\$distro\s*=\s*['"]([^'"]+)['"]""")]
    private static partial Regex WslDistributionRegex();
    [GeneratedRegex("""(?mi)['"]--config['"]\s*,\s*['"]([^'"]*worldserver\.conf)['"]""")]
    private static partial Regex WslWorldConfigRegex();
}
