using WoWCrucible.Core;
using WoWCrucible.Desktop;

internal static class DbcEditorTestSuite
{
    public static void Run(string corpus)
    {
        DbcColumnLayoutTestSuite.Run(corpus);
        var file = WdbcFile.Load(Path.Combine(corpus, "ScalingStatDistribution.dbc"));
        var stat = DbcSemanticCatalog.Get("ScalingStatDistribution", 1, file)
            ?? throw new InvalidOperationException("Scaling stat types have no readable names.");
        if (stat.Format(4) != "Strength [4]" || stat.Format(3) != "Agility [3]" ||
            stat.Format(uint.MaxValue) != "Unused [-1]" || stat.Parse("-1") != uint.MaxValue ||
            stat.Parse(stat.Format(9999)) != 9999 || stat.Parse(stat.Format(unchecked((uint)-2))) != unchecked((uint)-2))
            throw new InvalidOperationException("Stat names, unused slots, or unknown stat IDs failed to round-trip.");
        var unsigned = new SemanticField("Unsigned test", SemanticKind.Enum, [new(4, "Four")]);
        var rejectedNegative = false;
        try { unsigned.Parse("-1"); }
        catch (FormatException) { rejectedNegative = true; }
        if (!rejectedNegative)
            throw new InvalidOperationException("Signed scaling stats weakened unsigned-field validation.");
        if (DbcSemanticCatalog.Get("ScalingStatDistribution", 11, file) is not null)
            throw new InvalidOperationException("A scaling bonus was mislabeled as a stat type.");
        var values = WdbcFile.Load(Path.Combine(corpus, "SpellDuration.dbc"));
        if (DbcSemanticCatalog.Get("ScalingStatDistribution", 1, values) is not null)
            throw new InvalidOperationException("Scaling labels were applied to an incompatible layout.");

        // Exercise the actual desktop history against a full-width field, not a 32-bit stand-in.
        var column = new DbcColumn(1, 4, 8, "WideValue", DbcValueType.UInt64);
        var before = (1UL << 45) + 17;
        var after = (1UL << 58) + 29;
        values.SetRaw64(0, column, before);
        var history = new DesktopEditHistory();
        values.SetRaw64(0, column, after);
        history.Record(0, column, before, after);
        if (history.Undo(values) is null || values.GetRaw64(0, column) != before)
            throw new InvalidOperationException("Desktop Undo truncated a 64-bit field.");
        if (history.Redo(values) is null || values.GetRaw64(0, column) != after)
            throw new InvalidOperationException("Desktop Redo truncated a 64-bit field.");
        if (!history.CanUndo || history.CanRedo)
            throw new InvalidOperationException("Desktop edit history state is inconsistent.");
        Console.WriteLine("PASS DBC editor: stat names, unused/unknown IDs, layout bounds, and 64-bit undo/redo.");
    }
}
