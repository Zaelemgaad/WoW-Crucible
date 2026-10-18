using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

/// <summary>Uses the same class token and Interface/Icons path as the client's advancement UI.</summary>
public static class ClassStudioIconService
{
    public static IReadOnlyDictionary<uint, string> Read(ClientEffectiveAssetCatalog catalog,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> classes, string cache, List<string> notices, CancellationToken token = default)
    {
        var result = new Dictionary<uint, string>();
        catalog.ResolveMany(classes.Select(row => Convert.ToString(row.GetValueOrDefault("Filename")) ?? "")
            .Where(name => Regex.IsMatch(name, "^[A-Za-z0-9_]+$")).Select(name => $"Interface\\Icons\\classicon_{name.ToLowerInvariant()}.blp"), token);
        var icons = new List<(uint Id, ClientEffectiveAssetCandidate Asset)>();
        foreach (var row in classes)
        {
            token.ThrowIfCancellationRequested();
            var name = Convert.ToString(row.GetValueOrDefault("Filename")) ?? "";
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$")) { notices.Add($"Class {row["ID"]} has no valid client icon token."); continue; }
            var path = $"Interface\\Icons\\classicon_{name.ToLowerInvariant()}.blp";
            var asset = catalog.Resolve(path, cancellationToken: token);
            if (asset.State != ClientEffectiveAssetState.Effective || asset.Effective is null)
            { notices.Add($"Class icon {name}: {asset.Message}"); continue; }
            icons.Add((Convert.ToUInt32(row["ID"]), asset.Effective));
        }
        var paths = catalog.ExtractEffectiveMany(icons.Select(icon => icon.Asset).DistinctBy(asset => asset.Entry.ArchivePath, StringComparer.OrdinalIgnoreCase), cache, overwrite: true, cancellationToken: token)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);
        foreach (var icon in icons)
        {
            var extracted = paths[Path.GetFileNameWithoutExtension(icon.Asset.Entry.ArchivePath)];
            var png = extracted + ".png";
            BlpTextureService.DecodeToPng(extracted, png, overwrite: true);
            result.Add(icon.Id, "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(png)));
        }
        return result;
    }
}
