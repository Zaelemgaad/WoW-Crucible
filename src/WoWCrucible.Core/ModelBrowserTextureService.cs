namespace WoWCrucible.Core;

public static class ModelBrowserTextureService
{
    public static string SlotName(uint type) => type switch
    {
        0 => "Model texture", 1 => "Body and clothing", 2 => "Item / cape", 3 => "Weapon blade", 4 => "Weapon handle",
        5 => "Environment reflection", 6 => "Hair", 7 => "Facial hair", 8 => "Skin details", 9 => "Inventory artwork", 10 => "Mane",
        11 => "Creature skin 1", 12 => "Creature skin 2", 13 => "Creature skin 3", 14 => "Item icon",
        15 => "Guild background", 16 => "Guild emblem color", 17 => "Guild border", 18 => "Guild emblem",
        19 => "Eyes", 20 => "Jewelry / accessories", _ => $"Unidentified material ({type})"
    };

    public static IReadOnlyDictionary<int, string> SuggestBindings(ModelBrowserSource source, M2PreviewGeometry geometry)
    {
        var result = new Dictionary<int, string>();
        var nearby = source.Nearby(".blp");
        var stem = Path.GetFileNameWithoutExtension(source.ModelName).Replace("_hd", "", StringComparison.OrdinalIgnoreCase).Replace("_sdr", "", StringComparison.OrdinalIgnoreCase);
        foreach (var slot in geometry.TextureSlots)
        {
            var path = !string.IsNullOrWhiteSpace(slot.EmbeddedPath) ? source.Find(slot.EmbeddedPath) : null;
            path ??= source.FindFileDataId(slot.FileDataId, ".blp");
            if (path is null && slot.Type == 1)
                path = nearby.FirstOrDefault(path => Path.GetFileName(path).StartsWith(stem + "skin00_00", StringComparison.OrdinalIgnoreCase))
                    ?? nearby.FirstOrDefault(path => Path.GetFileName(path).StartsWith(stem + "skin00", StringComparison.OrdinalIgnoreCase));
            if (path is null && slot.Type == 6)
                path = nearby.FirstOrDefault(path => Path.GetFileName(path).StartsWith(stem + "hair00_00", StringComparison.OrdinalIgnoreCase));
            if (path is not null) result[slot.Index] = path;
        }
        return result;
    }

    public static RgbaTexture Decode(ModelBrowserSource source, string path)
    {
        using var stream = new MemoryStream(source.Read(path), false);
        return BlpTextureService.Decode(stream, path);
    }
}
