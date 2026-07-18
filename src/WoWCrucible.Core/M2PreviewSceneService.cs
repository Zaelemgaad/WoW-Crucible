using System.Numerics;

namespace WoWCrucible.Core;

public sealed record ItemModelMountSource(int ModelSlot, string ModelPath, string? TexturePath, string Provenance);

public static class M2PreviewSceneService
{
    public static (Vector3 Minimum, Vector3 Maximum) TransformBounds(Vector3 minimum, Vector3 maximum, Matrix4x4 transform)
    {
        if (!Finite(minimum) || !Finite(maximum) || !Finite(transform)) throw new ArgumentException("Preview scene bounds and transforms must contain only finite values.");
        var resultMinimum = new Vector3(float.PositiveInfinity); var resultMaximum = new Vector3(float.NegativeInfinity);
        for (var bits = 0; bits < 8; bits++)
        {
            var point = new Vector3((bits & 1) == 0 ? minimum.X : maximum.X, (bits & 2) == 0 ? minimum.Y : maximum.Y, (bits & 4) == 0 ? minimum.Z : maximum.Z);
            point = Vector3.Transform(point, transform); resultMinimum = Vector3.Min(resultMinimum, point); resultMaximum = Vector3.Max(resultMaximum, point);
        }
        return (resultMinimum, resultMaximum);
    }

    public static IReadOnlyList<ItemModelMountSource> FindItemModelSources(ItemDisplayInfoRecord display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var result = new List<ItemModelMountSource>();
        foreach (var modelAsset in display.Assets.Where(asset => asset.Kind.Equals("model", StringComparison.OrdinalIgnoreCase)))
        {
            var textureAsset = display.Assets.FirstOrDefault(asset => asset.Kind.Equals("model-texture", StringComparison.OrdinalIgnoreCase) && asset.Slot == modelAsset.Slot);
            foreach (var modelPath in modelAsset.ExistingPaths.Where(File.Exists))
            {
                var provenance = Provenance(modelPath);
                var texturePath = textureAsset?.ExistingPaths.Where(File.Exists).Where(path => Provenance(path).Equals(provenance, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
                result.Add(new(modelAsset.Slot, modelPath, texturePath, provenance));
            }
        }
        return result.OrderBy(source => source.ModelSlot).ThenBy(source => source.Provenance, StringComparer.OrdinalIgnoreCase).ThenBy(source => source.ModelPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static uint? RecommendedAttachmentId(int inventoryType) => inventoryType switch
    {
        1 => 11,                          // helmet
        13 or 17 or 21 => 1,             // right palm / main hand
        14 or 22 or 23 => 2,             // shield / off hand
        15 or 25 or 26 => 1,             // ranged / thrown
        16 => 12,                         // back / cloak model
        27 => 30,                         // quiver on left back
        _ => null                         // shoulders and custom slots require an explicit choice
    };

    private static string Provenance(string path) => Directory.GetParent(path)?.Name is { Length: > 0 } value ? value : "unidentified source";
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
