using System.Globalization;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record SqlNumericRange(decimal Minimum, decimal Maximum, bool Integral, string Storage);

public sealed record ItemNumericFieldContract(
    string Field,
    decimal Minimum,
    decimal Maximum,
    decimal Increment,
    string Storage,
    string Explanation)
{
    public string Tooltip =>
        $"{Field}: {Storage}; allowed {Minimum.ToString(CultureInfo.InvariantCulture)} to {Maximum.ToString(CultureInfo.InvariantCulture)}. {Explanation}";
}

public static partial class SqlNumericRangeService
{
    public static bool TryResolve(DatabaseColumnCapability column, out SqlNumericRange range)
    {
        var type = column.DataType.Trim().ToLowerInvariant();
        var unsigned = column.ColumnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        range = type switch
        {
            "tinyint" => unsigned ? new(0, byte.MaxValue, true, "TINYINT UNSIGNED (8-bit)") : new(sbyte.MinValue, sbyte.MaxValue, true, "TINYINT SIGNED (8-bit)"),
            "smallint" => unsigned ? new(0, ushort.MaxValue, true, "SMALLINT UNSIGNED (16-bit)") : new(short.MinValue, short.MaxValue, true, "SMALLINT SIGNED (16-bit)"),
            "mediumint" => unsigned ? new(0, 16_777_215m, true, "MEDIUMINT UNSIGNED (24-bit)") : new(-8_388_608m, 8_388_607m, true, "MEDIUMINT SIGNED (24-bit)"),
            "int" or "integer" => unsigned ? new(0, uint.MaxValue, true, "INT UNSIGNED (32-bit)") : new(int.MinValue, int.MaxValue, true, "INT SIGNED (32-bit)"),
            "bigint" => unsigned ? new(0, 18_446_744_073_709_551_615m, true, "BIGINT UNSIGNED (64-bit)") : new(long.MinValue, long.MaxValue, true, "BIGINT SIGNED (64-bit)"),
            "float" => new(-decimal.MaxValue, decimal.MaxValue, false, "FLOAT (32-bit floating point; editor decimal range)"),
            "double" or "real" => new(-decimal.MaxValue, decimal.MaxValue, false, "DOUBLE (64-bit floating point; editor decimal range)"),
            "decimal" or "numeric" => DecimalRange(column),
            _ => default!
        };
        return range is not null;
    }

