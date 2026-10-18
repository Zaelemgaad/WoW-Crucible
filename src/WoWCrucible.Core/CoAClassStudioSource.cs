using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;

namespace WoWCrucible.Core;

/// <summary>
/// Named offsets follow CoA's server readers and the matching native client metadata.
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
                columns.Add(new("Type", "StringOffset", false, 4));
                columns.Add(new("PositionX", "Float", false, 398)); columns.Add(new("PositionY", "Float", false, 402));
                Array("RequiredEntry", 8, 3); Array("SpellRank", 20, 9); Field("AbilityEssence", 56); Field("TalentEssence", 60);
                columns.Add(new("Name", "StringOffset", false, 188));
                columns.Add(new("TextureFilename", "StringOffset", false, 256));
                Field("RequiredLevel", 104); Field("Group", 116); Field("Column", 120); Field("Row", 124); Field("ClassGroup", 128); Field("Tab", 132);
                Array("RequiredInvestment", 136, 6); Field("Points", 160); Field("RequiredClassPoints", 164);
                Field("Starting", 414, flag: true); Array("ConnectedEntry", 415, 15); Field("Flags", 479);
                Field("AvailableCoA", 483, flag: true); Field("AvailableSeasonal", 484, flag: true); Array("AdditionalRequirement", 656, 9); break;
            case "CharacterAdvancementClassTypes":
                fields = 23; size = 92; columns.Add(new("Token", "StringOffset", false, 4)); Field("ClassID", 8); Field("Classless", 12); Field("CoA", 16); Field("WarcraftReborn", 20); break;
            case "ChrSpecs":
                fields = 65; size = 260;
                columns.Add(new("ClassToken", "StringOffset", false, 4)); columns.Add(new("Token", "StringOffset", false, 8));
                columns.Add(new("Name", "StringOffset", false, 116));
                Field("StarterSpell", 96); Field("RootEntry", 112); break;
            case "CharacterAdvancementTabTypes":
                fields = 19; size = 76; columns.Add(new("Token", "StringOffset", false, 4)); break;
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
            foreach (var column in columns)
            {
                var value = column.Type == "Byte" ? (uint)bytes[start + column.ByteOffset!.Value] : Word(start + column.ByteOffset!.Value);
                if (column.Type == "StringOffset")
                {
                    if (value >= Word(16)) throw new InvalidDataException($"Invalid {name} string offset.");
                    var offset = checked(20 + (int)Word(4) * size + (int)value);
                    var end = System.Array.IndexOf(bytes, (byte)0, offset);
                    if (end < 0) throw new InvalidDataException($"Unterminated {name} string.");
                    row.Add(column.Name, Encoding.UTF8.GetString(bytes, offset, end - offset));
                }
                else if (column.Type == "Float") row.Add(column.Name, BitConverter.Int32BitsToSingle((int)value));
                else row.Add(column.Name, value);
            }
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
        var policyPath = Path.Combine(directory, "AscensionRemixPolicy.h");
        if (File.Exists(policyPath))
        {
            var policyText = File.ReadAllText(policyPath);
            var policy = new Dictionary<string, object?>();
            foreach (var name in new[] { "FirstEntry", "LastEntry", "PaladinClassType", "PriestClassType" })
            {
                var match = Regex.Match(policyText, @"constexpr\s+std::uint32_t\s+" + name + @"\s*=\s*(\d+)\s*;");
                if (!match.Success) throw new InvalidDataException($"Unsupported Remix policy constant: {name}.");
                policy.Add(name, uint.Parse(match.Groups[1].Value));
            }
            result.Add(new("core", "CoARemixPolicy", policyPath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(policyPath))),
                policy.Keys.Select(n => new ClassStudioColumn(n, "UInt32", false)).ToArray(), [policy], "core-plan"));
        }
        var presentationPath = Path.Combine(root, "modules", "mod-ascension-compat", "client", "wxl", "RemixRealm.cpp");
        if (File.Exists(presentationPath))
        {
            var sourceText = File.ReadAllText(presentationPath);
            var classBody = Regex.Match(sourceText, @"int __cdecl ClassInfo\(.*?(?=int __cdecl SpecInfo)", RegexOptions.Singleline).Value;
            var classId = Regex.Match(classBody, @"state, -1\) == (\d+)");
            var className = Regex.Match(classBody, "state, \"([^\"]+)\"\\)");
            var token = Regex.Match(sourceText, "strcmp\\(string\\(state, 1, nullptr\\), \"([^\"]+)\"");
            var specNames = Regex.Matches(sourceText, "strcmp\\(spec, \"([^\"]+)\"\\) \\? \"([^\"]+)\"");
            if (!classId.Success || !className.Success || !token.Success || specNames.Count == 0)
                throw new InvalidDataException("Unsupported Remix client presentation hooks; update the reader before loading this version.");
            var presentation = specNames.Select(m => (IReadOnlyDictionary<string,object?>)new Dictionary<string,object?> {
                ["ClassID"] = uint.Parse(classId.Groups[1].Value), ["ClassName"] = className.Groups[1].Value,
                ["ClassToken"] = token.Groups[1].Value, ["SpecToken"] = m.Groups[1].Value, ["SpecName"] = m.Groups[2].Value }).ToArray();
            result.Add(new("core", "CoARemixPresentation", presentationPath, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(presentationPath))),
                new[] { "ClassID", "ClassName", "ClassToken", "SpecToken", "SpecName" }.Select(n => new ClassStudioColumn(n, n == "ClassID" ? "UInt32" : "String", false)).ToArray(), presentation, "core-plan"));
        }
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
