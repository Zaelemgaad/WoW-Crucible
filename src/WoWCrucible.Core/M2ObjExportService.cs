using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record M2ObjExportMaterial(int SubmeshIndex, ushort GeosetId, string Name, int? TextureDefinitionIndex, ushort BlendMode, ushort RenderFlags, int Passes);
public sealed record M2ObjExportReceipt(string Format, DateTimeOffset ExportedUtc, string ModelPath, string ModelSha256, string SkinPath, string SkinSha256,
    M2PreviewVisibilityMode VisibilityMode, IReadOnlyDictionary<int, int>? GeosetSelection, int? AnimationSequenceIndex, double? AnimationTimeMilliseconds,
    int Vertices, int Triangles, IReadOnlyList<M2ObjExportMaterial> Materials, IReadOnlyList<string> TextureFiles);
public sealed record M2ObjExportResult(string ObjPath, string MaterialPath, string ReceiptPath, IReadOnlyList<string> TexturePaths, int Vertices, int Triangles, bool Posed);

/// <summary>Exports the exact visible Wrath M2/SKIN preview mesh to portable Wavefront OBJ.</summary>
public static class M2ObjExportService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static M2ObjExportResult Export(M2PreviewGeometry geometry, string outputPath, M2AnimationPose? pose = null,
        IReadOnlyDictionary<int, RgbaTexture>? textures = null, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("An OBJ output path is required.", nameof(outputPath));
        outputPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(outputPath).Equals(".obj", StringComparison.OrdinalIgnoreCase)) outputPath += ".obj";
        ValidateGeometry(geometry, pose);

        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        var stem = Path.GetFileNameWithoutExtension(outputPath);
        var materialPath = Path.Combine(outputDirectory, stem + ".mtl");
        var receiptPath = Path.Combine(outputDirectory, stem + ".crucible-model.json");
        var groups = Groups(geometry);
        var textureOutputs = (textures ?? new Dictionary<int, RgbaTexture>())
            .Where(pair => pair.Key >= 0 && geometry.TextureSlots.Any(slot => slot.Index == pair.Key))
            .OrderBy(pair => pair.Key)
            .Select(pair => (pair.Key, Texture: pair.Value, Path: Path.Combine(outputDirectory, $"{stem}-texture-{pair.Key:000}.png")))
            .ToArray();
        var destinations = new[] { outputPath, materialPath, receiptPath }.Concat(textureOutputs.Select(item => item.Path)).ToArray();
        if (!overwrite && destinations.FirstOrDefault(File.Exists) is { } existing) throw new IOException($"Export output already exists: {existing}");

        var stage = Path.Combine(outputDirectory, $".{stem}.crucible-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            var stagedObj = Path.Combine(stage, Path.GetFileName(outputPath));
            var stagedMtl = Path.Combine(stage, Path.GetFileName(materialPath));
            var stagedReceipt = Path.Combine(stage, Path.GetFileName(receiptPath));
            var stagedTextures = textureOutputs.Select(item => (item.Key, item.Texture, Destination: item.Path, Stage: Path.Combine(stage, Path.GetFileName(item.Path)))).ToArray();
            foreach (var item in stagedTextures) BlpTextureService.WritePng(item.Stage, item.Texture);
            WriteMaterials(stagedMtl, groups, stagedTextures.ToDictionary(item => item.Key, item => Path.GetFileName(item.Destination)));
            WriteObj(stagedObj, Path.GetFileName(materialPath), geometry, pose, groups);
            var receipt = new M2ObjExportReceipt("wow-crucible-m2-obj-v1", DateTimeOffset.UtcNow, geometry.ModelPath, Sha256(geometry.ModelPath), geometry.SkinPath, Sha256(geometry.SkinPath),
                geometry.VisibilityMode, geometry.GeosetSelection?.GroupVariants, pose?.SequenceIndex, pose?.TimeMilliseconds, geometry.Vertices.Count, geometry.TriangleIndices.Count / 3,
                groups.Select(group => new M2ObjExportMaterial(group.SubmeshIndex, group.GeosetId, group.MaterialName, group.Batch.TextureDefinitionIndex, group.Batch.BlendMode, group.Batch.RenderFlags,
                    geometry.Batches.Count(batch => batch.SubmeshIndex == group.SubmeshIndex))).ToArray(), stagedTextures.Select(item => Path.GetFileName(item.Destination)).ToArray());
            File.WriteAllText(stagedReceipt, JsonSerializer.Serialize(receipt, Json) + Environment.NewLine, new UTF8Encoding(false));

            PublishAll(new[] { (Stage: stagedObj, Destination: outputPath), (Stage: stagedMtl, Destination: materialPath) }
                .Concat(stagedTextures.Select(item => (item.Stage, item.Destination))).Append((stagedReceipt, receiptPath)).ToArray(), overwrite, stage);
            return new(outputPath, materialPath, receiptPath, textureOutputs.Select(item => item.Path).ToArray(), geometry.Vertices.Count, geometry.TriangleIndices.Count / 3, pose is not null);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private sealed record ExportGroup(int SubmeshIndex, ushort GeosetId, string Name, string MaterialName, M2PreviewBatch Batch);

    private static IReadOnlyList<ExportGroup> Groups(M2PreviewGeometry geometry)
    {
        if (geometry.Batches.Count == 0)
            return [new(0, 0, "complete_mesh", "material_complete_mesh", new(0, 0, 0, geometry.TriangleIndices.Count, null, null))];
        return geometry.Batches.GroupBy(batch => batch.SubmeshIndex).OrderBy(group => group.Key).Select(group =>
        {
            var batch = group.OrderBy(value => value.PriorityPlane).ThenBy(value => value.MaterialUnitIndex ?? int.MinValue).First();
            var name = $"geoset_{batch.GeosetId:0000}_submesh_{batch.SubmeshIndex:000}";
            return new ExportGroup(batch.SubmeshIndex, batch.GeosetId, name, "material_" + name, batch);
        }).ToArray();
    }

    private static void WriteObj(string path, string materialFile, M2PreviewGeometry geometry, M2AnimationPose? pose, IReadOnlyList<ExportGroup> groups)
    {
        var vertices = pose?.Vertices ?? geometry.Vertices;
        var normals = pose?.Normals ?? geometry.Normals;
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1024 * 1024);
        writer.WriteLine("# WoW Crucible deterministic visible-mesh export");
        writer.WriteLine($"# source-model {geometry.ModelPath}");
        writer.WriteLine($"# source-skin {geometry.SkinPath}");
        writer.WriteLine($"# visibility {geometry.VisibilityMode}");
        if (pose is not null) writer.WriteLine($"# animation sequence={pose.SequenceIndex} time-ms={Number(pose.TimeMilliseconds)}");
        writer.WriteLine($"mtllib {materialFile}");
        writer.WriteLine($"o {Identifier(Path.GetFileNameWithoutExtension(geometry.ModelPath))}");
        foreach (var vertex in vertices) writer.WriteLine($"v {Number(vertex.X)} {Number(vertex.Y)} {Number(vertex.Z)}");
        foreach (var uv in geometry.TextureCoordinates) writer.WriteLine($"vt {Number(uv.X)} {Number(1f - uv.Y)}");
        foreach (var normal in normals) writer.WriteLine($"vn {Number(normal.X)} {Number(normal.Y)} {Number(normal.Z)}");
        foreach (var group in groups)
        {
            writer.WriteLine($"g {group.Name}"); writer.WriteLine($"usemtl {group.MaterialName}");
            var end = checked(group.Batch.TriangleStart + group.Batch.TriangleIndexCount);
            if (group.Batch.TriangleStart < 0 || end > geometry.TriangleIndices.Count) throw new InvalidDataException($"Export group {group.Name} references triangle indices outside the visible mesh.");
            for (var offset = group.Batch.TriangleStart; offset < end; offset += 3)
            {
                var a = geometry.TriangleIndices[offset] + 1; var b = geometry.TriangleIndices[offset + 1] + 1; var c = geometry.TriangleIndices[offset + 2] + 1;
                writer.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
        }
    }

    private static void WriteMaterials(string path, IReadOnlyList<ExportGroup> groups, IReadOnlyDictionary<int, string> textureFiles)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        writer.WriteLine("# WoW Crucible material approximation; exact WoW pass metadata is retained in the JSON receipt.");
        foreach (var group in groups)
        {
            writer.WriteLine(); writer.WriteLine($"newmtl {group.MaterialName}");
            writer.WriteLine($"# wow-blend-mode {group.Batch.BlendMode}"); writer.WriteLine($"# wow-render-flags 0x{group.Batch.RenderFlags:X4}");
            writer.WriteLine("Ka 0 0 0"); writer.WriteLine("Kd 1 1 1"); writer.WriteLine("Ks 0 0 0"); writer.WriteLine("d 1"); writer.WriteLine("illum 1");
            if (group.Batch.TextureDefinitionIndex is { } texture && textureFiles.TryGetValue(texture, out var file)) writer.WriteLine($"map_Kd {file}");
        }
    }

    private static void ValidateGeometry(M2PreviewGeometry geometry, M2AnimationPose? pose)
    {
        if (geometry.Vertices.Count == 0 || geometry.TriangleIndices.Count == 0) throw new InvalidDataException("The selected visible M2 mesh is empty.");
        if (geometry.Vertices.Count != geometry.Normals.Count || geometry.Vertices.Count != geometry.TextureCoordinates.Count) throw new InvalidDataException("M2 export requires one normal and UV coordinate per vertex.");
        if (geometry.TriangleIndices.Count % 3 != 0 || geometry.TriangleIndices.Any(index => index < 0 || index >= geometry.Vertices.Count)) throw new InvalidDataException("M2 export received invalid triangle indices.");
        if (pose is not null && (pose.Vertices.Length != geometry.Vertices.Count || pose.Normals.Length != geometry.Vertices.Count || pose.SequenceIndex < 0)) throw new ArgumentException("The sampled animation pose does not belong to this geometry or has not been sampled.", nameof(pose));
        var vertices = pose?.Vertices ?? geometry.Vertices; var normals = pose?.Normals ?? geometry.Normals;
        if (vertices.Any(value => !Finite(value)) || normals.Any(value => !Finite(value)) || geometry.TextureCoordinates.Any(value => !float.IsFinite(value.X) || !float.IsFinite(value.Y))) throw new InvalidDataException("M2 export rejected non-finite geometry data.");
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Identifier(string value) { var result = new string(value.Select(character => char.IsLetterOrDigit(character) || character is '_' or '-' ? character : '_').ToArray()); return result.Length == 0 ? "model" : result; }
    private static string Sha256(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static void PublishAll(IReadOnlyList<(string Stage, string Destination)> files, bool overwrite, string stageRoot)
    {
        var existing = files.Where(file => File.Exists(file.Destination)).ToArray();
        if (!overwrite && existing.Length > 0) throw new IOException($"Export output already exists: {existing[0].Destination}");
        var backupRoot = Path.Combine(stageRoot, "previous"); var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var published = new List<string>();
        try
        {
            if (existing.Length > 0) Directory.CreateDirectory(backupRoot);
            foreach (var file in existing)
            {
                var backup = Path.Combine(backupRoot, $"{backups.Count:000}-{Path.GetFileName(file.Destination)}"); File.Move(file.Destination, backup); backups[file.Destination] = backup;
            }
            foreach (var file in files) { File.Move(file.Stage, file.Destination); published.Add(file.Destination); }
        }
        catch
        {
            foreach (var destination in published.AsEnumerable().Reverse()) if (File.Exists(destination)) File.Delete(destination);
            foreach (var pair in backups.Reverse()) if (File.Exists(pair.Value)) File.Move(pair.Value, pair.Key);
            throw;
        }
    }
}
