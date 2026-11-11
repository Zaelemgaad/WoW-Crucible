using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Formats.Materials;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures;
using WoWCrucible.Core;
using ILoadableObject = CUE4Parse.UE4.Objects.UObject.ILoadableObject;

/// <summary>
/// <c>wowcrucible models import-mod</c>: an Unreal Engine game mod as it comes from a mod site (a <c>.rar</c>, <c>.zip</c> or
/// <c>.7z</c>, or loose <c>.pak</c>/<c>.utoc</c>/<c>.ucas</c> files) read over the installed game it is for, the way FModel
/// reads a mod pak loaded over a game's paks, and written out as an FModel-style export: the character's meshes as
/// <c>.psk</c>, its materials as <c>.json</c>, their colour textures as <c>.png</c> and a basic set of its animations as
/// <c>.psa</c>, with a <c>look.txt</c> naming it. benilla's "Add Models Here" reads that folder like a hand-made export.
/// What could not be done, or what the user must give once (a game's AES key, its mappings), is written to
/// <c>import.txt</c> in plain words. CUE4Parse (Apache-2.0) reads the game files.
/// </summary>
internal sealed class UnrealModImporter
{
    /// <summary>The user must give something once; the message says what and where.</summary>
    private sealed class NeedsUser(string message) : Exception(message);

    /// <summary>The mod cannot become a model; the message says why.</summary>
    private sealed class Unusable(string message) : Exception(message);

    private const int MaxTextureSide = 2048;
    private readonly List<string> _notes = [];
    /// <summary>Lines for <c>look.txt</c> beyond its name: the clip chosen for each action.</summary>
    private readonly List<string> _look = [];
    private readonly string _settingsPath;
    private readonly UnrealModSettings _settings;

    private UnrealModImporter(string? settingsPath)
    {
        _settingsPath = settingsPath ?? "";
        _settings = UnrealModSettings.Load(settingsPath);
    }

    private static void Say(string line) => Console.WriteLine(line);

