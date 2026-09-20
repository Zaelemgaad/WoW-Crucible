using System.Buffers.Binary;
using WoWCrucible.Core;

internal static class M2SkinGeosetTestSuite
{
    public static void Run()
    {
        Expect<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<M2SkinGeosetProfile>("{\"Geosets\":{\"0\":0}}"));
        foreach (var layout in Enum.GetValues<M2SkinLayout>())
        {
            var input = Fixture(layout);
            var original = input.ToArray();
            var profile = new M2SkinGeosetProfile(layout, new Dictionary<ushort, ushort> { [0] = 0, [3202] = 0 });
            var result = M2SkinGeosetService.Rewrite(input, profile);
            Require(input.SequenceEqual(original), "Source SKIN must not be mutated.");
            Require(result.Sections.Count == 4 && result.Sections[1].OutputIndex is null && result.Sections[3].OutputIndex == 2,
                "Remove the extra foot while retaining both sections of the chosen face.");
            Require(result.Batches == 3 && result.ShadowBatches == (layout == M2SkinLayout.Wrath ? 0 : 3), "Render and shadow batches follow the same section map.");
            var output = result.SkinData;
            Require(Array(input, 4, 2).SequenceEqual(Array(output, 4, 2)) && Array(input, 20, 4).SequenceEqual(Array(output, 20, 4)),
                "Vertex lookup and skinning properties must be byte-preserved.");
            Require(result.TriangleIndices == 9 && Array(output, 12, 2).SequenceEqual(new byte[] { 0, 0, 1, 0, 2, 0, 2, 0, 0, 0, 1, 0, 1, 0, 0, 0, 2, 0 }),
                "Kept triangles remain in section order with their original winding.");
            var sections = Array(output, 28, 48);
            var batches = Array(output, 36, 24);
            var shadows = layout == M2SkinLayout.Wrath ? [] : Array(output, 48, 12);
            for (var index = 0; index < 3; index++)
            {
                Require(U16(sections, index * 48) == 0 && U16(sections, index * 48 + 8) == index * 3, "Baked base IDs and compacted triangle offsets.");
                Require(U16(batches, index * 24 + 4) == index, "Render section references remapped.");
                Require(U16(batches, index * 24 + 6) == (layout == M2SkinLayout.Modern ? 0x800a : index), "Modern flags stay intact; legacy secondary indices are remapped.");
                Require(U16(batches, index * 24 + 8) == ushort.MaxValue && U16(batches, index * 24 + 10) == 123, "Non-section material fields remain intact.");
                if (shadows.Length > 0)
                    Require(U16(shadows, index * 12 + 4) == index && U16(shadows, index * 12 + 6) == 456, "Shadow section references change, texture references do not.");
            }
            var invalid = input.ToArray();
            Put32(invalid, 32, uint.MaxValue);
            Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            invalid = input.ToArray(); Put16(invalid, checked((int)U32(invalid, 40)) + 4, 4);
            Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            invalid = input.ToArray(); Put16(invalid, checked((int)U32(invalid, 16)), 3);
            Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            invalid = input.ToArray(); Put16(invalid, checked((int)U32(invalid, 32)) + 10, 4);
            Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            invalid = input.ToArray(); Put32(invalid, 16, U32(invalid, 8));
            Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            if (layout != M2SkinLayout.Modern)
            {
                invalid = input.ToArray(); Put16(invalid, checked((int)U32(invalid, 40)) + 6, 1);
                Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            }
            if (layout != M2SkinLayout.Wrath)
            {
                invalid = input.ToArray(); Put16(invalid, checked((int)U32(invalid, 52)) + 4, 4);
                Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(invalid, profile));
            }
            Expect<InvalidDataException>(() => M2SkinGeosetService.Rewrite(input, new(layout, new Dictionary<ushort, ushort>())));
            Expect<OperationCanceledException>(() => M2SkinGeosetService.Rewrite(input, profile, new(true)));
        }
        var large = Fixture(M2SkinLayout.Modern, large: true);
        var keep = new M2SkinGeosetProfile(M2SkinLayout.Modern, new Dictionary<ushort, ushort> { [0] = 0, [2003] = 2003, [3202] = 0 });
        var rewritten = M2SkinGeosetService.Rewrite(large, keep).SkinData;
        Require(Array(large, 12, 2).SequenceEqual(Array(rewritten, 12, 2)), "Triangle streams larger than 64K must survive.");
        var highSection = Array(rewritten, 28, 48);
        Require(U16(highSection, 96 + 2) == 1 && U16(highSection, 96 + 8) == 2, "High triangle start bits are emitted, not truncated.");
        Console.WriteLine("PASS SKIN geoset rewrite: explicit selection, multiple sections per ID, render/shadow remaps, legacy vs modern batch layout, >64K triangles, immutable input and malformed-data rejection.");
    }

    private static byte[] Fixture(M2SkinLayout layout, bool large = false)
    {
        using var file = new MemoryStream();
        var header = new byte[layout == M2SkinLayout.Wrath ? 48 : 56]; "SKIN"u8.CopyTo(header);
        Put32(header, 44, 256);
        file.Write(header);
        Write(4, 2, [10, 0, 11, 0, 12, 0]);
        using var triangles = new MemoryStream();
        var sections = new byte[4 * 48];
        var batches = new byte[4 * 24];
        var shadows = new byte[4 * 12];
        ushort[] geosets = [0, 2003, 3202, 3202];
        ushort[][] winding = [[0, 1, 2], [0, 2, 1], [2, 0, 1], [1, 0, 2]];
        for (ushort index = 0; index < 4; index++)
        {
            var start = (uint)(triangles.Length / 2);
            var count = large && index == 0 ? 65535 : 3;
            Put16(sections, index * 48, geosets[index]);
            Put16(sections, index * 48 + 2, (ushort)(start >> 16));
            Put16(sections, index * 48 + 6, 3);
            Put16(sections, index * 48 + 8, (ushort)(start & 0xffff));
            Put16(sections, index * 48 + 10, (ushort)count);
            for (var tri = 0; tri < count; tri++) { triangles.WriteByte((byte)winding[index][tri % 3]); triangles.WriteByte(0); }
            Put16(batches, index * 24 + 4, index);
            Put16(batches, index * 24 + 6, layout == M2SkinLayout.Modern ? (ushort)0x800a : index);
            Put16(batches, index * 24 + 8, ushort.MaxValue);
            Put16(batches, index * 24 + 10, 123);
            Put16(shadows, index * 12 + 4, index);
            Put16(shadows, index * 12 + 6, 456);
        }
        Write(12, 2, triangles.ToArray());
        Write(20, 4, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        Write(28, 48, sections);
        Write(36, 24, batches);
        if (layout != M2SkinLayout.Wrath) Write(48, 12, shadows);
        file.Position = 0; file.Write(header);
        return file.ToArray();
        void Write(int field, int stride, byte[] bytes)
        {
            Put32(header, field, (uint)(bytes.Length / stride)); Put32(header, field + 4, (uint)file.Position); file.Write(bytes);
        }
    }

    private static byte[] Array(byte[] data, int field, int stride) => data.AsSpan(checked((int)U32(data, field + 4)), checked((int)U32(data, field) * stride)).ToArray();
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Put16(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
}
