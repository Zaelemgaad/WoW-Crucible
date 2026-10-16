using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

/// <summary>
/// Named offsets match CoA's AscensionCharacterBuild.h and AscensionBooks.cpp readers.
/// Unknown fields are never invented or rewritten; exports are implementation plans.
/// </summary>
public static class CoAClassStudioSource
{
    public static ClassStudioTable ReadTable(string path, string layer, string source)
    {
        var name = Path.GetFileNameWithoutExtension(path); var columns = new List<ClassStudioColumn>();
        void Field(string label, int offset, bool key = false, bool flag = false) => columns.Add(new(label, flag ? "Byte" : "UInt32", key, offset));
        void Array(string label, int offset, int count) { for (var i = 0; i < count; i++) Field($"{label}[{i}]", offset + i * 4); }
        Field("ID", 0, true); int fields, size;
        switch (name)
        {
            case "CharacterAdvancement":
                fields = 179; size = 692;
                Array("RequiredEntry", 8, 3); Array("SpellRank", 20, 9); Field("AbilityEssence", 56); Field("TalentEssence", 60);
                Field("RequiredLevel", 104); Field("Group", 116); Field("ClassGroup", 128); Field("Tab", 132);
                Array("RequiredInvestment", 136, 6); Field("Points", 160); Field("RequiredClassPoints", 164);
                Field("Starting", 414, flag: true); Array("ConnectedEntry", 415, 15); Field("Flags", 479);
                Field("AvailableCoA", 483, flag: true); Field("AvailableSeasonal", 484, flag: true); Array("AdditionalRequirement", 656, 9); break;
            case "CharacterAdvancementClassTypes":
                fields = 23; size = 92; Field("ClassID", 8); Field("Classless", 12); Field("CoA", 16); break;
            case "ChrSpecs":
                fields = 65; size = 260; Field("StarterSpell", 96); Field("RootEntry", 112); break;
            case "SpellRank":
                fields = 4; size = 16; Field("FirstSpellID", 4); Field("SpellID", 8); Field("Rank", 12); break;
            case "NPCTrainer":
                fields = 4; size = 16; Field("SpellID", 4); break;
            default: throw new InvalidDataException($"No verified CoA layout for {name}.");
        }
        var bytes = File.ReadAllBytes(path);
        uint Word(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (bytes.Length < 20 || !bytes.AsSpan(0, 4).SequenceEqual("WDBC"u8) || Word(8) != fields || Word(12) != size || 20L + (long)Word(4) * size + Word(16) != bytes.Length)
            throw new InvalidDataException($"{source}: unsupported CoA layout; expected {fields} fields / {size} bytes. No guessed decoding was used.");
        var rows = new List<IReadOnlyDictionary<string, object?>>(); var ids = new HashSet<uint>();
        for (var i = 0; i < Word(4); i++)
        {
            var start = checked(20 + i * size); var row = new Dictionary<string, object?>();
            foreach (var column in columns) row.Add(column.Name, column.Type == "Byte" ? (uint)bytes[start + column.ByteOffset!.Value] : Word(start + column.ByteOffset!.Value));
            if (!ids.Add((uint)row["ID"]!)) throw new InvalidDataException($"Duplicate {name} key {row["ID"]}.");
            rows.Add(row);
        }
        return new(layer, name, source, Convert.ToHexString(SHA256.HashData(bytes)), columns, rows, "coa-plan");
    }

    public static IReadOnlyList<ClassStudioTable> ReadCore(string root, List<string> notices)
    {
        var directory = Path.Combine(root, "modules", "mod-ascension-compat", "src");
        if (!Directory.Exists(directory)) { notices.Add("No CoA module source at the selected core path; compiled starting/trainer/taught-ability lists were not loaded."); return []; }
        var result = new List<ClassStudioTable>();
        Read("AscensionCustomClassData.h", "ClassSpells", "CoAClassSpells", ["ClassID", "RequiredLevel", "SpellID"], 3);
        Read("AscensionSpellProgressionData.h", "Ranks", "CoARankSpells", ["ClassID", "FirstSpellID", "SpellID", "RequiredLevel"], 4);
        Read("AscensionTaughtAbilityData.h", "TaughtAbilities", "CoATaughtAbilities", ["ClassID", "SpecID", "RequiredLevel", "ParentSpellID", "SpellID", "RequiredSpellID", "RankWithLevel"], 5);
        return result;

        void Read(string filename, string array, string table, string[] names, int minimum)
        {
            var path = Path.Combine(directory, filename);
            if (!File.Exists(path)) { notices.Add($"CoA core list missing: {filename}"); return; }
            var text = File.ReadAllText(path);
            var pattern = @"std::array<\w+,\s*(\d+)>\s+" + Regex.Escape(array) + @"\s*=\s*\{\{(.*?)\}\};";
            var match = Regex.Match(text, pattern, RegexOptions.Singleline);
            if (!match.Success) throw new InvalidDataException($"Unsupported declaration in {filename}; update the reader before using this core version.");
            var body = Regex.Replace(match.Groups[2].Value, @"//[^\r\n]*|/\*.*?\*/", "", RegexOptions.Singleline);
            var records = Regex.Matches(body, @"\{([^{}]+)\}"); var rows = new List<IReadOnlyDictionary<string, object?>>();
            if (records.Count != int.Parse(match.Groups[1].Value)) throw new InvalidDataException($"Incomplete {array} parse in {filename}.");
            foreach (Match record in records)
            {
                var values = record.Groups[1].Value.Split(',').Select(v => v.Trim()).ToArray();
                if (values.Length < minimum || values.Length > names.Length) throw new InvalidDataException($"Unsupported {array} row in {filename}.");
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < names.Length; i++) row.Add(names[i], i >= values.Length || values[i] == "false" ? 0u : values[i] == "true" ? 1u : uint.Parse(values[i]));
                rows.Add(row);
            }
            result.Add(new("core", table, path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                names.Select(n => new ClassStudioColumn(n, "UInt32", n is "ClassID" or "SpellID" or "ParentSpellID")).ToArray(), rows, "core-plan"));
        }
    }
}
