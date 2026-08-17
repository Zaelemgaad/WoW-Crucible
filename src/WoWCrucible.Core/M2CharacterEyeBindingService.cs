using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WoWCrucible.Core;

public sealed record M2CharacterEyeBindingResult(
    string InputModelPath,
    string InputSkinPath,
    string OutputModelPath,
    string OutputSkinPath,
    string NormalEyeTexture,
    string DeathKnightEyeTexture,
    int OriginalTextureDefinitions,
    int ResultTextureDefinitions,
    int OriginalTextureLookups,
    int ResultTextureLookups,
    int NormalEyeMaterials,
    int DeathKnightEyeMaterials,
    int NormalEyeTextureDefinition,
    int DeathKnightEyeTextureDefinition,
    string OutputModelSha256,
    string OutputSkinSha256);

public static class M2CharacterEyeBindingService
{
    private const int TextureCountOffset = 0x50;
    private const int TextureDataOffset = 0x54;
    private const int TextureStride = 16;
    private const int TextureLookupCountOffset = 0x80;
    private const int TextureLookupDataOffset = 0x84;
    private const int SubmeshCountOffset = 28;
    private const int SubmeshDataOffset = 32;
    private const int SubmeshStride = 48;
    private const int MaterialCountOffset = 36;
    private const int MaterialDataOffset = 40;
    private const int MaterialStride = 24;
    private const ushort NormalEyeGeoset = 1702;
    private const ushort DeathKnightEyeGeoset = 1703;

    private readonly record struct TextureDefinition(uint Type, uint Flags, uint NameLength, uint NameOffset, string? Path);

