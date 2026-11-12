using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

/// <summary>An installed Unreal Engine game: its name, its folder, its project and the folder of its paks.</summary>
public sealed record UnrealGame(string Name, string Root, string Project, string Paks, long PaksBytes);

/// <summary>What FModel's own settings know about one game folder.</summary>
public sealed record FModelGame(string Directory, string Name, uint UeVersion, IReadOnlyList<(string Guid, string Key)> Keys, string? Mappings);

/// <summary>
/// Finds installed Unreal Engine games without asking: Steam's libraries (<c>libraryfolders.vdf</c> and each
/// <c>appmanifest_*.acf</c>), the Epic launcher's manifests and the game folders FModel was pointed at. A game is a
/// folder holding <c>&lt;Project&gt;/Content/Paks</c>; a mod names that project in its asset paths
/// (<c>SB/Content/...</c>), so the two meet on it.
/// </summary>
public static class UnrealGameLocator
{
    /// <summary>Letters and digits only, lower case: "DRAGON BALL Sparking! ZERO" and "dragonballsparkingzero" are one.</summary>
    public static string Fold(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Every value of <paramref name="key"/> in a Valve KeyValues text (<c>.vdf</c>, <c>.acf</c>).</summary>
    public static IEnumerable<string> VdfValues(string text, string key) =>
        Regex.Matches(text, "\"" + Regex.Escape(key) + "\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)
            .Select(match => Regex.Unescape(match.Groups[1].Value));

    /// <summary>Steam's library folders, its own first.</summary>
    public static IReadOnlyList<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path) roots.Add(path.Replace('/', '\\'));
            }
            catch (Exception) { }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libraries = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            libraries.Add(root);
            libraries.AddRange(VdfValues(File.ReadAllText(vdf), "path"));
        }
        return libraries.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    /// <summary>
    /// Each installed game with Unreal paks: Steam's (by app manifest, so a copy beside a game is not a game),
    /// Epic's, FModel's and <paramref name="extraFolders"/> (a game folder the user named).
    /// </summary>
    public static IReadOnlyList<UnrealGame> InstalledGames(IEnumerable<(string Name, string Folder)>? extraFolders = null, IEnumerable<FModelGame>? fmodel = null)
    {
        var found = new List<(string Name, string Folder)>();
        foreach (var library in SteamLibraries())
        {
            var apps = Path.Combine(library, "steamapps");
            foreach (var manifest in SafeFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var name = VdfValues(text, "name").FirstOrDefault();
                var folder = VdfValues(text, "installdir").FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(folder)) found.Add((name ?? folder, Path.Combine(apps, "common", folder)));
            }
        }
        var epic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (var item in SafeFiles(epic, "*.item"))
        {
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(item));
                if (json.RootElement.TryGetProperty("InstallLocation", out var location) && location.GetString() is { Length: > 0 } folder)
                    found.Add((json.RootElement.TryGetProperty("DisplayName", out var display) ? display.GetString() ?? folder : folder, folder));
            }
            catch (Exception) { }
        }
        foreach (var game in fmodel ?? []) found.Add((game.Name, GameRootOf(game.Directory)));
        found.AddRange(extraFolders ?? []);

        var games = new List<UnrealGame>();
        foreach (var (name, folder) in found)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var project in SafeDirectories(folder))
            {
                var paks = Path.Combine(project, "Content", "Paks");
                var projectName = Path.GetFileName(project);
                if (projectName.Equals("Engine", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(paks)) continue;
                var bytes = SafeFiles(paks, "*").Where(IsContainer).Sum(file => new FileInfo(file).Length);
                if (bytes > 0) games.Add(new UnrealGame(name, Path.GetFullPath(folder), projectName, Path.GetFullPath(paks), bytes));
            }
        }
        return games.DistinctBy(game => game.Paks, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The games a mod's project is for, the most likely first: the full game before a demo, by size.</summary>
    public static IReadOnlyList<UnrealGame> ForProject(string project, IEnumerable<UnrealGame> games) =>
        games.Where(game => game.Project.Equals(project, StringComparison.OrdinalIgnoreCase)).OrderByDescending(game => game.PaksBytes).ToList();

    /// <summary>A pak or IoStore table of contents.</summary>
    public static bool IsContainer(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".pak" or ".utoc";

    /// <summary>The engine version a game was built with, read from its executables (<c>++UE5+Release-5.1</c>).</summary>
    public static (int Major, int Minor)? EngineVersion(UnrealGame game)
    {
        var binaries = Path.Combine(game.Root, game.Project, "Binaries", "Win64");
        var executables = SafeFiles(binaries, "*.exe").OrderByDescending(file => file.Contains("Shipping", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(file => new FileInfo(file).Length);
        foreach (var exe in executables)
            if (ScanVersion(exe) is { } version) return version;
        // A game built on its own branch (++streams+ProjectW-release) names no engine version; the mappings UE4SS
        // dumped for it in its folder do (Sandfall-5.4.4-68322+++streams+ProjectW-release-….usmap).
        foreach (var mappings in MappingsInside(game))
        {
            var match = Regex.Match(Path.GetFileName(mappings), @"^[^-]+-(\d+)\.(\d+)\.\d+-");
            if (match.Success) return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        }
        return null;
    }

    private static (int, int)? ScanVersion(string file)
    {
        var ascii = Encoding.ASCII.GetBytes("+Release-");
        var wide = Encoding.Unicode.GetBytes("+Release-");
        var buffer = new byte[1 << 22];
        using var stream = File.OpenRead(file);
        var carry = 0;
        while (true)
        {
            var read = stream.Read(buffer, carry, buffer.Length - carry);
            if (read <= 0) return null;
            var length = carry + read;
            var span = buffer.AsSpan(0, length);
            foreach (var (needle, step) in new[] { (ascii, 1), (wide, 2) })
            {
                for (var at = span.IndexOf(needle); at >= 0;)
                {
                    var tail = span[(at + needle.Length)..];
                    if (ReadVersion(tail, step) is { } version && at >= 2 * step && span[at - 2 * step] == (byte)'E') return version;
                    var next = span[(at + 1)..].IndexOf(needle);
                    at = next < 0 ? -1 : at + 1 + next;
                }
            }
            carry = Math.Min(64, length);
            Array.Copy(buffer, length - carry, buffer, 0, carry);
        }
    }

    private static (int, int)? ReadVersion(ReadOnlySpan<byte> bytes, int step)
    {
        var text = new StringBuilder();
        for (var i = 0; i < bytes.Length && text.Length < 8; i += step)
        {
            var c = (char)bytes[i];
            if (!char.IsDigit(c) && c != '.') break;
            text.Append(c);
        }
        var parts = text.ToString().Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && major is 4 or 5 ? (major, minor) : null;
    }

    /// <summary>The <c>.usmap</c> mappings files inside a game's folder, newest first (a mod tool or UE4SS may have left one).</summary>
    public static IReadOnlyList<string> MappingsInside(UnrealGame game)
    {
        var found = new List<string>();
        var stack = new Stack<(string Dir, int Depth)>([(game.Root, 0)]);
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (dir.Equals(game.Paks, StringComparison.OrdinalIgnoreCase)) continue;
            found.AddRange(SafeFiles(dir, "*.usmap"));
            if (depth < 5) foreach (var sub in SafeDirectories(dir)) stack.Push((sub, depth + 1));
        }
        return found.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
    }

    /// <summary>A folder FModel was pointed at, as the game's own folder (FModel names the <c>Paks</c> folder).</summary>
    public static string GameRootOf(string directory)
    {
        var full = Path.GetFullPath(directory.TrimEnd('\\', '/'));
        var parts = full.Split(Path.DirectorySeparatorChar);
        var content = Array.FindLastIndex(parts, part => part.Equals("Content", StringComparison.OrdinalIgnoreCase));
        return content >= 2 ? string.Join(Path.DirectorySeparatorChar, parts[..(content - 1)]) : full;
    }

    /// <summary>FModel's settings (<c>%AppData%\FModel\AppSettings.json</c>): each game folder it knows, and its data folder.</summary>
    public static (IReadOnlyList<FModelGame> Games, string? DataDirectory) FModelSettings(string? path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "AppSettings.json");
        var games = new List<FModelGame>();
        if (!File.Exists(path)) return (games, null);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            string? data = root.TryGetProperty("OutputDirectory", out var output) && output.GetString() is { Length: > 0 } o ? Path.Combine(o, ".data") : null;
            if (root.TryGetProperty("PerDirectory", out var directories) && directories.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in directories.EnumerateObject())
                {
                    var game = entry.Value;
                    var keys = new List<(string, string)>();
                    if (game.TryGetProperty("AesKeys", out var aes) && aes.ValueKind == JsonValueKind.Object)
                    {
                        if (aes.TryGetProperty("mainKey", out var main) && main.GetString() is { Length: > 0 } mainKey) keys.Add(("", mainKey));
                        if (aes.TryGetProperty("dynamicKeys", out var dynamic) && dynamic.ValueKind == JsonValueKind.Array)
                            foreach (var key in dynamic.EnumerateArray())
                                if (key.TryGetProperty("key", out var value) && value.GetString() is { Length: > 0 } k)
                                    keys.Add((key.TryGetProperty("guid", out var guid) ? guid.GetString() ?? "" : "", k));
                    }
                    string? mappings = null;
                    if (game.TryGetProperty("Endpoints", out var endpoints) && endpoints.ValueKind == JsonValueKind.Array)
                        foreach (var endpoint in endpoints.EnumerateArray())
                            if (endpoint.TryGetProperty("FilePath", out var file) && file.GetString() is { Length: > 0 } f && f.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase))
                                mappings = f;
                    var directory = game.TryGetProperty("GameDirectory", out var d) && d.GetString() is { Length: > 0 } g ? g : entry.Name;
                    var name = game.TryGetProperty("GameName", out var n) && n.GetString() is { Length: > 0 } gameName ? gameName : Path.GetFileName(GameRootOf(directory));
                    var version = game.TryGetProperty("UeVersion", out var v) && v.TryGetUInt32(out var ue) ? ue : 0;
                    games.Add(new FModelGame(directory, name, version, keys, mappings));
                }
            }
            return (games, data);
        }
        catch (Exception)
        {
            return (games, null);
        }
    }

    /// <summary>FModel's entry for <paramref name="game"/>, if it has one.</summary>
    public static FModelGame? FModelEntry(UnrealGame game, IEnumerable<FModelGame> fmodel) =>
        fmodel.FirstOrDefault(entry => GameRootOf(entry.Directory).Equals(game.Root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : []; }
        catch (Exception) { return []; }
    }

    private static IEnumerable<string> SafeDirectories(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : []; }
        catch (Exception) { return []; }
    }
}

