namespace WoWCrucible.Core;

public static class CliHelpRouting
{
    public const string Root = "";

    public static string? Resolve(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0) return null;

        if (arguments[0].Equals("help", StringComparison.OrdinalIgnoreCase))
            return arguments.Count > 1 ? arguments[1].Trim().ToLowerInvariant() : Root;

        if (IsHelpFlag(arguments[0])) return Root;
        return arguments.Skip(1).Any(IsHelpFlag)
            ? arguments[0].Trim().ToLowerInvariant()
            : null;
    }

    private static bool IsHelpFlag(string argument) =>
        argument.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
        argument.Equals("-h", StringComparison.OrdinalIgnoreCase);
}
