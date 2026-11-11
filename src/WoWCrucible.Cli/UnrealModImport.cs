using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Formats.Materials;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using SkiaSharp;
using WoWCrucible.Core;
using ILoadableObject = CUE4Parse.UE4.Objects.UObject.ILoadableObject;

/// <summary>
/// <c>wowcrucible models import-mod</c> and <c>import-mods</c>: Unreal Engine game mods as they come from a mod site (a
/// <c>.rar</c>, <c>.zip</c> or <c>.7z</c>, loose <c>.pak</c>/<c>.utoc</c>/<c>.ucas</c> files, or a mod manager's folder
/// of them) read together over the installed game they are for, in their load order, the way the game mounts them, and
/// written out as FModel-style exports: one folder a look, its <c>look.txt</c> naming it and the files it uses (the
/// meshes as <c>.psk</c>, materials as <c>.json</c>, a colour picture each material really shows as <c>.png</c>, and a
/// basic set of animations as <c>.psa</c>). A mod for Dekita's Custom Nano Suit framework (CNS: a
/// <c>.dekcns.json</c> beside its paks) becomes a look for each outfit variant its JSON lists, worn with the character's
/// own face and hair; any other mod becomes a look of the character meshes it changes. What the game itself holds is
/// written once under <c>shared\</c> and named by every look that uses it. What could not be done, or what the user
/// must give once (a game's AES key, its mappings), is written to each mod's <c>import.txt</c> in plain words.
/// CUE4Parse (Apache-2.0) reads the game files.
/// </summary>
internal sealed class UnrealModImporter
{
    /// <summary>The user must give something once; the message says what and where.</summary>
    private sealed class NeedsUser(string message) : Exception(message);

    /// <summary>The mod cannot become a model; the message says why.</summary>
    private sealed class Unusable(string message) : Exception(message);

    /// <summary>One mod to mount: <paramref name="Export"/> when its looks are to be written.</summary>
    internal sealed record Job(string Id, bool Export, string Name, IReadOnlyList<string> Sources);