/// <summary>
/// A community list of Unreal games' AES keys (<c>aes keys.txt</c>), read as it is written: one game a line,
/// <c>&lt;game name&gt;&lt;spaces or tab&gt;&lt;key&gt;</c>, the key <c>0x</c> and 64 hex digits, 64 hex digits, base64
/// of 32 bytes, or the 32 characters an old Unreal game used as its key. A heading in capitals naming keys
/// (<c>USM KEYS</c>) or a line ending in a colon starts a section: lines before any section and under one naming AES are pak keys; any other
/// section (CriWare video keys) is kept apart and never tried on game files. Lines that are no key (notes, links,
/// fragments) are passed over. A game is matched by its name without its <c>(PC)</c>, <c>(switch)</c> or
/// <c>(demo)</c> part; the plain or PC entry is tried first.
/// </summary>
public sealed class AesKeyList
{
    public const string FileName = "aes keys.txt";

    /// <summary>The list read, or <c>null</c> when there is none.</summary>
    public string? Path { get; private init; }

    /// <summary>Pak keys: the entry's name as written and the key's 32 bytes.</summary>
    public List<(string Name, byte[] Key)> Entries { get; } = [];

    /// <summary>Entries of another section, by section: not keys for game files.</summary>
    public List<(string Section, string Name)> Other { get; } = [];