    /// <summary>Runs the import; 0 when the folder holds a model, 3 when the user must give something, 1 otherwise.</summary>
    public static int Run(IReadOnlyList<string> inputs, string outDir, string? settingsPath, CancellationToken cancel)
    {
        outDir = Path.GetFullPath(outDir);
        var work = outDir.TrimEnd('\\', '/') + ".work";
        var importer = new UnrealModImporter(settingsPath);
        var result = new StringBuilder();
        int code;
        try
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(outDir);
            var (game, name) = importer.Import(inputs, outDir, work, cancel);
            File.WriteAllText(Path.Combine(outDir, "look.txt"), $"name: {name}\r\n" + string.Concat(importer._look.Select(line => line + "\r\n")));
            if (game.Length > 0) result.Append($"game\t{game}\n");
            result.Append($"name\t{name}\n");
            code = 0;
        }
        catch (NeedsUser need)
        {
            result.Append($"need\t{need.Message}\n");
            if (settingsPath is not null && !File.Exists(settingsPath))
            {
                try { File.WriteAllText(settingsPath, UnrealModSettings.Template); } catch (Exception) { }
            }
            code = 3;
        }
        catch (Unusable unusable)
        {
            result.Append($"problem\t{unusable.Message}\n");
            code = 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Append($"problem\tIt could not be read: {ex.Message.ReplaceLineEndings(" ")}\n");
            code = 1;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch (Exception) { }
        }
        foreach (var note in importer._notes) result.Append($"note\t{note}\n");
        if (code != 0)
        {
            // A folder that is no model holds nothing but the result.
            foreach (var entry in Directory.EnumerateFileSystemEntries(outDir))
                if (Directory.Exists(entry)) Directory.Delete(entry, true); else File.Delete(entry);
        }
        File.WriteAllText(Path.Combine(outDir, "import.txt"), result.ToString());
        Console.Write(result.ToString());
        return code;
    }

    private (string Game, string Name) Import(IReadOnlyList<string> inputs, string outDir, string work, CancellationToken cancel)
    {
        // What was dropped: archives are opened, loose files read where they are.
        var files = new List<string>();
        var archiveName = "";
        foreach (var input in inputs.Select(Path.GetFullPath))
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories));
            }
            else if (ArchiveKind(input) is not null)
            {
                archiveName = archiveName.Length == 0 ? Path.GetFileNameWithoutExtension(input) : archiveName;
                var dest = Path.Combine(work, "a" + files.Count.ToString("D3"));
                Extract(input, dest, 0);
                files.AddRange(Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories));
            }
            else if (File.Exists(input))
            {
                files.Add(input);
            }
            else throw new Unusable($"\"{input}\" is not there.");
        }
        var stemName = archiveName.Length > 0 ? archiveName : Path.GetFileNameWithoutExtension(inputs[0]);
        var name = ModName(files) ?? Regex.Replace(stemName, @"[_]+", " ").Trim();

        var containers = Containers(files);
        if (containers.Count == 0)
        {
            var exported = files.Where(file => Path.GetExtension(file).ToLowerInvariant() is ".psk" or ".pskx" or ".psa" or ".json" or ".png" or ".tga").ToList();
            if (exported.Any(file => Path.GetExtension(file).ToLowerInvariant() is ".psk" or ".pskx"))
            {
                // Already an export: copied as it is.
                var root = CommonRoot(exported);
                foreach (var file in exported)
                {
                    var dest = Path.Combine(outDir, Path.GetRelativePath(root, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                }
                return ("", name);
            }
            if (exported.Count > 0) throw new Unusable("this has textures but no model; export the mesh too (a .psk or .pskx file).");
            throw new Unusable("it holds no game files (.pak, .utoc) and no model (.psk), so there is nothing to make a model from.");
        }

        // The game it is for, from the project its asset paths name.
        var modFiles = ListMod(containers, work);
        var project = modFiles.Select(ProjectOf).Where(p => p is not null).GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault()
            ?? throw new Unusable("its game files name no game content (no \"<Game>/Content/...\" paths), so the game it is for is unknown.");
        var (fmodelGames, fmodelData) = UnrealGameLocator.FModelSettings();
        var games = UnrealGameLocator.InstalledGames(_settings.Folders(), fmodelGames);
        var candidates = UnrealGameLocator.ForProject(project, games);
        if (candidates.Count == 0)
            throw new NeedsUser($"it is a mod for a game whose files are called \"{project}\", and no installed game has them. If that game is installed, add a line to {_settingsPath}:  {project} folder: <the game's folder>");
        var game = candidates[0];
        Say($"Game: {game.Name} ({game.Paks})");
        var fmodel = UnrealGameLocator.FModelEntry(game, fmodelGames);
        var version = Version(game, fmodel)
            ?? throw new NeedsUser($"the Unreal Engine version of {game.Name} could not be told. Add a line to {_settingsPath}:  {game.Name} engine: <version, like 5.1>");
        Say($"Engine: {version}");
        InitNatives(fmodelData);

        var mappings = Mappings(game, fmodel, fmodelData);
        var provider = new DefaultFileProvider(new DirectoryInfo(game.Paks), SearchOption.TopDirectoryOnly, new VersionContainer(version), StringComparer.OrdinalIgnoreCase);
        if (mappings is not null)
        {
            Say($"Mappings: {mappings}");
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(mappings, StringComparer.OrdinalIgnoreCase);
        }
        provider.Initialize();
        var modNames = Register(provider, containers);
        var keys = new List<KeyValuePair<FGuid, FAesKey>> { new(new FGuid(), new FAesKey(new byte[32])) };
        foreach (var (guid, key) in _settings.Keys(game.Name, game.Project).Concat(fmodel?.Keys ?? []))
        {
            try { keys.Add(new(guid.Length == 0 ? new FGuid() : new FGuid(guid.Replace("-", "")), new FAesKey(key))); }
            catch (Exception) { _notes.Add($"The key given for {game.Name} ({Short(key)}) is not a valid key, so it was not used."); }
        }
        provider.SubmitKeys(keys);
        var locked = provider.UnloadedVfs.Where(vfs => vfs.IsEncrypted).ToList();
        if (locked.Count > 0)
        {
            var needed = locked.Select(vfs => vfs.EncryptionKeyGuid).Distinct().ToList();
            var which = needed.All(g => g == new FGuid()) ? "key" : $"key ({string.Join(", ", needed.Select(g => g.ToString()))})";
            throw new NeedsUser($"{game.Name} keeps its files locked and needs its AES {which}. Put it in {_settingsPath} as a line:  {game.Name} key: 0x<64 letters and digits>");
        }
        provider.PostMount();
        Say($"Mounted {provider.MountedVfs.Count} containers, {provider.Files.Count:N0} files");

        try
        {
            ExportLook(provider, modNames, outDir, cancel);
        }
        catch (MappingException)
        {
            throw new NeedsUser($"{game.Name} needs its mappings file (a .usmap made for this version of the game) to be read. Put a line in {_settingsPath}:  {game.Name} mappings: <where the .usmap file is>");
        }
        catch (Exception ex) when (ex.Message.Contains("Oodle", StringComparison.OrdinalIgnoreCase))
        {
            throw new NeedsUser("reading this game needs the Oodle decompressor (oodle-data-shared.dll), which FModel downloads the first time it runs. Run FModel once, or put that file beside wowcrucible.exe.");
        }
        return (game.Name, name);
    }

    // ---- What was dropped ----

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

    private static string CommonRoot(IReadOnlyList<string> files)
    {
        var root = Path.GetDirectoryName(files[0])!;
        while (!files.All(f => f.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            root = Path.GetDirectoryName(root)!;
        return root;
    }

    /// <summary>The mod's containers: an IoStore table (<c>.utoc</c>, with its <c>.ucas</c>) or a <c>.pak</c> with no table beside it.</summary>
    private static List<string> Containers(IEnumerable<string> files)
    {
        var all = files.ToList();
        var tables = all.Where(f => f.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.ChangeExtension(f, ".ucas"))).ToList();
        var paks = all.Where(f => f.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)
            && !tables.Any(t => Path.ChangeExtension(t, ".pak").Equals(f, StringComparison.OrdinalIgnoreCase))).ToList();
        // A container named like the game's own global one holds no mod.
        return tables.Concat(paks).Where(f => !Path.GetFileNameWithoutExtension(f).Equals("global", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Registers each mod container with the provider as a patch (<c>_P</c>), so it wins over the game's own files the
    /// way the game loads it; nothing is copied. Returns the names it was registered by.
    /// </summary>
    private static HashSet<string> Register(AbstractVfsFileProvider provider, IEnumerable<string> containers)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in containers)
        {
            var stem = Path.GetFileNameWithoutExtension(container);
            var patch = stem.EndsWith("_P", StringComparison.OrdinalIgnoreCase) ? stem : stem + "_P";
            var ext = Path.GetExtension(container);
            var dir = Path.GetDirectoryName(container)!;
            var named = Path.Combine(dir, patch + ext);
            names.Add(patch + ext);
            var real = container;
            provider.RegisterVfs(named, [File.OpenRead(real)], open =>
            {
                var file = Path.GetFileName(open);
                var actual = file.StartsWith(patch, StringComparison.OrdinalIgnoreCase) ? stem + file[patch.Length..] : file;
                return new FRandomAccessFileStreamArchive(Path.Combine(dir, actual), provider.Versions);
            });
        }
        return names;
    }

    /// <summary>Every path the mod's containers hold, read with no game behind them.</summary>
    private static List<string> ListMod(IEnumerable<string> containers, string work)
    {
        var empty = Path.Combine(work, "none");
        Directory.CreateDirectory(empty);
        var lister = new DefaultFileProvider(new DirectoryInfo(empty), SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE4_LATEST), StringComparer.OrdinalIgnoreCase);
        lister.Initialize();
        Register(lister, containers);
        lister.SubmitKey(new FGuid(), new FAesKey(new byte[32]));
        var paths = lister.MountedVfs.SelectMany(vfs => vfs.Files.Keys).ToList();
        if (paths.Count == 0 && lister.UnloadedVfs.Any(vfs => vfs.IsEncrypted))
            throw new Unusable("the mod's own files are locked (encrypted) by its maker, so they cannot be read.");
        lister.Dispose();
        return paths;
    }

    private static string? ProjectOf(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 && parts[1].Equals("Content", StringComparison.OrdinalIgnoreCase) && !parts[0].Equals("Engine", StringComparison.OrdinalIgnoreCase) ? parts[0] : null;
    }

    // ---- The game ----

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

    /// <summary>Oodle and zlib for compressed game files: beside this tool, or where FModel keeps its own copies.</summary>
    private static void InitNatives(string? fmodelData)
    {
        var places = new[] { AppContext.BaseDirectory, fmodelData }.Where(p => p is not null && Directory.Exists(p)).Cast<string>().ToList();
        foreach (var name in new[] { OodleHelper.OODLE_NAME_CURRENT, OodleHelper.OODLE_NAME_OLD })
        {
            var file = places.Select(p => Path.Combine(p, name)).FirstOrDefault(File.Exists);
            if (file is null) continue;
            try { OodleHelper.Initialize(file); break; } catch (Exception) { }
        }
        var zlib = places.Select(p => Path.Combine(p, ZlibHelper.DLL_NAME)).FirstOrDefault(File.Exists);
        if (zlib is not null)
        {
            try { ZlibHelper.Initialize(zlib); } catch (Exception) { }
        }
    }

    // ---- The look ----

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

    private void ExportLook(DefaultFileProvider provider, HashSet<string> modNames, string outDir, CancellationToken cancel)
    {
        var project = provider.ProjectName ?? "Game";
        var modReaders = provider.MountedVfs.Where(vfs => modNames.Contains(Path.GetFileName(vfs.Name))).ToList();
        var modPackages = modReaders.SelectMany(vfs => vfs.Files.Keys)
            .Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Say($"The mod changes {modPackages.Count} assets.");

        var meshes = new List<USkeletalMesh>();
        var overrides = new HashSet<string>();   // textures and materials the mod changes
        foreach (var path in modPackages)
        {
            cancel.ThrowIfCancellationRequested();
            if (!provider.TryLoadPackage(path, out var package)) continue;
            foreach (var export in SafeExports(package))
            {
                switch (export)
                {
                    case USkeletalMesh mesh: meshes.Add(mesh); break;
                    case UTexture or UMaterialInterface: overrides.Add(PackageOf(export, project)); break;
                }
            }
        }

        // What the mod's own meshes wear; what is left is worn by meshes of the game it did not change.
        var worn = new HashSet<string>();
        foreach (var mesh in meshes) foreach (var p in WornBy(mesh, project)) worn.Add(p);
        var leftover = overrides.Where(o => !worn.Contains(o)).ToList();
        foreach (var mesh in BaseMeshesWearing(provider, leftover, meshes, project, cancel))
        {
            Say($"Also worn: {mesh.Name} (from the game; the mod changes its look)");
            meshes.Add(mesh);
        }
        if (meshes.Count == 0)
            throw new Unusable(overrides.Count > 0
                ? "it changes textures or materials, but no character in the game was found that wears them."
                : "it changes no character model (no skeletal mesh, texture or material of one), so there is no look in it.");

        var body = meshes[0];
        if (!meshes.Any(m => Words(m.Name).Contains("hair")) && Hair(provider, body, cancel) is { } hair)
        {
            Say($"Also worn: {hair.Name} (the character's hair, from the game)");
            meshes.Add(hair);
        }
        var skeleton = body.Skeleton?.ResolvedObject?.Load() as USkeleton;
        var chosen = skeleton is null ? [] : Animations(provider, skeleton, project, cancel);
        if (chosen.Count == 0) _notes.Add("No animations were found in the game for it, so it will stand still.");
        else Say($"Animations: {string.Join(", ", chosen.Select(c => $"{c.Word} {c.Clip.Name}"))}");
        foreach (var (word, clip) in chosen) _look.Add($"{word}: {clip.Name}");
        var clips = chosen.Select(c => c.Clip).DistinctBy(c => c.GetPathName()).ToList();

        var options = new ExportOptions(meshFormat: EMeshFormat.ActorX, meshQuality: EMeshQuality.Highest, texturePlatform: ETexturePlatform.DesktopMobile,
            textureFormat: ETextureFormat.Png, exportMaterials: false, exportMorphTargets: false, socketFormat: ESocketFormat.Bone);
        var session = new ExportSession();
        foreach (var mesh in meshes) session.Add(mesh);
        foreach (var clip in clips) session.Add(clip);
        var results = session.RunAsync(outDir, options, null, cancel).GetAwaiter().GetResult();
        foreach (var failed in results.Where(r => !r.Success))
        {
            if (failed.Error is MappingException mapping) throw mapping;
            _notes.Add($"{Path.GetFileName(failed.ObjectPath)} could not be exported: {failed.Error?.Message}");
        }
        var models = results.Where(r => r.Success).SelectMany(r => r.DiskFilePaths).Where(p => p.EndsWith(".psk", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".pskx", StringComparison.OrdinalIgnoreCase)).ToList();
        if (models.Count == 0) throw new Unusable("its model could not be exported from the game files.");
        // A game skeleton can have thousands of bones (cloth, hair, face rigs) the meshes do not use: the clips keep the used ones.
        var used = new HashSet<string>(models.SelectMany(PsaTrim.BoneNames), StringComparer.OrdinalIgnoreCase);
        foreach (var clip in results.Where(r => r.Success).SelectMany(r => r.DiskFilePaths).Where(p => p.EndsWith(".psa", StringComparison.OrdinalIgnoreCase)))
            PsaTrim.Keep(clip, used);

        // Materials as FModel writes them, and the pictures that colour them.
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in meshes)
        {
            foreach (var slot in mesh.Materials)
            {
                if (slot?.ResolvedObject?.Load() is not UMaterialInterface material || !written.Add(material.GetPathName())) continue;
                var parameters = new CMaterialParams2();
                try { material.GetParams(parameters, EMaterialDepth.AllLayersNoRef); }
                catch (Exception ex) { _notes.Add($"The material {material.Name} could not be read: {ex.Message}"); continue; }
                var diffuse = Diffuse(parameters, SavePath(provider, mesh));
                if (diffuse is not null) parameters.Textures[CMaterialParams2.FallbackDiffuse] = diffuse;
                var json = new JsonMaterialFormat().Build(material.Name, parameters);
                var dest = Path.Combine(outDir, SavePath(provider, material) + ".json");
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.WriteAllBytes(dest, json.Data);
                foreach (var (param, loadable) in parameters.Textures)
                {
                    var wanted = diffuse is not null ? ReferenceEquals(loadable, diffuse) || Cutout(param) : Colourish(param);
                    if (!wanted || !loadable.TryLoad<UTexture>(out var texture) || texture is null) continue;
                    if (diffuse is null && !Colourish(param, texture.Name)) continue;
                    var picture = Path.Combine(outDir, SavePath(provider, texture) + ".png");
                    if (File.Exists(picture)) continue;
                    SaveTexture(texture, picture);
                }
            }
        }
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
    private List<USkeletalMesh> BaseMeshesWearing(DefaultFileProvider provider, List<string> leftover, List<USkeletalMesh> have, string project, CancellationToken cancel)
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
        var allPaths = provider.Files.Keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                if (!provider.TryLoadPackage(path, out var package)) continue;
                foreach (var mesh in SafeExports(package).OfType<USkeletalMesh>())
                {
                    if (have.Any(h => h.GetPathName() == mesh.GetPathName())) continue;
                    var wears = WornBy(mesh, project).Distinct().Count(leftover.Contains);
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

    /// <summary>
    /// The character's own hair when what it wears has none (games often keep hair a mesh of its own): a mesh of the
    /// same skeleton beside the body's folder whose name is the body's character (<c>CH_P_EVE</c> of <c>CH_P_EVE_09</c>)
    /// and "hair", the plainest name first.
    /// </summary>
    private static USkeletalMesh? Hair(DefaultFileProvider provider, USkeletalMesh body, CancellationToken cancel)
    {
        var bodyPath = provider.FixPath(body.Owner?.Name ?? "").Replace('\\', '/');
        var folder = bodyPath.Contains('/') ? bodyPath[..bodyPath.LastIndexOf('/')] : "";
        if (!folder.Contains('/')) return null;
        var around = folder[..folder.LastIndexOf('/')].ToLowerInvariant() + "/";
        var character = Words(body.Name).TakeWhile(w => !w.Any(char.IsDigit)).ToList();
        if (character.Count == 0) return null;
        var skeleton = body.Skeleton?.ResolvedObject?.GetPathName();
        var candidates = provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && k.ToLowerInvariant().StartsWith(around))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(k => (Path: k, Words: Words(Path.GetFileNameWithoutExtension(k))))
            .Where(c => c.Words.Contains("hair") && c.Words.Take(character.Count).SequenceEqual(character)
                && !c.Words.Any(w => OddMesh.Contains(w) || w is "skeleton" or "physics" or "clothdata" or "anim" or "blueprint" or "mi" or "ma" or "t" or "tex"))
            .OrderBy(c => c.Words.Count).ThenBy(c => c.Path.Length).Take(20);
        foreach (var (path, _) in candidates)
        {
            cancel.ThrowIfCancellationRequested();
            if (!provider.TryLoadPackage(path, out var package)) continue;
            var hair = SafeExports(package).OfType<USkeletalMesh>().FirstOrDefault();
            if (hair is not null && (skeleton is null || hair.Skeleton?.ResolvedObject?.GetPathName() == skeleton)) return hair;
        }
        return null;
    }

    private static string SavePath(DefaultFileProvider provider, UObject obj)
    {
        var package = obj.Owner?.Name ?? obj.GetPathName();
        var fixedPath = provider.FixPath(package);
        var dot = fixedPath.LastIndexOf('.');
        if (dot > fixedPath.LastIndexOf('/')) fixedPath = fixedPath[..dot];
        if (!fixedPath[(fixedPath.LastIndexOf('/') + 1)..].Equals(obj.Name, StringComparison.OrdinalIgnoreCase)) fixedPath += "/" + obj.Name;
        return fixedPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    }

    // The words benilla's drop folder reads a colour picture by (added_models.rs), so only those pictures are written.
    private static readonly string[] NotColour = ["normal", "n", "orm", "roughness", "metallic", "occlusion", "ao", "mask", "depth", "id", "specular", "spec", "height", "displacement", "emissive", "sss", "cavity", "flow", "tangent", "rough", "metal", "bump", "noise"];
    private static readonly string[] Colour = ["basecolor", "base", "color", "colour", "diffuse", "albedo", "d", "col"];

    private static bool Colourish(string param, string? texture = null)
    {
        var p = param.ToLowerInvariant();
        if (p == "pm_diffuse" || p == "alpha" || p.Contains("opacity")) return true;
        if (new[] { "basecolor", "base color", "base_color", "diffuse", "albedo" }.Any(p.Contains) && !NotColour.Any(w => w.Length > 2 && p.Contains(w))) return true;
        if (texture is null) return true; // decided by the texture's name
        var words = Words(texture);
        return !words.Any(NotColour.Contains) && words.Any(Colour.Contains);
    }

    private static bool Cutout(string param) => param.Equals("alpha", StringComparison.OrdinalIgnoreCase) || param.Contains("opacity", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>A texture as a PNG, no side over 2048 (a smaller mip where it has one).</summary>
    private void SaveTexture(UTexture texture, string dest)
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
            if (decoded is null) { _notes.Add($"The picture {texture.Name} could not be read, so what it colours draws grey."); return; }
            var bytes = decoded.Encode(ETextureFormat.Png, false, out _);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, bytes);
        }
        catch (Exception ex) when (ex is not MappingException)
        {
            _notes.Add($"The picture {texture.Name} could not be read ({ex.Message}), so what it colours draws grey.");
        }
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
    private static List<(string Word, UAnimSequence Clip)> Animations(DefaultFileProvider provider, USkeleton skeleton, string project, CancellationToken cancel)
    {
        var skeletonPath = PackageOf(skeleton, project);
        var all = provider.Files.Keys.Where(k => k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
                if (picked.FirstOrDefault(p => p.Clip.Owner?.Name is { } owner && Norm(owner, project) == Norm(path, project)).Clip is { } again)
                {
                    picked.Add((action.Word, again));
                    got++;
                    continue;
                }
                if (!tried.Add(path) || !provider.TryLoadPackage(path, out var package)) continue;
                var clip = SafeExports(package).OfType<UAnimSequence>().FirstOrDefault();
                if (clip?.Skeleton?.ResolvedObject is not { } owned || Norm(owned.GetPathName(), project) != skeletonPath) continue;
                picked.Add((action.Word, clip));
                got++;
            }
        }
        return picked;
    }

    private static string Short(string key) => key.Length > 10 ? key[..10] + "…" : key;
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