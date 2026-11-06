using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WoWCrucible.Core;

internal static class TactCdnFetchTestSuite
{
    private const string Build = "11111111111111111111111111111111";
    private const string Cdn = "22222222222222222222222222222222";

    private sealed class Server(byte[] archive, string archiveName) : HttpMessageHandler
    {
        public Func<int, HttpResponseMessage?>? Interfere;
        public int ArchiveRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (request.Headers.Authorization is not null || request.Headers.Contains("Cookie")) throw new InvalidOperationException("Credentials were sent.");
            if (url.EndsWith("/versions", StringComparison.Ordinal))
                return Task.FromResult(Text($"Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16\n## seqn = 1\nus|{Build}|{Cdn}\n"));
            if (url.EndsWith("/cdns", StringComparison.Ordinal))
                return Task.FromResult(Text("Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0\nus|tpr/wow|cdn.test|http://cdn.test|tpr/configs/data\n"));
            if (!url.Equals($"http://cdn.test/tpr/wow/data/{archiveName[..2]}/{archiveName[2..4]}/{archiveName}", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var count = Interlocked.Increment(ref ArchiveRequests);
            if (Interfere?.Invoke(count) is { } interference) return Task.FromResult(interference);
            var range = request.Headers.Range!.Ranges.Single();
            var slice = archive[(int)range.From!.Value..((int)range.To!.Value + 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(slice) });
        }

        private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }

