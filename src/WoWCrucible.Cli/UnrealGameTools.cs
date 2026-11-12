using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WoWCrucible.Core;

/// <summary>
/// Third-party tools <c>import-mods</c> fetches the first time a game needs them, from their official GitHub releases,
/// each pinned to one file and its SHA-256 and kept in <c>Tools\</c> beside Crucible. None is in this repository.
/// </summary>
internal static class UnrealTools
{
    public static string Folder => Path.Combine(CruciblePaths.DataRoot, "Tools");

    /// <summary>The file at <paramref name="url"/>, downloaded once into <c>Tools\&lt;folder&gt;\</c> and checked against its SHA-256.</summary>
    public static string Fetch(string folder, string url, string sha256)
    {
        var dir = Path.Combine(Folder, folder);
        var file = Path.Combine(dir, Path.GetFileName(new Uri(url).LocalPath));
        if (File.Exists(file) && Sha256(file).Equals(sha256, StringComparison.OrdinalIgnoreCase)) return file;
        Directory.CreateDirectory(dir);
        var temporary = file + ".download";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WoWCrucible");
                using var response = http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                using var output = File.Create(temporary);
                response.Content.ReadAsStream().CopyTo(output);
            }
            var actual = Sha256(temporary);
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{Path.GetFileName(file)} from {url} is not the file this version of Crucible expects (SHA-256 {actual}), so it is not used.");
            File.Move(temporary, file, true);
            return file;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// AESDumpster by GHFear (<c>github.com/GHFear/AESDumpster</c>, the original; it publishes no licence, so it is fetched,
/// never shipped): reads an Unreal game's executable as a file, never running or changing it, and lists the AES keys
/// its code holds. Each is tried on the game's locked containers by the caller; only one that opens them is kept.
/// </summary>
internal static class AesDumpster
{
    private const string Url = "https://github.com/GHFear/AESDumpster/releases/download/1.2.5/AESDumpster-Win64.exe";
    private const string Sha = "2C0C0DE4853EB4988C4739C9714F6960A928B61527B9C98A7BADCA7291C917BB";

    /// <summary>The keys AESDumpster finds in <paramref name="exe"/>, the likeliest first as it lists them. Never logged.</summary>
    public static List<byte[]> Keys(string exe)
    {
        var tool = UnrealTools.Fetch("AESDumpster-1.2.5", Url, Sha);
        var start = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(tool)!,
        };
        start.ArgumentList.Add(exe);
        using var process = Process.Start(start)!;
        // It waits for a key press at the end: no input, so it ends.
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(true);
            return [];
        }
        return Regex.Matches(output.GetAwaiter().GetResult(), @"Key:\s*0x([0-9A-Fa-f]{64})").Select(m => m.Groups[1].Value.ToUpperInvariant())
            .Distinct().Select(Convert.FromHexString).ToList();
    }
}

/// <summary>
/// Makes a game's mappings file (<c>.usmap</c>) when its packages need one and none is on the PC: UE4SS (the official
/// <c>UE4SS-RE/RE-UE4SS</c> release, MIT, fetched once) is put beside the game's executable with a one-time Lua mod that
/// calls <c>DumpUSMAP()</c>, the game is started (through Steam for a Steam game), the file is taken as soon as it is
/// there, and the game copy this started is closed. The game's folder is recorded exactly before anything is put in
/// it and put back exactly afterwards. It refuses while the game runs, and for a game with online anti-cheat.
/// </summary>
internal static class UnrealMappingsMaker
{
    private const string Ue4ssFolder = "UE4SS-v4.0.0";
    private const string Ue4ssUrl = "https://github.com/UE4SS-RE/RE-UE4SS/releases/download/v4.0.0/UE4SS_v4.0.0.zip";
    private const string Ue4ssSha = "251AD0D1381A241750E036F5918CBF2B5D7EC58EBC608CB2DD09D459D0C81E45";
    private const string ModName = "CrucibleDumpUSMAP";
    /// <summary>Files the run keeps in memory to put back if the game rewrites them (a game's <c>steam_appid.txt</c>, a log).</summary>
    private const long KeepBytes = 16L << 20;

    /// <summary>
    /// The one-time mod: the mappings are written as soon as UE4SS starts its mods (the engine's classes are all there
    /// by then; a game may close again a few seconds after it starts when it is not started by its own launcher), then
    /// nothing more. It touches no game object.
    /// </summary>
    private const string Script = """
        -- WoW Crucible: writes this game's mappings (.usmap) once; Crucible removes this mod again.
        DumpUSMAP()
        """;

