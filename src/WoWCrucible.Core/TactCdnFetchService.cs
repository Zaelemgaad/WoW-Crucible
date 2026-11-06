using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

/// <summary>What a TACT fetch was asked to do. <see cref="CensusDump"/> is <c>casc_census --dump</c>'s
/// <c>fdid, class, encoding key</c> lines; only <c>NotResident</c> rows are fetched.</summary>
public sealed record TactFetchRequest(
    string InstallRoot,
    string TactUrl,
    string CensusDump,
    string OutputDirectory,
    string? Listfile = null,
    bool PlanOnly = false,
    int Connections = 2,
    int MinRequestIntervalMs = 250,
    long MaxBytes = 8L * 1024 * 1024 * 1024,
    int MaxAttempts = 5);

public sealed record TactFetchSummary(
    string BuildConfig,
    string CdnConfig,
    string Host,
    int Requested,
    int AlreadyPresent,
    int NotInIndex,
    int Fetched,
    int Failed,
    int Encrypted,
    long PlannedBytes,
    long BytesDownloaded,
    string? StoppedBecause);

/// <summary>A BLTE container checked against its encoding key, then decoded.</summary>
internal readonly record struct BlteResult(byte[]? Decoded, ulong? EncryptedWith, string? Failure);

/// <summary>Fetches named files from a TACT CDN by range request into the archives, reading the build
/// from the server's public <c>versions</c> and <c>cdns</c> and locations from the install's own CDN
/// config and archive indices. Sends no credentials, and stops at the first refusal. A file is kept only
/// once its bytes match its encoding key.</summary>
public sealed class TactCdnFetchService(HttpMessageHandler? handler = null)
{
    private readonly record struct Location(string Archive, uint Offset, uint Size);

    private sealed record Item(string Key, Location Where, List<uint> Ids);

