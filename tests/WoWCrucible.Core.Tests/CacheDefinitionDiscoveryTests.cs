using WoWCrucible.Core;

internal static class CacheDefinitionDiscoveryTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"crucible-cache-discovery-{Guid.NewGuid():N}");
        var tools = Path.Combine(root, "Tools");
        Directory.CreateDirectory(tools);
        try
        {
            string Add(string relative)
            {
                var path = Path.Combine(tools, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "<Definition />");
                return path;
            }

            var expected = new[]
            {
                Add(Path.Combine("Renamed Editor", "Definitions", "WDB.xml")),
                Add(Path.Combine("Legacy Working Copy", "Nested Editor", "source", "Definitions", "WDB.xml")),
                Add(Path.Combine("Renamed Parser", "wdb-definitions.xml")),
                Add(Path.Combine("ADB_WDB_Parser for 4.3.x 1.0.0", "adb-definitions.xml"))
            };
            var ignored = new[]
            {
                Add(Path.Combine("Renamed Editor", "bin", "WDB.xml")),
                Add(Path.Combine(".private", "WDB.xml")),
                Add(Path.Combine("a", "b", "c", "d", "e", "f", "WDB.xml")),
                Add(Path.Combine("Renamed Editor", "unrelated.xml"))
            };
            var cache = Path.Combine(root, "Client", "Cache", "itemcache.wdb");
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
            File.WriteAllBytes(cache, []);
            var found = WowCacheDefinitionCatalog.Discover(cache);
            if (expected.Any(path => !found.Contains(path, StringComparer.OrdinalIgnoreCase)) ||
                ignored.Any(path => found.Contains(path, StringComparer.OrdinalIgnoreCase)) ||
                found.Count != found.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                throw new InvalidOperationException("Cache definitions were lost after tool renaming/nesting, or discovery escaped its bounds.");
        }
        finally
        {
            // Only remove this test's unique scratch directory.
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cache discovery fixture is outside the test scratch root.");
            Directory.Delete(root, recursive: true);
        }
    }
}