    /// <summary>Names of online anti-cheat a game ships (folders or files at any depth of its first levels).</summary>
    private static readonly string[] AntiCheat = ["easyanticheat", "battleye", "beservice", "beclient", "start_protected_game", "gameguard", "xigncode",
        "nprotect", "anticheatexpert", "ace-base", "vanguard", "vgk", "mhyprot", "wellbia", "nexon_ac", "hackshield", "faceit", "eac_launcher"];

    public sealed record Result(string? Mappings, string? Problem, bool Again = false);

    /// <summary>
    /// What identifies the installed version of a game: its Steam build and its executable's size and time. A mappings
    /// file made for another is stale.
    /// </summary>
    public static string Identity(UnrealGame game)
    {
        var exe = UnrealGameLocator.ShippingExe(game);
        var info = exe is null ? null : new FileInfo(exe);
        return $"build {UnrealGameLocator.SteamApp(game.Root)?.BuildId ?? "-"}, {Path.GetFileName(exe) ?? "-"} {info?.Length ?? 0} bytes {info?.LastWriteTimeUtc.Ticks ?? 0}";
    }

    /// <summary>The record kept beside a mappings file this made: the game version it is for, and when one could not be made.</summary>
    public static Dictionary<string, string> Record(string mappings)
    {
        var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = mappings + ".txt";
        if (!File.Exists(file)) return record;
        foreach (var line in File.ReadAllLines(file))
            if (line.Split('\t', 2) is [var key, var value]) record[key] = value;
        return record;
    }

    private static void WriteRecord(string mappings, Dictionary<string, string> record) =>
        File.WriteAllText(mappings + ".txt", "# WoW Crucible made this game's mappings file beside this one. Delete this file to have it made again.\r\n"
            + string.Concat(record.Select(r => $"{r.Key}\t{r.Value}\r\n")));

