namespace WoWCrucible.Core;

/// <summary>
/// Chooses a layout from the shape actually available to a workspace. The policy
/// deliberately has no pixel minimum or maximum: resizing remains continuous and
/// callers decide only which orientation makes better use of the current aspect.
/// </summary>
public static class ResponsiveLayoutService
{
    public static bool UseSideBySide(double width, double height, double sideBySideAspect)
    {
        if (!double.IsFinite(sideBySideAspect) || sideBySideAspect <= 0)
            throw new ArgumentOutOfRangeException(nameof(sideBySideAspect));
        return double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0 && width / height >= sideBySideAspect;
    }
}
