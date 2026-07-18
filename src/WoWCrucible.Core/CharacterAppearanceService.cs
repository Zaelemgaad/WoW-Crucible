namespace WoWCrucible.Core;

public sealed record CharacterAppearanceIdentity(uint RaceId, uint SexId, string RaceName, string SexName);
public sealed record CharacterBaseSkin(uint Id, uint RaceId, uint SexId, uint Flags, uint VariationIndex, uint ColorIndex, string TexturePath)
{
    public override string ToString() => $"Skin {ColorIndex:N0} · record {Id:N0} · {Path.GetFileName(TexturePath)} · flags 0x{Flags:X}";
}

public static class CharacterAppearanceService
{
    private static readonly (string Name, uint Id)[] Races =
    [
        ("BloodElf", 10), ("NightElf", 4), ("Draenei", 11), ("Scourge", 5), ("Undead", 5),
        ("Human", 1), ("Dwarf", 3), ("Tauren", 6), ("Gnome", 7), ("Goblin", 9), ("Worgen", 22), ("Orc", 2), ("Troll", 8)
    ];

    private static readonly DbcColumn[] Columns =
    [
        new(0, 0, 4, "ID", DbcValueType.UInt32, true), new(1, 4, 4, "RaceID", DbcValueType.UInt32), new(2, 8, 4, "SexID", DbcValueType.UInt32),
        new(3, 12, 4, "BaseSection", DbcValueType.UInt32), new(4, 16, 4, "TextureName[0]", DbcValueType.StringOffset), new(5, 20, 4, "TextureName[1]", DbcValueType.StringOffset),
        new(6, 24, 4, "TextureName[2]", DbcValueType.StringOffset), new(7, 28, 4, "Flags", DbcValueType.UInt32), new(8, 32, 4, "VariationIndex", DbcValueType.UInt32), new(9, 36, 4, "ColorIndex", DbcValueType.UInt32)
    ];

    public static CharacterAppearanceIdentity? Infer(string logicalPath, string fileName)
    {
        var tokens = (logicalPath + "\\" + Path.GetFileNameWithoutExtension(fileName)).Split(['\\', '/', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var race = Races.FirstOrDefault(candidate => tokens.Any(token => token.Equals(candidate.Name, StringComparison.OrdinalIgnoreCase)) || Path.GetFileNameWithoutExtension(fileName).StartsWith(candidate.Name, StringComparison.OrdinalIgnoreCase));
        if (race.Name is null) return null;
        var sex = tokens.Any(token => token.Equals("Female", StringComparison.OrdinalIgnoreCase)) ? (Id: 1u, Name: "Female")
            : tokens.Any(token => token.Equals("Male", StringComparison.OrdinalIgnoreCase)) ? (Id: 0u, Name: "Male") : ((uint Id, string Name)?)null;
        return sex is null ? null : new(race.Id, sex.Value.Id, race.Name == "Undead" ? "Scourge" : race.Name, sex.Value.Name);
    }

    public static IReadOnlyList<CharacterBaseSkin> LoadBaseSkins(string charSectionsPath, CharacterAppearanceIdentity identity)
    {
        var file = WdbcFile.Load(charSectionsPath);
        if (file.FieldCount != Columns.Length || file.RecordSize != 40) throw new InvalidDataException($"CharSections.dbc has {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; Wrath build 12340 requires 10 fields and 40-byte records.");
        var result = new List<CharacterBaseSkin>();
        for (var row = 0; row < file.RowCount; row++)
        {
            if (file.GetRaw(row, Columns[1]) != identity.RaceId || file.GetRaw(row, Columns[2]) != identity.SexId || file.GetRaw(row, Columns[3]) != 0) continue;
            var texture = Convert.ToString(file.GetDisplayValue(row, Columns[4]), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(texture)) continue;
            result.Add(new(file.GetRaw(row, Columns[0]), identity.RaceId, identity.SexId, file.GetRaw(row, Columns[7]), file.GetRaw(row, Columns[8]), file.GetRaw(row, Columns[9]), PatchInputMapper.NormalizeArchivePath(texture)));
        }
        return result.OrderBy(skin => skin.VariationIndex).ThenBy(skin => skin.ColorIndex).ThenBy(skin => skin.Id).ToArray();
    }
}
