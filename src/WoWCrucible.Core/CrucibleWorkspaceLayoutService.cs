using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record CrucibleWorkspaceLayout(
    int FormatVersion,
    string Name,
    string RootPath,
    string ServerRootPath,
    string CoreSourcePath,
    string ClientRootPath,
    string ClientDataPath,
    string ClientExecutablePath,
    string CoreDbcPath,
    string SchemaDefinitionPath,
    string DbdDefinitionsPath,
    string ProcessedAssetLibraryPath,
    string ProjectsPath,
    string StagingPath,
    string ToolsPath,
    string NoggitExecutablePath,
    string MapSourcePath,
    IReadOnlyList<string> Findings)
{
    public const int CurrentFormatVersion = 1;
    public static string ManifestPath(string rootPath) => Path.Combine(Path.GetFullPath(rootPath), ".crucible", "workspace.json");
}

/// <summary>
/// Discovers a conventional WoW development install from one top-level folder and persists a portable manifest.
/// Paths inside the workspace are stored relative to the manifest; credentials are deliberately outside this model.
/// </summary>
public static class CrucibleWorkspaceLayoutService
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "bin", "obj", "node_modules", "Cache", "Logs", "Temp", "Backup", "Backups"
    };

    public static CrucibleWorkspaceLayout Discover(string rootPath)
    {
        var root = NormalizeExistingDirectory(rootPath);
        var directories = EnumerateDirectories(root, 4).ToArray();
        var files = EnumerateFiles(root, 5).ToArray();
        var findings = new List<string>();

        var server = FindServerRoot(root, files);
        AddFinding(findings, "Server", server);
        var wowExecutables = files.Where(file => Path.GetFileName(file).Equals("Wow.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        var clientExecutable = BestFile(wowExecutables, file => Directory.Exists(Path.Combine(Path.GetDirectoryName(file)!, "Data")))
                               ?? BestFile(wowExecutables, _ => true);
        var clientRoot = clientExecutable is null ? string.Empty : Path.GetDirectoryName(clientExecutable)!;
        var clientData = FirstExisting(Path.Combine(clientRoot, "Data"));
        AddFinding(findings, "Client", clientRoot);

        var coreSource = directories
            .Where(directory => File.Exists(Path.Combine(directory, "CMakeLists.txt")) && Directory.Exists(Path.Combine(directory, "src", "server")))
            .OrderBy(Depth).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
        AddFinding(findings, "Core source", coreSource);

        var coreDbc = FindCoreDbc(server, directories);
        AddFinding(findings, "Server DBC", coreDbc);
        var schema = BestFile(files, file => Path.GetFileName(file).Equals("WotLK 3.3.5 (12340).xml", StringComparison.OrdinalIgnoreCase));
        AddFinding(findings, "WotLK schema", schema);
        var dbd = directories.Where(IsDbdDefinitionsDirectory).OrderBy(Depth).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
        AddFinding(findings, "WoWDBDefs", dbd);

        var processedAssets = directories.Where(directory => Path.GetFileName(directory).Contains("Processed", StringComparison.OrdinalIgnoreCase) &&
                                                              Path.GetFileName(directory).Contains("Asset", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Depth).FirstOrDefault() ?? string.Empty;
        var projects = FindNamedDirectory(root, directories, "Projects");
        var staging = FindNamedDirectory(root, directories, "Staging");
        var tools = FindNamedDirectory(root, directories, "Tools");
        var noggit = BestFile(files, file => Path.GetFileName(file).Equals("noggit.exe", StringComparison.OrdinalIgnoreCase));
        var mapSource = directories.Where(IsMapSourceDirectory).OrderBy(Depth).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;

        return new(
            CrucibleWorkspaceLayout.CurrentFormatVersion,
            Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            root,
            server,
            coreSource,
            clientRoot,
            clientData,
            clientExecutable ?? string.Empty,
            coreDbc,
            schema ?? string.Empty,
            dbd,
            processedAssets,
            projects,
            staging,
            tools,
            noggit ?? string.Empty,
            mapSource,
            findings);
    }

    public static void Save(CrucibleWorkspaceLayout layout)
    {
        var root = Path.GetFullPath(layout.RootPath);
        Directory.CreateDirectory(Path.Combine(root, ".crucible"));
        var portable = layout with
        {
            RootPath = ".",
            ServerRootPath = Portable(root, layout.ServerRootPath),
            CoreSourcePath = Portable(root, layout.CoreSourcePath),
            ClientRootPath = Portable(root, layout.ClientRootPath),
            ClientDataPath = Portable(root, layout.ClientDataPath),
            ClientExecutablePath = Portable(root, layout.ClientExecutablePath),
            CoreDbcPath = Portable(root, layout.CoreDbcPath),
            SchemaDefinitionPath = Portable(root, layout.SchemaDefinitionPath),
            DbdDefinitionsPath = Portable(root, layout.DbdDefinitionsPath),
            ProcessedAssetLibraryPath = Portable(root, layout.ProcessedAssetLibraryPath),
            ProjectsPath = Portable(root, layout.ProjectsPath),
            StagingPath = Portable(root, layout.StagingPath),
            ToolsPath = Portable(root, layout.ToolsPath),
            NoggitExecutablePath = Portable(root, layout.NoggitExecutablePath),
            MapSourcePath = Portable(root, layout.MapSourcePath),
            Findings = layout.Findings.Select(finding => finding.Replace(root, ".", StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        var path = CrucibleWorkspaceLayout.ManifestPath(root);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(portable, JsonOptions));
        File.Move(temporary, path, true);
    }

    public static CrucibleWorkspaceLayout Load(string rootPath)
    {
        var root = NormalizeExistingDirectory(rootPath);
        var path = CrucibleWorkspaceLayout.ManifestPath(root);
        if (!File.Exists(path)) throw new FileNotFoundException("This folder has no .crucible/workspace.json manifest.", path);
        var stored = JsonSerializer.Deserialize<CrucibleWorkspaceLayout>(File.ReadAllText(path), JsonOptions)
                     ?? throw new InvalidDataException("The Crucible workspace manifest is empty or invalid.");
        if (stored.FormatVersion != CrucibleWorkspaceLayout.CurrentFormatVersion)
            throw new InvalidDataException($"Unsupported Crucible workspace format {stored.FormatVersion}.");
        return stored with
        {
            RootPath = root,
            ServerRootPath = Absolute(root, stored.ServerRootPath),
            CoreSourcePath = Absolute(root, stored.CoreSourcePath),
            ClientRootPath = Absolute(root, stored.ClientRootPath),
            ClientDataPath = Absolute(root, stored.ClientDataPath),
            ClientExecutablePath = Absolute(root, stored.ClientExecutablePath),
            CoreDbcPath = Absolute(root, stored.CoreDbcPath),
            SchemaDefinitionPath = Absolute(root, stored.SchemaDefinitionPath),
            DbdDefinitionsPath = Absolute(root, stored.DbdDefinitionsPath),
            ProcessedAssetLibraryPath = Absolute(root, stored.ProcessedAssetLibraryPath),
            ProjectsPath = Absolute(root, stored.ProjectsPath),
            StagingPath = Absolute(root, stored.StagingPath),
            ToolsPath = Absolute(root, stored.ToolsPath),
            NoggitExecutablePath = Absolute(root, stored.NoggitExecutablePath),
            MapSourcePath = Absolute(root, stored.MapSourcePath)
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static IEnumerable<string> EnumerateDirectories(string root, int maximumDepth)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.TryDequeue(out var current))
        {
            yield return current.Path;
            if (current.Depth >= maximumDepth) continue;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(current.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (!SkippedDirectories.Contains(name) && !name.StartsWith(".", StringComparison.Ordinal)) queue.Enqueue((child, current.Depth + 1));
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string root, int maximumDepth)
    {
        foreach (var directory in EnumerateDirectories(root, maximumDepth))
        {
            string[]? files = null;
            try { files = Directory.EnumerateFiles(directory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray(); }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            if (files is null) continue;
            foreach (var file in files) yield return file;
        }
    }

    private static string FindServerRoot(string root, IReadOnlyList<string> files)
    {
        var config = BestFile(files, file => Path.GetFileName(file).Equals("worldserver.conf", StringComparison.OrdinalIgnoreCase));
        if (config is not null)
        {
            var directory = Path.GetDirectoryName(config)!;
            if (new[] { "etc", "configs", "bin" }.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase)) directory = Directory.GetParent(directory)?.FullName ?? directory;
            return directory;
        }
        var launcher = BestFile(files, file => Path.GetFileName(file).Equals("Start-Server.ps1", StringComparison.OrdinalIgnoreCase));
        return launcher is null ? string.Empty : Path.GetDirectoryName(launcher)!;
    }

    private static string FindCoreDbc(string server, IReadOnlyList<string> directories)
    {
        if (!string.IsNullOrWhiteSpace(server))
        {
            var direct = FirstExisting(Path.Combine(server, "data", "dbc"), Path.Combine(server, "Data", "dbc"), Path.Combine(server, "dbc"));
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
        }
        return directories.Where(directory => Path.GetFileName(directory).Equals("dbc", StringComparison.OrdinalIgnoreCase) &&
                                               Directory.EnumerateFiles(directory, "*.dbc", SearchOption.TopDirectoryOnly).Any())
            .OrderBy(Depth).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? string.Empty;
    }

    private static bool IsDbdDefinitionsDirectory(string directory) => Path.GetFileName(directory).Equals("definitions", StringComparison.OrdinalIgnoreCase) &&
                                                                        Directory.EnumerateFiles(directory, "*.dbd", SearchOption.TopDirectoryOnly).Any();
    private static bool IsMapSourceDirectory(string directory) => Path.GetFileName(directory).Equals("Maps", StringComparison.OrdinalIgnoreCase) &&
                                                                  Directory.EnumerateFiles(directory, "*.wdt", SearchOption.TopDirectoryOnly).Any();
    private static string FindNamedDirectory(string root, IEnumerable<string> directories, string name) =>
        directories.Where(directory => Path.GetFileName(directory).Equals(name, StringComparison.OrdinalIgnoreCase)).OrderBy(Depth).FirstOrDefault()
        ?? Path.Combine(root, name);
    private static string? BestFile(IEnumerable<string> files, Func<string, bool> predicate) => files.Where(predicate).OrderBy(Depth).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    private static string FirstExisting(params string?[] paths) => paths.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) ?? string.Empty;
    private static int Depth(string path) => path.Count(character => character is '\\' or '/');
    private static void AddFinding(ICollection<string> findings, string label, string? path) => findings.Add(string.IsNullOrWhiteSpace(path) ? $"{label}: not found" : $"{label}: {path}");
    private static string NormalizeExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a workspace folder first.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Workspace folder does not exist: {full}");
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
    private static string Portable(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        return relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? full : relative;
    }
    private static string Absolute(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }
}
