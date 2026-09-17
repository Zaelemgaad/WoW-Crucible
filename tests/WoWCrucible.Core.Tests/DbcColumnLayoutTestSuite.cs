using System.Text.Json;
using WoWCrucible.Core;
using WoWCrucible.Desktop;
using WoWCrucible.Desktop.Controls;

internal static class DbcColumnLayoutTestSuite
{
    public static void Run(string corpus)
    {
        // A non-leading physical ID must stay pinned without consuming a scrolling column.
        var layout = new DbcColumnLayout(5, 2);
        var changes = 0;
        layout.Changed += (_, _) => changes++;
        layout.SetWidth(DbcColumnLayout.RowHeader, 64);
        layout.SetWidth(2, 120);
        layout.SetWidth(0, 80);
        layout.SetWidth(1, 280);
        layout.SetWidth(3, 40);
        Require(changes == 5, "Layout changes must notify both views of a split document.");
        layout.SetWidth(1, 280);
        Require(changes == 5, "Unchanged widths must not trigger a redraw loop.");
        Require(layout.ScrollColumns.SequenceEqual(new[] { 0, 1, 3, 4 }), "Physical ID duplicated in scrolling fields.");
        Require(layout.FrozenWidth == 184 && layout.ContentWidth == 556, "Variable-width totals are incorrect.");
        Require(layout.Bounds(2, 130) == new DbcColumnSpan(2, 64, 120), "Pinned ID moved with scrolling.");
        Require(layout.Bounds(1, 130) == new DbcColumnSpan(1, 134, 280), "Partially scrolled cell/editor bounds diverged.");
        Require(layout.VisibleColumns(130, 500).SequenceEqual(new[] {
            new DbcColumnSpan(1, 134, 280), new DbcColumnSpan(3, 414, 40), new DbcColumnSpan(4, 454, 156)
        }), "Variable-width viewport omitted or misplaced a visible column.");
        Require(layout.VisibleColumns(80, 264).Single().Column == 1, "Exact viewport boundaries included an invisible column.");
        Require(!layout.VisibleColumns(0, 150).Any(), "Hidden scrolling area produced visible columns.");

        Require(layout.HitColumn(63, 130) == DbcColumnLayout.RowHeader &&
            layout.HitColumn(64, 130) == DbcColumnLayout.RecordKey &&
            layout.HitColumn(184, 130) == 1 && layout.HitColumn(414, 130) == 3 &&
            layout.HitColumn(454, 130) == 4 && layout.HitColumn(610, 130) is null &&
            layout.HitColumn(-1, 130) is null, "Cell hit-testing does not match drawn boundaries.");
        Require(layout.ResizeEdgeAt(66, 130) == DbcColumnLayout.RowHeader &&
            layout.ResizeEdgeAt(182, 130) == DbcColumnLayout.RecordKey &&
            layout.ResizeEdgeAt(412, 130) == 1 && layout.ResizeEdgeAt(456, 130) == 3 &&
            layout.ResizeEdgeAt(609, 130) == 4 && layout.ResizeEdgeAt(350, 130) is null,
            "Resize handles do not match pinned or horizontally scrolled boundaries.");
        Require(layout.MaximumOffset(500) == 240 && layout.OffsetToReveal(4, 0, 500) == 240 &&
            layout.OffsetToReveal(0, 240, 500) == 0 && layout.OffsetToReveal(2, 130, 500) == 130,
            "Navigation or the scrollbar still assumes equal-width columns.");
        layout.SetWidth(1, 800);
        Require(layout.OffsetToReveal(1, 600, 500) == 80, "A field wider than the viewport must reveal its start.");
        layout.SetWidth(1, 40);
        Require(layout.OffsetToReveal(4, 240, 500) == 0, "Shrinking columns must clamp stale scroll offsets.");
        layout.SetWidth(3, -100);
        Require(layout.GetWidth(3) == DbcColumnLayout.MinimumWidth, "Dragging left produced a negative-width column.");
        var invalidRejected = false;
        try { layout.SetWidth(1, double.NaN); }
        catch (ArgumentOutOfRangeException) { invalidRejected = true; }
        Require(invalidRejected, "Non-finite widths were accepted.");

        // Settings use JSON integer keys; preserve the gutter and ID aliases across a real round-trip.
        var saved = JsonSerializer.Deserialize<Dictionary<int, double>>(JsonSerializer.Serialize(layout.ExportWidths()))!;
        var reopened = new DbcColumnLayout(5, 2);
        reopened.RestoreWidths(saved);
        Require(reopened.ExportWidths().OrderBy(pair => pair.Key).SequenceEqual(saved.OrderBy(pair => pair.Key)),
            "Saved widths did not survive a JSON round-trip.");
        Require(reopened.GetWidth(2) == reopened.GetWidth(DbcColumnLayout.RecordKey), "Physical and pinned ID widths disagree.");
        reopened.Reset(2);
        Require(reopened.KeyWidth == 108 && reopened.GetWidth(0) == 80, "Resetting one column changed other widths.");
        reopened.Reset();
        Require(reopened.ExportWidths().Count == 0 && reopened.ContentWidth == 4 * 156, "Reset all left stale geometry/settings.");
        reopened.RestoreWidths(new Dictionary<int, double> { [-3] = 90, [5] = 90, [0] = double.PositiveInfinity, [1] = -5, [3] = 60 });
        Require(reopened.ExportWidths().Count == 1 && reopened.GetWidth(3) == 60, "Invalid saved widths corrupted a layout.");

        var virtualId = new DbcColumnLayout(3);
        virtualId.SetWidth(DbcColumnLayout.RecordKey, 200);
        Require(virtualId.ScrollColumns.Count == 3 && virtualId.Bounds(0, 0).Left == 258 && virtualId.GetWidth(0) == 156,
            "A virtual record ID displaced or resized the first real field.");
        Require(!new DbcColumnLayout(0).VisibleColumns(0, 500).Any(), "An empty document exposed a phantom column.");

        var file = WdbcFile.Load(Path.Combine(corpus, "SpellDuration.dbc"));
        var schema = DbcSchemaCatalog.CreateBuiltIn12340().ResolveColumns(file.LogicalTableName, file.FieldCount);
        var first = new DbcDocumentSession(file, schema, "built-in");
        var second = new DbcDocumentSession(file, schema, "equivalent schema source");
        first.ColumnLayout.SetWidth(1, 320);
        Require(!file.IsDirty && !first.History.CanUndo && second.ColumnLayout.GetWidth(1) == 156,
            "Presentation changes altered DBC data/history or a different document.");
        Require(first.ColumnLayoutKey == second.ColumnLayoutKey, "Equivalent schemas cannot restore saved widths.");
        var altered = schema with { Columns = schema.Columns.Select((column, index) => index == 1 ? column with { Name = "OtherField" } : column).ToArray() };
        Require(first.ColumnLayoutKey != new DbcDocumentSession(file, altered, "other layout").ColumnLayoutKey &&
            first.ColumnLayoutKey != new DbcDocumentSession(file, schema with { KeyStrategy = DbcRecordKeyStrategy.Virtual() }, "virtual").ColumnLayoutKey,
            "Different schemas or record-ID strategies share incompatible saved widths.");
        Console.WriteLine("PASS DBC columns: resize, pinned/virtual IDs, scrolling, hit tests, editor bounds, navigation, persistence, reset, and clean data.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
