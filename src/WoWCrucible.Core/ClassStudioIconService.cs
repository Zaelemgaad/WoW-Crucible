namespace WoWCrucible.Core;

/// <summary>Resolves class and talent textures through the selected client's archive order.</summary>
public static class ClassStudioIconService
{
    public static IReadOnlyDictionary<uint, string> Read(ClientEffectiveAssetCatalog catalog,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> classes, string cache, List<string> notices, CancellationToken token = default)
    {
        var names = classes.ToDictionary(row => Convert.ToUInt32(row["ID"]), row => "classicon_" + Convert.ToString(row.GetValueOrDefault("Filename"))!.ToLowerInvariant());
        var icons = ReadPaths(catalog, names.Values, cache, notices, token);
        return names.Where(p => icons.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => icons[p.Value]);
    }

    public static IReadOnlyDictionary<string, string> ReadPaths(ClientEffectiveAssetCatalog catalog,
        IEnumerable<string> names, string cache, List<string> notices, CancellationToken token = default)
    {
        var requested = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (requested.Any(name => string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Contains("..")))
            throw new InvalidDataException("Expected client icon basenames.");
        var resolutions = catalog.ResolveMany(requested.Select(name => $"Interface\\Icons\\{name}.blp"), token);
        var assets = resolutions.Where(r => r.State == ClientEffectiveAssetState.Effective && r.Effective is not null).Select(r => r.Effective!).ToArray();
        foreach (var missing in resolutions.Where(r => r.State != ClientEffectiveAssetState.Effective)) notices.Add(missing.Message);
        var files = catalog.ExtractEffectiveMany(assets, cache, overwrite: true, cancellationToken: token);
        var result = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var png = file + ".png";
            BlpTextureService.DecodeToPng(file, png, overwrite: true);
            result.Add(Path.GetFileNameWithoutExtension(file), "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png)));
        }
        return result;
    }
}