    /// <summary>Lines of the AES part that hold no usable key.</summary>
    public int Skipped { get; private set; }

    /// <summary>Variants that are the game itself on PC, tried before a demo's or a console's.</summary>
    private static readonly string[] Plain = ["pc", "steam", "windows", "win64", "epic", "epicgames", "egs", "gog", "release", "retail", "full", "global", "main", "mainkey", "live"];

    public static AesKeyList Load(string? path)
    {
        if (path is null || !File.Exists(path)) return new AesKeyList();
        var list = new AesKeyList { Path = path };
        string? section = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim().TrimStart('﻿').Trim();
            if (line.Length == 0) continue;
            var split = Regex.Match(line, @"^(.*?\S)\s+(\S+)$");
            var key = split.Success ? Key(split.Groups[2].Value) : null;
            if (key is null && (line.EndsWith(':') || (!line.Any(char.IsLower) && UnrealGameLocator.Fold(line).Contains("key"))))
            {
                section = line.TrimEnd(':').Trim();
                continue;
            }
            var aes = section is null || UnrealGameLocator.Fold(section).Contains("aes");
            if (!aes)
            {
                if (split.Success) list.Other.Add((section!, split.Groups[1].Value.Trim()));
                continue;
            }
            if (key is not null) list.Entries.Add((split.Groups[1].Value.Trim(), key));
            else list.Skipped++;
        }
        return list;
    }

    /// <summary>A key as written, as its 32 bytes; <c>null</c> when it is none.</summary>
    public static byte[]? Key(string text)
    {
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (hex.Length == 64 && hex.All(Uri.IsHexDigit)) return Convert.FromHexString(hex);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length == 32 && text.All(c => c is > ' ' and < (char)127)) return Encoding.ASCII.GetBytes(text);
        var bytes = new byte[48];
        return Convert.TryFromBase64String(text, bytes, out var written) && written == 32 ? bytes[..32] : null;
    }

    /// <summary>
    /// The keys to try for a game known by <paramref name="names"/> (its Steam name, its project), with the entry each
    /// is from, the likeliest first: the same name before one that holds the other, the plain or PC entry before a
    /// demo's or a console's, then in the list's order.
    /// </summary>
    public IReadOnlyList<(string Entry, byte[] Key)> Candidates(params string[] names)
    {
        var wanted = names.Select(UnrealGameLocator.Fold).Where(n => n.Length > 0).Distinct().ToList();
        return Entries.Select((entry, index) =>
            {
                var variants = Regex.Matches(entry.Name, @"\(([^)]*)\)").Select(m => UnrealGameLocator.Fold(m.Groups[1].Value)).ToList();
                var name = UnrealGameLocator.Fold(Regex.Replace(entry.Name, @"\([^)]*\)", ""));
                var tier = wanted.Contains(name) ? 0
                    : name.Length >= 6 && wanted.Any(w => w.Length >= 6 && (w.Contains(name) || name.Contains(w))) ? 1 : -1;
                return (entry, tier, plain: variants.All(Plain.Contains) ? 0 : 1, index);
            })
            .Where(x => x.tier >= 0).OrderBy(x => x.tier).ThenBy(x => x.plain).ThenBy(x => x.index)
            .DistinctBy(x => Convert.ToHexString(x.entry.Key)).Select(x => (x.entry.Name, x.entry.Key)).ToList();
    }
}

