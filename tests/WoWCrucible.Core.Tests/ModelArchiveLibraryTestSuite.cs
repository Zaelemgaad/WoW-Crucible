using System.IO.Compression;
using WoWCrucible.Core;

internal static class ModelArchiveLibraryTestSuite
{
    public static void Run()
    {
        var listing = "Path = Character\\Human\\a.m2\nFolder = -\nSize = 42\nSymbolic Link = \nHard Link = \n\n";
        Require(ModelArchiveLibraryService.ReadEntries(listing)["Character/Human/a.m2"] == 42, "RAR empty link metadata is not a link.");
        Require(ModelArchiveLibraryService.ReadEntries("Path = \nFolder = +\nSize = 0\n\n" + listing).Count == 1, "An explicit archive-root directory is not a payload.");
        Require(ModelArchiveLibraryService.ReadEntries("Path = folder\nAttributes = RD\nSize = 0\n\n" + listing).Count == 1, "Read-only 7z directories are not file payloads.");
        Expect(() => ModelArchiveLibraryService.ReadEntries("Path = ../outside\nSize = 1\n\n"));
        Expect(() => ModelArchiveLibraryService.ReadEntries("Path = C:\\outside\nSize = 1\n\n"));
        Expect(() => ModelArchiveLibraryService.ReadEntries("Path = file\nSize = 1\nSymbolic Link = outside\n\n"));
        Expect(() => ModelArchiveLibraryService.ReadEntries(listing + listing.ToUpperInvariant().Replace("PATH", "Path").Replace("SIZE", "Size")));
        var executable = Environment.GetEnvironmentVariable("CRUCIBLE_TEST_7ZIP");
        if (executable is not null)
        {
            var root = Path.Combine(Path.GetTempPath(), "crucible-archive-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var library = Path.Combine(root, "library"); var state = Path.Combine(root, "state"); Directory.CreateDirectory(library);
                var bytes = new byte[2 * 1024 * 1024]; new Random(22).NextBytes(bytes);
                File.WriteAllBytes(Path.Combine(library, "a.bin"), bytes); File.WriteAllBytes(Path.Combine(library, "b.bin"), bytes);
                using (var zip = ZipFile.Open(Path.Combine(library, "pack.zip"), ZipArchiveMode.Create))
                {
                    using (var stream = zip.CreateEntry("Character/model.m2").Open()) stream.Write(bytes);
                }
                // More than one duplicate provides both the extraction scratch budget and a collision fixture.
                File.WriteAllBytes(Path.Combine(library, "c.bin"), bytes);
                foreach (var name in new[] { "a.bin", "b.bin", "c.bin" }) File.WriteAllText(Path.Combine(library, name) + ":Zone.Identifier", "[ZoneTransfer]\nZoneId=3\n");
                // Origin metadata is part of the duplicate check, not an excuse to skip all downloaded assets.
                var collision = Path.Combine(library, "pack", "Character", "model.m2"); Directory.CreateDirectory(Path.GetDirectoryName(collision)!); File.WriteAllText(collision, "user edit");
                var result = new ModelArchiveLibraryService().Expand(library, state, executable);
                Require(result.Errors.Count == 0 && result.LinkedFiles == 2 && result.ExtractedArchives == 1 && result.AddedBytes == bytes.Length, "Exact duplicates with origin metadata are linked, while different metadata is preserved: " + System.Text.Json.JsonSerializer.Serialize(result));
                Require(File.ReadAllText(collision) == "user edit", "Extraction overwrote a user edit.");
                Require(NativeHardLinks.ReadIdentity(Path.Combine(library, "a.bin")).Key == NativeHardLinks.ReadIdentity(Path.Combine(library, "b.bin")).Key, "Duplicate paths do not share storage.");
                var second = new ModelArchiveLibraryService().Expand(library, state, executable);
                Require(second.Errors.Count == 0 && second.ReusedArchives == 1 && second.AddedFiles == 0, "Completed archive does not resume idempotently.");
                var other = new byte[] { 4, 5, 6 };
                using (var zip = ZipFile.Open(Path.Combine(library, "other.zip"), ZipArchiveMode.Create))
                {
                    using (var stream = zip.CreateEntry("Character/model.m2").Open()) stream.Write(other);
                }
                var third = new ModelArchiveLibraryService().Expand(library, state, executable, deleteVerifiedArchives: true);
                Require(third.Errors.Count == 0 && third.ExtractedArchives == 1 && File.ReadAllBytes(Path.Combine(library, "other", "Character", "model.m2")).SequenceEqual(other), "Staging hashes must not carry between archives with the same inner paths.");
                Require(!File.Exists(Path.Combine(library, "pack.zip")) && !File.Exists(Path.Combine(library, "other.zip")), "Verified archives were not removed when requested.");
                Require(File.ReadAllText(collision) == "user edit", "Archive cleanup altered a conflicting user edit.");
                var mpqPath = Path.Combine(library, "patch-test.mpq");
                new PatchArchiveService().Create(mpqPath, [new PatchEntry(Path.Combine(library, "other", "Character", "model.m2"), "Character/Test/model.m2")]);
                var fourth = new ModelArchiveLibraryService().Expand(library, state, executable, deleteVerifiedArchives: true);
                Require(fourth.Errors.Count == 0 && fourth.ExtractedArchives == 1 && File.Exists(mpqPath)
                    && File.ReadAllBytes(Path.Combine(library, "patch-test", "Character", "Test", "model.m2")).SequenceEqual(other), "MPQ contents extract through StormLib while preserving the installable patch.");
                CheckDeferredBudget(root, executable);
            }
            finally
            {
                if (Path.GetDirectoryName(root) != Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !Path.GetFileName(root).StartsWith("crucible-archive-test-")) throw new IOException("Unexpected fixture cleanup path.");
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        Console.WriteLine("PASS model archive library: archive traversal, link and duplicate-path rejection; optional real 7-Zip extraction, collision preservation, physical dedup and resume.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void CheckDeferredBudget(string root, string executable)
    {
        var library = Path.Combine(root, "deferred"); Directory.CreateDirectory(library);
        var small = new byte[4096]; var large = new byte[8192];
        new Random(1).NextBytes(small); new Random(2).NextBytes(large);
        File.WriteAllBytes(Path.Combine(library, "small.bin"), small); File.WriteAllBytes(Path.Combine(library, "copy.bin"), small);
        File.WriteAllBytes(Path.Combine(library, "large.bin"), large);
        Zip("a.zip", large); Zip("b.zip", small);
        var progress = new List<string>();
        var result = new ModelArchiveLibraryService(progress.Add).Expand(library, Path.Combine(root, "deferred-state"), executable, deleteVerifiedArchives: true);
        Require(result.Errors.Count == 0 && result.ExtractedArchives == 2 && result.AddedBytes == 0
            && !File.Exists(Path.Combine(library, "a.zip")) && !File.Exists(Path.Combine(library, "b.zip"))
            && progress.Where(line => line.StartsWith("Extracting")).Count() == 3, "A deferred large archive must retry after a verified smaller archive releases enough space.");
        void Zip(string name, byte[] bytes)
        {
            using var zip = ZipFile.Open(Path.Combine(library, name), ZipArchiveMode.Create);
            using var stream = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression).Open(); stream.Write(bytes);
        }
    }
    private static void Expect(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("Unsafe archive listing was accepted."); }
}
