namespace WoWCrucible.Core;

public static class ModelBrowserTextureService
{
    public static string SlotName(uint type) => type switch
    {
        0 => "Fixed texture", 1 => "Body", 2 => "Object skin", 6 => "Hair", 8 => "Fur", 9 => "Cape",
        11 => "Creature skin 1", 12 => "Creature skin 2", 13 => "Creature skin 3", _ => $"Texture type {type}"
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
