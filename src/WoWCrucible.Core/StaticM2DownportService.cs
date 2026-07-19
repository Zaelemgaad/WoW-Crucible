using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record StaticM2DownportPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourceModelPath,
    string SourceModelSha256,
    string? SourceSkinPath,
    string? SourceSkinSha256,
    uint SourceVersion,
    uint SourceFlags,
    uint OutputFlags,
    int VertexCount,
    int TriangleCount,
    int SubmeshCount,
    int MaterialCount,
    int ShadowBatchCount,
    IReadOnlyList<string> Transformations,
    IReadOnlyList<string> Losses,
    IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0;
}

public sealed record StaticM2DownportResult(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    StaticM2DownportPlan Plan,
    string OutputDirectory,
    string OutputModelPath,
    string OutputModelSha256,
    string OutputSkinPath,
    string OutputSkinSha256,
    string ReceiptPath,
    int ValidatedVertices,
    int ValidatedTriangles,
    int ValidatedSubmeshes,
    int ValidatedMaterials);

/// <summary>
/// Clean-room, loss-accounted conversion for the deliberately narrow modern static M2 profile
/// found in the imported corpus. Unsupported structures are refused instead of discarded.
/// </summary>
public static class StaticM2DownportService
{
    private const int PlanFormatVersion = 1;
    private const int ReceiptFormatVersion = 1;
    private const uint ModernVersion = 274;
    private const uint WotlkVersion = 264;
    private const uint SupportedModernFlags = 0x2080;
    private const int MaximumArrayCount = 20_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static StaticM2DownportPlan Plan(string sourceModelPath, string? sourceSkinPath = null)
    {
        sourceModelPath = Path.GetFullPath(sourceModelPath);
        if (!File.Exists(sourceModelPath)) throw new FileNotFoundException("The modern M2 source does not exist.", sourceModelPath);
        var source = File.ReadAllBytes(sourceModelPath);
        var blockers = new List<string>(); var transformations = new List<string>(); var losses = new List<string>();
        var modelHash = Hash(source); uint version = 0, flags = 0; int vertices = 0, triangles = 0, submeshes = 0, materials = 0, shadows = 0;
        byte[]? payload = null; ModernSkin? skin = null; string? skinHash = null; var omittedEmptyTxac = false;

        if (source.Length < 8 || FourCc(source, 0) != "MD21") blockers.Add("Input is not a modern MD21 chunk container.");
        else
        {
            var chunks = ReadChunks(source, blockers);
            var txac = chunks.Where(chunk => chunk.Id == "TXAC").ToArray();
            if (txac.Length > 1) blockers.Add($"Expected at most one TXAC chunk; found {txac.Length:N0}.");
            else if (txac.Length == 1)
            {
                if (txac[0].Size == 0 || source.AsSpan(checked((int)txac[0].DataOffset), checked((int)txac[0].Size)).IndexOfAnyExcept((byte)0) < 0) omittedEmptyTxac = true;
                else blockers.Add("TXAC contains nonzero extended texture-animation data that has no verified Wrath translation yet.");
            }
            var unexpected = chunks.Where(chunk => chunk.Id is not "MD21" and not "SFID" and not "TXID" and not "TXAC").Select(chunk => chunk.Id).Distinct().Order().ToArray();
            if (unexpected.Length > 0) blockers.Add($"Modern semantic chunk(s) are outside the static profile: {string.Join(", ", unexpected)}.");
            var md21 = chunks.Where(chunk => chunk.Id == "MD21").ToArray();
            if (md21.Length != 1) blockers.Add($"Expected exactly one MD21 payload chunk; found {md21.Length:N0}.");
            else if (md21[0].Size > int.MaxValue || md21[0].DataOffset + md21[0].Size > source.LongLength) blockers.Add("MD21 payload range is invalid.");
            else
            {
                payload = source.AsSpan(checked((int)md21[0].DataOffset), checked((int)md21[0].Size)).ToArray();
                if (payload.Length < 0x130 || FourCc(payload, 0) != "MD20") blockers.Add("MD21 does not contain a complete unwrapped MD20 header.");
                else
                {
                    version = U32(payload, 4); flags = U32(payload, 0x10);
                    if (version != ModernVersion) blockers.Add($"Static downport currently requires modern M2 version {ModernVersion}; found {version}.");
                    if (flags != SupportedModernFlags) blockers.Add($"Static downport currently requires verified global flags 0x{SupportedModernFlags:X}; found 0x{flags:X}.");
                    vertices = Count(payload, 0x3C, "vertex", blockers);
                    ValidateArray(payload, 0x3C, 0x40, 48, "vertices", blockers);
                    var viewCount = Count(payload, 0x44, "skin profile", blockers);
                    if (viewCount != 1) blockers.Add($"Static profile requires exactly one skin profile; found {viewCount:N0}.");
                    RequireZero(payload, 0x14, "global sequences", blockers);
                    var animationCount = Count(payload, 0x1C, "animation", blockers);
                    if (animationCount > 1) blockers.Add($"Static profile supports at most one embedded animation; found {animationCount:N0}.");
                    if (animationCount > 0)
                    {
                        ValidateArray(payload, 0x1C, 0x20, 64, "animations", blockers);
                        var animationOffset = Offset(payload, 0x20, "animations", blockers);
                        if (animationOffset >= 0 && animationOffset + 2 <= payload.Length && U16(payload, animationOffset) != 0)
                            blockers.Add($"Static profile accepts only animation ID 0 (Stand); found {U16(payload, animationOffset)}.");
                    }
                    ValidateArray(payload, 0x2C, 0x30, 88, "bones", blockers);
                    RequireZero(payload, 0x48, "color tracks", blockers);
                    RequireZero(payload, 0x60, "texture transforms", blockers);
                    RequireZero(payload, 0xF0, "attachments", blockers);
                    RequireZero(payload, 0x100, "events", blockers);
                    RequireZero(payload, 0x108, "lights", blockers);
                    RequireZero(payload, 0x110, "cameras", blockers);
                    RequireZero(payload, 0x120, "ribbon emitters", blockers);
                    RequireZero(payload, 0x128, "particle emitters", blockers);
                    if (Count(payload, 0x88, "texture-coordinate lookup", blockers) != 0) blockers.Add("Modern texture-coordinate lookup is not empty; the static profile only synthesizes the verified primary-UV lookup.");

                    var textureCount = Count(payload, 0x50, "texture", blockers);
                    ValidateArray(payload, 0x50, 0x54, 16, "textures", blockers);
                    var textureOffset = Offset(payload, 0x54, "textures", blockers);
                    if (textureOffset >= 0)
                        for (var index = 0; index < textureCount && textureOffset + index * 16 + 16 <= payload.Length; index++)
                            if (U32(payload, textureOffset + index * 16) == 0) blockers.Add($"Texture {index:N0} uses an embedded filename; modern FileDataID-to-client-path reconstruction is not implemented yet.");
                    ValidateArray(payload, 0x70, 0x74, 4, "render flags", blockers);
                    var renderCount = Count(payload, 0x70, "render flag", blockers); var renderOffset = Offset(payload, 0x74, "render flags", blockers);
                    if (renderOffset >= 0)
                        for (var index = 0; index < renderCount && renderOffset + index * 4 + 4 <= payload.Length; index++)
                            if (U16(payload, renderOffset + index * 4 + 2) > 7) blockers.Add($"Render flag {index:N0} uses unsupported blend mode {U16(payload, renderOffset + index * 4 + 2)}.");
                    ValidateArray(payload, 0x80, 0x84, 2, "texture lookup", blockers);

                    var txid = chunks.Where(chunk => chunk.Id == "TXID").ToArray();
                    if (txid.Length != 1) blockers.Add($"Expected exactly one TXID chunk; found {txid.Length:N0}.");
                    else
                    {
                        if (txid[0].Size != checked((uint)textureCount * 4u)) blockers.Add($"TXID has {txid[0].Size / 4:N0} entries but the model has {textureCount:N0} texture definitions.");
                        else for (long cursor = txid[0].DataOffset; cursor < txid[0].DataOffset + txid[0].Size; cursor += 4)
                            if (U32(source, checked((int)cursor)) != 0) { blockers.Add("TXID contains nonzero external texture FileDataIDs that require a listfile/source mapping."); break; }
                    }
                    var sfid = chunks.Where(chunk => chunk.Id == "SFID").ToArray();
                    if (sfid.Length != 1 || sfid[0].Size != 4) blockers.Add("Static profile requires one four-byte SFID skin reference.");
                }
            }
        }

        sourceSkinPath = ResolveSkin(sourceModelPath, sourceSkinPath);
        if (sourceSkinPath is null) blockers.Add("The conventional companion <model>00.skin file was not found; pass it explicitly.");
        else
        {
            var skinBytes = File.ReadAllBytes(sourceSkinPath); skinHash = Hash(skinBytes);
            skin = ParseModernSkin(skinBytes, blockers);
            if (skin is not null)
            {
                triangles = skin.TriangleIndexCount / 3; submeshes = skin.SubmeshCount; materials = skin.MaterialCount; shadows = skin.ShadowCount;
                if (payload is not null && vertices > 0) ValidateSkinReferences(skin, skinBytes, payload, vertices, blockers);
                if (shadows > 0) losses.Add($"Wrath SKIN v2 has no shadow-batch array; {shadows:N0} modern shadow-batch record(s) will be omitted. Vertex, triangle, submesh, and material arrays remain byte-preserved.");
            }
        }

        transformations.Add("Unwrap the MD21 container into its embedded MD20 payload.");
        transformations.Add($"Translate M2 version {ModernVersion} to Wrath version {WotlkVersion} after structural validation.");
        transformations.Add("Clear verified modern FileDataID/LOD flags only after proving TXID values are zero and exactly one SKIN is present.");
        transformations.Add("Append the missing primary-UV texture-coordinate lookup used by the verified single-stage SKIN batches.");
        transformations.Add("Repack modern SKIN v3 common arrays into the Wrath SKIN v2 header without changing their contents.");
        if (omittedEmptyTxac) transformations.Add("Omit the proven zero-filled TXAC extension chunk; it contains no texture-animation values to translate.");
        return new(PlanFormatVersion, DateTimeOffset.UtcNow, sourceModelPath, modelHash, sourceSkinPath, skinHash, version, flags,
            flags & ~SupportedModernFlags, vertices, triangles, submeshes, materials, shadows, transformations, losses, blockers.Distinct().ToArray());
    }