    private static SqlNumericRange DecimalRange(DatabaseColumnCapability column)
    {
        var match = DecimalType().Match(column.ColumnType);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var precision) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var scale))
            return new(-decimal.MaxValue, decimal.MaxValue, false, "DECIMAL");

        var integerDigits = Math.Max(0, precision - scale);
        decimal whole = 0;
        for (var index = 0; index < integerDigits && whole <= (decimal.MaxValue - 9) / 10; index++) whole = whole * 10 + 9;
        decimal fraction = 0;
        decimal place = 0.1m;
        for (var index = 0; index < scale && place > 0; index++, place /= 10) fraction += 9 * place;
        var maximum = Math.Min(decimal.MaxValue, whole + fraction);
        var unsigned = column.ColumnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase);
        return new(unsigned ? 0 : -maximum, maximum, scale == 0, $"DECIMAL({precision},{scale}){(unsigned ? " UNSIGNED" : string.Empty)}");
    }

    [GeneratedRegex(@"(?:decimal|numeric)\s*\(\s*(\d+)\s*,\s*(\d+)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex DecimalType();
}

public static class ItemNumericFieldCatalog
{
    private static readonly IReadOnlyDictionary<string, ItemNumericFieldContract> Exact =
        new Dictionary<string, ItemNumericFieldContract>(StringComparer.OrdinalIgnoreCase)
        {
            ["entry"] = Unsigned32("entry", "Client/server item identity is an unsigned 32-bit value."),
            ["class"] = Unsigned8("class"),
            ["subclass"] = Unsigned8("subclass"),
            ["displayid"] = Unsigned32("displayid"),
            ["Quality"] = Unsigned8("Quality"),
            ["InventoryType"] = Unsigned8("InventoryType"),
            ["SoundOverrideSubclass"] = Signed8("SoundOverrideSubclass"),
            ["Material"] = Signed8("Material"),
            ["sheath"] = Unsigned8("sheath"),
            ["ItemLevel"] = Unsigned16("ItemLevel", "The 3.3.5 core loader reads this as uint16; it is not capped at 1000."),
            ["RequiredLevel"] = Unsigned8("RequiredLevel"),
            ["BuyPrice"] = new("BuyPrice", 0, int.MaxValue, 1, "effective SIGNED INT32 runtime value",
                "AzerothCore's SQL column is BIGINT, but the 3.3.5 runtime casts it to int32. Values above 2,147,483,647 would overflow in the server consumer."),
            ["SellPrice"] = Unsigned32("SellPrice"),
            ["bonding"] = Unsigned8("bonding"),
            ["Flags"] = Unsigned32("Flags", "All 32 flag bits remain available."),
            ["armor"] = Unsigned32("armor", "The core consumes this as uint32; the old 100,000 limit was artificial."),
            ["dmg_min1"] = NonNegativeFloat("dmg_min1"),
            ["dmg_max1"] = NonNegativeFloat("dmg_max1"),
            ["dmg_type1"] = Unsigned8("dmg_type1"),
            ["delay"] = Unsigned16("delay", "Milliseconds; the 3.3.5 database/core field is uint16."),
            ["MaxDurability"] = Unsigned16("MaxDurability", "The 3.3.5 database/core loader narrows this to uint16."),
            ["itemset"] = Unsigned32("itemset"),
            ["StatsCount"] = Unsigned8("StatsCount"),
        };

    public static ItemNumericFieldContract Resolve(string field, DatabaseColumnCapability? liveColumn = null)
    {
        var portable = ResolvePortable(field);
        if (liveColumn is null || !SqlNumericRangeService.TryResolve(liveColumn, out var sql)) return portable;
        var minimum = Math.Max(portable.Minimum, sql.Minimum);
        var maximum = Math.Min(portable.Maximum, sql.Maximum);
        if (minimum > maximum)
            throw new NotSupportedException($"{field} has incompatible target storage {liveColumn.ColumnType}; Crucible's safe 3.3.5 range is {portable.Minimum}..{portable.Maximum}.");
        return portable with
        {
            Minimum = minimum,
            Maximum = maximum,
            Storage = $"{portable.Storage}; live SQL {sql.Storage}",
            Explanation = $"{portable.Explanation} The connected schema is also enforced."
        };
    }

    public static void Validate(string field, object value, DatabaseColumnCapability? liveColumn = null)
    {
        var contract = Resolve(field, liveColumn);
        decimal number;
        try { number = Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch (Exception exception) when (exception is OverflowException or FormatException or InvalidCastException)
        {
            throw new InvalidDataException($"{field} value '{value}' is not representable by the editor/server numeric contract.", exception);
        }
        if (number < contract.Minimum || number > contract.Maximum)
            throw new InvalidDataException($"{field} value {number.ToString(CultureInfo.InvariantCulture)} is outside {contract.Minimum.ToString(CultureInfo.InvariantCulture)}..{contract.Maximum.ToString(CultureInfo.InvariantCulture)} ({contract.Storage}). The write was refused before SQL/client overflow.");
        if (contract.Increment == 1 && decimal.Truncate(number) != number)
            throw new InvalidDataException($"{field} requires a whole number ({contract.Storage}).");
    }

    public static string PortableSqlType(string field)
    {
        var contract = ResolvePortable(field);
        if (contract.Storage.Contains("FLOAT", StringComparison.OrdinalIgnoreCase)) return "float";
        if (contract.Minimum >= 0 && contract.Maximum == byte.MaxValue) return "tinyint unsigned";
        if (contract.Minimum == sbyte.MinValue && contract.Maximum == sbyte.MaxValue) return "tinyint";
        if (contract.Minimum >= 0 && contract.Maximum == ushort.MaxValue) return "smallint unsigned";
        if (contract.Minimum == short.MinValue && contract.Maximum == short.MaxValue) return "smallint";
        if (contract.Minimum >= 0 && contract.Maximum == uint.MaxValue) return "int unsigned";
        return "int";
    }

    private static ItemNumericFieldContract ResolvePortable(string field)
    {
        if (Exact.TryGetValue(field, out var exact)) return exact;
        if (field.StartsWith("stat_type", StringComparison.OrdinalIgnoreCase)) return Unsigned8(field);
        if (field.StartsWith("stat_value", StringComparison.OrdinalIgnoreCase)) return Signed32(field);
        if (field.StartsWith("spellid_", StringComparison.OrdinalIgnoreCase)) return new(field, 0, int.MaxValue, 1, "SIGNED INT32 spell identity", "Negative values fit SQL but are not valid WotLK spell IDs.");
        if (field.StartsWith("spelltrigger_", StringComparison.OrdinalIgnoreCase)) return Unsigned8(field);
        if (field.StartsWith("spellcharges_", StringComparison.OrdinalIgnoreCase)) return Signed16(field);
        if (field.StartsWith("spellppmRate_", StringComparison.OrdinalIgnoreCase)) return NonNegativeFloat(field);
        if (field.StartsWith("spellcooldown_", StringComparison.OrdinalIgnoreCase) || field.StartsWith("spellcategorycooldown_", StringComparison.OrdinalIgnoreCase))
            return new(field, -1, int.MaxValue, 1, "SIGNED INT32 milliseconds", "-1 delegates to spell/default timing; values below -1 have no supported item-template meaning.");
        if (field.StartsWith("spellcategory_", StringComparison.OrdinalIgnoreCase)) return Unsigned16(field);
        throw new KeyNotFoundException($"No item numeric contract is registered for '{field}'.");
    }

    private static ItemNumericFieldContract Unsigned8(string field, string explanation = "Unsigned 8-bit storage.") => new(field, 0, byte.MaxValue, 1, "UNSIGNED 8-bit", explanation);
    private static ItemNumericFieldContract Signed8(string field) => new(field, sbyte.MinValue, sbyte.MaxValue, 1, "SIGNED 8-bit", "Signed 8-bit storage.");
    private static ItemNumericFieldContract Unsigned16(string field, string explanation = "Unsigned 16-bit storage.") => new(field, 0, ushort.MaxValue, 1, "UNSIGNED 16-bit", explanation);
    private static ItemNumericFieldContract Signed16(string field) => new(field, short.MinValue, short.MaxValue, 1, "SIGNED 16-bit", "Signed 16-bit storage.");
    private static ItemNumericFieldContract Unsigned32(string field, string explanation = "Unsigned 32-bit storage.") => new(field, 0, uint.MaxValue, 1, "UNSIGNED 32-bit", explanation);
    private static ItemNumericFieldContract Signed32(string field) => new(field, int.MinValue, int.MaxValue, 1, "SIGNED 32-bit", "Signed 32-bit storage.");
    private static ItemNumericFieldContract NonNegativeFloat(string field) => new(field, 0, decimal.MaxValue, 0.01m, "FLOAT (32-bit floating point)", "Fractional values are supported; the editor is limited only by its wider decimal input representation.");
}