    public async Task<TactFetchSummary> RunAsync(
        TactFetchRequest request,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var install = Path.GetFullPath(request.InstallRoot);
        var output = Path.GetFullPath(request.OutputDirectory);
        if (IsInside(output, install))
            throw new ArgumentException($"The output folder {output} is inside the install, which is read-only.");

        using var http = new HttpClient(
            handler ?? new SocketsHttpHandler { MaxConnectionsPerServer = request.Connections },
            disposeHandler: handler is null);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WoWCrucible-tact-fetch");
        var tact = request.TactUrl.TrimEnd('/');

        var versions = ParsePsv(await http.GetStringAsync($"{tact}/versions", cancellationToken));
        var cdns = ParsePsv(await http.GetStringAsync($"{tact}/cdns", cancellationToken));
        if (versions.Count == 0 || cdns.Count == 0) throw new InvalidDataException("The server's versions or cdns table is empty.");
        var (buildConfig, cdnConfig) = (versions[0]["BuildConfig"], versions[0]["CDNConfig"]);
        var host = cdns[0]["Hosts"].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var cdnPath = cdns[0]["Path"].Trim('/');
        var server = $"http://{host}";

        var data = Path.Combine(install, "Data");
        var configPath = Path.Combine(data, "config", cdnConfig[..2], cdnConfig[2..4], cdnConfig);
        if (!File.Exists(configPath)) throw new FileNotFoundException("The server's CDN config is not in this install.", configPath);
        var archives = ConfigValues(File.ReadAllLines(configPath), "archives");

        var wanted = ReadWanted(request.CensusDump);
        var names = request.Listfile is null ? [] : ReadNames(request.Listfile, wanted.Values.SelectMany(ids => ids).ToHashSet());
        Directory.CreateDirectory(output);
        var present = Directory.EnumerateFiles(output).Select(Path.GetFileNameWithoutExtension).ToHashSet();

        var locations = LocateAll(Path.Combine(data, "indices"), archives, wanted.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        var log = new List<string>();
        var (requested, alreadyPresent, notInIndex) = (wanted.Values.Sum(ids => ids.Count), 0, 0);
        var work = new List<Item>();
        foreach (var (key, ids) in wanted)
        {
            var open = ids.Where(id => !present.Contains(id.ToString())).ToList();
            alreadyPresent += ids.Count - open.Count;
            if (open.Count == 0) continue;
            if (locations.TryGetValue(key, out var where)) work.Add(new Item(key, where, open));
            else
            {
                notInIndex += open.Count;
                log.AddRange(open.Select(id => $"{id}\tfailed\t{key}\t\t0\t0\tnot in any archive index of {cdnConfig}"));
            }
        }
        var planned = work.Sum(item => (long)item.Where.Size);
        progress?.Invoke($"build {buildConfig} cdn {cdnConfig} via {server}/{cdnPath}: {work.Sum(item => item.Ids.Count)} files, {planned:N0} bytes in {work.Select(item => item.Where.Archive).Distinct().Count()} archives");
        TactFetchSummary Summary(int ok, int bad, int secret, long bytes, string? why) =>
            new(buildConfig, cdnConfig, host, requested, alreadyPresent, notInIndex, ok, bad, secret, planned, bytes, why);

        if (planned > request.MaxBytes) return Summary(0, 0, 0, 0, $"The plan is {planned:N0} bytes, over the {request.MaxBytes:N0} limit.");
        if (request.PlanOnly) return Summary(0, 0, 0, 0, null);

        var (fetched, failed, encrypted, done) = (0, 0, 0, 0);
        var downloaded = 0L;
        string? stopped = null;
        var nextSlot = DateTime.UtcNow;
        var gate = new object();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void Stop(string why)
        {
            lock (gate) stopped ??= why;
            stop.Cancel();
        }

        // One shared schedule across connections; a server's Retry-After pushes everyone back.
        async Task Pace(TimeSpan delay)
        {
            TimeSpan wait;
            lock (gate)
            {
                var now = DateTime.UtcNow;
                if (nextSlot < now) nextSlot = now;
                if (delay > TimeSpan.Zero) nextSlot += delay;
                wait = nextSlot - now;
                nextSlot += TimeSpan.FromMilliseconds(request.MinRequestIntervalMs);
            }
            if (wait > TimeSpan.Zero) await Task.Delay(wait, stop.Token);
        }

        // The container's bytes, or why not. A refusal stops the run.
        async Task<(byte[]? Bytes, string? Failure)> Download(Location where)
        {
            var archive = where.Archive;
            var url = $"{server}/{cdnPath}/data/{archive[..2]}/{archive[2..4]}/{archive}";
            var last = "no attempt";
            var backoff = TimeSpan.Zero;
            for (var attempt = 1; attempt <= request.MaxAttempts; attempt++)
            {
                await Pace(backoff);
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.Range = new RangeHeaderValue(where.Offset, (long)where.Offset + where.Size - 1);
                TimeSpan? asked = null;
                try
                {
                    using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    var code = (int)response.StatusCode;
                    if (code is 401 or 403 or 407 or 451)
                    {
                        Stop($"{url} refused with {code}.");
                        return (null, null);
                    }
                    if (code == 200)
                    {
                        // Never read on: that body is a whole archive.
                        Stop($"{url} ignored the range request.");
                        return (null, null);
                    }
                    if (code == 206)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(stop.Token);
                        Interlocked.Add(ref downloaded, bytes.Length);
                        if (bytes.Length == where.Size) return (bytes, null);
                        last = $"got {bytes.Length} bytes, expected {where.Size}";
                    }
                    else
                    {
                        last = $"status {code}";
                        if (code != 429 && code < 500) return (null, last);
                        asked = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
                    }
                }
                catch (HttpRequestException e) { last = e.Message; }
                catch (TaskCanceledException) when (!stop.IsCancellationRequested) { last = "timed out"; }
                backoff = asked ?? TimeSpan.FromSeconds(1 << attempt);
            }
            return (null, last);
        }

        void Record(Item item, string status, long decoded, string detail)
        {
            lock (log)
            {
                log.AddRange(item.Ids.Select(id => $"{id}\t{status}\t{item.Key}\t{item.Where.Archive}\t{item.Where.Size}\t{decoded}\t{detail}"));
                switch (status)
                {
                    case "fetched": fetched += item.Ids.Count; break;
                    case "encrypted": encrypted += item.Ids.Count; break;
                    default: failed += item.Ids.Count; break;
                }
            }
        }

        async Task Fetch(Item item)
        {
            var (raw, failure) = await Download(item.Where);
            if (raw is null)
            {
                if (failure is not null) Record(item, "failed", 0, failure);
                return;
            }
            var result = CheckBlte(raw, Convert.FromHexString(item.Key));
            if (result.Decoded is { } bytes)
            {
                foreach (var id in item.Ids)
                {
                    var final = Path.Combine(output, $"{id}.{Extension(names.GetValueOrDefault(id), bytes)}");
                    await File.WriteAllBytesAsync(final + ".part", bytes, CancellationToken.None);
                    File.Move(final + ".part", final);
                }
                Record(item, "fetched", bytes.Length, "");
            }
            else if (result.EncryptedWith is { } name) Record(item, "encrypted", 0, $"TACT key {name:X16}");
            else Record(item, "failed", 0, result.Failure ?? "");

            var count = Interlocked.Increment(ref done);
            if (count % 50 == 0) progress?.Invoke($"{count} of {work.Count} keys, {Interlocked.Read(ref downloaded):N0} bytes");
        }

        try
        {
            await Parallel.ForEachAsync(work,
                new ParallelOptions { MaxDegreeOfParallelism = request.Connections, CancellationToken = stop.Token },
                async (item, _) => await Fetch(item));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && stopped is not null)
        {
            // A refusal: the summary says so, and what was kept stays kept.
        }
        finally
        {
            File.AppendAllLines(Path.Combine(output, "tact-fetch-log.tsv"), log);
        }
        return Summary(fetched, failed + notInIndex, encrypted, Interlocked.Read(ref downloaded), stopped);
    }

