namespace WoWCrucible.Desktop;

internal sealed class EditorLayoutSettings
{
    public bool BrowserVisible { get; set; } = true;
    public bool BrowserOnRight { get; set; }
    public double BrowserWidth { get; set; } = 260;
    public double InspectorWidth { get; set; } = 340;
    public Dictionary<string, SplitPaneLayout> Splits { get; set; } = [];
}

internal sealed class SplitPaneLayout
{
    public double WideFraction { get; set; } = 0.5;
    public double TallFraction { get; set; } = 0.5;
    public bool Reversed { get; set; }
    public int FocusedPane { get; set; }
    public string Arrangement { get; set; } = "auto";

    public static double ValidFraction(double value, double fallback) => double.IsFinite(value) && value is >= 0.02 and <= 0.98 ? value : fallback;
}