    public static StaticM2DownportResult Convert(StaticM2DownportPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException($"Unsupported static M2 plan version {plan.FormatVersion}.");
        cancellationToken.ThrowIfCancellationRequested();
        var current = Plan(plan.SourceModelPath, plan.SourceSkinPath);
        if (!current.SourceModelSha256.Equals(plan.SourceModelSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.SourceSkinSha256, plan.SourceSkinSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source M2 or SKIN changed after planning; create a fresh conversion plan.");
        if (!current.Ready) throw new InvalidOperationException("Static M2 downport is blocked:\n- " + string.Join("\n- ", current.Blockers));
        if (current.SourceSkinPath is null) throw new InvalidOperationException("Conversion plan has no companion SKIN.");

        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Conversion output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Conversion output has no parent folder."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".crucible-static-m2-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelSource = File.ReadAllBytes(current.SourceModelPath); var chunks = ReadChunks(modelSource, []); var md21 = chunks.Single(chunk => chunk.Id == "MD21");
            var payload = modelSource.AsSpan(checked((int)md21.DataOffset), checked((int)md21.Size)).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), WotlkVersion); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10, 4), current.OutputFlags);
            var coordinateOffset = Align(payload.Length, 2); Array.Resize(ref payload, coordinateOffset + 2); BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(coordinateOffset, 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x88, 4), 1); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x8C, 4), checked((uint)coordinateOffset));

            var skinSource = File.ReadAllBytes(current.SourceSkinPath); var skin = ParseModernSkin(skinSource, []) ?? throw new InvalidDataException("Companion SKIN changed into an invalid layout.");
            var outputSkin = BuildWotlkSkin(skinSource, skin);
            var modelName = Path.GetFileName(current.SourceModelPath); var skinName = Path.GetFileNameWithoutExtension(current.SourceModelPath) + "00.skin";
            var stagedModel = Path.Combine(staging, modelName); var stagedSkin = Path.Combine(staging, skinName); File.WriteAllBytes(stagedModel, payload); File.WriteAllBytes(stagedSkin, outputSkin);
            cancellationToken.ThrowIfCancellationRequested();

            var inspection = NativeAssetConversionService.Inspect(stagedModel);
            if (inspection.Compatibility != AssetCompatibility.AlreadyWotlk335) throw new InvalidDataException("Converted model did not pass Crucible's Wrath M2 inspection.");
            var geometry = M2PreviewGeometryService.Load(stagedModel, stagedSkin, M2PreviewVisibilityMode.AllGeosets);
            if (geometry.Vertices.Count != current.VertexCount || geometry.TotalTriangleIndices / 3 != current.TriangleCount || geometry.Submeshes.Count != current.SubmeshCount || geometry.MaterialUnits.Count != current.MaterialCount)
                throw new InvalidDataException("Converted M2/SKIN geometry or material counts differ from the immutable plan.");

            var finalModel = Path.Combine(outputDirectory, modelName); var finalSkin = Path.Combine(outputDirectory, skinName); var receipt = Path.Combine(outputDirectory, "conversion-receipt.json");
            var result = new StaticM2DownportResult(ReceiptFormatVersion, DateTimeOffset.UtcNow, current, outputDirectory, finalModel, Hash(payload), finalSkin, Hash(outputSkin), receipt,
                geometry.Vertices.Count, geometry.TotalTriangleIndices / 3, geometry.Submeshes.Count, geometry.MaterialUnits.Count);
            File.WriteAllText(Path.Combine(staging, "conversion-receipt.json"), JsonSerializer.Serialize(result, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory);
            Directory.Move(staging, outputDirectory);
            return result;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static ModernSkin? ParseModernSkin(byte[] data, List<string> blockers)
    {
        if (data.Length < 56 || FourCc(data, 0) != "SKIN") { blockers.Add("Companion file is not a complete modern SKIN v3 container."); return null; }
        var result = new ModernSkin(
            Count(data, 4, "SKIN vertex lookup", blockers), Offset(data, 8, "SKIN vertex lookup", blockers),
            Count(data, 12, "SKIN triangle index", blockers), Offset(data, 16, "SKIN triangles", blockers),
            Count(data, 20, "SKIN bone property", blockers), Offset(data, 24, "SKIN bone properties", blockers),
            Count(data, 28, "SKIN submesh", blockers), Offset(data, 32, "SKIN submeshes", blockers),
            Count(data, 36, "SKIN material", blockers), Offset(data, 40, "SKIN materials", blockers),
            U32(data, 44), Count(data, 48, "SKIN shadow batch", blockers), Offset(data, 52, "SKIN shadow batches", blockers));
        ValidateRange(data, result.LookupOffset, result.LookupCount, 2, "SKIN vertex lookup", blockers);
        ValidateRange(data, result.TriangleOffset, result.TriangleIndexCount, 2, "SKIN triangles", blockers);
        ValidateRange(data, result.PropertyOffset, result.PropertyCount, 4, "SKIN bone properties", blockers);
        ValidateRange(data, result.SubmeshOffset, result.SubmeshCount, 48, "SKIN submeshes", blockers);
        ValidateRange(data, result.MaterialOffset, result.MaterialCount, 24, "SKIN materials", blockers);
        ValidateRange(data, result.ShadowOffset, result.ShadowCount, 12, "SKIN shadow batches", blockers);
        if (result.TriangleIndexCount % 3 != 0) blockers.Add($"SKIN triangle index count {result.TriangleIndexCount:N0} is not divisible by three.");
        return result;
    }

    private static void ValidateSkinReferences(ModernSkin skin, byte[] data, byte[] model, int vertexCount, List<string> blockers)
    {
        if (!RangesValid(skin, data.Length)) return;
        for (var index = 0; index < skin.LookupCount; index++) if (U16(data, skin.LookupOffset + index * 2) >= vertexCount) { blockers.Add($"SKIN vertex lookup {index:N0} exceeds the model's {vertexCount:N0} vertices."); break; }
        for (var index = 0; index < skin.TriangleIndexCount; index++) if (U16(data, skin.TriangleOffset + index * 2) >= skin.LookupCount) { blockers.Add($"SKIN triangle index {index:N0} exceeds the {skin.LookupCount:N0} vertex-lookup entries."); break; }
        for (var index = 0; index < skin.SubmeshCount; index++)
        {
            var offset = skin.SubmeshOffset + index * 48; var vertexStart = U16(data, offset + 4); var count = U16(data, offset + 6); var triangleStart = U16(data, offset + 8); var triangleCount = U16(data, offset + 10);
            if (vertexStart + count > skin.LookupCount) blockers.Add($"SKIN submesh {index:N0} vertex range exceeds the lookup table.");
            if (triangleStart + triangleCount > skin.TriangleIndexCount) blockers.Add($"SKIN submesh {index:N0} triangle range exceeds the triangle table.");
        }
        var renderCount = Count(model, 0x70, "render flag", blockers); var textureLookupCount = Count(model, 0x80, "texture lookup", blockers);
        for (var index = 0; index < skin.MaterialCount; index++)
        {
            var offset = skin.MaterialOffset + index * 24; var shader = U16(data, offset + 2); var submesh = U16(data, offset + 4); var submesh2 = U16(data, offset + 6); var render = U16(data, offset + 10); var stages = U16(data, offset + 14); var textureCombo = U16(data, offset + 16); var coordinateCombo = U16(data, offset + 18);
            if (shader is not 0 and not 16) blockers.Add($"SKIN material {index:N0} uses shader {shader}; the static profile accepts verified single-stage packed shaders 0 (Opaque) and 16 (Mod) only.");
            if (submesh >= skin.SubmeshCount || submesh2 >= skin.SubmeshCount) blockers.Add($"SKIN material {index:N0} references a missing submesh.");
            if (render >= renderCount) blockers.Add($"SKIN material {index:N0} references missing render flag {render:N0}.");
            if (stages != 1) blockers.Add($"SKIN material {index:N0} declares {stages:N0} texture stages; the first static profile requires one.");
            if (textureCombo >= textureLookupCount) blockers.Add($"SKIN material {index:N0} references missing texture lookup {textureCombo:N0}.");
            if (coordinateCombo != 0) blockers.Add($"SKIN material {index:N0} references texture-coordinate lookup {coordinateCombo:N0}; only the synthesized primary-UV entry 0 is supported.");
        }
    }

    private static byte[] BuildWotlkSkin(byte[] source, ModernSkin skin)
    {
        using var stream = new MemoryStream(); stream.Write(new byte[48]);
        var lookup = CopySection(stream, source, skin.LookupOffset, checked(skin.LookupCount * 2));
        var triangles = CopySection(stream, source, skin.TriangleOffset, checked(skin.TriangleIndexCount * 2));
        var properties = CopySection(stream, source, skin.PropertyOffset, checked(skin.PropertyCount * 4));
        var submeshes = CopySection(stream, source, skin.SubmeshOffset, checked(skin.SubmeshCount * 48));
        var materials = CopySection(stream, source, skin.MaterialOffset, checked(skin.MaterialCount * 24));
        var result = stream.ToArray(); Encoding.ASCII.GetBytes("SKIN").CopyTo(result, 0);
        WritePair(result, 4, skin.LookupCount, lookup); WritePair(result, 12, skin.TriangleIndexCount, triangles); WritePair(result, 20, skin.PropertyCount, properties);
        WritePair(result, 28, skin.SubmeshCount, submeshes); WritePair(result, 36, skin.MaterialCount, materials); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44, 4), skin.BoneCountMax);
        return result;
    }

    private static int CopySection(MemoryStream destination, byte[] source, int offset, int length)
    {
        while (destination.Position % 16 != 0) destination.WriteByte(0); var result = checked((int)destination.Position); destination.Write(source, offset, length); return result;
    }
    private static void WritePair(byte[] data, int offset, int count, int location) { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), checked((uint)count)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), checked((uint)location)); }

    private static IReadOnlyList<Chunk> ReadChunks(byte[] data, List<string> blockers)
    {
        var result = new List<Chunk>(); long offset = 0;
        while (offset < data.LongLength)
        {
            if (data.LongLength - offset < 8) { blockers.Add($"Trailing {data.LongLength - offset:N0} byte(s) cannot form a chunk header."); break; }
            var id = FourCc(data, checked((int)offset)); var size = U32(data, checked((int)offset + 4)); var end = offset + 8L + size;
            if (!id.All(character => character is >= ' ' and <= '~')) { blockers.Add($"Invalid chunk signature at byte {offset:N0}."); break; }
            if (end > data.LongLength) { blockers.Add($"Chunk {id} extends beyond end of file."); break; }
            result.Add(new(id, offset, offset + 8, size)); offset = end;
        }
        return result;
    }

    private static string? ResolveSkin(string modelPath, string? skinPath)
    {
        if (!string.IsNullOrWhiteSpace(skinPath))
        {
            var resolved = Path.IsPathRooted(skinPath) ? skinPath : Path.Combine(Path.GetDirectoryName(modelPath)!, skinPath);
            resolved = Path.GetFullPath(resolved); if (!File.Exists(resolved)) throw new FileNotFoundException("The supplied modern SKIN does not exist.", resolved); return resolved;
        }
        var expected = Path.GetFileNameWithoutExtension(modelPath) + "00.skin";
        return Directory.EnumerateFiles(Path.GetDirectoryName(modelPath)!, "*.skin", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireZero(byte[] data, int countOffset, string label, List<string> blockers) { var count = Count(data, countOffset, label, blockers); if (count != 0) blockers.Add($"Static profile requires zero {label}; found {count:N0}."); }
    private static void ValidateArray(byte[] data, int countOffset, int offsetOffset, int stride, string label, List<string> blockers) => ValidateRange(data, Offset(data, offsetOffset, label, blockers), Count(data, countOffset, label, blockers), stride, label, blockers);
    private static void ValidateRange(byte[] data, int offset, int count, int stride, string label, List<string> blockers)
    {
        if (offset < 0 || count < 0) return; var bytes = (long)count * stride;
        if (offset > data.Length || bytes > data.Length - (long)offset) blockers.Add($"{label} range ({count:N0} × {stride:N0} bytes at {offset:N0}) exceeds the containing file.");
    }
    private static int Count(byte[] data, int offset, string label, List<string> blockers)
    {
        if (offset < 0 || offset + 4 > data.Length) { blockers.Add($"Missing {label} count."); return 0; }
        var value = U32(data, offset); if (value > MaximumArrayCount) { blockers.Add($"{label} count {value:N0} exceeds the safety limit."); return 0; } return checked((int)value);
    }
    private static int Offset(byte[] data, int offset, string label, List<string> blockers)
    {
        if (offset < 0 || offset + 4 > data.Length) { blockers.Add($"Missing {label} offset."); return -1; }
        var value = U32(data, offset); if (value > int.MaxValue) { blockers.Add($"{label} offset exceeds the supported file range."); return -1; } return checked((int)value);
    }
    private static bool RangesValid(ModernSkin skin, int length) => new[] { (skin.LookupOffset, skin.LookupCount, 2), (skin.TriangleOffset, skin.TriangleIndexCount, 2), (skin.PropertyOffset, skin.PropertyCount, 4), (skin.SubmeshOffset, skin.SubmeshCount, 48), (skin.MaterialOffset, skin.MaterialCount, 24), (skin.ShadowOffset, skin.ShadowCount, 12) }.All(value => value.Item1 >= 0 && (long)value.Item1 + (long)value.Item2 * value.Item3 <= length);
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static string FourCc(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);
    private static string Hash(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data));
    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private sealed record Chunk(string Id, long Offset, long DataOffset, uint Size);
    private sealed record ModernSkin(int LookupCount, int LookupOffset, int TriangleIndexCount, int TriangleOffset, int PropertyCount, int PropertyOffset,
        int SubmeshCount, int SubmeshOffset, int MaterialCount, int MaterialOffset, uint BoneCountMax, int ShadowCount, int ShadowOffset);
}