    private static byte[] Tabled(params byte[][] rawBlocks)
    {
        var blocks = rawBlocks.Select(raw =>
        {
            using var memory = new MemoryStream();
            memory.WriteByte((byte)'Z');
            using (var zlib = new ZLibStream(memory, CompressionMode.Compress, leaveOpen: true)) zlib.Write(raw);
            return (Encoded: memory.ToArray(), Decoded: raw.Length);
        }).ToArray();
        using var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("BLTE"));
        var four = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, 12 + blocks.Length * 24); file.Write(four);
        file.Write([0x0F, 0, 0, (byte)blocks.Length]);
        foreach (var block in blocks)
        {
            BinaryPrimitives.WriteInt32BigEndian(four, block.Encoded.Length); file.Write(four);
            BinaryPrimitives.WriteInt32BigEndian(four, block.Decoded); file.Write(four);
            file.Write(MD5.HashData(block.Encoded));
        }
        foreach (var block in blocks) file.Write(block.Encoded);
        return file.ToArray();
    }

    private static byte[] Single(byte mode, byte[] payload)
    {
        var file = new byte[9 + payload.Length];
        Encoding.ASCII.GetBytes("BLTE").CopyTo(file, 0);
        file[8] = mode;
        payload.CopyTo(file, 9);
        return file;
    }

    private static byte[] KeyOf(byte[] blte)
    {
        var headerSize = BinaryPrimitives.ReadInt32BigEndian(blte.AsSpan(4));
        return headerSize == 0 ? MD5.HashData(blte) : MD5.HashData(blte.AsSpan(0, headerSize));
    }

    private static string Hex(byte[] key) => Convert.ToHexString(key).ToLowerInvariant();

    /// <summary>An archive index with one page, and the archive's name: the MD5 of its footer.</summary>
    private static (byte[] Index, string Name) BuildIndex(params (byte[] Key, int Size, int Offset)[] rows)
    {
        var bytes = new byte[4096 + 24 + 28];
        var at = 0;
        foreach (var (key, size, offset) in rows)
        {
            key.CopyTo(bytes, at);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 16), size);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(at + 20), offset);
            at += 24;
        }
        bytes[bytes.Length - 20] = 1;
        return (bytes, Hex(MD5.HashData(bytes.AsSpan(bytes.Length - 28))));
    }

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crucible-tact-fetch-{Guid.NewGuid():N}");
        try
        {
            Formats();
            Verification();
            FakeCdn(root).GetAwaiter().GetResult();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("TACT CDN fetch checks passed.");
    }

    private static void Formats()
    {
        var rows = TactCdnFetchService.ParsePsv("A!STRING:0|B!HEX:16\n## seqn = 3\nx|y\n");
        Check(rows.Count == 1 && rows[0]["B"] == "y", "A pipe table's cells are named by its header.");
        Check(TactCdnFetchService.Extension("a/b/C.BLP", [1]) == "blp", "The listfile's extension wins, lower-cased.");
        Check(TactCdnFetchService.Extension(null, Encoding.ASCII.GetBytes("BLP2xx")) == "blp", "A nameless texture is named by its magic.");
        Check(TactCdnFetchService.Extension(null, [1, 2, 3, 4]) == "bin", "An unknown nameless file is .bin.");

        var (index, name) = BuildIndex((new byte[16], 0, 0));
        Check(!TactCdnFetchService.ReadArchiveIndex(index, name).Any(), "A zero size ends a page.");
        var threw = false;
        try { _ = TactCdnFetchService.ReadArchiveIndex(index, new string('0', 32)).ToList(); }
        catch (InvalidDataException) { threw = true; }
        Check(threw, "An index that does not hash to its archive's name is refused.");
    }

    private static void Verification()
    {
        var content = Encoding.ASCII.GetBytes("hello hello hello");
        var tabled = Tabled(content, content);
        var ok = TactCdnFetchService.CheckBlte(tabled, KeyOf(tabled));
        Check(ok.Decoded is { } both && both.SequenceEqual(content.Concat(content)), "Two blocks decode in order.");

        var single = Single((byte)'N', content);
        Check(TactCdnFetchService.CheckBlte(single, KeyOf(single)).Decoded?.SequenceEqual(content) == true, "A table-less container decodes.");

        var wrongKey = TactCdnFetchService.CheckBlte(tabled, new byte[16]);
        Check(wrongKey.Decoded is null && wrongKey.Failure!.Contains("encoding key"), "A foreign encoding key is refused.");

        var flipped = (byte[])tabled.Clone();
        flipped[^1] ^= 0xFF;
        var corrupt = TactCdnFetchService.CheckBlte(flipped, KeyOf(tabled));
        Check(corrupt.Decoded is null && corrupt.Failure!.Contains("does not hash"), "A flipped body byte is caught by the block's MD5.");
    }

    private static async Task FakeCdn(string root)
    {
        var good = Encoding.ASCII.GetBytes("BLP2 a texture's bytes");
        var goodBlte = Tabled(good);
        var encrypted = Single((byte)'E', [8, 1, 2, 3, 4, 5, 6, 7, 8, 0]);
        var corruptSource = Tabled(Encoding.ASCII.GetBytes("will be damaged"));
        var corrupt = (byte[])corruptSource.Clone();
        corrupt[^1] ^= 0xFF;
        var archive = goodBlte.Concat(encrypted).Concat(corrupt).ToArray();
        var (goodKey, encryptedKey, corruptKey) = (KeyOf(goodBlte), KeyOf(encrypted), KeyOf(corruptSource));
        var (index, archiveName) = BuildIndex(
            (goodKey, goodBlte.Length, 0),
            (encryptedKey, encrypted.Length, goodBlte.Length),
            (corruptKey, corrupt.Length, goodBlte.Length + encrypted.Length));

        var install = Path.Combine(root, "install");
        Directory.CreateDirectory(Path.Combine(install, "Data", "indices"));
        var config = Path.Combine(install, "Data", "config", Cdn[..2], Cdn[2..4]);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, Cdn), $"# CDN Configuration\n\narchives = {archiveName}\n");
        File.WriteAllBytes(Path.Combine(install, "Data", "indices", archiveName + ".index"), index);

        var dump = Path.Combine(root, "dump.tsv");
        File.WriteAllLines(dump,
        [
            $"100\tNotResident in a CDN archive\t{Hex(goodKey)}",
            $"101\tNotResident in a CDN archive\t{Hex(goodKey)}",
            $"200\tNotResident in a CDN archive\t{Hex(encryptedKey)}",
            $"300\tNotResident in a CDN archive\t{Hex(corruptKey)}",
            $"400\tNotResident in no CDN archive\t{new string('a', 32)}",
            $"500\tResident\t{new string('b', 32)}",
            "600\tNotInRoot\t",
        ]);
        var listfile = Path.Combine(root, "listfile.csv");
        File.WriteAllText(listfile, "100;a/hundred.blp\n");

        TactFetchRequest Request(string folder) => new(install, "http://tact.test/wow", dump, Path.Combine(root, folder), listfile,
            MinRequestIntervalMs: 0, MaxAttempts: 3);
        Task<TactFetchSummary> Go(Server server, TactFetchRequest request) => new TactCdnFetchService(server).RunAsync(request);
        Server Fresh() => new(archive, archiveName);

        var refused = false;
        try { await Go(Fresh(), Request("unused") with { OutputDirectory = Path.Combine(install, "Data", "x") }); }
        catch (ArgumentException) { refused = true; }
        Check(refused, "An output folder inside the install is refused.");

        var plan = Fresh();
        var planned = await Go(plan, Request("plan") with { PlanOnly = true });
        Check(plan.ArchiveRequests == 0 && planned.Fetched == 0 && planned.PlannedBytes == archive.Length, "A plan touches no archive.");

        var overLimit = Fresh();
        var over = await Go(overLimit, Request("over") with { MaxBytes = 1 });
        Check(overLimit.ArchiveRequests == 0 && over.StoppedBecause!.Contains("limit"), "A plan over the limit stops before fetching.");

        // The first archive request is rate limited: it must be retried, not failed.
        var server = Fresh();
        server.Interfere = count => count == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } }
            : null;
        var run = await Go(server, Request("out"));
        var outDir = Path.Combine(root, "out");
        Check(run.Requested == 5 && run.Fetched == 2 && run.Encrypted == 1 && run.Failed == 2 && run.NotInIndex == 1 && run.StoppedBecause is null,
            $"Counts: fetched {run.Fetched}, encrypted {run.Encrypted}, failed {run.Failed}, not indexed {run.NotInIndex}, of {run.Requested}.");
        Check(server.ArchiveRequests == 4, $"Three keys and one retry make four requests, not {server.ArchiveRequests}.");
        Check(File.ReadAllBytes(Path.Combine(outDir, "100.blp")).SequenceEqual(good), "The named file holds its decoded bytes.");
        Check(File.ReadAllBytes(Path.Combine(outDir, "101.blp")).SequenceEqual(good), "Two ids of one key both land; a nameless one by its magic.");
        Check(Directory.GetFiles(outDir, "200.*").Length == 0, "An encrypted file is not kept.");
        Check(Directory.GetFiles(outDir, "300.*").Length == 0, "A file failing verification is not kept.");
        Check(Directory.GetFiles(outDir, "500.*").Length == 0 && Directory.GetFiles(outDir, "600.*").Length == 0, "Only not-resident rows are fetched.");
        Check(Directory.GetFiles(outDir, "*.part").Length == 0, "No partial file is left.");
        var log = File.ReadAllLines(Path.Combine(outDir, "tact-fetch-log.tsv"));
        Check(log.Any(line => line.StartsWith("200\tencrypted", StringComparison.Ordinal) && line.Contains("0807060504030201")), "The encrypted file's key name is logged.");
        Check(log.Any(line => line.StartsWith("300\tfailed", StringComparison.Ordinal)) && log.Any(line => line.StartsWith("400\tfailed", StringComparison.Ordinal)), "Failures are logged by id.");

        var again = Fresh();
        var resumed = await Go(again, Request("out"));
        Check(resumed.AlreadyPresent == 2 && again.ArchiveRequests == 2, "A rerun skips the files it already kept.");

        var forbidden = Fresh();
        forbidden.Interfere = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var stoppedRun = await Go(forbidden, Request("forbidden") with { Connections = 1 });
        Check(stoppedRun.StoppedBecause!.Contains("403") && forbidden.ArchiveRequests == 1 && stoppedRun.Fetched == 0, "A refusal stops the whole run at once.");

        var noRange = Fresh();
        noRange.Interfere = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) };
        var wholeRun = await Go(noRange, Request("norange") with { Connections = 1 });
        Check(wholeRun.StoppedBecause!.Contains("ignored the range") && wholeRun.BytesDownloaded == 0, "A server ignoring ranges stops the run without reading the archive.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