    /// <summary>
    /// Makes <paramref name="output"/> for <paramref name="game"/>; <paramref name="notice"/> is told just before the
    /// game opens. A result with <c>Again</c> failed for a passing reason (the game was running), to be tried next time.
    /// </summary>
    public static Result Make(UnrealGame game, (int Major, int Minor)? engine, string output, Action<string> notice, Action<string> log, CancellationToken cancel)
    {
        var name = UnrealGameLocator.PlainName(game.Name);
        var win64 = Path.Combine(game.Root, game.Project, "Binaries", "Win64");
        var exe = UnrealGameLocator.ShippingExe(game);
        if (exe is null) return new(null, $"{name}'s program (in {win64}) was not found, so its mappings file cannot be made.");
        if (engine is ({ } major, { } minor) && (major < 4 || (major == 4 && minor < 12)))
            return new(null, $"{name} is made with Unreal Engine {major}.{minor}, older than UE4SS (which makes mappings files) supports.");
        if (FindAntiCheat(game.Root) is { } guard)
            return new(null, $"{name} has online anti-cheat ({guard}), so it is not started with a mappings tool in it. Get a mappings file for it from elsewhere and name it in Game settings.txt.");
        if (Running(game).Count > 0) return new(null, $"{name} is running. Close it and start again: it opens once by itself to make its mappings file.", true);

        string zip;
        try { zip = UnrealTools.Fetch(Ue4ssFolder, Ue4ssUrl, Ue4ssSha); }
        catch (Exception ex) { return new(null, $"UE4SS, which makes {name}'s mappings file, could not be fetched ({ex.Message}).", true); }

        Recover(game.Root);
        // UE4SS already there (the user's own): the one-time mod goes into its Mods folder beside theirs. Else UE4SS is put in.
        var mods = File.Exists(Path.Combine(win64, "ue4ss", "UE4SS.dll")) ? Path.Combine(win64, "ue4ss", "Mods")
            : File.Exists(Path.Combine(win64, "UE4SS.dll")) ? Path.Combine(win64, "Mods") : null;
        if (mods is null && (File.Exists(Path.Combine(win64, "dwmapi.dll")) || Directory.Exists(Path.Combine(win64, "ue4ss"))))
            return new(null, $"{name} already has another program's dwmapi.dll beside it, which UE4SS would replace, so its mappings file is not made this way.");

        // The game's folders exactly as they are, before anything is put in: the executable's folder and the game's own top.
        var record = new Run(game.Root, win64);
        record.Save();
        var started = DateTime.UtcNow;
        var ours = new HashSet<int>();
        string? made = null;
        string? problem = null;
        var again = false;
        try
        {
            if (mods is null)
            {
                using var archive = ZipFile.OpenRead(zip);
                foreach (var entry in archive.Entries)
                {
                    var rel = entry.FullName.Replace('/', '\\');
                    if (rel is not ("dwmapi.dll" or @"ue4ss\UE4SS.dll" or @"ue4ss\UE4SS-settings.ini")) continue;
                    var target = Path.Combine(win64, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, false);
                }
                mods = Path.Combine(win64, "ue4ss", "Mods");
                Directory.CreateDirectory(mods);
                File.WriteAllText(Path.Combine(mods, "mods.txt"), $"{ModName} : 1\r\n");
            }
            var mod = Path.Combine(mods, ModName);
            Directory.CreateDirectory(Path.Combine(mod, "Scripts"));
            File.WriteAllText(Path.Combine(mod, "enabled.txt"), "");
            File.WriteAllText(Path.Combine(mod, "Scripts", "main.lua"), Script.Replace("\n", "\r\n"));

            var before = Process.GetProcesses().Select(p => p.Id).ToHashSet();
            notice($"Preparing {name} for its first mod: the game will open for a few seconds.");
            started = DateTime.UtcNow;
            var steam = UnrealGameLocator.SteamApp(game.Root);
            if (steam is { AppId.Length: > 0 } app)
                Process.Start(new ProcessStartInfo($"steam://rungameid/{app.AppId}") { UseShellExecute = true })?.Dispose();
            else
            {
                var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = win64 };
                foreach (var argument in new[] { "-windowed", "-ResX=1280", "-ResY=720" }) start.ArgumentList.Add(argument);
                using var own = Process.Start(start);
                if (own is not null) ours.Add(own.Id);
            }

            // Only processes of this game's folder that started after the launch are this run's (a Steam game may go
            // through a launcher, or Steam may start it again).
            var deadline = started + TimeSpan.FromMinutes(5);
            DateTime? allGone = null;
            var gone = new HashSet<int>();
            while (DateTime.UtcNow < deadline)
            {
                cancel.ThrowIfCancellationRequested();
                Thread.Sleep(1000);
                foreach (var (id, path) in Running(game, before, started))
                    if (ours.Add(id)) log($"Mappings: {Path.GetFileName(path)} started ({id})");
                if (NewMappings(game.Root, started) is { } file && Settled(file))
                {
                    made = file;
                    break;
                }
                foreach (var id in ours.Where(id => !Alive(id) && gone.Add(id))) log($"Mappings: process {id} ended");
                if (ours.Count > 0 && gone.Count == ours.Count) allGone ??= DateTime.UtcNow;
                else allGone = null;
                if (allGone is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(30)) break;
            }
            if (made is null)
            {
                problem = ours.Count == 0
                    ? $"{name} did not start (through {(steam is null ? "its program" : "Steam")}), so its mappings file was not made."
                    : allGone is not null ? $"{name} closed by itself before it wrote its mappings file, so none was made."
                    : $"{name} started but wrote no mappings file within five minutes, so none was made.";
                again = ours.Count == 0;
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                var temporary = output + ".new";
                File.Copy(made, temporary, true);
                File.Move(temporary, output, true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = $"{name}'s mappings file could not be made: {ex.Message}";
        }
        finally
        {
            Close(ours);
            // UE4SS's own log of the run, kept for whoever wants to know why it made nothing (its copy in the game goes).
            var ue4ss = Path.GetFullPath(Path.Combine(mods ?? win64, ".."));
            try
            {
                if (File.Exists(Path.Combine(ue4ss, "UE4SS.log"))) File.Copy(Path.Combine(ue4ss, "UE4SS.log"), Path.Combine(UnrealTools.Folder, $"UE4SS-{Token(game.Project)}.log"), true);
                foreach (var dump in Directory.GetFiles(ue4ss, "*.dmp").Where(d => File.GetLastWriteTimeUtc(d) >= started.AddSeconds(-2)))
                    File.Copy(dump, Path.Combine(UnrealTools.Folder, $"UE4SS-{Token(game.Project)}-{Path.GetFileName(dump)}"), true);
            }
            catch (Exception) { }
            var left = record.Restore();
            if (left.Count > 0)
                problem = (problem is null ? "" : problem + " ") + $"{name}'s folder could not be put back exactly as it was: {string.Join("; ", left.Take(5))}";
            else record.Forget();
        }
        if (problem is not null && made is not null) return new(null, problem);
        if (problem is not null) return new(null, problem, again);
        var info = new FileInfo(exe);
        WriteRecord(output, new Dictionary<string, string>
        {
            ["game"] = name, ["for"] = Identity(game), ["exe"] = exe, ["exe sha256"] = UnrealTools.Sha256(exe),
            ["made"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), ["with"] = "UE4SS v4.0.0 DumpUSMAP",
        });
        return new(output, null);
    }

    /// <summary>Notes that the mappings file made replaces <paramref name="given"/>, which the user's settings line names and which does not fit.</summary>
    public static void Replaces(string mappings, string given)
    {
        var record = Record(mappings);
        record["replaces"] = given;
        WriteRecord(mappings, record);
    }

    /// <summary>Notes, beside a mappings file this would make, that it could not be made for this version of the game: it is not tried again for it.</summary>
    public static void Failed(UnrealGame game, string output, string why)
    {
        var record = Record(output);
        record["failed for"] = Identity(game);
        record["why"] = why;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        WriteRecord(output, record);
    }

    private static string? FindAntiCheat(string root)
    {
        var stack = new Stack<(string Dir, int Depth)>([(root, 0)]);
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); } catch (Exception) { continue; }
            foreach (var entry in entries)
            {
                var folded = Path.GetFileNameWithoutExtension(entry).ToLowerInvariant();
                if (AntiCheat.FirstOrDefault(a => folded.Replace(" ", "").StartsWith(a, StringComparison.Ordinal)) is { } found) return Path.GetFileName(entry);
                if (depth < 4 && Directory.Exists(entry) && !Path.GetFileName(entry).Equals("Content", StringComparison.OrdinalIgnoreCase)) stack.Push((entry, depth + 1));
            }
        }
        return null;
    }

    /// <summary>Processes whose program is in the game's folder: all of them, or those that started after <paramref name="since"/> and were not there before.</summary>
    private static List<(int Id, string Path)> Running(UnrealGame game, HashSet<int>? before = null, DateTime? since = null)
    {
        var root = Path.GetFullPath(game.Root).TrimEnd('\\') + "\\";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(game.Root, "*.exe", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }))
                names.Add(Path.GetFileNameWithoutExtension(file));
        }
        catch (Exception) { }
        var found = new List<(int, string)>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (!names.Contains(process.ProcessName) || (before is not null && before.Contains(process.Id))) continue;
                string path;
                path = ImagePath(process.Id);
                // One that cannot be looked into but has a name of the game's is counted as the game's (it is never closed then).
                if (path.Length > 0 && !path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (since is { } after)
                {
                    try { if (process.StartTime.ToUniversalTime() < after.AddSeconds(-2)) continue; }
                    catch (Exception) { continue; }
                    if (path.Length == 0) continue;
                }
                found.Add((process.Id, path));
            }
        }
        return found;
    }

    private static string Token(string name) => Regex.Replace(name, "[^A-Za-z0-9_.-]+", "_");

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int id);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>A process's program file, asked with the least rights (a protected game cannot be looked into otherwise); "" when it cannot be told.</summary>
    private static string ImagePath(int id)
    {
        var handle = OpenProcess(0x1000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return "";
        try
        {
            var name = new StringBuilder(1024);
            var size = (uint)name.Capacity;
            return QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString(0, (int)size) : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool Alive(int id)
    {
        try { using var p = Process.GetProcessById(id); return !p.HasExited; }
        catch (Exception) { return false; }
    }

    /// <summary>Closes the game copies this run started, and waits until they are gone and their files are free.</summary>
    private static void Close(HashSet<int> ours)
    {
        foreach (var id in ours)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.HasExited) process.Kill();
            }
            catch (Exception) { }
        }
        foreach (var id in ours)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (Exception) { }
        }
        if (ours.Count > 0) Thread.Sleep(2000);
    }

    private static string? NewMappings(string root, DateTime since)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.usmap", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6, IgnoreInaccessible = true })
                .Where(f => File.GetLastWriteTimeUtc(f) >= since.AddSeconds(-2)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    /// <summary>A file written to the end: not empty, and the same size a few seconds on.</summary>
    private static bool Settled(string file)
    {
        var size = new FileInfo(file).Length;
        if (size == 0) return false;
        Thread.Sleep(3000);
        return File.Exists(file) && new FileInfo(file).Length == size;
    }

    /// <summary>
    /// A run for this game that stopped before it put the game's folder back (Crucible was closed meanwhile): what it
    /// changed is put back now, from its record. Its record is then let go, whatever the user changed since staying.
    /// </summary>
    private static void Recover(string root)
    {
        foreach (var file in Directory.Exists(Run.Folder) ? Directory.GetFiles(Run.Folder, "*.run") : [])
        {
            var run = Run.Load(file);
            if (run is null || !run.Roots.Contains(Path.GetFullPath(root), StringComparer.OrdinalIgnoreCase)) continue;
            run.Restore(recovering: true);
            run.Forget();
        }
    }

    /// <summary>
    /// A game's folders recorded exactly (each file's name, size, SHA-256, times and attributes; each folder's times),
    /// kept on disk under <c>Tools\</c> until they are put back, with a copy of each small file the game may rewrite.
    /// </summary>
    private sealed class Run
    {
        public static string Folder => Path.Combine(UnrealTools.Folder, "UE4SS-runs");
        private readonly string _id;
        /// <summary>The folders recorded: the executable's with all below it, the game's own top without.</summary>
        private readonly List<(string Dir, bool Deep)> _roots = [];
        private readonly Dictionary<string, (long Size, long Write, long Create, FileAttributes Attributes, string Sha)> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (long Write, long Create)> _dirs = new(StringComparer.OrdinalIgnoreCase);

        private Run(string id) => _id = id;

        public Run(string root, string win64) : this(Guid.NewGuid().ToString("N"))
        {
            _roots.Add((Path.GetFullPath(win64), true));
            _roots.Add((Path.GetFullPath(root), false));
            foreach (var (dir, deep) in _roots) Scan(dir, deep, _files, _dirs);
        }

        private string Copies => Path.Combine(Folder, _id);

        private static void Scan(string dir, bool deep, Dictionary<string, (long, long, long, FileAttributes, string)> files, Dictionary<string, (long, long)> dirs)
        {
            dirs[dir] = (Directory.GetLastWriteTimeUtc(dir).Ticks, Directory.GetCreationTimeUtc(dir).Ticks);
            foreach (var file in Directory.GetFiles(dir))
            {
                var info = new FileInfo(file);
                files[file] = (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks, info.Attributes, UnrealTools.Sha256(file));
            }
            if (deep) foreach (var sub in Directory.GetDirectories(dir)) Scan(sub, true, files, dirs);
        }

        /// <summary>Written before anything changes: the record, and a copy of each file small enough to be one the game rewrites.</summary>
        public void Save()
        {
            Directory.CreateDirectory(Copies);
            var text = new StringBuilder($"started\t{_started}\n");
            foreach (var (dir, deep) in _roots) text.Append($"root\t{dir}\t{(deep ? 1 : 0)}\n");
            foreach (var (dir, (write, create)) in _dirs) text.Append($"dir\t{dir}\t{write}\t{create}\n");
            var n = 0;
            foreach (var (file, (size, write, create, attributes, sha)) in _files)
            {
                var copy = "";
                if (size <= KeepBytes)
                {
                    copy = (n++).ToString("D5");
                    File.Copy(file, Path.Combine(Copies, copy), true);
                    _copyOf[file] = copy;
                }
                text.Append($"file\t{file}\t{size}\t{write}\t{create}\t{(int)attributes}\t{sha}\t{copy}\n");
            }
            File.WriteAllText(Path.Combine(Folder, _id + ".run"), text.ToString());
        }

        public static Run? Load(string path)
        {
            try
            {
                var run = new Run(Path.GetFileNameWithoutExtension(path));
                foreach (var line in File.ReadAllLines(path))
                {
                    var cells = line.Split('\t');
                    switch (cells[0])
                    {
                        case "started": run._started = long.Parse(cells[1]); break;
                        case "root": run._roots.Add((cells[1], cells[2] == "1")); break;
                        case "dir": run._dirs[cells[1]] = (long.Parse(cells[2]), long.Parse(cells[3])); break;
                        case "file":
                            run._files[cells[1]] = (long.Parse(cells[2]), long.Parse(cells[3]), long.Parse(cells[4]), (FileAttributes)int.Parse(cells[5]), cells[6]);
                            run._copyOf[cells[1]] = cells[7];
                            break;
                    }
                }
                return run;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Which folders a run recorded (to find a stopped run of the same game).</summary>
        public IEnumerable<string> Roots => _roots.Select(r => r.Dir);

        private readonly Dictionary<string, string> _copyOf = new(StringComparer.OrdinalIgnoreCase);
        private long _started = DateTime.UtcNow.Ticks;

        private string? CopyOf(string file) => _copyOf.TryGetValue(file, out var copy) && copy.Length > 0 ? Path.Combine(Copies, copy) : null;

        /// <summary>
        /// Puts the folders back as recorded: what is new goes, what changed or went comes back from its copy, every
        /// file's and folder's times and attributes as they were. Returns what still differs (nothing when all is back).
        /// For a run that stopped (<paramref name="recovering"/>) only what changed within the run's own time is touched,
        /// so nothing the user put in since is taken away.
        /// </summary>
        public List<string> Restore(bool recovering = false)
        {
            for (var attempt = 0; attempt < (recovering ? 1 : 5); attempt++)
            {
                try { Put(recovering ? new DateTime(_started, DateTimeKind.Utc).AddMinutes(15) : DateTime.MaxValue); }
                catch (Exception) { }
                var left = Differences();
                if (left.Count == 0) return left;
                if (!recovering) Thread.Sleep(2000);
            }
            return Differences();
        }

        private void Put(DateTime until)
        {
            bool Ours(string path) => File.GetLastWriteTimeUtc(path) <= until && File.GetCreationTimeUtc(path) <= until;
            foreach (var (root, deep) in _roots)
            {
                var files = Directory.GetFiles(root, "*", deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                foreach (var file in files.Where(f => !_files.ContainsKey(f) && Ours(f)))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                if (deep)
                    foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Where(d => !_dirs.ContainsKey(d)).OrderByDescending(d => d.Length))
                        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).Any(e => File.Exists(e) && !Ours(e)))
                            Directory.Delete(dir, true);
            }
            foreach (var (file, (size, write, create, attributes, sha)) in _files)
            {
                if (File.Exists(file) && !Ours(file)) continue;
                var same = File.Exists(file) && new FileInfo(file).Length == size && UnrealTools.Sha256(file) == sha;
                if (!same && CopyOf(file) is { } copy && File.Exists(copy))
                {
                    if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.Copy(copy, file, true);
                }
                if (!File.Exists(file)) continue;
                File.SetAttributes(file, FileAttributes.Normal);
                File.SetCreationTimeUtc(file, new DateTime(create, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(file, new DateTime(write, DateTimeKind.Utc));
                File.SetAttributes(file, attributes);
            }
            // Folder times last, deepest first: putting their files back changed them.
            foreach (var (dir, (write, create)) in _dirs.OrderByDescending(d => d.Key.Length))
            {
                if (!Directory.Exists(dir)) continue;
                Directory.SetCreationTimeUtc(dir, new DateTime(create, DateTimeKind.Utc));
                Directory.SetLastWriteTimeUtc(dir, new DateTime(write, DateTimeKind.Utc));
            }
        }

        private List<string> Differences()
        {
            var files = new Dictionary<string, (long, long, long, FileAttributes, string)>(StringComparer.OrdinalIgnoreCase);
            var dirs = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            var left = new List<string>();
            try { foreach (var (dir, deep) in _roots) Scan(dir, deep, files, dirs); }
            catch (Exception ex) { return [ex.Message]; }
            foreach (var file in files.Keys.Except(_files.Keys, StringComparer.OrdinalIgnoreCase)) left.Add($"{file} is new");
            foreach (var (file, was) in _files)
                if (!files.TryGetValue(file, out var now)) left.Add($"{file} is gone");
                else if (now != was) left.Add($"{file} differs");
            foreach (var dir in dirs.Keys.Except(_dirs.Keys, StringComparer.OrdinalIgnoreCase)) left.Add($"{dir} is new");
            foreach (var (dir, was) in _dirs)
                if (!dirs.TryGetValue(dir, out var now)) left.Add($"{dir} is gone");
                else if (now != was) left.Add($"{dir}'s times differ");
            return left;
        }

        public void Forget()
        {
            try { File.Delete(Path.Combine(Folder, _id + ".run")); } catch (Exception) { }
            try { if (Directory.Exists(Copies)) Directory.Delete(Copies, true); } catch (Exception) { }
        }
    }
}
