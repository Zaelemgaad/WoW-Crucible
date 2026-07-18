namespace WoWCrucible.Core;

public sealed record M2GeosetVariant(int Variant, IReadOnlyList<int> SubmeshIndices, int TriangleIndices, bool Visible)
{
    public int Triangles => TriangleIndices / 3;
    public override string ToString() => $"Variant {Variant:N0} · {SubmeshIndices.Count:N0} section(s) · {Triangles:N0} triangles{(Visible ? " · visible" : string.Empty)}";
}

public sealed record M2GeosetGroup(int Group, string Name, IReadOnlyList<M2GeosetVariant> Variants)
{
    public override string ToString() => $"{Group:N0} · {Name} · {Variants.Count:N0} variant(s)";
}

public static class M2GeosetCatalog
{
    private static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [0] = "Hair (ID 0 remains base body)", [1] = "Facial feature 1", [2] = "Facial feature 2", [3] = "Facial feature 3",
        [4] = "Hands / gloves", [5] = "Feet / boots", [6] = "Tail", [7] = "Ears", [8] = "Sleeves", [9] = "Kneepads",
        [10] = "Chest", [11] = "Pants", [12] = "Tabard", [13] = "Trousers / robe", [14] = "Loincloth", [15] = "Cloak",
        [16] = "Facial feature 4", [17] = "Facial feature 5 / eye glow", [18] = "Belt", [19] = "Bones", [20] = "Feet",
        [21] = "Extended geoset 21", [22] = "Torso", [23] = "Hand attachment", [24] = "Head attachment", [25] = "Blindfold",
        [26] = "Extended geoset 26", [27] = "Extended geoset 27", [28] = "Extended geoset 28", [29] = "Arm / hand customization",
        [30] = "Leg customization", [31] = "Foot customization", [32] = "Face", [33] = "Eyes", [34] = "Eyebrows", [35] = "Earrings",
        [36] = "Necklace", [37] = "Headdress", [38] = "Tails", [39] = "Vines", [40] = "Tusks", [41] = "Noses",
        [42] = "Hair decoration", [43] = "Horn decoration"
    };

    public static int Group(ushort geosetId) => geosetId < 100 ? 0 : geosetId / 100;
    public static int Variant(ushort geosetId) => geosetId < 100 ? geosetId : geosetId % 100;
    public static string GroupName(int group) => Names.TryGetValue(group, out var name) ? name : $"Unknown/custom group {group:N0}";

    public static IReadOnlyList<M2GeosetGroup> Describe(IReadOnlyList<M2PreviewSubmesh> submeshes) => submeshes
        .GroupBy(section => section.GeosetGroup)
        .OrderBy(group => group.Key)
        .Select(group => new M2GeosetGroup(group.Key, GroupName(group.Key), group
            .GroupBy(section => section.GeosetVariant)
            .OrderBy(variant => variant.Key)
            .Select(variant => new M2GeosetVariant(variant.Key, variant.Select(section => section.Index).Order().ToArray(), variant.Sum(section => section.TriangleIndexCount), variant.Any(section => section.Visible)))
            .ToArray()))
        .ToArray();

    public static IReadOnlyDictionary<int, int> NakedCharacterSelection => new Dictionary<int, int>
    {
        [0] = 0, [1] = 0, [2] = 0, [3] = 0, [16] = 0, [17] = 0
    };
}