    private static bool IsInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative));
    }

    /// <summary>A Blizzard pipe-separated table: a <c>Name!TYPE:n|...</c> header, with <c>#</c> comments.</summary>
    internal static List<Dictionary<string, string>> ParsePsv(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#')).ToArray();
        if (lines.Length == 0) return [];
        var header = lines[0].Split('|').Select(cell => cell.Split('!')[0]).ToArray();
        return lines.Skip(1)
            .Select(line => header.Zip(line.Split('|')).ToDictionary(pair => pair.First, pair => pair.Second))
            .ToList();
    }

    private static string[] ConfigValues(IEnumerable<string> lines, string key) =>
        lines.Select(line => line.Split(" = ", 2)).Where(pair => pair.Length == 2 && pair[0].Trim() == key)
            .Select(pair => pair[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault()
        ?? throw new InvalidDataException($"The CDN config has no {key}.");

    /// <summary>Encoding key to FileDataIDs, from the rows the census calls not resident.</summary>
    private static Dictionary<string, List<uint>> ReadWanted(string dump)
    {
        var wanted = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(dump))
        {
            var cells = line.Split('\t');
            if (cells.Length < 3 || !cells[1].StartsWith("NotResident", StringComparison.Ordinal)) continue;
            if (!wanted.TryGetValue(cells[2], out var ids)) wanted[cells[2]] = ids = [];
            ids.Add(uint.Parse(cells[0]));
        }
        return wanted;
    }

    private static Dictionary<uint, string> ReadNames(string listfile, HashSet<uint> ids)
    {
        var names = new Dictionary<uint, string>();
        foreach (var line in File.ReadLines(listfile))
        {
            var split = line.IndexOf(';');
            if (split > 0 && uint.TryParse(line.AsSpan(0, split), out var id) && ids.Contains(id)) names[id] = line[(split + 1)..];
        }
        return names;
    }

    private static Dictionary<string, Location> LocateAll(string indices, string[] archives, HashSet<string> keys)
    {
        var found = new Dictionary<string, Location>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            var path = Path.Combine(indices, archive + ".index");
            if (!File.Exists(path)) continue;
            foreach (var (key, size, offset) in ReadArchiveIndex(File.ReadAllBytes(path), archive))
                if (keys.Contains(key)) found.TryAdd(key, new Location(archive, offset, size));
        }
        return found;
    }

    /// <summary>An archive index: 4 KB pages of <c>key, BE size, BE offset</c>, a table of contents, and a
    /// footer whose MD5 is the archive's name. A size of zero ends a page.</summary>
    internal static IEnumerable<(string Key, uint Size, uint Offset)> ReadArchiveIndex(byte[] index, string archive)
    {
        const int footer = 28, page = 4096, record = 24, contentsPerPage = 24;
        if (index.Length < footer || (index.Length - footer) % (page + contentsPerPage) != 0)
            throw new InvalidDataException($"{archive}.index has an unexpected length, {index.Length}.");
        if (!Convert.ToHexString(MD5.HashData(index.AsSpan(index.Length - footer))).Equals(archive, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{archive}.index's footer does not hash to its name.");
        var pages = (index.Length - footer) / (page + contentsPerPage);
        for (var p = 0; p < pages; p++)
        {
            for (var at = p * page; at + record <= (p + 1) * page; at += record)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(at + 16, 4));
                if (size == 0) break;
                var offset = BinaryPrimitives.ReadUInt32BigEndian(index.AsSpan(at + 20, 4));
                yield return (Convert.ToHexString(index, at, 16), size, offset);
            }
        }
    }

    /// <summary>Check a BLTE container against its encoding key, the MD5 of its header when it has a block
    /// table (whose entries carry each block's MD5), or of the whole container when it has none.</summary>
    internal static BlteResult CheckBlte(byte[] data, byte[] encodingKey)
    {
        static BlteResult Bad(string why) => new(null, null, why);
        if (data.Length < 8 || Encoding.ASCII.GetString(data, 0, 4) != "BLTE") return Bad("missing BLTE magic");
        var headerSize = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        try
        {
            using var output = new MemoryStream();
            if (headerSize == 0)
            {
                if (!MD5.HashData(data).AsSpan().SequenceEqual(encodingKey)) return Bad("the container does not hash to its encoding key");
                var single = data[8..];
                if (single.Length > 0 && single[0] == (byte)'E') return new(null, EncryptedKeyName(single), null);
                return AppendBlock(single, null, output) is { } failure ? Bad(failure) : new(output.ToArray(), null, null);
            }

            if (headerSize > data.Length || headerSize < 12) return Bad("the BLTE header overruns the file");
            if (!MD5.HashData(data.AsSpan(0, headerSize)).AsSpan().SequenceEqual(encodingKey)) return Bad("the header does not hash to its encoding key");
            var stride = data[8] switch { 0x0F => 24, 0x10 => 40, _ => 0 };
            if (stride == 0) return Bad($"unknown BLTE table flags {data[8]:X2}");
            var blocks = (data[9] << 16) | (data[10] << 8) | data[11];
            if (12 + blocks * stride > headerSize) return Bad("the block table overruns its header");
            var position = headerSize;
            for (var i = 0; i < blocks; i++)
            {
                var entry = data.AsSpan(12 + i * stride, stride);
                var encoded = (int)BinaryPrimitives.ReadUInt32BigEndian(entry);
                var decoded = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                if (encoded <= 0 || position + encoded > data.Length) return Bad($"block {i} overruns the file");
                var block = data[position..(position + encoded)];
                if (!MD5.HashData(block).AsSpan().SequenceEqual(entry.Slice(8, 16))) return Bad($"block {i} does not hash to its table entry");
                if (block[0] == (byte)'E') return new(null, EncryptedKeyName(block), null);
                if (AppendBlock(block, decoded, output) is { } failure) return Bad($"block {i}: {failure}");
                position += encoded;
            }
            return position == data.Length ? new(output.ToArray(), null, null) : Bad("bytes left after the last block");
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return Bad(e.Message);
        }
    }

    private static string? AppendBlock(byte[] block, int? decodedSize, MemoryStream output)
    {
        var start = output.Length;
        switch ((char)block[0])
        {
            case 'N':
                output.Write(block, 1, block.Length - 1);
                break;
            case 'Z':
                using (var zlib = new ZLibStream(new MemoryStream(block, 1, block.Length - 1), CompressionMode.Decompress))
                    zlib.CopyTo(output);
                break;
            default:
                return $"unknown block mode {block[0]:X2}";
        }
        var got = output.Length - start;
        return decodedSize is { } size && got != size ? $"decoded to {got} bytes, its table says {size}" : null;
    }

    /// <summary>An E block: a name length, always 8, then the name as a little-endian <c>u64</c>.</summary>
    private static ulong EncryptedKeyName(byte[] block) =>
        block.Length >= 10 && block[1] == 8 ? BitConverter.ToUInt64(block, 2) : 0;

    /// <summary>The listfile's extension, else one read from the file's magic.</summary>
    internal static string Extension(string? listfileName, byte[] bytes)
    {
        var fromName = Path.GetExtension(listfileName)?.TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrEmpty(fromName)) return fromName;
        var magic = bytes.Length >= 4 ? Encoding.ASCII.GetString(bytes, 0, 4) : "";
        return magic switch
        {
            "BLP2" => "blp",
            "MD21" or "MD20" => "m2",
            "SKIN" => "skin",
            "SKL1" => "skel",
            "AFM2" or "AFSA" or "AFSB" => "anim",
            _ => "bin",
        };
    }
}
