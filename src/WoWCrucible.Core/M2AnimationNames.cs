using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace WoWCrucible.Core;

public static partial class M2AnimationNames
{
    private static readonly IReadOnlyDictionary<ushort, string> Names = Load();

    public static string Get(ushort animationId) => Names.TryGetValue(animationId, out var name)
        ? Words().Replace(name, " ").Replace("1 H", "1H").Replace("2 H", "2H")
        : $"Unknown animation {animationId}";

    public static string Label(M2PreviewSequence sequence) => Get(sequence.AnimationId)
        + (sequence.SubAnimationId == 0 ? string.Empty : $" (variant {sequence.SubAnimationId + 1})");

    private static IReadOnlyDictionary<ushort, string> Load()
    {
        using var stream = typeof(M2AnimationNames).Assembly.GetManifestResourceStream("WoWCrucible.Core.AnimationNames.csv")
            ?? throw new InvalidOperationException("The animation name catalog is missing.");
        using var parser = new TextFieldParser(stream) { TextFieldType = FieldType.Delimited };
        parser.SetDelimiters(";"); parser.ReadFields();
        var names = new Dictionary<ushort, string>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? throw new InvalidDataException("Invalid animation catalog row.");
            if (fields.Length != 2) throw new InvalidDataException("Invalid animation catalog columns.");
            names.Add(ushort.Parse(fields[0], CultureInfo.InvariantCulture), fields[1]);
        }
        return names;
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Za-z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex Words();
}
