using System.Drawing.Drawing2D;
using WoWCrucible.Core;

namespace WoWCrucible.App;

internal sealed class ItemTooltipControl : Control
{
    private readonly Font _nameFont = new("Segoe UI", 11, FontStyle.Bold);
    private readonly Font _bodyFont = new("Segoe UI", 9.5f);
    private readonly Font _smallFont = new("Segoe UI", 8.5f);
    private ItemDraft? _item;
    private string _className = string.Empty;
    private string _subclassName = string.Empty;
    private string _inventoryName = string.Empty;

    public ItemTooltipControl()
    {
        DoubleBuffered = true; MinimumSize = new(350, 470); BackColor = Color.FromArgb(10, 10, 18); Margin = new(18);
    }

    public void ShowItem(ItemDraft item, string className, string subclassName, string inventoryName)
    {
        _item = item; _className = className; _subclassName = subclassName; _inventoryName = inventoryName; Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var bounds = new Rectangle(8, 8, Math.Max(10, Width - 17), Math.Max(10, Height - 17));
        using var background = new LinearGradientBrush(bounds, Color.FromArgb(244, 8, 9, 15), Color.FromArgb(248, 18, 19, 30), LinearGradientMode.Vertical);
        using var border = new Pen(Color.FromArgb(180, 170, 145, 82), 2); using var inner = new Pen(Color.FromArgb(115, 78, 72, 105));
        e.Graphics.FillRectangle(background, bounds); e.Graphics.DrawRectangle(border, bounds); e.Graphics.DrawRectangle(inner, Rectangle.Inflate(bounds, -4, -4));
        if (_item is null) { DrawCentered(e.Graphics, "Item preview", bounds, Color.Gray); return; }
        var item = _item; var x = bounds.Left + 16f; var width = bounds.Width - 32f; var y = bounds.Top + 15f;
        Line(e.Graphics, string.IsNullOrWhiteSpace(item.Name) ? "Unnamed Item" : item.Name.Trim(), _nameFont, QualityColor(item.Quality), ref y, x, width, 3);
        Line(e.Graphics, $"Item Level {item.ItemLevel}", _smallFont, Color.FromArgb(255, 210, 190, 120), ref y, x, width);
        if (item.Bonding != 0) Line(e.Graphics, BondingName(item.Bonding), _bodyFont, Color.White, ref y, x, width);
        if (item.InventoryType != 0) PairedLine(e.Graphics, _inventoryName, string.IsNullOrWhiteSpace(_subclassName) ? _className : _subclassName, ref y, x, width);
        if (item.Armor > 0) Line(e.Graphics, $"{item.Armor:0.##} Armor", _bodyFont, Color.White, ref y, x, width);
        if (item.DamageMax > 0 || item.DamageMin > 0)
        {
            var speed = item.Delay <= 0 ? 0 : item.Delay / 1000f;
            PairedLine(e.Graphics, $"{item.DamageMin:0.##} - {item.DamageMax:0.##} Damage", speed > 0 ? $"Speed {speed:0.00}" : string.Empty, ref y, x, width);
            if (speed > 0) Line(e.Graphics, $"({(item.DamageMin + item.DamageMax) / 2f / speed:0.0} damage per second)", _smallFont, Color.White, ref y, x, width);
        }
        foreach (var stat in (item.Stats ?? []).Take(10)) Stat(e.Graphics, stat.Type, stat.Value, ref y, x, width);
        if (item.RequiredLevel > 0) Line(e.Graphics, $"Requires Level {item.RequiredLevel}", _bodyFont, Color.White, ref y, x, width);
        if (item.MaxDurability > 0) Line(e.Graphics, $"Durability {item.MaxDurability} / {item.MaxDurability}", _bodyFont, Color.White, ref y, x, width);
        foreach (var spell in (item.Spells ?? []).Take(5).Where(spell => spell.SpellId != 0)) Spell(e.Graphics, spell, ref y, x, width);
        if (!string.IsNullOrWhiteSpace(item.Description)) Line(e.Graphics, $"\"{item.Description.Trim()}\"", _bodyFont, Color.FromArgb(255, 255, 209, 0), ref y, x, width, 5);
        if (item.SellPrice > 0) DrawPrice(e.Graphics, item.SellPrice, ref y, x);
        Line(e.Graphics, $"Entry {item.Entry}  •  Display {item.DisplayId}", _smallFont, Color.FromArgb(255, 120, 120, 130), ref y, x, width, 0);
    }

    private void Stat(Graphics graphics, int type, int value, ref float y, float x, float width)
    {
        if (value == 0) return; var sign = value > 0 ? "+" : string.Empty;
        if (type is 0 or 1 or 3 or 4 or 5 or 6 or 7)
            Line(graphics, $"{sign}{value} {StatName(type)}", _bodyFont, Color.White, ref y, x, width);
        else
            Line(graphics, SecondaryStatText(type, value), _bodyFont, Color.FromArgb(255, 30, 255, 0), ref y, x, width);
    }

    private void Spell(Graphics graphics, ItemSpellDraft spell, ref float y, float x, float width)
    {
        var prefix = spell.Trigger switch { 0 or 5 => "Use", 1 => "Equip", 2 => "Chance on hit", 4 => "Use", 6 => "Use", _ => "Effect" };
        var text = spell.Trigger == 6 ? $"Use: Teaches spell #{spell.SpellId}." : $"{prefix}: Spell #{spell.SpellId}.";
        if (spell.Charges != 0) text += $" ({Math.Abs(spell.Charges)} charge{(Math.Abs(spell.Charges) == 1 ? "" : "s")})";
        Line(graphics, text, _bodyFont, Color.FromArgb(255, 30, 255, 0), ref y, x, width);
    }