    /// <summary>A mod as gathered: its containers, its CNS outfit lists and what was found wrong with it.</summary>
    private sealed class Mod(Job job)
    {
        public Job Job { get; } = job;
        public string Name { get; set; } = job.Name;
        public List<string> Containers { get; } = [];
        public List<string> Cns { get; } = [];
        public List<string> Notes { get; } = [];
        public string? Problem { get; set; }
        public string? Need { get; set; }
        /// <summary>The names its containers were registered by, and its files by content path.</summary>
        public HashSet<string> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The same names, in the order of <see cref="Containers"/>.</summary>
        public List<string> Order { get; } = [];
        public Dictionary<string, GameFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Name, string Folder)> Looks { get; } = [];
        public string? Project { get; set; }
        /// <summary>The game it was read over.</summary>
        public string GameName { get; set; } = "";
    }

    private const int MaxTextureSide = 2048;
    private readonly string _settingsPath;
    private readonly UnrealModSettings _settings;
    private string _cache = "";
    private string _shared = "";
    private DefaultFileProvider _provider = null!;
    private string _project = "Game";
    private readonly HashSet<string> _modVfs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Every package of every mod by content path, the last in load order winning.</summary>
    private readonly Dictionary<string, GameFile> _allModFiles = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What was written this run, by object path: the files a look names for it.</summary>
    private readonly Dictionary<string, List<string>> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<(string Word, string Clip, string File)>> _clips = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _notes = [];
    /// <summary>Bones the meshes written this run move, by skeleton; the clips written this run, kept to those.</summary>
    private readonly Dictionary<string, HashSet<string>> _bones = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Skeleton, string Clip)> _newClips = [];

    private UnrealModImporter(string? settingsPath)
    {
        _settingsPath = settingsPath ?? "";
        _settings = UnrealModSettings.Load(settingsPath);
    }

    private static void Say(string line) => Console.WriteLine(line);

    /// <summary>One mod into <paramref name="outDir"/>: <c>import-mod</c>. The game's own files go beside it, in <c>shared\</c>.</summary>
    public static int Run(IReadOnlyList<string> inputs, string outDir, string? settingsPath, CancellationToken cancel)
    {
        outDir = Path.GetFullPath(outDir).TrimEnd('\\', '/');
        var name = Path.GetFileNameWithoutExtension(inputs[0]);
        return RunJobs([new Job(Path.GetFileName(outDir), true, name, inputs)], Path.GetDirectoryName(outDir)!, settingsPath, cancel);
    }

    /// <summary>
    /// <c>import-mods</c>: the mods of <paramref name="jobs"/> grouped by the game each is for, each game's mounted
    /// together in that order over that game, the later winning; each one marked for export written to
    /// <c>&lt;cache&gt;\&lt;id&gt;\</c>. 0 when every exported mod became at least one look, 3 when one needs the user,
    /// 1 otherwise.
    /// </summary>
    public static int RunJobs(IReadOnlyList<Job> jobs, string cache, string? settingsPath, CancellationToken cancel)
    {
        cache = Path.GetFullPath(cache);
        Directory.CreateDirectory(cache);
        var work = Path.Combine(cache, ".work");
        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
        var mods = jobs.Select(job => new Mod(job)).ToList();
        foreach (var mod in mods.Where(m => m.Job.Export))
        {
            var dir = Path.Combine(cache, mod.Job.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
        }
        try
        {
            for (var i = 0; i < mods.Count; i++)
            {
                try { Gather(mods[i], Path.Combine(work, i.ToString("D4"))); }
                catch (Unusable unusable) { mods[i].Problem = unusable.Message; }
                catch (Exception ex) when (ex is not OperationCanceledException) { mods[i].Problem = $"It could not be read: {Flat(ex.Message)}"; }
            }
            foreach (var (game, group) in ByGame(mods, cache, settingsPath))
            {
                cancel.ThrowIfCancellationRequested();
                var importer = new UnrealModImporter(settingsPath) { _cache = cache };
                try { importer.ImportGame(game, group, cancel); }
                catch (Unusable unusable)
                {
                    foreach (var mod in group.Where(m => m.Job.Export && m.Problem is null && m.Looks.Count == 0)) mod.Problem = unusable.Message;
                }
                catch (NeedsUser need)
                {
                    foreach (var mod in group.Where(m => m.Job.Export && m.Problem is null)) mod.Need = need.Message;
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
        }

        var code = 0;
        foreach (var mod in mods.Where(m => m.Job.Export))
        {
            var result = new StringBuilder();
            if (mod.Need is not null) { result.Append($"need\t{mod.Need}\n"); code = Math.Max(code, 3); }
            else if (mod.Problem is not null || mod.Looks.Count == 0)
            {
                result.Append($"problem\t{mod.Problem ?? "no look could be made of it."}\n");
                if (code == 0) code = 1;
            }
            if (mod.Need is null && mod.Problem is null)
            {
                result.Append($"game\t{mod.GameName}\n");
                foreach (var (name, folder) in mod.Looks) result.Append($"look\t{folder}\t{name}\n");
            }
            foreach (var note in mod.Notes.Distinct().Where(n => n != mod.Problem)) result.Append($"note\t{note}\n");
            var dir = Path.Combine(cache, mod.Job.Id);
            if (mod.Looks.Count == 0 || mod.Need is not null || mod.Problem is not null)
                foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
                    if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
            File.WriteAllText(Path.Combine(dir, "import.txt"), result.ToString());
            Console.Write($"== {mod.Job.Name}\n{result}");
        }
        return code;
    }

    private string _gameName = "";

    private static string Flat(string text) => text.ReplaceLineEndings(" ");

    // ---- What was dropped ----

    /// <summary>A mod's files: archives opened into <paramref name="work"/>, folders and loose files read where they are.</summary>
    private static void Gather(Mod mod, string work)
    {
        var files = new List<string>();
        var archiveName = "";
        foreach (var input in mod.Job.Sources.Select(Path.GetFullPath))
        {
            if (Directory.Exists(input)) files.AddRange(Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories));
            else if (ArchiveKind(input) is not null)
            {
                archiveName = archiveName.Length == 0 ? Path.GetFileNameWithoutExtension(input) : archiveName;
                var dest = Path.Combine(work, "a" + files.Count.ToString("D3"));
                Extract(input, dest, 0);
                files.AddRange(Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories));
            }
            else if (File.Exists(input)) files.Add(input);
            else throw new Unusable($"\"{input}\" is not there.");
        }
        if (ModName(files) is { } named) mod.Name = named;
        else if (archiveName.Length > 0) mod.Name = Regex.Replace(archiveName, "[_]+", " ").Trim();
        else mod.Name = Nice(mod.Job.Name);
        mod.Cns.AddRange(files.Where(f => f.EndsWith("dekcns.json", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase));
        mod.Containers.AddRange(Containers(files, mod.Notes));
        if (mod.Containers.Count == 0)
        {
            if (files.Any(f => Path.GetExtension(f).ToLowerInvariant() is ".psk" or ".pskx"))
                throw new Unusable("it is already an export (.psk); drop it as a folder of its own, not with game files.");
            throw new Unusable(mod.Notes.Count > 0 ? mod.Notes[0] : "it holds no game files (.pak, .utoc), so there is nothing to make a model from.");
        }
    }

    /// <summary>A Nexus or Vortex folder name made plain: no load-order prefix (<c>ZZZZ-</c>, <c>AIK-</c>), no id, version and time after it.</summary>
    internal static string Nice(string name)
    {
        var n = Regex.Replace(name, @"^(Z{2,}|[A-Z]{3})-", "");
        n = Regex.Replace(n, @"-\d+(-[0-9v]+)*-\d{9,}$", "");
        return n.Trim(' ', '-');
    }

    private static string? ArchiveKind(string file)
    {
        if (!File.Exists(file)) return null;
        Span<byte> head = stackalloc byte[6];
        using (var stream = File.OpenRead(file)) head = head[..stream.Read(head)];
        if (head.StartsWith("PK"u8)) return "zip";
        if (head.StartsWith("Rar!"u8)) return "rar";
        if (head.StartsWith(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })) return "7z";
        return null;
    }

    /// <summary>Opens an archive into <paramref name="dest"/>, and the archives inside it, with what this PC has.</summary>
    private static void Extract(string archive, string dest, int depth)
    {
        var kind = ArchiveKind(archive)!;
        Directory.CreateDirectory(dest);
        var opened = false;
        if (kind == "zip")
        {
            try { ZipFile.ExtractToDirectory(archive, dest, true); opened = true; }
            catch (Exception) { }
        }
        foreach (var (tool, arguments) in Extractors(kind, archive, dest))
        {
            if (opened) break;
            try
            {
                var start = new ProcessStartInfo(tool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(TimeSpan.FromMinutes(10))) { process.Kill(true); continue; }
                opened = process.ExitCode == 0 && Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Any();
            }
            catch (Exception) { }
        }
        if (!opened)
            throw new Unusable($"the archive \"{Path.GetFileName(archive)}\" could not be opened. Install 7-Zip or WinRAR, or unpack it into a folder in here.");
        if (depth >= 2) return;
        foreach (var inner in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).Where(f => ArchiveKind(f) is not null).ToList())
        {
            Extract(inner, inner + "_x", depth + 1);
            File.Delete(inner);
        }
    }

    private static IEnumerable<(string Tool, string[] Arguments)> Extractors(string kind, string archive, string dest)
    {
        var programs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        foreach (var root in programs)
        {
            var sevenZip = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(sevenZip)) yield return (sevenZip, ["x", "-y", "-o" + dest, archive]);
        }
        foreach (var root in programs)
        {
            var winrar = Path.Combine(root, "WinRAR");
            if (kind == "rar")
                foreach (var console in new[] { "UnRAR.exe", "Rar.exe" })
                    if (File.Exists(Path.Combine(winrar, console))) yield return (Path.Combine(winrar, console), ["x", "-y", "-idq", archive, dest + Path.DirectorySeparatorChar]);
            if (File.Exists(Path.Combine(winrar, "WinRAR.exe"))) yield return (Path.Combine(winrar, "WinRAR.exe"), ["x", "-ibck", "-y", archive, dest + Path.DirectorySeparatorChar]);
        }
        var tar = Path.Combine(Environment.SystemDirectory, "tar.exe");
        if (File.Exists(tar)) yield return (tar, ["-xf", archive, "-C", dest]);
    }

    /// <summary>The mod's name from its <c>modinfo.ini</c> (<c>name=</c>), if it has one.</summary>
    private static string? ModName(IEnumerable<string> files)
    {
        foreach (var ini in files.Where(f => Path.GetFileName(f).Equals("modinfo.ini", StringComparison.OrdinalIgnoreCase)))
            foreach (var line in File.ReadLines(ini))
            {
                var match = Regex.Match(line, @"^\s*name\s*=\s*(.+?)\s*$", RegexOptions.IgnoreCase);
                if (match.Success && match.Groups[1].Value.Length > 0) return match.Groups[1].Value;
            }
        return null;
    }

    /// <summary>
    /// The mod's containers, in the game's order (by path): an IoStore table (<c>.utoc</c>, with its <c>.ucas</c>) or a
    /// <c>.pak</c> that is a whole pak, not an IoStore set's stub. A set missing its table or its data is said in <paramref name="notes"/>.
    /// </summary>
    private static List<string> Containers(IEnumerable<string> files, List<string> notes)
    {
        var all = files.ToList();
        bool Has(string f, string ext) => File.Exists(Path.ChangeExtension(f, ext));
        var tables = all.Where(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var lone in tables.Where(t => !Has(t, ".ucas")))
            notes.Add($"\"{Path.GetFileName(lone)}\" has no .ucas beside it, so its set is incomplete and was not read.");
        foreach (var data in all.Where(f => f.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase) && !Has(f, ".utoc")))
            notes.Add($"\"{Path.GetFileName(data)}\" has no .utoc beside it, so its set is incomplete and cannot be read (a mod manager may have given that file to another mod with the same file names).");
        var paks = all.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !Has(f, ".utoc") && !Has(f, ".ucas")).ToList();
        return tables.Where(t => Has(t, ".ucas")).Concat(paks)
            .Where(f => !Path.GetFileNameWithoutExtension(f).Equals("global", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The project a path is of: <c>SB</c> of <c>SB/Content/...</c>, and the game's of a mod packed as a plugin of it
    /// (<c>SparkingZERO/Mods/&lt;Mod&gt;/Content/...</c>, <c>&lt;Game&gt;/Plugins/.../&lt;Mod&gt;/Content/...</c>).
    /// </summary>
    internal static string? ProjectOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2 || parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase)) return null;
        if (parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase)) return parts[0];
        var plugin = parts[1].Equals("Mods", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("Plugins", StringComparison.OrdinalIgnoreCase);
        return plugin && parts.Skip(3).SkipLast(1).Contains("Content", StringComparer.OrdinalIgnoreCase) ? parts[0] : null;
    }

    /// <summary>A plugin's content folder in a path, and what is under it: the game names it <c>/&lt;Plugin&gt;/...</c>.</summary>
    private static readonly Regex PluginContent = new(@"(?:^|/)(?:Mods|Plugins)/(?:.+/)?([^/]+)/Content/(.*)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// A path's part under its project's <c>Content</c> folder, lower case, no extension: how a mod cooked under another
    /// project name is still found. A plugin's content keeps the plugin's name in front, as the game names it
    /// (<c>/Android18_2B_Outfit/SK_...</c>).
    /// </summary>
    internal static string ContentRel(string path)
    {
        path = path.Replace('\\', '/');
        var dot = path.LastIndexOf('.');
        if (dot > path.LastIndexOf('/')) path = path[..dot];
        if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return path[6..].ToLowerInvariant();
        if (PluginContent.Match(path) is { Success: true } plugin) return $"{plugin.Groups[1].Value}/{plugin.Groups[2].Value}".ToLowerInvariant();
        var content = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        return (content >= 0 ? path[(content + 9)..] : path.TrimStart('/')).ToLowerInvariant();
    }

    // ---- The game, and every mod over it ----

    /// <summary>
    /// The mods grouped by the installed game each is for, in load order within a group: the game whose project its
    /// paths name. A CNS outfit cooked under a project name of its own (<c>Yor/Content/...</c>) goes with the game most
    /// of the others are for (CNS is Stellar Blade's). Only a game with a mod to write is returned; a mod no installed
    /// game is found for says why.
    /// </summary>
    private static List<(UnrealGame Game, List<Mod> Mods)> ByGame(List<Mod> mods, string cache, string? settingsPath)
    {
        var usable = mods.Where(m => m.Problem is null).ToList();
        if (!usable.Any(m => m.Job.Export)) return [];

        // Each container's paths, read with no game behind them: the project they name, and a locked one.
        var empty = Path.Combine(cache, ".work", "none");
        Directory.CreateDirectory(empty);
        using (var lister = new DefaultFileProvider(new DirectoryInfo(empty), SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_LATEST), StringComparer.OrdinalIgnoreCase))
        {
            lister.Initialize();
            Register(lister, usable);
            lister.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
            foreach (var mod in usable)
            {
                var paths = lister.MountedVfs.Where(v => mod.Registered.Contains(v.Name)).SelectMany(v => v.Files.Keys).ToList();
                if (paths.Count == 0 && lister.UnloadedVfs.Any(v => mod.Registered.Contains(v.Name) && v.IsEncrypted))
                    mod.Problem = "the mod's own files are locked (encrypted) by its maker, so they cannot be read.";
                else if (paths.Count == 0) mod.Problem = mod.Notes.FirstOrDefault() ?? "its game files hold nothing that could be read.";
                mod.Project = paths.Select(ProjectOf).Where(p => p is not null).GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            }
        }
        usable = usable.Where(m => m.Problem is null).ToList();
        if (!usable.Any(m => m.Job.Export)) return [];

        var settings = UnrealModSettings.Load(settingsPath);
        var games = UnrealGameLocator.InstalledGames(settings.Folders(), UnrealGameLocator.FModelSettings().Games);
        var groups = new List<(UnrealGame Game, List<Mod> Mods)>();
        var homeless = new List<Mod>();
        foreach (var mod in usable)
        {
            var game = mod.Project is null ? null : UnrealGameLocator.ForProject(mod.Project, games).FirstOrDefault();
            if (game is null) { homeless.Add(mod); continue; }
            var group = groups.FirstOrDefault(g => g.Game.Paks.Equals(game.Paks, StringComparison.OrdinalIgnoreCase));
            if (group.Mods is null) groups.Add(group = (game, []));
            group.Mods.Add(mod);
        }
        var main = groups.OrderByDescending(g => g.Mods.Count).FirstOrDefault();
        foreach (var mod in homeless)
        {
            if (mod.Cns.Count > 0 && main.Mods is not null) { main.Mods.Add(mod); continue; }
            if (mod.Project is null)
                mod.Problem = "its game files name no game content (no \"<Game>/Content/...\" paths), so the game it is for is unknown.";
            else
                mod.Need = $"it is a mod for a game whose files are called \"{mod.Project}\", and no installed game has them. If that game is installed, add a line to {settingsPath}:  {mod.Project} folder: <the game's folder>";
        }
        foreach (var (_, group) in groups) group.Sort((a, b) => mods.IndexOf(a).CompareTo(mods.IndexOf(b)));
        return groups.Where(g => g.Mods.Any(m => m.Job.Export)).ToList();
    }

    /// <summary>One game's mods mounted together over it, in their order, and each one marked for export written.</summary>
    private void ImportGame(UnrealGame game, List<Mod> usable, CancellationToken cancel)
    {
        var (fmodelGames, fmodelData) = UnrealGameLocator.FModelSettings();
        foreach (var mod in usable) mod.GameName = game.Name;
        _gameName = game.Name;
        Say($"Game: {game.Name} ({game.Paks})");
        var fmodel = UnrealGameLocator.FModelEntry(game, fmodelGames);
        var version = Version(game, fmodel)
            ?? throw new NeedsUser($"the Unreal Engine version of {game.Name} could not be told. Add a line to {_settingsPath}:  {game.Name} engine: <version, like 5.1>");
        Say($"Engine: {version}");
        InitNatives(fmodelData);

        var mappings = Mappings(game, fmodel, fmodelData);
        _provider = new DefaultFileProvider(new DirectoryInfo(game.Paks), SearchOption.TopDirectoryOnly, new VersionContainer(version), StringComparer.OrdinalIgnoreCase);
        if (mappings is not null)
        {
            Say($"Mappings: {mappings}");
            _provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappings, StringComparer.OrdinalIgnoreCase);
        }
        _provider.Initialize();
        Register(_provider, usable);
        // Keys one at a time (CUE4Parse keeps one key a GUID from one call): the user's, then FModel's, then the key list's.
        _provider.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
        List<CUE4Parse.UE4.VirtualFileSystem.IAesVfsReader> Locked() =>
            _provider.UnloadedVfs.Where(vfs => vfs.IsEncrypted && !usable.Any(m => m.Registered.Contains(vfs.Name))).ToList();
        foreach (var (guid, key, from) in _settings.Keys(game.Name, game.Project).Select(k => (k.Guid, k.Key, "Game settings.txt"))
                     .Concat((fmodel?.Keys ?? []).Select(k => (k.Guid, k.Key, "FModel's settings"))))
        {
            if (Locked().Count == 0) break;
            try { _provider.SubmitKey(guid.Length == 0 ? new FGuid() : new FGuid(guid.Replace("-", "")), new FAesKey(key)); }
            catch (Exception) { usable.ForEach(m => m.Notes.Add($"A key given for {game.Name} in {from} is not a valid key, so it was not used.")); }
        }
        var keyList = AesKeyList.Load(KeyListPath());
        if (Locked().Count > 0 && keyList.Path is not null)
        {
            if (keyList.Other.Count > 0)
                Say($"Key list: {keyList.Entries.Count} AES keys; {keyList.Other.Count} under {string.Join(", ", keyList.Other.Select(o => o.Section).Distinct().Select(s => $"\"{s}\""))} are not game file keys and are not used.");
            foreach (var (entry, key) in keyList.Candidates(game.Name, game.Project))
            {
                int opened;
                try { opened = _provider.SubmitKey(new FGuid(), new FAesKey(key)); }
                catch (Exception) { continue; }
                if (opened == 0) continue;
                Say($"AES key: the key list's entry \"{entry}\" opened {opened} containers");
                usable.ForEach(m => m.Notes.Add($"{game.Name}'s files were opened with the key list's entry \"{entry}\"."));
                if (Locked().Count == 0) break;
            }
        }
        var locked = Locked();
        if (locked.Count > 0)
        {
            var needed = locked.Select(vfs => vfs.EncryptionKeyGuid).Distinct().ToList();
            var which = needed.All(g => g == new FGuid()) ? "key" : $"key ({string.Join(", ", needed.Select(g => g.ToString()))})";
            var tried = keyList.Path is null ? "" : $" No entry for it in the key list ({keyList.Path}) opens them.";
            throw new NeedsUser($"{game.Name} keeps its files locked and needs its AES {which}.{tried} Put it in {_settingsPath} as a line:  {game.Name} key: 0x<64 letters and digits>");
        }
        _provider.PostMount();
        _project = _provider.ProjectName ?? game.Project;
        // A game whose own files need mappings and has none: a mod over it would be read half (its own meshes, none of
        // the game's skeleton, animations or textures), so it is said at once instead.
        if (mappings is null && _provider.Files.Values.FirstOrDefault(f => f.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)
                && f.Path.StartsWith(_project + "/Content/", StringComparison.OrdinalIgnoreCase) && !(f is VfsEntry { Vfs: { } v } && usable.Any(m => m.Registered.Contains(v.Name)))) is { } sample)
        {
            try { _provider.LoadPackage(sample).GetExports().ToList(); }
            catch (MappingException) { throw new NeedsUser(MappingsNeed(game, null)); }
            catch (Exception) { }
        }
        _shared = Path.Combine(_cache, "shared", Token(game.Name).Trim('_'));
        Say($"Mounted {_provider.MountedVfs.Count} containers, {_provider.Files.Count:N0} files");
        foreach (var mod in usable)
        {
            foreach (var vfs in _provider.MountedVfs.Where(v => mod.Registered.Contains(v.Name)))
                foreach (var (path, file) in vfs.Files)
                {
                    if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) continue;
                    mod.Files[ContentRel(path)] = file;
                    _allModFiles[ContentRel(path)] = file;
                }
            _modVfs.UnionWith(mod.Registered);
        }
        var byId = PackageIndex();

        foreach (var mod in usable.Where(m => m.Job.Export))
        {
            cancel.ThrowIfCancellationRequested();
            Say($"-- {mod.Name}");
            _notes = mod.Notes;
            var undo = Prefer(byId, mod);
            try { ExportMod(mod, cancel); }
            catch (MappingException) { mod.Need = MappingsNeed(game, mappings); }
            catch (Unusable unusable) { mod.Problem = unusable.Message; }
            catch (Exception ex) when (ex.Message.Contains("Oodle", StringComparison.OrdinalIgnoreCase))
            {
                mod.Need = "reading this game needs the Oodle decompressor (oodle-data-shared.dll), which FModel downloads the first time it runs. Run FModel once, or put that file beside wowcrucible.exe.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { mod.Problem = $"It could not be read: {Flat(ex.Message)}"; }
            finally { foreach (var (id, file) in undo) byId[id] = file; }
        }
        // A game skeleton can have hundreds of bones (cloth, hair, face rigs) no mesh here moves: the clips keep the used ones.
        foreach (var (skeleton, clip) in _newClips)
            if (_bones.TryGetValue(skeleton, out var bones) && bones.Count > 0) PsaTrim.Keep(clip, bones);
    }

    /// <summary>
    /// Registers every mod's containers with the provider in load order, each under a name whose patch number is its
    /// place (<c>m0003_3_P</c>), so the later wins over the earlier and all over the game, as the game mounts them.
    /// </summary>
    private static void Register(AbstractVfsFileProvider provider, IEnumerable<Mod> mods)
    {
        var order = 0;
        foreach (var mod in mods)
        {
            // Each provider numbers the mods again: only this one's names count.
            mod.Registered.Clear();
            mod.Order.Clear();
            foreach (var container in mod.Containers)
            {
                order++;
                var stem = Path.GetFileNameWithoutExtension(container);
                var patch = $"m{order:D4}_{order}_P";
                var ext = Path.GetExtension(container);
                var dir = Path.GetDirectoryName(container)!;
                mod.Registered.Add(patch + ext);
                mod.Order.Add(patch + ext);
                provider.RegisterVfs(Path.Combine(dir, patch + ext), [File.OpenRead(container)], open =>
                {
                    var file = Path.GetFileName(open);
                    var actual = file.StartsWith(patch, StringComparison.OrdinalIgnoreCase) ? stem + file[patch.Length..] : file;
                    return new FRandomAccessFileStreamArchive(Path.Combine(dir, actual), provider.Versions);
                });
            }
        }
    }

    /// <summary>
    /// The provider's packages by id, with the version that wins by path put in: CUE4Parse resolves a reference (a
    /// material's texture) by package id from whichever container it met first, the game's own, so a mod's
    /// replacement texture would never be seen.
    /// </summary>
    private ConcurrentDictionary<FPackageId, GameFile> PackageIndex()
    {
        if (_provider.Files.ById is not ConcurrentDictionary<FPackageId, GameFile> byId)
            throw new InvalidOperationException("CUE4Parse's package index is not the one this was written for.");
        var replaced = 0;
        foreach (var (id, file) in byId.ToArray())
            if (_provider.Files.TryGetValue(file.Path, out var winner) && !ReferenceEquals(winner, file)) { byId[id] = winner; replaced++; }
        Say($"Package index: {replaced} replaced by a mod's version");
        return byId;
    }

    /// <summary>While one mod is exported its own packages win over every other mod's, as when it is the only one installed; the undo list puts the others back.</summary>
    private List<(FPackageId, GameFile)> Prefer(ConcurrentDictionary<FPackageId, GameFile> byId, Mod mod)
    {
        var undo = new List<(FPackageId, GameFile)>();
        var mine = mod.Files.Values.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, file) in byId)
            if (mine.TryGetValue(file.Path, out var own) && !ReferenceEquals(own, file)) { undo.Add((id, file)); byId[id] = own; }
        return undo;
    }

    private IPackage? Load(GameFile file)
    {
        try { return _provider.LoadPackage(file); }
        catch (MappingException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>An object by its game path (<c>/Game/A/B.B</c>): the mod's own version first, then the winner of all.</summary>
    private T? Find<T>(string gamePath, Mod? mod) where T : UObject
    {
        if (string.IsNullOrWhiteSpace(gamePath)) return null;
        var rel = ContentRel(gamePath);
        var file = mod?.Files.GetValueOrDefault(rel) ?? (_provider.Files.TryGetValue($"{_project}/Content/{rel}.uasset", out var g) ? g : null) ?? _allModFiles.GetValueOrDefault(rel);
        if (file is null || Load(file) is not { } package) return null;
        var objectName = gamePath.Contains('.') ? gamePath[(gamePath.LastIndexOf('.') + 1)..] : rel[(rel.LastIndexOf('/') + 1)..];
        var exports = SafeExports(package).OfType<T>().ToList();
        return exports.FirstOrDefault(e => e.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) ?? exports.FirstOrDefault();
    }

    /// <summary>Whether an object comes from a mod (any dropped one), not from the game itself.</summary>
    private bool FromMod(UObject obj)
    {
        var package = obj.Owner?.Name ?? obj.GetPathName();
        var rel = ContentRel(package);
        return _provider.Files.TryGetValue($"{_project}/Content/{rel}.uasset", out var file) ? file is VfsEntry { Vfs: { } vfs } && _modVfs.Contains(vfs.Name) : _allModFiles.ContainsKey(rel);
    }

    // ---- Versions, mappings, natives (as FModel finds them) ----

    /// <summary>
    /// How CUE4Parse reads the game: the user's line, FModel's choice for the folder, or the engine version in the game's
    /// executable, as a game of its own when CUE4Parse has one by that name.
    /// </summary>
    private EGame? Version(UnrealGame game, FModelGame? fmodel)
    {
        (int, int)? version = null;
        if (_settings.Value("engine", game.Name, game.Project) is { } given)
        {
            var match = Regex.Match(given, @"(\d+)\.(\d+)");
            if (match.Success) version = (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
            else if (Enum.TryParse<EGame>(given, true, out var named)) return named;
        }
        if (version is null && fmodel is { UeVersion: > 0 } && Enum.IsDefined(typeof(EGame), fmodel.UeVersion)) return (EGame)fmodel.UeVersion;
        version ??= UnrealGameLocator.EngineVersion(game);
        if (version is not var (major, minor)) return null;
        var family = (uint)((major << 24) | (minor << 16));
        var names = new[] { UnrealGameLocator.Fold(game.Name), UnrealGameLocator.Fold(game.Project) };
        foreach (var value in Enum.GetValues<EGame>())
        {
            var folded = UnrealGameLocator.Fold(value.ToString().Replace("GAME_", ""));
            if (((uint)value & 0xFFFF0000) == family && folded.Length >= 5 && names.Any(n => n.Contains(folded) || (n.Length >= 5 && folded.Contains(n))))
                return value;
        }
        return Enum.TryParse<EGame>($"GAME_UE{major}_{minor}", out var plain) ? plain : major == 5 ? EGame.GAME_UE5_LATEST : EGame.GAME_UE4_LATEST;
    }

    /// <summary>What the user is told when a game's files need a mappings file: the missing piece, where it was looked for, the line to add.</summary>
    private string MappingsNeed(UnrealGame game, string? mappings) => mappings is null
        ? $"{game.Name} needs a mappings file to be read: a .usmap file made for the version of {game.Name} that is installed. None is on this PC (not in {Path.GetFileName(_settingsPath)}, FModel's settings, FModel's .data folder or the game's folder). Get one made for this version of the game, then put a line in {_settingsPath}:  {game.Name} mappings: <where the .usmap file is>"
        : $"{game.Name}'s mappings file ({mappings}) does not fit the installed version of the game, so its files cannot be read. Get a .usmap made for this version and name it in {_settingsPath}:  {game.Name} mappings: <where the .usmap file is>";

    /// <summary>
    /// The community AES key list (<see cref="AesKeyList.FileName"/>) beside the settings file or in the folder above it:
    /// tried after the user's keys and FModel's, for a game whose files are still locked.
    /// </summary>
    private string? KeyListPath()
    {
        if (_settingsPath.Length == 0) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(_settingsPath));
        for (var i = 0; i < 2 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
            if (Path.Combine(dir, AesKeyList.FileName) is var file && File.Exists(file)) return file;
        return null;
    }

    private string? Mappings(UnrealGame game, FModelGame? fmodel, string? fmodelData)
    {
        if (_settings.Value("mappings", game.Name, game.Project) is { } given)
        {
            if (File.Exists(given)) return given;
            _notes.Add($"The mappings file given for {game.Name} is not there: {given}");
        }
        if (fmodel?.Mappings is { } fm && File.Exists(fm)) return fm;
        if (UnrealGameLocator.MappingsInside(game).FirstOrDefault() is { } inside) return inside;
        if (fmodelData is not null && Directory.Exists(Path.Combine(fmodelData, "mappings")))
        {
            var names = new[] { UnrealGameLocator.Fold(game.Name), UnrealGameLocator.Fold(game.Project) };
            return Directory.GetFiles(Path.Combine(fmodelData, "mappings"), "*.usmap", SearchOption.AllDirectories)
                .Where(f => names.Any(n => UnrealGameLocator.Fold(Path.GetFileNameWithoutExtension(f)).Contains(n)))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    /// <summary>
    /// Oodle and zlib for compressed game files, and Detex for some texture formats: beside this tool, or where FModel keeps its own copies.
    /// CUE4Parse-Natives (animation decompression, ACL) ships beside this tool and is found by its name.
    /// </summary>
    private static void InitNatives(string? fmodelData)
    {
        var places = new[] { AppContext.BaseDirectory, fmodelData }.Where(p => p is not null && Directory.Exists(p)).Cast<string>().ToList();
        foreach (var name in new[] { OodleHelper.OODLE_NAME_CURRENT, OodleHelper.OODLE_NAME_OLD })
        {
            var file = places.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
            if (file is null) continue;
            try { OodleHelper.Initialize(file); break; } catch (Exception) { }
        }
        // Detex decodes BC7 and other block formats some mods use; FModel keeps its copy beside Oodle.
        var detex = places.Select(p => Path.Combine(p, CUE4Parse_Conversion.Textures.BC.DetexHelper.DLL_NAME)).FirstOrDefault(File.Exists);
        if (detex is not null)
        {
            try { CUE4Parse_Conversion.Textures.BC.DetexHelper.Initialize(detex); } catch (Exception) { }
        }
        var zlib = places.Select(p => Path.Combine(p, ZlibHelper.DLL_NAME)).FirstOrDefault(File.Exists);
        if (zlib is not null)
        {
            try { ZlibHelper.Initialize(zlib); } catch (Exception) { }
        }
    }

    // ---- Looks ----

    private static string Norm(string path, string project)
    {
        path = path.Replace('\\', '/');
        var dot = path.LastIndexOf('.');
        if (dot > path.LastIndexOf('/')) path = path[..dot];
        if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) path = project + "/Content/" + path[6..];
        return path.TrimStart('/').ToLowerInvariant();
    }

    private static string PackageOf(UObject obj, string project) => Norm(obj.Owner?.Name ?? obj.GetPathName(), project);

    /// <summary>The words of a name: split at anything not a letter or digit and where lower case meets upper.</summary>
    internal static List<string> Words(string name)
    {
        var words = new List<string>();
        foreach (var part in Regex.Split(name, "[^A-Za-z0-9]+"))
        {
            var word = new StringBuilder();
            var previous = ' ';
            foreach (var c in part)
            {
                if (char.IsLower(previous) && char.IsUpper(c) && word.Length > 0) { words.Add(word.ToString().ToLowerInvariant()); word.Clear(); }
                word.Append(c);
                previous = c;
            }
            if (word.Length > 0) words.Add(word.ToString().ToLowerInvariant());
        }
        return words;
    }

    /// <summary>Words of a mesh not meant to be worn: cinematics, debug and leftovers.</summary>
    private static readonly string[] OddMesh = ["title", "cin", "cinematic", "test", "dead", "die", "lod", "morph", "old", "temp", "ui", "photo", "preview", "proxy", "dummy", "physics", "jali"];

    /// <summary>One mesh a look wears, and what its material slots show instead of their own.</summary>
    private sealed class Worn(USkeletalMesh mesh)
    {
        public USkeletalMesh Mesh { get; } = mesh;
        public Dictionary<int, UMaterialInterface> Materials { get; } = [];
        public Dictionary<int, List<(string Name, JsonElement Value, string Type)>> Parameters { get; } = [];
        public HashSet<int> Hidden { get; } = [];
        /// <summary><c>hair</c> or <c>head</c> for a piece a CNS list says goes on the head, so benilla hangs it there.</summary>
        public string? Role { get; set; }
        public bool Changed => Materials.Count > 0 || Parameters.Count > 0 || Hidden.Count > 0;
    }

    private void ExportMod(Mod mod, CancellationToken cancel)
    {
        var looks = new List<(string Name, List<Worn> Meshes)>();
        if (mod.Cns.Count > 0) looks.AddRange(CnsLooks(mod, cancel));
        if (looks.Count == 0) looks.AddRange(PlainLooks(mod, cancel));
        if (looks.Count == 0) throw new Unusable("it changes no character model (no skeletal mesh, texture or material of one), so there is no look in it.");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var k = 0; k < looks.Count; k++)
        {
            cancel.ThrowIfCancellationRequested();
            var (name, meshes) = looks[k];
            var unique = name;
            for (var n = 2; !used.Add(unique); n++) unique = $"{name} {n}";
            try
            {
                var folder = (k + 1).ToString("D2");
                WriteLook(mod, folder, unique, meshes, cancel);
                mod.Looks.Add((unique, folder));
            }
            catch (MappingException) { throw; }
            catch (Exception ex) when (ex is not OperationCanceledException) { mod.Notes.Add($"{unique}: it could not be written: {Flat(ex.Message)}"); }
        }
    }

    // A CNS outfit list: what it says, read the way DekCNS's main.lua reads it.

    private IEnumerable<(string Name, List<Worn> Meshes)> CnsLooks(Mod mod, CancellationToken cancel)
    {
        var entries = new List<JsonElement>();
        foreach (var file in mod.Cns)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Array) entries.AddRange(doc.RootElement.EnumerateArray().Select(e => e.Clone()));
                else if (doc.RootElement.ValueKind == JsonValueKind.Object) entries.Add(doc.RootElement.Clone());
            }
            catch (JsonException ex) { mod.Notes.Add($"Its outfit list \"{Path.GetFileName(file)}\" could not be read ({Flat(ex.Message)})."); }
        }
        string Str(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        List<string> Strs(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToList() : [];
        string Type(JsonElement e) => Str(e, "FitMeshType") is { Length: > 0 } t ? t : "Body";

        // The variants an entry offers: (name, the mesh it is, the slots it changes).
        List<(string Variant, Worn Worn)> Variants(JsonElement e)
        {
            var list = new List<(string, Worn)>();
            var names = Strs(e, "OutfitNames");
            var hidden = new HashSet<int>();
            if (e.TryGetProperty("UserConfigs", out var configs) && configs.TryGetProperty("MaterialToggles", out var toggles) && toggles.ValueKind == JsonValueKind.Array)
                foreach (var t in toggles.EnumerateArray())
                    if (t.TryGetProperty("Value", out var on) && on.ValueKind == JsonValueKind.False && t.TryGetProperty("MaterialIndex", out var mi) && mi.TryGetInt32(out var index))
                        hidden.Add(index);
            var datas = e.TryGetProperty("OutfitDatas", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray().ToList() : [];
            if (datas.Count > 0)
            {
                for (var i = 0; i < datas.Count; i++)
                {
                    var data = datas[i];
                    var meshPath = Str(data, "Mesh");
                    if (Find<USkeletalMesh>(meshPath, mod) is not { } mesh) { mod.Notes.Add($"{Str(e, "DisplayName")}: its mesh {meshPath} is not in the mod or the game, so that variant was skipped."); continue; }
                    var worn = new Worn(mesh);
                    worn.Hidden.UnionWith(hidden);
                    var mats = Strs(data, "Materials");
                    for (var m = 0; m < mats.Count; m++)
                        if (mats[m].Length > 0 && Find<UMaterialInterface>(mats[m], mod) is { } material) worn.Materials[m] = material;
                    if (data.TryGetProperty("Parameters", out var ps) && ps.ValueKind == JsonValueKind.Array)
                        foreach (var p in ps.EnumerateArray())
                        {
                            if (p.TryGetProperty("LayerIndex", out var layer) && layer.TryGetInt32(out var li) && li >= 0) continue;
                            var slot = p.TryGetProperty("MaterialIndex", out var s) && s.TryGetInt32(out var si) ? si : 0;
                            if (!p.TryGetProperty("Value", out var value)) continue;
                            if (!worn.Parameters.TryGetValue(slot, out var list2)) worn.Parameters[slot] = list2 = [];
                            list2.Add((Str(p, "ParamName"), value.Clone(), Str(p, "ParamType")));
                        }
                    if (data.TryGetProperty("HiddenMats", out var hm) && hm.ValueKind == JsonValueKind.Array)
                        foreach (var h in hm.EnumerateArray())
                        {
                            if (h.TryGetInt32(out var hi)) worn.Hidden.Add(hi);
                            else if (h.ValueKind == JsonValueKind.String)
                                for (var slot = 0; slot < mesh.Materials.Length; slot++)
                                    if (mesh.Materials[slot]?.ResolvedObject?.Name.Text.Equals(h.GetString(), StringComparison.OrdinalIgnoreCase) == true) worn.Hidden.Add(slot);
                        }
                    list.Add((i < names.Count && names[i].Length > 0 ? names[i] : mesh.Name, worn));
                }
            }
            else
            {
                var paths = Strs(e, "OutfitPaths");
                for (var i = 0; i < paths.Count; i++)
                {
                    if (Find<USkeletalMesh>(paths[i], mod) is not { } mesh) { mod.Notes.Add($"{Str(e, "DisplayName")}: its mesh {paths[i]} is not in the mod or the game, so that variant was skipped."); continue; }
                    var worn = new Worn(mesh);
                    worn.Hidden.UnionWith(hidden);
                    list.Add((i < names.Count && names[i].Length > 0 ? names[i] : Regex.Replace(mesh.Name, "[_]+", " "), worn));
                }
            }
            return list;
        }

        var bodies = entries.Where(e => Type(e).Equals("Body", StringComparison.OrdinalIgnoreCase)).ToList();
        var hairs = entries.Where(e => Type(e).Equals("Hair", StringComparison.OrdinalIgnoreCase)).ToList();
        var pieces = entries.Except(bodies).Except(hairs).ToList();
        var hair = hairs.SelectMany(Variants).Select(v => v.Worn).FirstOrDefault();
        foreach (var piece in pieces.Where(_ => bodies.Count > 0))
            mod.Notes.Add($"Its {Type(piece).ToLowerInvariant()} piece \"{Str(piece, "DisplayName")}\" is chosen apart from the outfit in the game, so it is not worn here.");
        var made = 0;
        foreach (var entry in bodies)
        {
            cancel.ThrowIfCancellationRequested();
            var title = Str(entry, "DisplayName") is { Length: > 0 } dn ? dn : Str(entry, "UniqueFitID");
            var variants = Variants(entry);
            foreach (var (variant, worn) in variants)
            {
                made++;
                var name = variants.Count == 1 ? title : $"{title} - {variant}";
                yield return (name, WithCompanions([worn], hair));
            }
        }
        if (bodies.Count > 0) yield break;

        // No outfit of its own: what it adds, worn over the character's own suit.
        foreach (var entry in pieces.Concat(hairs))
        {
            var type = Type(entry);
            if (type.Equals("Weapon", StringComparison.OrdinalIgnoreCase)) { mod.Notes.Add($"\"{Str(entry, "DisplayName")}\" is a weapon, not something worn, so it was skipped."); continue; }
            foreach (var (variant, worn) in Variants(entry).Take(1))
            {
                if ((OwnSuit(worn.Mesh) ?? SuitOf(Str(entry, "CharacterID"))) is not { } suit) { mod.Notes.Add($"\"{Str(entry, "DisplayName")}\": the character's own suit was not found to wear it with, so it was skipped."); continue; }
                var name = Str(entry, "DisplayName") is { Length: > 0 } dn ? dn : variant;
                var isHair = type.Equals("Hair", StringComparison.OrdinalIgnoreCase);
                var meshes = WithCompanions([new Worn(suit)], isHair ? worn : null);
                if (!isHair) { worn.Role = "head"; meshes.Add(worn); }
                yield return ($"{name} (on{CharacterOf(suit)}'s own suit)", meshes);
            }
        }
    }

    private static string CharacterOf(USkeletalMesh mesh)
    {
        var words = Words(mesh.Name).TakeWhile(w => !w.Any(char.IsDigit)).Where(w => w is not ("ch" or "p" or "npc" or "sk")).ToList();
        return words.Count == 0 ? "" : " " + string.Join(" ", words.Select(w => w.ToUpperInvariant()));
    }

    /// <summary>The character's own suit: <c>&lt;skeleton folder&gt;_Body</c> beside its skeleton (<c>CH_P_EVE_01_Body</c>).</summary>
    private USkeletalMesh? OwnSuit(USkeletalMesh any)
    {
        var skeleton = any.Skeleton?.ResolvedObject?.GetPathName();
        if (skeleton is null) return null;
        var folder = skeleton[..skeleton.LastIndexOf('/')];
        var name = folder[(folder.LastIndexOf('/') + 1)..];
        return Find<USkeletalMesh>($"{folder}/{name}_Body.{name}_Body", null);
    }

    /// <summary>A CNS character's own suit by its id (<c>EVE</c>: <c>CH_P_EVE_01_Body</c>), for a piece made with a skeleton of its own.</summary>
    private USkeletalMesh? SuitOf(string character)
    {
        if (character.Length == 0) character = "EVE";
        var pattern = new Regex($@"/[A-Za-z]+_[A-Za-z]+_{Regex.Escape(character)}_0?1/[A-Za-z]+_[A-Za-z]+_{Regex.Escape(character)}_0?1_Body\.uasset$", RegexOptions.IgnoreCase);
        var path = _provider.Files.Keys.FirstOrDefault(k => pattern.IsMatch(k));
        return path is not null && _provider.Files.TryGetValue(path, out var file) && Load(file) is { } package ? SafeExports(package).OfType<USkeletalMesh>().FirstOrDefault() : null;
    }

    /// <summary>A body with the character's own face and hair added where it has none: found beside its skeleton.</summary>
    private List<Worn> WithCompanions(List<Worn> worn, Worn? hair)
    {
        var body = worn[0].Mesh;
        bool Wears(USkeletalMesh mesh, params string[] what) =>
            Words(mesh.Name).Any(what.Contains) || mesh.Materials.Any(m => m?.ResolvedObject?.Name.Text is { } n && Words(n).Any(what.Contains));
        var list = new List<Worn>(worn);
        if (!worn.Any(w => Wears(w.Mesh, "head", "face")) && Companion(body, "face") is { } face) list.Add(new Worn(face));
        if (hair is not null) { hair.Role = "hair"; list.Add(hair); }
        else if (!worn.Any(w => Wears(w.Mesh, "hair")) && Companion(body, "hair") is { } own) list.Add(new Worn(own));
        return list;
    }

    private readonly Dictionary<string, USkeletalMesh?> _companions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The character's own <paramref name="what"/> (face or hair): a mesh of the body's skeleton in the skeleton's
    /// folder or one beside it, named for the character (<c>CH_P_EVE</c> of <c>CH_P_EVE_01</c>) and <paramref name="what"/>,
    /// the plainest name first.
    /// </summary>
    private USkeletalMesh? Companion(USkeletalMesh body, string what)
    {
        var skeleton = body.Skeleton?.ResolvedObject?.GetPathName();
        if (skeleton is null) return null;
        var key = skeleton + "|" + what;
        if (_companions.TryGetValue(key, out var known)) return known;
        var rel = ContentRel(skeleton);
        var folder = rel[..Math.Max(0, rel.LastIndexOf('/'))];
        var around = folder.Contains('/') ? folder[..folder.LastIndexOf('/')] + "/" : "";
        var character = Words(folder[(folder.LastIndexOf('/') + 1)..]).TakeWhile(w => !w.Any(char.IsDigit)).ToList();
        USkeletalMesh? found = null;
        if (character.Count > 0 && around.Length > 0)
        {
            var prefix = $"{_project}/Content/{around}".ToLowerInvariant();
            string[] never = ["skeleton", "physics", "clothdata", "anim", "blueprint", "mi", "ma", "t", "tex", "snapshot", "collision", "for", "seperate", "seperated", "separate", "ex", "exhair", "rig", "rigpls", "ctrl", "ponytail"];
            var candidates = _provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && k.Count(c => c == '/') <= prefix.Count(c => c == '/') + 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(k => (Path: k, Words: Words(Path.GetFileNameWithoutExtension(k))))
                .Where(c => c.Words.Contains(what) && c.Words.Take(character.Count).SequenceEqual(character)
                    && !c.Words.Any(w => OddMesh.Contains(w) || never.Contains(w) || w.StartsWith("jali", StringComparison.Ordinal)))
                .OrderBy(c => c.Words.Count).ThenByDescending(c => Path.GetFileNameWithoutExtension(c.Path), StringComparer.OrdinalIgnoreCase).Take(20);
            foreach (var (path, _) in candidates)
            {
                if (!_provider.Files.TryGetValue(path, out var file) || Load(file) is not { } package) continue;
                var mesh = SafeExports(package).OfType<USkeletalMesh>().FirstOrDefault();
                if (mesh is not null && mesh.Skeleton?.ResolvedObject?.GetPathName() == skeleton) { found = mesh; break; }
            }
        }
        _companions[key] = found;
        return found;
    }

    // Any other mod: the character meshes it changes, as the game would show them.

    private IEnumerable<(string Name, List<Worn> Meshes)> PlainLooks(Mod mod, CancellationToken cancel)
    {
        // A folder of several pak sets each with a character of its own: a look each (options of one mod).
        var perSet = new List<(string Name, List<string> Packages)>();
        for (var i = 0; i < mod.Containers.Count; i++)
        {
            var registered = mod.Order[i];
            if (_provider.MountedVfs.FirstOrDefault(v => v.Name.Equals(registered, StringComparison.OrdinalIgnoreCase)) is not { } vfs) continue;
            perSet.Add((Path.GetFileNameWithoutExtension(mod.Containers[i]), vfs.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).ToList()));
        }
        var all = perSet.SelectMany(s => s.Packages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        (List<USkeletalMesh> Meshes, HashSet<string> Overrides) Read(List<string> packages)
        {
            var meshes = new List<USkeletalMesh>();
            var overrides = new HashSet<string>();
            foreach (var path in packages)
            {
                cancel.ThrowIfCancellationRequested();
                if (!mod.Files.TryGetValue(ContentRel(path), out var file) || Load(file) is not { } package) continue;
                foreach (var export in SafeExports(package))
                {
                    switch (export)
                    {
                        case USkeletalMesh mesh: meshes.Add(mesh); break;
                        case UTexture or UMaterialInterface: overrides.Add(PackageOf(export, _project)); break;
                    }
                }
            }
            return (meshes, overrides);
        }

        var readSets = perSet.Select(s => (s.Name, Read: Read(s.Packages))).ToList();
        if (readSets.Count(s => s.Read.Meshes.Count > 0) > 1 && readSets.All(s => s.Read.Meshes.Count > 0))
        {
            foreach (var (name, read) in readSets)
                if (Assemble(read.Meshes, read.Overrides, cancel) is { Count: > 0 } meshes) yield return ($"{mod.Name} - {Regex.Replace(Regex.Replace(name, "_P$", ""), "[_]+", " ")}", meshes);
            yield break;
        }
        var whole = (Meshes: readSets.SelectMany(s => s.Read.Meshes).ToList(), Overrides: readSets.SelectMany(s => s.Read.Overrides).ToHashSet());
        Say($"The mod changes {all.Count} assets.");
        var assembled = Assemble(whole.Meshes, whole.Overrides, cancel);
        if (assembled.Count == 0)
        {
            if (whole.Overrides.Count > 0) throw new Unusable("it changes textures or materials, but no character in the game was found that wears them.");
            yield break;
        }
        yield return (mod.Name, assembled);
    }

    private List<Worn> Assemble(List<USkeletalMesh> meshes, HashSet<string> overrides, CancellationToken cancel)
    {
        // What the mod's own meshes wear; what is left is worn by meshes of the game it did not change.
        var worn = new HashSet<string>();
        foreach (var mesh in meshes) foreach (var p in WornBy(mesh, _project)) worn.Add(p);
        var leftover = overrides.Where(o => !worn.Contains(o)).ToList();
        foreach (var mesh in BaseMeshesWearing(leftover, meshes, cancel))
        {
            Say($"Also worn: {mesh.Name} (from the game; the mod changes its look)");
            meshes.Add(mesh);
        }
        if (meshes.Count == 0) return [];
        var list = meshes.Select(m => new Worn(m)).ToList();
        if (!meshes.Any(m => Words(m.Name).Contains("hair")) && Companion(meshes[0], "hair") is { } hair)
        {
            Say($"Also worn: {hair.Name} (the character's hair, from the game)");
            list.Add(new Worn(hair));
        }
        return list;
    }

    private static IEnumerable<UObject> SafeExports(IPackage package)
    {
        try { return package.GetExports().ToList(); }
        catch (MappingException) { throw; }
        catch (Exception) { return []; }
    }

    /// <summary>The materials a mesh wears and the textures they show, as package paths.</summary>
    private static IEnumerable<string> WornBy(USkeletalMesh mesh, string project)
    {
        foreach (var slot in mesh.Materials)
        {
            if (slot?.ResolvedObject?.Load() is not UMaterialInterface material) continue;
            yield return PackageOf(material, project);
            var parameters = new CMaterialParams2();
            try { material.GetParams(parameters, EMaterialDepth.AllLayersNoRef); } catch (Exception) { continue; }
            foreach (var loadable in parameters.Textures.Values)
                if (loadable.TryLoad<UTexture>(out var texture) && texture is not null) yield return PackageOf(texture, project);
        }
    }

    /// <summary>
    /// For textures and materials the mod changes that its own meshes do not wear: the game's meshes that wear them,
    /// found in the character's folder (the texture's folder, above any "Textures" or "Materials" folder), one a folder.
    /// </summary>
    private List<USkeletalMesh> BaseMeshesWearing(List<string> leftover, List<USkeletalMesh> have, CancellationToken cancel)
    {
        var found = new List<USkeletalMesh>();
        if (leftover.Count == 0) return found;
        string[] plain = ["textures", "texture", "materials", "material", "tex", "mat", "maps", "old", "new", "ma", "mi"];
        var folders = leftover.Select(path =>
        {
            var dir = path[..Math.Max(0, path.LastIndexOf('/'))];
            while (dir.Contains('/') && plain.Contains(dir[(dir.LastIndexOf('/') + 1)..])) dir = dir[..dir.LastIndexOf('/')];
            return dir;
        }).Distinct().ToList();
        var haveSkeletons = have.Select(m => m.Skeleton?.ResolvedObject?.GetPathName()).Where(p => p is not null).ToHashSet();
        var allPaths = _provider.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var folder in folders)
        {
            if (folder.Split('/').Length < 4) continue; // never a whole game's content
            var inside = allPaths.Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && p.ToLowerInvariant().StartsWith(folder + "/")).Take(1500).ToList();
            var best = (Mesh: (USkeletalMesh?)null, Score: (0, 0, 0));
            foreach (var path in inside)
            {
                cancel.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(path);
                if (Words(name).Any(w => w is "t" or "tex" or "mi" or "ma" or "m")) continue; // a texture or material by its name
                if (!_provider.TryLoadPackage(path, out var package)) continue;
                foreach (var mesh in SafeExports(package).OfType<USkeletalMesh>())
                {
                    if (have.Any(h => h.GetPathName() == mesh.GetPathName())) continue;
                    var wears = WornBy(mesh, _project).Distinct().Count(leftover.Contains);
                    if (wears == 0) continue;
                    var sameSkeleton = haveSkeletons.Count == 0 || haveSkeletons.Contains(mesh.Skeleton?.ResolvedObject?.GetPathName()) ? 1 : 0;
                    var score = (wears + sameSkeleton, -Words(mesh.Name).Count(OddMesh.Contains), -mesh.Name.Length);
                    if (best.Mesh is null || score.CompareTo(best.Score) > 0) best = (mesh, score);
                }
            }
            if (best.Mesh is not null) found.Add(best.Mesh);
        }
        return found;
    }

    // ---- Writing a look ----

    private static string Token(string name)
    {
        var t = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray()).Trim('.');
        return t.Length == 0 ? "_" : t;
    }

    private string Rel(string full) => Path.GetRelativePath(_cache, full).Replace('\\', '/');

    private string SavePath(UObject obj)
    {
        var package = obj.Owner?.Name ?? obj.GetPathName();
        var fixedPath = _provider.FixPath(package);
        var dot = fixedPath.LastIndexOf('.');
        if (dot > fixedPath.LastIndexOf('/')) fixedPath = fixedPath[..dot];
        if (!fixedPath[(fixedPath.LastIndexOf('/') + 1)..].Equals(obj.Name, StringComparison.OrdinalIgnoreCase)) fixedPath += "/" + obj.Name;
        return fixedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>Where an object's files go: <c>shared\</c> for the game's own, the mod's <c>files\</c> for a mod's.</summary>
    private string RootFor(Mod mod, bool fromMod) => fromMod ? Path.Combine(_cache, mod.Job.Id, "files") : _shared;

    private void WriteLook(Mod mod, string folder, string name, List<Worn> meshes, CancellationToken cancel)
    {
        var uses = new List<string>();
        var roles = new List<string>();
        var options = new ExportOptions(meshFormat: EMeshFormat.ActorX, meshQuality: EMeshQuality.Highest, texturePlatform: ETexturePlatform.DesktopMobile,
            textureFormat: ETextureFormat.Png, exportMaterials: false, exportMorphTargets: false, socketFormat: ESocketFormat.Bone);
        foreach (var worn in meshes)
        {
            var mesh = worn.Mesh;
            var fromMod = FromMod(mesh);
            var key = $"{(fromMod ? mod.Job.Id : "")}|{mesh.GetPathName()}";
            if (!_written.TryGetValue(key, out var files))
            {
                var root = RootFor(mod, fromMod);
                var stem = Path.Combine(root, SavePath(mesh));
                var existing = new[] { ".pskx", ".psk" }.Select(e => stem + e).FirstOrDefault(File.Exists);
                if (existing is null || fromMod)
                {
                    var session = new ExportSession();
                    session.Add(mesh);
                    var results = session.RunAsync(root, options, null, cancel).GetAwaiter().GetResult();
                    foreach (var failed in results.Where(r => !r.Success))
                    {
                        if (failed.Error is MappingException mapping) throw mapping;
                        _notes.Add($"{mesh.Name} could not be exported: {failed.Error?.Message}");
                    }
                    existing = results.Where(r => r.Success).SelectMany(r => r.DiskFilePaths ?? []).Cast<string?>()
                        .FirstOrDefault(p => p!.EndsWith(".psk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".pskx", StringComparison.OrdinalIgnoreCase));
                }
                files = existing is null ? [] : [Rel(existing)];
                _written[key] = files;
            }
            uses.AddRange(files);
            if (worn.Role is { } role && files.Count > 0) roles.Add($"{role}: {Path.GetFileNameWithoutExtension(files[0])}");
            if (meshes[0].Mesh.Skeleton?.ResolvedObject?.GetPathName() is { } main)
                foreach (var psk in files)
                {
                    if (!_bones.TryGetValue(main, out var bones)) _bones[main] = bones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    try { bones.UnionWith(PsaTrim.BoneNames(Path.Combine(_cache, psk))); } catch (Exception) { }
                }
            for (var slot = 0; slot < mesh.Materials.Length; slot++)
                uses.AddRange(MaterialFiles(mod, folder, worn, slot));
        }
        if (!uses.Any(u => u.EndsWith(".psk", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".pskx", StringComparison.OrdinalIgnoreCase)))
            throw new Unusable("its model could not be exported from the game files.");

        var look = new StringBuilder($"name: {name}\r\n");
        foreach (var role in roles) look.Append(role + "\r\n");
        var skeleton = meshes[0].Mesh.Skeleton?.ResolvedObject?.Load() as USkeleton;
        if (skeleton is not null)
        {
            var clips = Clips(skeleton, cancel);
            if (clips.Count == 0) _notes.Add("No animations were found in the game for it, so it will stand still.");
            foreach (var (word, clip, file) in clips)
            {
                look.Append($"{word}: {clip}\r\n");
                uses.Add(file);
            }
        }
        foreach (var use in uses.Distinct(StringComparer.OrdinalIgnoreCase)) look.Append($"use: {use}\r\n");
        var dir = Path.Combine(_cache, mod.Job.Id, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "look.txt"), look.ToString());
    }

    /// <summary>A few animations of the skeleton, written once under <c>shared\</c> (or the mod's own where the mod has them).</summary>
    private List<(string Word, string Clip, string File)> Clips(USkeleton skeleton, CancellationToken cancel)
    {
        var key = skeleton.GetPathName();
        if (_clips.TryGetValue(key, out var known)) return known;
        var chosen = Animations(skeleton, cancel);
        var list = new List<(string, string, string)>();
        if (chosen.Count > 0) Say($"Animations: {string.Join(", ", chosen.Select(c => $"{c.Word} {c.Clip.Name}"))}");
        var options = new ExportOptions(meshFormat: EMeshFormat.ActorX, meshQuality: EMeshQuality.Highest, texturePlatform: ETexturePlatform.DesktopMobile,
            textureFormat: ETextureFormat.Png, exportMaterials: false, exportMorphTargets: false, socketFormat: ESocketFormat.Bone);
        var done = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (word, clip) in chosen)
        {
            if (!done.TryGetValue(clip.GetPathName(), out var file))
            {
                var root = FromMod(clip) ? Path.Combine(_cache, "shared-mods", Token(_gameName).Trim('_')) : _shared;
                var target = Path.Combine(root, SavePath(clip) + ".psa");
                if (!File.Exists(target))
                {
                    var session = new ExportSession();
                    session.Add(clip);
                    var results = session.RunAsync(root, options, null, cancel).GetAwaiter().GetResult();
                    foreach (var failed in results.Where(r => !r.Success))
                    {
                        if (failed.Error is MappingException mapping) throw mapping;
                        _notes.Add($"{Path.GetFileName(failed.ObjectPath)} could not be exported: {failed.Error?.Message}");
                    }
                    target = results.Where(r => r.Success).SelectMany(r => r.DiskFilePaths ?? []).Cast<string?>().FirstOrDefault(p => p!.EndsWith(".psa", StringComparison.OrdinalIgnoreCase));
                    if (target is not null) _newClips.Add((key, target));
                }
                file = target is null ? null : Rel(target);
                done[clip.GetPathName()] = file;
            }
            if (file is not null) list.Add((word, clip.Name, file));
        }
        _clips[key] = list;
        return list;
    }

    // ---- Materials: the colour each really shows ----

    /// <summary>
    /// The files a mesh's material slot draws with: its <c>.json</c> as FModel writes it, and a <c>.png</c> of the
    /// colour the material really shows (<see cref="Bake"/>) named in it as <c>PM_Diffuse</c>. A slot an outfit
    /// changes (another material, parameters, hidden) is written for this look alone.
    /// </summary>
    private List<string> MaterialFiles(Mod mod, string lookFolder, Worn worn, int slot)
    {
        var original = worn.Mesh.Materials[slot]?.ResolvedObject?.Load() as UMaterialInterface;
        var name = original?.Name ?? worn.Mesh.SkeletalMaterials.ElementAtOrDefault(slot)?.MaterialSlotName.Text;
        if (string.IsNullOrEmpty(name) || name == "None") return [];
        var material = worn.Materials.GetValueOrDefault(slot) ?? original;
        var changed = worn.Materials.ContainsKey(slot) || worn.Parameters.ContainsKey(slot) || worn.Hidden.Contains(slot);
        var key = changed || material is null ? null : $"{mod.Job.Id}|{material.GetPathName()}";
        if (key is not null && _written.TryGetValue(key, out var known)) return known;

        var parameters = new CMaterialParams2();
        if (material is not null)
        {
            try { material.GetParams(parameters, EMaterialDepth.AllLayersNoRef); }
            catch (MappingException) { throw; }
            catch (Exception ex) { _notes.Add($"The material {material.Name} could not be read: {ex.Message}"); }
        }
        foreach (var (param, value, type) in worn.Parameters.GetValueOrDefault(slot) ?? [])
        {
            if (type.Equals("Texture", StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String && Find<UTexture>(value.GetString()!, mod) is { } texture)
                parameters.Textures[param] = new LoadedTexture(texture);
            else if (type.Equals("Vector", StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.Array)
            {
                var c = value.EnumerateArray().Select(v => v.TryGetSingle(out var f) ? f : 0f).Concat([0f, 0f, 0f, 1f]).ToArray();
                parameters.Colors[param] = new FLinearColor(c[0], c[1], c[2], c[3]);
            }
        }
        var fromMod = changed || (material is not null && FromMod(material)) || parameters.Textures.Values.Any(t => t.TryLoad<UTexture>(out var x) && x is not null && FromMod(x));
        var dir = changed ? Path.Combine(_cache, mod.Job.Id, "files", "looks", lookFolder) : RootFor(mod, fromMod);
        var basePath = changed ? Path.Combine(dir, Token(name)) : Path.Combine(dir, material is not null ? SavePath(material) : Token(name));
        if (!changed && Path.GetFileName(basePath) != name) basePath = Path.Combine(Path.GetDirectoryName(basePath)!, name);
        var jsonPath = basePath + ".json";
        var picturePath = basePath + "_Colour.png";
        var files = new List<string> { Rel(jsonPath) };
        if (!fromMod && File.Exists(jsonPath))
        {
            if (File.Exists(picturePath)) files.Add(Rel(picturePath));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
            var json = JsonNode.Parse(new JsonMaterialFormat().Build(name, parameters).Data)!.AsObject();
            var hidden = worn.Hidden.Contains(slot);
            if (hidden) json["Hide"] = true;
            else if (Bake(name, parameters, worn.Mesh) is { } png)
            {
                File.WriteAllBytes(picturePath, png);
                files.Add(Rel(picturePath));
                var textures = json["Textures"] as JsonObject ?? [];
                json["Textures"] = textures;
                var picture = Path.GetFileNameWithoutExtension(picturePath);
                textures[CMaterialParams2.FallbackDiffuse] = new JsonObject
                {
                    ["ObjectName"] = $"Texture2D'{picture}'",
                    ["ObjectPath"] = $"/{Rel(Path.GetDirectoryName(picturePath)!)}/{picture}.0",
                };
            }
            File.WriteAllText(jsonPath, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        if (key is not null) _written[key] = files;
        return files;
    }

    /// <summary>A texture already loaded, as a material parameter.</summary>
    private sealed class LoadedTexture(UTexture texture) : ILoadableObject
    {
        public Type GetObjectType() => texture.GetType();
        public UObject Load() => texture;
        public bool TryLoad(out UObject export)
        {
            export = texture;
            return true;
        }
        public Task<UObject?> LoadAsync() => Task.FromResult<UObject?>(texture);
        public Task<UObject?> TryLoadAsync() => Task.FromResult<UObject?>(texture);
        public override string ToString() => texture.GetPathName();
    }

    // Vector parameters that tint the base colour, and ones that are something else.
    private static readonly string[] NotTint = ["pattern", "layer", "cosmetic", "emissive", "emission", "glow", "fresnel", "hdr", "hdri", "ao", "shadow", "blur", "rim",
        "sss", "subsurface", "specular", "spec", "edge", "outline", "fog", "light", "flake", "metallic", "inner", "outer", "corner", "darkness", "sclera", "iris",
        "limbus", "env", "cloth", "ml", "sheen", "scatter", "detail", "dirt", "blood", "wet", "noise", "scratch", "fx", "pivot", "axis", "balance", "ch", "mask"];

    private static bool TintLike(string param, bool alone)
    {
        var words = Words(param);
        if (words.Any(NotTint.Contains)) return false;
        return words.Contains("tint") || (alone && words.Any(w => w is "color" or "colour" or "basecolor" or "basecolour" or "tone" or "albedo" or "diffuse"));
    }

    /// <summary>
    /// The colour a material shows, baked into one picture: its base colour texture (the mod's own version) times the
    /// tints it names, in linear light as Unreal multiplies them; with no texture, the colour it names; the alpha its mask
    /// or opacity texture gives a see-through one (eyebrows, lashes). No side over 2048. <c>null</c> when it names no colour.
    /// </summary>
    private byte[]? Bake(string name, CMaterialParams2 parameters, USkeletalMesh mesh)
    {
        var diffuse = Diffuse(parameters, SavePath(mesh));
        SKBitmap? colour = null;
        if (diffuse is not null && diffuse.TryLoad<UTexture>(out var texture) && texture is not null) colour = Decode(texture);
        var tints = parameters.Colors.Where(c => TintLike(c.Key, colour is null)).Select(c => c.Value).ToList();
        if (colour is null && tints.Count == 0) return null;
        var translucent = parameters.IsTranslucent || parameters.BlendMode is not EBlendMode.BLEND_Opaque;
        SKBitmap? mask = null;
        if (translucent)
        {
            var maskParam = parameters.Textures.FirstOrDefault(t => Words(t.Key).Any(w => w is "mask" or "opacity" or "alpha") && t.Value.TryLoad<UTexture>(out var x) && x is not null);
            if (maskParam.Value is not null && maskParam.Value.TryLoad<UTexture>(out var m) && m is not null) mask = Decode(m);
        }
        using var image = colour ?? new SKBitmap(mask?.Width ?? 64, mask?.Height ?? 64, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        if (colour is null)
        {
            var c = tints[0];
            image.Erase(new SKColor(Srgb(c.R), Srgb(c.G), Srgb(c.B), 255));
        }
        else if (tints.Count > 0)
        {
            var (r, g, b) = (1f, 1f, 1f);
            foreach (var t in tints) { r *= t.R; g *= t.G; b *= t.B; }
            var pixels = image.Pixels;
            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                pixels[i] = new SKColor(Srgb(Linear(p.Red) * r), Srgb(Linear(p.Green) * g), Srgb(Linear(p.Blue) * b), p.Alpha);
            }
            image.Pixels = pixels;
        }
        if (mask is not null)
        {
            using var sized = mask.Width == image.Width && mask.Height == image.Height ? null : mask.Resize(new SKImageInfo(image.Width, image.Height), SKFilterQuality.Medium);
            var source = sized ?? mask;
            var pixels = image.Pixels;
            var alpha = source.Pixels;
            // A grey mask keeps its coverage in red; one with its own alpha keeps it there.
            var useAlpha = alpha.Any(p => p.Alpha < 250);
            for (var i = 0; i < pixels.Length && i < alpha.Length; i++)
                pixels[i] = pixels[i].WithAlpha(useAlpha ? alpha[i].Alpha : alpha[i].Red);
            image.Pixels = pixels;
            mask.Dispose();
        }
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static float Linear(byte c)
    {
        var v = c / 255f;
        return v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
    }

    private static byte Srgb(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        var s = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1 / 2.4f) - 0.055f;
        return (byte)Math.Round(s * 255);
    }

    /// <summary>A texture as a picture, no side over 2048 (a smaller mip where it has one).</summary>
    private SKBitmap? Decode(UTexture texture)
    {
        try
        {
            var mips = texture.PlatformData.Mips;
            var first = texture.GetFirstMipIndex();
            var index = first;
            while (index < mips.Length - 1 && Math.Max(mips[index].SizeX, mips[index].SizeY) > MaxTextureSide) index++;
            CTexture? decoded = null;
            if (index != first && mips[index].EnsureValidBulkData(texture.MipDataProvider, index)) decoded = texture.DecodeMip(index, ETexturePlatform.DesktopMobile);
            decoded ??= texture.DecodeMip(first, ETexturePlatform.DesktopMobile);
            if (decoded is null) { _notes.Add($"The picture {texture.Name} could not be read, so what it colours draws grey."); return null; }
            var png = decoded.Encode(ETextureFormat.Png, false, out _);
            var bitmap = SKBitmap.Decode(png);
            if (bitmap is null) return null;
            if (bitmap.ColorType == SKColorType.Rgba8888 && bitmap.AlphaType == SKAlphaType.Unpremul) return bitmap;
            var copy = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            bitmap.CopyTo(copy, SKColorType.Rgba8888);
            if (copy.Width != bitmap.Width || copy.Pixels.Length == 0) { copy.Dispose(); return bitmap; }
            bitmap.Dispose();
            return copy;
        }
        catch (Exception ex) when (ex is not MappingException)
        {
            _notes.Add($"The picture {texture.Name} could not be read ({ex.Message}), so what it colours draws grey.");
            return null;
        }
    }

    /// <summary>
    /// The texture that colours a material: a parameter named only for the base colour (<c>BaseColor</c>, <c>Diffuse</c>)
    /// over one of a layer or pattern (<c>Pattern02_BaseColor</c>), a texture of the mesh's own folders over a shared
    /// library one, and never an environment or mask picture. <c>null</c> when nothing names a colour.
    /// </summary>
    private static ILoadableObject? Diffuse(CMaterialParams2 parameters, string meshPath)
    {
        string[] plain = ["basecolor", "basecolour", "diffuse", "albedo", "color", "colour", "basecolormap", "diffusemap", "basemap", "maintex", "d"];
        string[] colour = ["basecolor", "basecolour", "diffuse", "albedo", "color", "colour"];
        string[] never = ["hdr", "hdri", "env", "cube", "reflection", "matcap", "normal", "mask", "orm", "emissive", "rough", "noise", "grunge", "grime", "scratch", "flake", "blood"];
        var meshFolder = (Path.GetDirectoryName(meshPath) ?? "").Replace('\\', '/').ToLowerInvariant();
        var content = meshFolder.IndexOf("/content/", StringComparison.Ordinal);
        var own = content >= 0 ? meshFolder[(content + 8)..] : meshFolder;
        var best = (Texture: (ILoadableObject?)null, Score: int.MinValue);
        foreach (var (param, loadable) in parameters.Textures)
        {
            if (param.StartsWith("PM_", StringComparison.Ordinal)) continue;
            var key = new string(param.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            var path = (loadable as CUE4Parse.UE4.Objects.UObject.FPackageIndex)?.ResolvedObject?.GetPathName() ?? loadable.ToString() ?? "";
            var name = path[(path.LastIndexOf('/') + 1)..];
            var words = Words(param).Concat(Words(name)).ToList();
            if (words.Any(never.Contains) || never.Any(key.Contains)) continue;
            var score = plain.Contains(key) ? 4 : colour.Any(key.Contains) ? 2 : 0;
            if (Words(name).Any(w => w is "d" or "basecolor" or "diffuse" or "albedo" or "a" or "bc")) score += 1;
            if (score == 0) continue;
            if (own.Length > 0 && path.ToLowerInvariant().Replace("/game/", "/").Contains(own)) score += 3;
            if (score > best.Score) best = (loadable, score);
        }
        return best.Texture;
    }

    // ---- Animations ----

    private sealed record Action(string Word, string[][] Groups, string[] Bonus, string[] Penalty, int Count = 1);

    // benilla's actions (auto.rs ACTIONS), so the clips written are the ones it will pick from.
    private static readonly string[] AlwaysWorse = ["montage", "additive", "add", "old", "copy", "preview", "pose", "mask", "unused", "temp", "facial", "face", "aim", "photo", "cin", "event", "lockon", "test", "hair",
        "swim", "uw", "underwater", "fishing", "fish", "rope", "ladder", "climb", "hang", "balance", "ride", "riding", "vehicle", "boat", "sit", "lie", "selfie", "social", "cutscene", "air", "wall", "sand", "water"];
    private static readonly Action[] Actions =
    [
        new("stand", [["idle", "stand", "breath", "breathing", "rest"]], ["world", "relax", "relaxed", "neutral", "loop", "locomotion", "default", "calm", "normal", "peaceful"],
            ["battle", "combat", "fight", "ready", "to", "transition", "start", "end", "stun", "skill", "item", "ultimate", "freeaim", "defend", "wait", "turn", "left", "right", "injured", "hurt", "sit", "crouch", "lie", "selfie"]),
        new("ready", [["ready", "idle", "stance"]], ["combat", "battle", "fight", "base", "ready", "stance"], ["to", "transition", "stun", "skill", "item", "turn", "left", "right", "world", "selfie", "lie", "sit"]),
        new("walk", [["walk"]], ["loop", "forward", "locomotion"], ["back", "backward", "left", "right", "strafe", "start", "stop", "end", "to", "turn", "crouch", "injured", "balance", "hang"]),
        new("run", [["run", "sprint", "jog"]], ["loop", "forward", "locomotion"], ["back", "backward", "left", "right", "strafe", "start", "stop", "end", "to", "turn", "crouch", "attack"]),
        new("death", [["death", "die", "dying", "dead", "defeat", "killed"]], [], ["revive", "to", "idle", "loop", "end"]),
        new("hit", [["hit", "hurt", "damage", "react", "impact", "flinch"]], ["light", "small", "weak"], ["stun", "heavy", "big", "strong", "attack"]),
        new("attack", [["attack", "slash", "strike", "punch", "swing", "combo", "melee", "kick", "stab"]], ["normal"], ["jump", "aoe", "dash", "to", "transition", "finish", "counter", "back", "link"]),
        new("dodge", [["dodge", "evade", "roll", "sidestep"]], [], ["attack"]),
        new("cast", [["cast", "spell", "magic", "skill", "ability"]], [], ["idle", "to", "transition"]),
        new("jump", [["jump"]], [], ["counter", "attack", "strike", "start", "end", "land", "landing", "loop", "lockon"]),
    ];

    private static bool Has(List<string> words, string w) => words.Any(t =>
        t.StartsWith(w, StringComparison.Ordinal) && (t.Length == w.Length || w.Length >= 4 || new[] { "s", "es", "ed", "ing", "ning", "ting", "ging", "er" }.Contains(t[w.Length..])));

    private static float? Score(string path, Action action)
    {
        var name = Words(Path.GetFileNameWithoutExtension(path));
        var folder = Words(Path.GetDirectoryName(path) ?? "");
        if (!action.Groups.All(g => g.Any(w => Has(name, w)))) return null;
        float Count(List<string> words, string[] list) => list.Count(w => Has(words, w));
        var bonus = Count(name, action.Bonus) + 0.5f * Count(folder, action.Bonus);
        var penalty = Count(name, action.Penalty) + 0.5f * Count(folder, action.Penalty) + AlwaysWorse.Count(w => Has(name, w) || (w.Length >= 4 && name.Any(t => t.Contains(w)))) + 0.5f * Count(folder, AlwaysWorse);
        return 10 + 2 * bonus - 4 * penalty - 0.3f * name.Count;
    }

    /// <summary>
    /// A few animations of the body's skeleton, the ones benilla plays: from the skeleton's folder (and the folders
    /// beside it when that has too few), picked by name and proved by the skeleton they move.
    /// </summary>
    private List<(string Word, UAnimSequence Clip)> Animations(USkeleton skeleton, CancellationToken cancel)
    {
        var skeletonPath = PackageOf(skeleton, _project);
        var all = _provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var dir = skeletonPath[..skeletonPath.LastIndexOf('/')];
        List<string> candidates = [];
        for (var up = 0; up < 3 && dir.Contains('/'); up++)
        {
            var prefix = dir + "/";
            candidates = all.Where(p => p.ToLowerInvariant().StartsWith(prefix)).ToList();
            if (candidates.Count(p => Actions.Any(a => Score(p, a) is not null)) >= 20) break;
            dir = dir[..dir.LastIndexOf('/')];
        }
        var picked = new List<(string Word, UAnimSequence Clip)>();
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in Actions)
        {
            var ranked = candidates.Select(p => (Path: p, Score: Score(p, action))).Where(c => c.Score is not null)
                .OrderByDescending(c => c.Score).ThenBy(c => c.Path.Length).Select(c => c.Path).ToList();
            var got = 0;
            foreach (var path in ranked.Take(12))
            {
                cancel.ThrowIfCancellationRequested();
                if (got >= action.Count) break;
                if (picked.FirstOrDefault(p => p.Clip.Owner?.Name is { } owner && Norm(owner, _project) == Norm(path, _project)).Clip is { } again)
                {
                    picked.Add((action.Word, again));
                    got++;
                    continue;
                }
                if (!tried.Add(path) || !_provider.TryLoadPackage(path, out var package)) continue;
                var clip = SafeExports(package).OfType<UAnimSequence>().FirstOrDefault();
                if (clip?.Skeleton?.ResolvedObject is not { } owned || Norm(owned.GetPathName(), _project) != skeletonPath) continue;
                picked.Add((action.Word, clip));
                got++;
            }
        }
        return picked;
    }
}


/// <summary>ActorX files read as their chunks: a mesh's bone names, and a clip cut down to the bones meshes use.</summary>
internal static class PsaTrim
{
    private const int Header = 32;

    private static List<(string Id, int Size, int Count, int Offset)> Chunks(byte[] bytes)
    {
        var chunks = new List<(string, int, int, int)>();
        for (var at = 0; at + Header <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 20).TrimEnd('\0');
            var size = BitConverter.ToInt32(bytes, at + 24);
            var count = BitConverter.ToInt32(bytes, at + 28);
            chunks.Add((id, size, count, at + Header));
            at += Header + size * count;
        }
        return chunks;
    }

    private static string Name(byte[] bytes, int at) => Encoding.ASCII.GetString(bytes, at, 64).Split('\0')[0].Trim();

    /// <summary>The bone names of a <c>.psk</c>/<c>.pskx</c>.</summary>
    public static IEnumerable<string> BoneNames(string mesh)
    {
        var bytes = File.ReadAllBytes(mesh);
        var skeleton = Chunks(bytes).FirstOrDefault(c => c.Id.StartsWith("REFSKEL", StringComparison.Ordinal));
        for (var i = 0; i < skeleton.Count; i++) yield return Name(bytes, skeleton.Offset + i * skeleton.Size);
    }

    /// <summary>Rewrites a <c>.psa</c> with only the bones in <paramref name="keep"/> and their parents.</summary>
    public static void Keep(string clip, HashSet<string> keep)
    {
        var bytes = File.ReadAllBytes(clip);
        var chunks = Chunks(bytes);
        var names = chunks.FirstOrDefault(c => c.Id == "BONENAMES");
        var info = chunks.FirstOrDefault(c => c.Id == "ANIMINFO");
        if (names.Count == 0 || info.Count == 0) return;
        var parent = new int[names.Count];
        var kept = new bool[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var at = names.Offset + i * names.Size;
            parent[i] = BitConverter.ToInt32(bytes, at + 72);
            kept[i] = i == 0 || keep.Contains(Name(bytes, at));
        }
        for (var i = 0; i < names.Count; i++)
            if (kept[i]) for (var p = parent[i]; p > 0 && p < names.Count && !kept[p]; p = parent[p]) kept[p] = true;
        var order = Enumerable.Range(0, names.Count).Where(i => kept[i]).ToArray();
        if (order.Length == names.Count) return;
        var index = new int[names.Count];
        for (var i = 0; i < order.Length; i++) index[order[i]] = i;

        using var output = new MemoryStream();
        void Chunk((string Id, int Size, int Count, int Offset) chunk, int count, Action<Stream> body)
        {
            var head = new byte[Header];
            Encoding.ASCII.GetBytes(chunk.Id).CopyTo(head, 0);
            BitConverter.GetBytes(BitConverter.ToInt32(bytes, chunk.Offset - Header + 20)).CopyTo(head, 20);
            BitConverter.GetBytes(chunk.Size).CopyTo(head, 24);
            BitConverter.GetBytes(count).CopyTo(head, 28);
            output.Write(head);
            body(output);
        }
        var frames = new List<(int Bones, int Frames)>();
        for (var s = 0; s < info.Count; s++)
        {
            var at = info.Offset + s * info.Size;
            frames.Add((BitConverter.ToInt32(bytes, at + 128), BitConverter.ToInt32(bytes, at + 164)));
        }
        foreach (var chunk in chunks)
        {
            switch (chunk.Id)
            {
                case "BONENAMES":
                    Chunk(chunk, order.Length, o =>
                    {
                        foreach (var i in order)
                        {
                            var bone = bytes.AsSpan(chunk.Offset + i * chunk.Size, chunk.Size).ToArray();
                            BitConverter.GetBytes(order.Count(j => j != i && parent[j] == i)).CopyTo(bone, 68);
                            BitConverter.GetBytes(i == 0 ? 0 : index[parent[i]]).CopyTo(bone, 72);
                            o.Write(bone);
                        }
                    });
                    break;
                case "ANIMINFO":
                    Chunk(chunk, chunk.Count, o =>
                    {
                        for (var s = 0; s < chunk.Count; s++)
                        {
                            var sequence = bytes.AsSpan(chunk.Offset + s * chunk.Size, chunk.Size).ToArray();
                            BitConverter.GetBytes(order.Length).CopyTo(sequence, 128);
                            BitConverter.GetBytes(order.Length * frames[s].Frames).CopyTo(sequence, 140);
                            o.Write(sequence);
                        }
                    });
                    break;
                case "ANIMKEYS" or "SCALEKEYS":
                    var total = frames.Sum(f => f.Frames);
                    if (chunk.Count != total * names.Count) goto default;
                    Chunk(chunk, total * order.Length, o =>
                    {
                        for (var f = 0; f < total; f++)
                            foreach (var i in order) o.Write(bytes, chunk.Offset + (f * names.Count + i) * chunk.Size, chunk.Size);
                    });
                    break;
                default:
                    output.Write(bytes, chunk.Offset - Header, Header + chunk.Size * chunk.Count);
                    break;
            }
        }
        File.WriteAllBytes(clip, output.ToArray());
    }
}
