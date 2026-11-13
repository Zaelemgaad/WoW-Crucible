using System.Buffers.Binary;
using System.Text;
using WoWCrucible.Core;

/// <summary>data export/build: TSV cells, DBC tables back to identical bytes, and world SQL from TSV.</summary>
internal static class GameDataTextTestSuite
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Game data text: " + message);
    }

    // ID, a float, a byte and padding, a string, and a localized string (16 slots + flags).
    private const string Definition = "COLUMNS\nint ID\nfloat Scale\nint Kind\nint Pad\nstring Path\nlocstring Name_lang\n\nBUILD 3.3.5.12340\n$id$ID<32>\nScale\nKind<u8>\nPad<u8>[3]\nPath\nName_lang\n";

    private static byte[] Table(IReadOnlyList<(uint Id, uint ScaleBits, string Path, string Name, string German)> rows, bool leadingNul, uint? brokenOffset = null)
    {
        var block = new List<byte>(); var offsets = new Dictionary<string, uint>();
        if (leadingNul) { block.Add(0); offsets[""] = 0; }
        uint Intern(string text)
        {
            if (offsets.TryGetValue(text, out var offset)) return offset;
            offset = (uint)block.Count; block.AddRange(Encoding.UTF8.GetBytes(text)); block.Add(0); offsets[text] = offset; return offset;
        }
        const int size = 4 + 4 + 4 + 4 + 17 * 4;
        var records = new byte[rows.Count * size];
        for (var i = 0; i < rows.Count; i++)
        {
            var span = records.AsSpan(i * size, size); var row = rows[i];
            BinaryPrimitives.WriteUInt32LittleEndian(span, row.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(span[4..], row.ScaleBits);
            span[8] = (byte)(i + 1);
            BinaryPrimitives.WriteUInt32LittleEndian(span[12..], Intern(row.Path));
            BinaryPrimitives.WriteUInt32LittleEndian(span[16..], Intern(row.Name));
            for (var locale = 1; locale < 16; locale++) BinaryPrimitives.WriteUInt32LittleEndian(span[(16 + locale * 4)..], Intern(locale == 3 ? row.German : ""));
            BinaryPrimitives.WriteUInt32LittleEndian(span[80..], 0xFF01FE);
        }
        if (brokenOffset is { } broken) BinaryPrimitives.WriteUInt32LittleEndian(records.AsSpan(16 + 9 * 4), broken);
        var bytes = new byte[20 + records.Length + block.Count];
        "WDBC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), rows.Count);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 22);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), size);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), block.Count);
        records.CopyTo(bytes, 20); block.ToArray().CopyTo(bytes, 20 + records.Length);
        return bytes;
    }

    public static void Run(string corpus)
    {
        foreach (var text in new[] { "", "plain", "tab\there", "line\nbreak\r\n", "back\\slash", "\\N", "nul\0byte" })
            Require(GameDataTsv.Unescape(GameDataTsv.Escape(text)) == text && !GameDataTsv.Escape(text).Contains('\t') && !GameDataTsv.Escape(text).Contains('\n'), $"escape {text}");
        Require(GameDataTsv.Escape("\\N") != GameDataTsv.Null, "a \\N string is not NULL");
        byte[] invalid = [0x41, 0xFF, 0xC3, 0x0A];
        Require(GameDataTsv.EncodeBytes(invalid) == "A\\xFF\\xC3\\n" && GameDataTsv.DecodeBytes(GameDataTsv.EncodeBytes(invalid)).SequenceEqual(invalid), "invalid UTF-8 survives as \\xHH");
        Require(GameDataTsv.DecodeBytes(GameDataTsv.EncodeBytes(Encoding.UTF8.GetBytes("Ünïcode ✓"))).SequenceEqual(Encoding.UTF8.GetBytes("Ünïcode ✓")), "UTF-8 text stays text");

        var root = Path.Combine(Path.GetTempPath(), "crucible-gamedata-" + Guid.NewGuid().ToString("N"));
        try
        {
            var defs = Path.Combine(root, "defs"); Directory.CreateDirectory(defs);
            File.WriteAllText(Path.Combine(defs, "Sample.dbd"), Definition);
            string[] roots = [defs];
            (uint, uint, string, string, string)[] rows =
            [
                (1, BitConverter.SingleToUInt32Bits(1.5f), "World\\a.m2", "First", ""),
                (2, 0x7FC00001, "", "Second\twith tab", "Zweite"),
                (5, BitConverter.SingleToUInt32Bits(-0f), "World\\a.m2", "", ""),
            ];
            foreach (var leading in new[] { true, false })
            {
                var source = Table(rows, leading);
                var tsv = Path.Combine(root, "dbc", "server", "Sample.tsv");
                var entry = GameDataDbcService.ExportTable(source, "server", "Sample.dbc", tsv, roots, out var message);
                Require(entry.Strings == (leading ? "blizzard" : "inline") && message.Length == 0, $"layout {entry.Strings}: {message}");
                var header = File.ReadLines(tsv).First().Split('\t');
                Require(header.Contains("Name_lang[enUS]") && !header.Contains("Name_lang[deDE]") && header.Contains("Name_lang[Flags]"), "other locales leave the main TSV");
                var locales = File.ReadAllLines(Path.ChangeExtension(tsv, ".locales.tsv"));
                Require(locales.SequenceEqual(["ID\tcolumn\tvalue", "2\tName_lang[deDE]\tZweite"]), "German lives in Sample.locales.tsv: " + string.Join(" | ", locales));
                Require(File.ReadAllText(tsv).Contains("0x7FC00001") && File.ReadAllText(tsv).Contains("\t-0\t"), "NaN payload and -0 are exact");
                var rebuilt = GameDataDbcService.BuildTable(entry, tsv, roots);
                Require(rebuilt.AsSpan().SequenceEqual(source), $"byte-identical rebuild ({(leading ? "blizzard" : "inline")})");
                Require(GameDataDbcService.Compare(source, rebuilt, "Sample", roots) == "identical", "compare says identical");
            }

            // An offset outside the block stays a number; text edits rebuild with the edit.
            var broken = Table(rows, true, 0xFF01FE);
            var brokenTsv = Path.Combine(root, "dbc", "client", "Sample.tsv");
            var brokenEntry = GameDataDbcService.ExportTable(broken, "client", "Sample.dbc", brokenTsv, roots, out var brokenMessage);
            Require(brokenMessage.Contains("\\o<offset>") && File.ReadAllText(Path.ChangeExtension(brokenTsv, ".locales.tsv")).Contains("\\o16712190"), "outside offsets are \\o cells");
            Require(GameDataDbcService.BuildTable(brokenEntry, brokenTsv, roots).AsSpan().SequenceEqual(broken), "\\o cells rebuild exactly");
            File.WriteAllText(brokenTsv, File.ReadAllText(brokenTsv).Replace("First", "Edited"));
            var edited = GameDataDbcService.BuildTable(brokenEntry, brokenTsv, roots);
            Require(GameDataDbcService.Compare(broken, edited, "Sample", roots).Contains("Name_lang[enUS] differs"), "an edit is reported as a difference");

            // A block with a string nothing uses (an editor's leftover) is rebuilt canonically and proven equal value by value.
            var leftover = Table(rows, true).Concat(Encoding.UTF8.GetBytes("unused\0")).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(leftover.AsSpan(16), BinaryPrimitives.ReadInt32LittleEndian(leftover.AsSpan(16)) + 7);
            var leftoverTsv = Path.Combine(root, "dbc", "client", "Leftover.tsv");
            File.Copy(Path.Combine(defs, "Sample.dbd"), Path.Combine(defs, "Leftover.dbd"));
            var leftoverEntry = GameDataDbcService.ExportTable(leftover, "client", "Leftover.dbc", leftoverTsv, roots, out var leftoverMessage);
            Require(leftoverEntry.Strings == "blizzard" && leftoverMessage.Contains("1 unreferenced"), "leftover strings are explained: " + leftoverMessage);
            var canonical = GameDataDbcService.BuildTable(leftoverEntry, leftoverTsv, roots);
            Require(!canonical.AsSpan().SequenceEqual(leftover) && GameDataDbcService.Compare(leftover, canonical, "Leftover", roots) == "same values", "leftover block: same values");

            // Unknown tables: Field_<offset> columns, the block kept verbatim, still identical.
            var unknownTsv = Path.Combine(root, "dbc", "client", "Unknown.tsv");
            var unknownEntry = GameDataDbcService.ExportTable(broken, "client", "Unknown.dbc", unknownTsv, roots, out var unknownMessage);
            Require(unknownEntry.Strings == "raw" && unknownMessage.Contains("no 3.3.5.12340 definition") && File.Exists(Path.ChangeExtension(unknownTsv, ".strings.tsv")), "raw fallback");
            Require(GameDataDbcService.BuildTable(unknownEntry, unknownTsv, roots).AsSpan().SequenceEqual(broken), "raw rebuild is identical");

            // Real Blizzard tables from the corpus, through the folder export, manifest and verify.
            File.WriteAllText(Path.Combine(defs, "SpellCastTimes.dbd"), "COLUMNS\nint ID\nint Base\nint PerLevel\nint Minimum\n\nBUILD 3.3.5.12340\n$id$ID<32>\nBase<32>\nPerLevel<32>\nMinimum<32>\n");
            var corpusTables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "SpellCastTimes.dbc", "SpellDuration.dbc", "Light.dbc", "ScalingStatDistribution.dbc" })
                corpusTables[name] = Path.Combine(corpus, name);
            var gamedata = Path.Combine(root, "gamedata"); Directory.CreateDirectory(gamedata);
            var exported = GameDataDbcService.Export(gamedata, corpusTables, new Dictionary<string, string> { ["SpellCastTimes.dbc"] = corpusTables["SpellCastTimes.dbc"] }, roots);
            Require(exported.Single(result => result.Path == "SpellCastTimes.dbc").Set == "shared", "identical tables are shared");
            Require(exported.Single(result => result.Path == "SpellCastTimes.dbc").Status is "blizzard" or "inline", "a defined table is decoded");
            var verified = GameDataDbcService.Verify(gamedata, corpusTables, null, roots);
            Require(verified.Count == 4 && verified.All(result => result.Status == "identical"), "corpus tables rebuild identically: " + string.Join("; ", verified.Select(result => $"{result.Path} {result.Status}")));
            var built = GameDataDbcService.BuildAll(gamedata, Path.Combine(root, "out", "server"), Path.Combine(root, "out", "client"), roots);
            Require(built.Count == 4 && File.Exists(Path.Combine(root, "out", "client", "SpellCastTimes.dbc")) && !File.Exists(Path.Combine(root, "out", "client", "Light.dbc")), "build writes shared tables to both sides only");

            // World: schema.sql plus TSV become DROP/CREATE and INSERT with NULL, numbers, strings and binary.
            var world = Path.Combine(root, "world"); Directory.CreateDirectory(world);
            File.WriteAllText(Path.Combine(world, GameDataWorldService.SchemaName),
                "CREATE TABLE `t` (\n  `id` int unsigned NOT NULL,\n  `rate` float DEFAULT NULL,\n  `name` varchar(20) NOT NULL,\n  `blob` varbinary(4) DEFAULT NULL,\n  PRIMARY KEY (`id`)\n) ENGINE=InnoDB;\n\n");
            File.WriteAllText(Path.Combine(world, "t.tsv"), "id\trate\tname\tblob\n1\t0.1\tit's\\tok\t0x00FF\n2\t\\N\t\\\\N\t\\N\n");
            var sqlPath = Path.Combine(root, "world.sql");
            var sqlResults = GameDataWorldService.BuildSql(world, sqlPath, null);
            var sql = File.ReadAllText(sqlPath);
            Require(sqlResults.Single().Rows == 2 && sql.Contains("DROP TABLE IF EXISTS `t`;"), "world table built");
            Require(sql.Contains("(1,0.1,'it\\'s\tok',X'00FF')") && sql.Contains("(2,NULL,'\\\\N',NULL)"), "SQL literals: " + sql);
            File.WriteAllText(Path.Combine(world, "t.tsv"), "id\trate\tname\tblob\nx\t1\ta\t\\N\n");
            var refused = false;
            try { GameDataWorldService.BuildSql(world, sqlPath, null); } catch (InvalidDataException) { refused = true; }
            Require(refused, "a non-number in an int column is refused");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("Game data text checks passed.");
    }
}