    private void DrawPrice(Graphics graphics, uint copper, ref float y, float x)
    {
        var gold = copper / 10000; var silver = copper % 10000 / 100; var remainder = copper % 100;
        var parts = new List<(string Text, Color Color)> { ("Sell Price: ", Color.White) };
        if (gold > 0) parts.Add(($"{gold}g ", Color.FromArgb(255, 255, 209, 0))); if (silver > 0) parts.Add(($"{silver}s ", Color.Silver)); if (remainder > 0 || parts.Count == 1) parts.Add(($"{remainder}c", Color.FromArgb(255, 205, 127, 50)));
        foreach (var part in parts) { using var brush = new SolidBrush(part.Color); graphics.DrawString(part.Text, _smallFont, brush, x, y); x += graphics.MeasureString(part.Text, _smallFont).Width - 2; }
        y += _smallFont.GetHeight(graphics) + 2;
    }

    private void PairedLine(Graphics graphics, string left, string right, ref float y, float x, float width)
    {
        graphics.DrawString(left, _bodyFont, Brushes.White, x, y); var rightWidth = graphics.MeasureString(right, _bodyFont).Width;
        if (!string.IsNullOrWhiteSpace(right)) graphics.DrawString(right, _bodyFont, Brushes.White, x + width - rightWidth, y);
        y += _bodyFont.GetHeight(graphics) + 2;
    }

    private static void Line(Graphics graphics, string text, Font font, Color color, ref float y, float x, float width, float after = 1)
    {
        using var brush = new SolidBrush(color); using var format = new StringFormat { Trimming = StringTrimming.Word, FormatFlags = StringFormatFlags.LineLimit };
        var measured = graphics.MeasureString(text, font, new SizeF(width, 500), format); graphics.DrawString(text, font, brush, new RectangleF(x, y, width, measured.Height + 2), format); y += measured.Height + after;
    }

    private void DrawCentered(Graphics graphics, string text, Rectangle bounds, Color color) { using var brush = new SolidBrush(color); using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; graphics.DrawString(text, _nameFont, brush, bounds, format); }
    private static Color QualityColor(int quality) => quality switch { 0 => Color.FromArgb(157, 157, 157), 1 => Color.White, 2 => Color.FromArgb(30, 255, 0), 3 => Color.FromArgb(0, 112, 221), 4 => Color.FromArgb(163, 53, 238), 5 => Color.FromArgb(255, 128, 0), 6 => Color.FromArgb(230, 40, 30), 7 => Color.FromArgb(0, 204, 255), _ => Color.White };
    private static string BondingName(uint bonding) => bonding switch { 1 => "Binds when picked up", 2 => "Binds when equipped", 3 => "Binds when used", 4 or 5 => "Quest Item", _ => string.Empty };
    private static string SecondaryStatText(int type, int value) => type switch
    {
        38 => $"Equip: Increases attack power by {value}.", 39 => $"Equip: Increases ranged attack power by {value}.",
        43 => $"Equip: Restores {value} mana per 5 sec.", 45 => $"Equip: Increases spell power by {value}.",
        46 => $"Equip: Restores {value} health per 5 sec.", 47 => $"Equip: Increases spell penetration by {value}.",
        48 => $"Equip: Increases the block value of your shield by {value}.",
        _ => $"Equip: Improves your {StatName(type).ToLowerInvariant()} by {value}."
    };
    private static string StatName(int type) => type switch { 0 => "Mana", 1 => "Health", 3 => "Agility", 4 => "Strength", 5 => "Intellect", 6 => "Spirit", 7 => "Stamina", 12 => "Defense Rating", 13 => "Dodge Rating", 14 => "Parry Rating", 15 => "Block Rating", 16 => "Melee Hit Rating", 17 => "Ranged Hit Rating", 18 => "Spell Hit Rating", 19 => "Melee Critical Strike Rating", 20 => "Ranged Critical Strike Rating", 21 => "Spell Critical Strike Rating", 22 => "Melee Hit Avoidance Rating", 23 => "Ranged Hit Avoidance Rating", 24 => "Spell Hit Avoidance Rating", 25 => "Melee Critical Avoidance Rating", 26 => "Ranged Critical Avoidance Rating", 27 => "Spell Critical Avoidance Rating", 28 => "Melee Haste Rating", 29 => "Ranged Haste Rating", 30 => "Spell Haste Rating", 31 => "Hit Rating", 32 => "Critical Strike Rating", 33 => "Hit Avoidance Rating", 34 => "Critical Avoidance Rating", 35 => "Resilience Rating", 36 => "Haste Rating", 37 => "Expertise Rating", 38 => "Attack Power", 39 => "Ranged Attack Power", 40 => "Feral Attack Power", 41 => "Spell Healing", 42 => "Spell Damage", 43 => "Mana per 5 sec", 44 => "Armor Penetration Rating", 45 => "Spell Power", 46 => "Health Regeneration", 47 => "Spell Penetration", 48 => "Block Value", _ => $"Stat {type}" };

    protected override void Dispose(bool disposing) { if (disposing) { _nameFont.Dispose(); _bodyFont.Dispose(); _smallFont.Dispose(); } base.Dispose(disposing); }
}