/// <summary>
/// The one file where a user gives what a game needs before its mods can be read and nothing on this PC
/// has it: one line a thing, <c>&lt;game&gt; key: 0x…</c>, <c>&lt;game&gt; mappings: &lt;file&gt;</c>,
/// <c>&lt;game&gt; engine: 5.1</c> or <c>&lt;game&gt; folder: &lt;where it is installed&gt;</c>. A game is named
/// as Steam names it or by its project; case, spaces and punctuation do not matter.
/// </summary>
public sealed class UnrealModSettings
{
    private readonly List<(string Game, string Name, string What, string Value)> _lines = [];

    public string? Path { get; private init; }

    public static string Template =>
        "# Some games need one thing more before their mods can be turned into models.\r\n" +
        "# The log says when, and what. Write it here, one line each, then start the game again:\r\n" +
        "#\r\n" +
        "#   <game> key: 0x<the game's AES key, 64 letters and digits>\r\n" +
        "#   <game> mappings: <where the game's .usmap file is>\r\n" +
        "#   <game> folder: <where the game is installed, if it is not found by itself>\r\n" +
        "#   <game> engine: <its Unreal Engine version, like 5.1, if the log asks>\r\n" +
        "#\r\n" +
        "# For example:\r\n" +
        "#   Stellar Blade mappings: D:\\Tools\\StellarBlade.usmap\r\n";

    public static UnrealModSettings Load(string? path)
    {
        var settings = new UnrealModSettings { Path = path };
        if (path is null || !File.Exists(path)) return settings;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var match = Regex.Match(line, @"^(.+?)\s+(key(?:\s+[0-9A-Fa-f-]{32,36})?|mappings|engine|folder)\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (match.Success) settings._lines.Add((UnrealGameLocator.Fold(match.Groups[1].Value), match.Groups[1].Value.Trim(), match.Groups[2].Value.ToLowerInvariant(), match.Groups[3].Value.Trim().Trim('"')));
        }
        return settings;
    }

    private IEnumerable<(string What, string Value)> For(params string[] names)
    {
        var folded = names.Select(UnrealGameLocator.Fold).Where(name => name.Length > 0).ToHashSet();
        return _lines.Where(line => folded.Contains(line.Game)).Select(line => (line.What, line.Value));
    }

    /// <summary>Keys given for a game: <c>(guid or "", key)</c>.</summary>
    public IReadOnlyList<(string Guid, string Key)> Keys(params string[] names) =>
        For(names).Where(line => line.What.StartsWith("key", StringComparison.Ordinal))
            .Select(line => (line.What.Length > 3 ? line.What[3..].Trim() : "", line.Value)).ToList();

    public string? Value(string what, params string[] names) => For(names).Where(line => line.What == what).Select(line => line.Value).LastOrDefault();

    /// <summary>The game folders the user named, with the name they gave.</summary>
    public IEnumerable<(string Name, string Folder)> Folders() =>
        _lines.Where(line => line.What == "folder").Select(line => (line.Name, line.Value));
}