    public static M2CharacterEyeBindingResult Apply(
        string inputModelPath,
        string inputSkinPath,
        string outputModelPath,
        string outputSkinPath,
        string normalEyeTexture,
        string deathKnightEyeTexture,
        bool overwrite = false)
    {
        var inputModel = Path.GetFullPath(inputModelPath);
        var inputSkin = Path.GetFullPath(inputSkinPath);
        var outputModel = Path.GetFullPath(outputModelPath);
        var outputSkin = Path.GetFullPath(outputSkinPath);
        if (!File.Exists(inputModel)) throw new FileNotFoundException("The source M2 model does not exist.", inputModel);
        if (!File.Exists(inputSkin)) throw new FileNotFoundException("The source SKIN file does not exist.", inputSkin);
        if (outputModel.Equals(inputModel, StringComparison.OrdinalIgnoreCase) || outputSkin.Equals(inputSkin, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Eye-binding compatibility requires separate output M2 and SKIN paths.");
        if (outputModel.Equals(outputSkin, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The output M2 and SKIN paths must be different.");
        if (!overwrite && (File.Exists(outputModel) || File.Exists(outputSkin)))
            throw new IOException("An eye-binding output already exists. Use --overwrite explicitly.");

        normalEyeTexture = NormalizeTexturePath(normalEyeTexture, nameof(normalEyeTexture));
        deathKnightEyeTexture = NormalizeTexturePath(deathKnightEyeTexture, nameof(deathKnightEyeTexture));
        if (normalEyeTexture.Equals(deathKnightEyeTexture, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Normal and Death Knight eye textures must be different.");

        var sourceModel = File.ReadAllBytes(inputModel);
        var sourceSkin = File.ReadAllBytes(inputSkin);
        ValidateModel(sourceModel);
        ValidateSkin(sourceSkin);

        var definitions = ReadTextureDefinitions(sourceModel);
        var originalDefinitionCount = definitions.Count;
        var model = sourceModel.ToList();
        var normalDefinition = FindOrAppendDefinition(model, definitions, normalEyeTexture);
        var deathKnightDefinition = FindOrAppendDefinition(model, definitions, deathKnightEyeTexture);

        Align(model, 16);
        var definitionOffset = checked((uint)model.Count);
        foreach (var definition in definitions)
        {
            AppendUInt32(model, definition.Type);
            AppendUInt32(model, definition.Flags);
            AppendUInt32(model, definition.NameLength);
            AppendUInt32(model, definition.NameOffset);
        }

        var textureLookups = ReadTextureLookups(sourceModel, originalDefinitionCount);
        var originalLookupCount = textureLookups.Count;
        var patchedSkin = sourceSkin.ToArray();
        var (normalMaterials, deathKnightMaterials) = PatchMaterials(
            patchedSkin,
            textureLookups,
            normalDefinition,
            deathKnightDefinition);

        Align(model, 2);
        var lookupOffset = checked((uint)model.Count);
        foreach (var lookup in textureLookups) AppendUInt16(model, lookup);

        var patchedModel = model.ToArray();
        WriteUInt32(patchedModel, TextureCountOffset, checked((uint)definitions.Count));
        WriteUInt32(patchedModel, TextureDataOffset, definitionOffset);
        WriteUInt32(patchedModel, TextureLookupCountOffset, checked((uint)textureLookups.Count));
        WriteUInt32(patchedModel, TextureLookupDataOffset, lookupOffset);

        var modelDirectory = Path.GetDirectoryName(outputModel) ?? throw new InvalidOperationException("Output M2 path has no parent directory.");
        var skinDirectory = Path.GetDirectoryName(outputSkin) ?? throw new InvalidOperationException("Output SKIN path has no parent directory.");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(skinDirectory);
        var temporaryModel = Path.Combine(modelDirectory, $".{Path.GetFileName(outputModel)}.{Guid.NewGuid():N}.tmp");
        var temporarySkin = Path.Combine(skinDirectory, $".{Path.GetFileName(outputSkin)}.{Guid.NewGuid():N}.tmp");
        try
        {
            WriteThrough(temporaryModel, patchedModel);
            WriteThrough(temporarySkin, patchedSkin);
            VerifyBindings(temporaryModel, temporarySkin, normalEyeTexture, deathKnightEyeTexture);
            File.Move(temporaryModel, outputModel, overwrite);
            File.Move(temporarySkin, outputSkin, overwrite);
        }
        finally
        {
            if (File.Exists(temporaryModel)) File.Delete(temporaryModel);
            if (File.Exists(temporarySkin)) File.Delete(temporarySkin);
        }

        return new(
            inputModel,
            inputSkin,
            outputModel,
            outputSkin,
            normalEyeTexture,
            deathKnightEyeTexture,
            originalDefinitionCount,
            definitions.Count,
            originalLookupCount,
            textureLookups.Count,
            normalMaterials,
            deathKnightMaterials,
            normalDefinition,
            deathKnightDefinition,
            Sha256(outputModel),
            Sha256(outputSkin));
    }

    private static List<TextureDefinition> ReadTextureDefinitions(byte[] model)
    {
        var count = CheckedCount(ReadUInt32(model, TextureCountOffset), 4096, "M2 texture definition");
        var offset = CheckedOffset(ReadUInt32(model, TextureDataOffset), "M2 texture definition");
        RequireRange(model, offset, count, TextureStride, "M2 texture definitions");
        var result = new List<TextureDefinition>(count + 2);
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * TextureStride;
            var type = ReadUInt32(model, item);
            var flags = ReadUInt32(model, item + 4);
            var nameLength = ReadUInt32(model, item + 8);
            var nameOffset = ReadUInt32(model, item + 12);
            string? path = null;
            if (type == 0 && nameLength > 0)
            {
                var length = CheckedCount(nameLength, 1024 * 1024, "M2 texture name");
                var start = CheckedOffset(nameOffset, "M2 texture name");
                RequireRange(model, start, length, 1, "M2 texture name");
                path = Encoding.UTF8.GetString(model, start, length).TrimEnd('\0').Replace('/', '\\');
            }
            result.Add(new(type, flags, nameLength, nameOffset, path));
        }
        return result;
    }

    private static List<ushort> ReadTextureLookups(byte[] model, int definitionCount)
    {
        var count = CheckedCount(ReadUInt32(model, TextureLookupCountOffset), ushort.MaxValue + 1, "M2 texture lookup");
        var offset = CheckedOffset(ReadUInt32(model, TextureLookupDataOffset), "M2 texture lookup");
        RequireRange(model, offset, count, 2, "M2 texture lookups");
        var result = new List<ushort>(count + 4);
        for (var index = 0; index < count; index++)
        {
            var value = ReadUInt16(model, offset + index * 2);
            if (value >= definitionCount)
                throw new InvalidDataException($"M2 texture lookup {index:N0} references definition {value:N0}, but only {definitionCount:N0} definitions exist.");
            result.Add(value);
        }
        return result;
    }

    private static int FindOrAppendDefinition(List<byte> model, List<TextureDefinition> definitions, string path)
    {
        var existing = definitions.FindIndex(definition =>
            definition.Type == 0 &&
            string.Equals(definition.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) return existing;
        if (definitions.Count >= ushort.MaxValue)
            throw new InvalidDataException("The M2 texture-definition table cannot accept another SKIN-addressable entry.");

        var name = Encoding.ASCII.GetBytes(path + "\0");
        var offset = checked((uint)model.Count);
        model.AddRange(name);
        definitions.Add(new(0, 0, checked((uint)name.Length), offset, path));
        return definitions.Count - 1;
    }

    private static (int Normal, int DeathKnight) PatchMaterials(
        byte[] skin,
        List<ushort> lookups,
        int normalDefinition,
        int deathKnightDefinition)
    {
        var submeshCount = CheckedCount(ReadUInt32(skin, SubmeshCountOffset), 131_072, "SKIN submesh");
        var submeshOffset = CheckedOffset(ReadUInt32(skin, SubmeshDataOffset), "SKIN submesh");
        RequireRange(skin, submeshOffset, submeshCount, SubmeshStride, "SKIN submeshes");
        var geosets = new ushort[submeshCount];
        for (var index = 0; index < submeshCount; index++)
            geosets[index] = ReadUInt16(skin, submeshOffset + index * SubmeshStride);

        var materialCount = CheckedCount(ReadUInt32(skin, MaterialCountOffset), 131_072, "SKIN material");
        var materialOffset = CheckedOffset(ReadUInt32(skin, MaterialDataOffset), "SKIN material");
        RequireRange(skin, materialOffset, materialCount, MaterialStride, "SKIN materials");
        var sequenceLookup = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var normal = 0;
        var deathKnight = 0;
        for (var index = 0; index < materialCount; index++)
        {
            var item = materialOffset + index * MaterialStride;
            var submeshIndex = ReadUInt16(skin, item + 4);
            if (submeshIndex >= geosets.Length)
                throw new InvalidDataException($"SKIN material {index:N0} references submesh {submeshIndex:N0}, but only {geosets.Length:N0} exist.");
            var geoset = geosets[submeshIndex];
            if (geoset is not (NormalEyeGeoset or DeathKnightEyeGeoset)) continue;

            var stageCount = ReadUInt16(skin, item + 14);
            if (stageCount == 0)
                throw new InvalidDataException($"Eye material {index:N0} on geoset {geoset:N0} has no texture stages.");
            var originalStart = ReadUInt16(skin, item + 16);
            if ((long)originalStart + stageCount > lookups.Count)
                throw new InvalidDataException(
                    $"Eye material {index:N0} references lookup range {originalStart:N0}+{stageCount:N0}, but only {lookups.Count:N0} entries exist.");

            var sequence = lookups.Skip(originalStart).Take(stageCount).ToArray();
            sequence[0] = checked((ushort)(geoset == NormalEyeGeoset ? normalDefinition : deathKnightDefinition));
            var key = string.Join(',', sequence);
            if (!sequenceLookup.TryGetValue(key, out var newStart))
            {
                newStart = FindOrAppendLookupSequence(lookups, sequence);
                sequenceLookup.Add(key, newStart);
            }
            WriteUInt16(skin, item + 16, newStart);
            if (geoset == NormalEyeGeoset) normal++; else deathKnight++;
        }

        if (normal == 0 || deathKnight == 0)
            throw new InvalidDataException(
                $"The SKIN must expose both eye material surfaces: normal={normal:N0}, Death Knight={deathKnight:N0}.");
        return (normal, deathKnight);
    }

    private static ushort FindOrAppendLookupSequence(List<ushort> lookups, IReadOnlyList<ushort> sequence)
    {
        for (var start = 0; start + sequence.Count <= lookups.Count && start <= ushort.MaxValue; start++)
        {
            var match = true;
            for (var index = 0; index < sequence.Count; index++)
            {
                if (lookups[start + index] == sequence[index]) continue;
                match = false;
                break;
            }
            if (match) return checked((ushort)start);
        }

        if (lookups.Count > ushort.MaxValue || (long)lookups.Count + sequence.Count > ushort.MaxValue + 1L)
            throw new InvalidDataException("The M2 texture-lookup table cannot accept the patched eye-material sequence.");
        var result = checked((ushort)lookups.Count);
        lookups.AddRange(sequence);
        return result;
    }

    private static void VerifyBindings(string modelPath, string skinPath, string normalPath, string deathKnightPath)
    {
        var geometry = M2PreviewGeometryService.Load(modelPath, skinPath, M2PreviewVisibilityMode.AllGeosets);
        Verify(NormalEyeGeoset, normalPath);
        Verify(DeathKnightEyeGeoset, deathKnightPath);

        void Verify(ushort geoset, string expectedPath)
        {
            var materials = geometry.MaterialUnits.Where(material =>
                material.SubmeshIndex < geometry.Submeshes.Count &&
                geometry.Submeshes[material.SubmeshIndex].GeosetId == geoset).ToArray();
            if (materials.Length == 0)
                throw new InvalidDataException($"Written model has no material for eye geoset {geoset:N0}.");
            foreach (var material in materials)
            {
                if (material.TextureDefinitionIndex < 0 || material.TextureDefinitionIndex >= geometry.TextureSlots.Count)
                    throw new InvalidDataException($"Written eye material {material.Index:N0} has no valid first texture definition.");
                var actual = geometry.TextureSlots[material.TextureDefinitionIndex].EmbeddedPath;
                if (!string.Equals(actual, expectedPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Written eye material {material.Index:N0} resolves '{actual ?? "<external>"}' instead of '{expectedPath}'.");
            }
        }
    }

    private static string NormalizeTexturePath(string value, string argumentName)
    {
        var path = (value ?? string.Empty).Trim().Replace('/', '\\').TrimStart('\\');
        if (path.Length == 0 || path.IndexOf('\0') >= 0 || !path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Eye texture must be a non-empty client-relative .blp path.", argumentName);
        if (path.Any(character => character > 0x7F))
            throw new ArgumentException("Wrath M2 embedded texture paths must use ASCII characters.", argumentName);
        return path;
    }

    private static void ValidateModel(byte[] bytes)
    {
        if (bytes.Length < TextureLookupDataOffset + 4 || Encoding.ASCII.GetString(bytes, 0, 4) != "MD20" || ReadUInt32(bytes, 4) != 264)
            throw new InvalidDataException("Eye binding requires an unwrapped Wrath MD20 version 264 model.");
    }

    private static void ValidateSkin(byte[] bytes)
    {
        if (bytes.Length < MaterialDataOffset + 4 || Encoding.ASCII.GetString(bytes, 0, 4) != "SKIN")
            throw new InvalidDataException("Eye binding requires a Wrath SKIN companion file.");
    }

    private static int CheckedCount(uint value, int maximum, string label)
    {
        if (value > maximum) throw new InvalidDataException($"{label} count {value:N0} exceeds the safety limit {maximum:N0}.");
        return checked((int)value);
    }

    private static int CheckedOffset(uint value, string label)
    {
        if (value > int.MaxValue) throw new InvalidDataException($"{label} offset 0x{value:X8} exceeds the supported range.");
        return (int)value;
    }

    private static void RequireRange(byte[] bytes, int offset, int count, int stride, string label)
    {
        if (offset < 0 || count < 0 || stride < 0 || (long)offset + (long)count * stride > bytes.Length)
            throw new InvalidDataException($"{label} range is outside the file.");
    }

    private static uint ReadUInt32(byte[] bytes, int offset)
    {
        RequireRange(bytes, offset, 1, 4, "UInt32");
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    private static ushort ReadUInt16(byte[] bytes, int offset)
    {
        RequireRange(bytes, offset, 1, 2, "UInt16");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    }

    private static void WriteUInt32(byte[] bytes, int offset, uint value)
    {
        RequireRange(bytes, offset, 1, 4, "UInt32");
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value)
    {
        RequireRange(bytes, offset, 1, 2, "UInt16");
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
    }

    private static void AppendUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void AppendUInt16(List<byte> bytes, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void Align(List<byte> bytes, int alignment)
    {
        while (bytes.Count % alignment != 0) bytes.Add(0);
    }

    private static void WriteThrough(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
