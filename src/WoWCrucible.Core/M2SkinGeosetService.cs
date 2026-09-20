using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace WoWCrucible.Core;

public enum M2SkinLayout { Wrath, CataclysmToLegion, Modern }

public sealed record M2SkinGeosetProfile([property: JsonRequired] M2SkinLayout Layout,
    [property: JsonRequired] IReadOnlyDictionary<ushort, ushort> Geosets);
public sealed record M2SkinSectionRemap(int SourceIndex, ushort SourceGeoset, int? OutputIndex, ushort? OutputGeoset);
public sealed record M2SkinGeosetResult(byte[] SkinData, IReadOnlyList<M2SkinSectionRemap> Sections,
    int TriangleIndices, int Batches, int ShadowBatches);

/// <summary>Retains and remaps explicitly selected SKIN geosets without altering M2 vertices, bones or materials.</summary>
public static class M2SkinGeosetService
{
    public static M2SkinGeosetResult Rewrite(byte[] skin, M2SkinGeosetProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.Geosets);
        if (!Enum.IsDefined(profile.Layout)) throw new ArgumentOutOfRangeException(nameof(profile));
        cancellationToken.ThrowIfCancellationRequested();
        var headerSize = profile.Layout == M2SkinLayout.Wrath ? 48 : 56;
        if (skin.Length < headerSize || !skin.AsSpan(0, 4).SequenceEqual("SKIN"u8))
            throw new InvalidDataException("Expected an external SKIN file with the selected layout.");
        var ranges = new List<(int Start, int End)>();
        var vertices = ReadArray(4, 2);
        var indices = ReadArray(12, 2);
        var properties = ReadArray(20, 4);
        var sections = ReadArray(28, 48);
        var batches = ReadArray(36, 24);
        var shadows = headerSize == 56 ? ReadArray(48, 12) : [];
        var vertexCount = vertices.Length / 2;
        var sectionCount = sections.Length / 48;
        if (sectionCount > ushort.MaxValue + 1 || properties.Length != 0 && properties.Length / 4 != vertexCount)
            throw new InvalidDataException("Invalid SKIN section count or vertex-property count.");
        for (var i = 0; i < indices.Length; i += 2)
            if (U16(indices, i) >= vertexCount) throw new InvalidDataException("A SKIN triangle references a missing vertex lookup.");
        var remaps = new List<M2SkinSectionRemap>(sectionCount);
        var indexMap = Enumerable.Repeat(-1, sectionCount).ToArray();
        using var outputIndices = new MemoryStream();
        using var outputSections = new MemoryStream();
        for (var index = 0; index < sectionCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var section = sections.AsSpan(index * 48, 48).ToArray();
            var geoset = U16(section, 0);
            var vertexEnd = (uint)U16(section, 4) + U16(section, 6);
            var start = ((uint)U16(section, 2) << 16) | U16(section, 8);
            var count = U16(section, 10);
            if (vertexEnd > vertexCount || count % 3 != 0 || (ulong)start + count > (ulong)indices.Length / 2)
                throw new InvalidDataException($"SKIN section {index} has an invalid vertex or triangle range.");
            if (!profile.Geosets.TryGetValue(geoset, out var target))
            {
                remaps.Add(new(index, geoset, null, null));
                continue;
            }
            var outputIndex = checked((int)(outputSections.Length / 48));
            indexMap[index] = outputIndex;
            remaps.Add(new(index, geoset, outputIndex, target));
            var triangleStart = checked((uint)(outputIndices.Length / 2));
            Put16(section, 0, target);
            // Section.level holds the high triangle-offset bits, not the model's LOD number.
            Put16(section, 2, checked((ushort)(triangleStart >> 16)));
            Put16(section, 8, (ushort)(triangleStart & 0xffff));
            outputSections.Write(section);
            outputIndices.Write(indices.AsSpan(checked((int)start * 2), count * 2));
        }
        if (outputSections.Length == 0) throw new InvalidDataException("The geoset profile retains no SKIN sections.");

        var outputBatches = RewriteBatches(batches, 24, profile.Layout != M2SkinLayout.Modern);
        var outputShadows = RewriteBatches(shadows, 12, false);
        var header = skin.AsSpan(0, headerSize).ToArray();
        using var output = new MemoryStream();
        output.Write(header);
        WriteArray(4, 2, vertices);
        WriteArray(12, 2, outputIndices.ToArray());
        WriteArray(20, 4, properties);
        WriteArray(28, 48, outputSections.ToArray());
        WriteArray(36, 24, outputBatches);
        if (headerSize == 56) WriteArray(48, 12, outputShadows);
        output.Position = 0;
        output.Write(header);
        return new(output.ToArray(), remaps, checked((int)(outputIndices.Length / 2)), outputBatches.Length / 24, outputShadows.Length / 12);

        byte[] ReadArray(int field, int stride)
        {
            var count = U32(skin, field);
            var offset = U32(skin, field + 4);
            if (count == 0) return [];
            var end = (ulong)offset + (ulong)count * (uint)stride;
            if (offset < headerSize || end > (ulong)skin.Length)
                throw new InvalidDataException($"SKIN array at header offset {field} lies outside the file.");
            var range = (Start: (int)offset, End: (int)end);
            if (ranges.Any(other => range.Start < other.End && range.End > other.Start))
                throw new InvalidDataException("SKIN arrays overlap.");
            ranges.Add(range);
            return skin.AsSpan(range.Start, range.End - range.Start).ToArray();
        }

        byte[] RewriteBatches(byte[] source, int stride, bool secondaryIndex)
        {
            using var result = new MemoryStream();
            for (var offset = 0; offset < source.Length; offset += stride)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = source.AsSpan(offset, stride).ToArray();
                var primary = U16(batch, 4);
                var secondary = U16(batch, 6);
                if (primary >= sectionCount || secondaryIndex && secondary >= sectionCount)
                    throw new InvalidDataException("A SKIN render/shadow batch references a missing section.");
                if (indexMap[primary] < 0) continue;
                Put16(batch, 4, checked((ushort)indexMap[primary]));
                // BfA+ repurposes the old secondary geoset index as flags2. Never remap those bits.
                if (secondaryIndex)
                {
                    if (indexMap[secondary] < 0)
                        throw new InvalidDataException("A retained render batch references a removed secondary section.");
                    Put16(batch, 6, checked((ushort)indexMap[secondary]));
                }
                result.Write(batch);
            }
            return result.ToArray();
        }

        void WriteArray(int field, int stride, byte[] data)
        {
            while (output.Length % 16 != 0) output.WriteByte(0);
            Put32(header, field, checked((uint)(data.Length / stride)));
            Put32(header, field + 4, data.Length == 0 ? 0 : checked((uint)output.Position));
            output.Write(data);
        }
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
