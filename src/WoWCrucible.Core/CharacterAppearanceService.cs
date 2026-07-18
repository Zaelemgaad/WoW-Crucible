namespace WoWCrucible.Core;

public sealed record CharacterAppearanceIdentity(uint RaceId, uint SexId, string RaceName, string SexName);
public enum CharacterSectionKind : uint { Skin = 0, Face = 1, FacialHair = 2, Hair = 3, Underwear = 4, Custom1 = 5, Custom2 = 6, Custom3 = 7 }
public sealed record CharacterSection(uint Id, uint RaceId, uint SexId, CharacterSectionKind Kind, uint Flags, uint VariationIndex, uint ColorIndex, string? Texture0, string? Texture1, string? Texture2)
{
    public override string ToString() => $"{Kind} · style {VariationIndex:N0} · color {ColorIndex:N0} · record {Id:N0} · flags 0x{Flags:X}";
}
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
        return LoadSections(charSectionsPath, identity).Where(section => section.Kind == CharacterSectionKind.Skin && !string.IsNullOrWhiteSpace(section.Texture0))
            .Select(section => new CharacterBaseSkin(section.Id, section.RaceId, section.SexId, section.Flags, section.VariationIndex, section.ColorIndex, section.Texture0!))
            .OrderBy(skin => skin.VariationIndex).ThenBy(skin => skin.ColorIndex).ThenBy(skin => skin.Id).ToArray();
    }

    public static IReadOnlyList<CharacterSection> LoadSections(string charSectionsPath, CharacterAppearanceIdentity identity)
    {
        var file = WdbcFile.Load(charSectionsPath);
        if (file.FieldCount != Columns.Length || file.RecordSize != 40) throw new InvalidDataException($"CharSections.dbc has {file.FieldCount:N0} fields and {file.RecordSize:N0}-byte records; Wrath build 12340 requires 10 fields and 40-byte records.");
        var result = new List<CharacterSection>();
        for (var row = 0; row < file.RowCount; row++)
        {
            if (file.GetRaw(row, Columns[1]) != identity.RaceId || file.GetRaw(row, Columns[2]) != identity.SexId) continue;
            var rawKind = file.GetRaw(row, Columns[3]); if (rawKind > 7) continue;
            result.Add(new(file.GetRaw(row, Columns[0]), identity.RaceId, identity.SexId, (CharacterSectionKind)rawKind, file.GetRaw(row, Columns[7]), file.GetRaw(row, Columns[8]), file.GetRaw(row, Columns[9]),
                Texture(4), Texture(5), Texture(6)));
            string? Texture(int column)
            {
                var value = Convert.ToString(file.GetDisplayValue(row, Columns[column]), System.Globalization.CultureInfo.InvariantCulture);
                return string.IsNullOrWhiteSpace(value) ? null : PatchInputMapper.NormalizeArchivePath(value);
            }
        }
        return result.OrderBy(section => section.Kind).ThenBy(section => section.VariationIndex).ThenBy(section => section.ColorIndex).ThenBy(section => section.Id).ToArray();
    }
}
