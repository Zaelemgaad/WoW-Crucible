using System.Numerics;
using System.Text;

namespace WoWCrucible.Core;

public sealed record AdtTerrainSceneCell(int CellX, int CellY, int VertexStart, int VertexCount, int TriangleStart,
    int TriangleIndexCount, Vector3 WorldOrigin, float MinimumHeight, float MaximumHeight, uint Holes);

public sealed record AdtTerrainSceneGeometry(string AdtPath, IReadOnlyList<Vector3> Vertices, IReadOnlyList<int> TriangleIndices,
    IReadOnlyList<AdtTerrainSceneCell> Cells, Vector3 Minimum, Vector3 Maximum, int CulledHoleSquares, IReadOnlyList<string> Findings);

/// <summary>Builds the real WotLK MCVT diamond grid in the same world-coordinate space as MDDF/MODF placements.</summary>
public static class AdtTerrainSceneService
{
    private const int VertexCount = 145;
    private const float ChunkSize = 100f / 3f;

    public static AdtTerrainSceneGeometry Load(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The ADT scene source does not exist.", path);
        var inspection = MapAssetInspectionService.Inspect(path); if (inspection.Kind != MapAssetKind.Adt || inspection.Version != 18) throw new InvalidDataException("Terrain scene composition requires a validated WotLK MVER 18 ADT.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.RandomAccess);
        var vertices = new List<Vector3>(16 * 16 * VertexCount); var indices = new List<int>(16 * 16 * 8 * 8 * 12); var cells = new List<AdtTerrainSceneCell>(); var findings = new List<string>(); var header = new byte[8]; var culled = 0;
        while (stream.Position < stream.Length)
        {
            var chunkOffset = stream.Position; if (stream.Read(header) != 8) throw new InvalidDataException($"ADT chunk header is truncated at byte {chunkOffset:N0}.");
            var id = Decode(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header, 4); var payload = stream.Position; var end = payload + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"ADT chunk {id} at byte {chunkOffset:N0} extends beyond the file.");
            if (id == "MCNK") LoadCell(chunkOffset, end, size);
            stream.Position = end;
        }
        if (cells.Count == 0) throw new InvalidDataException("ADT contains no complete MCVT terrain cells.");
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity); foreach (var vertex in vertices) { minimum = Vector3.Min(minimum, vertex); maximum = Vector3.Max(maximum, vertex); }
        if (cells.Count != inspection.PresentCells) findings.Add($"Scene geometry loaded {cells.Count:N0} MCVT cells while map inspection reports {inspection.PresentCells:N0} present cells.");
        if (culled > 0) findings.Add($"Culled {culled:N0} terrain square(s) identified by MCNK hole masks.");
        findings.Add("Water and area lighting are not yet composed; MTEX/MCLY/MCAL terrain materials are supplied separately by the bounded terrain-material service.");
        return new(path, vertices, indices, cells.OrderBy(cell => cell.CellY).ThenBy(cell => cell.CellX).ToArray(), minimum, maximum, culled, findings);

        void LoadCell(long chunkOffset, long end, uint size)
        {
            if (size < 128) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} is shorter than its 128-byte header.");
            var data = new byte[128]; stream.ReadExactly(data); var cellX = BitConverter.ToUInt32(data, 4); var cellY = BitConverter.ToUInt32(data, 8); var relativeOffset = BitConverter.ToUInt32(data, 0x14); var holes = BitConverter.ToUInt32(data, 0x3C);
            var origin = new Vector3(BitConverter.ToSingle(data, 0x68), BitConverter.ToSingle(data, 0x6C), BitConverter.ToSingle(data, 0x70));
            if (cellX >= 16 || cellY >= 16 || !Finite(origin) || relativeOffset == 0) throw new InvalidDataException($"ADT MCNK at byte {chunkOffset:N0} has invalid coordinates, world origin, or no MCVT grid.");
            var nested = chunkOffset + relativeOffset; if (nested < chunkOffset + 8 || nested + 8L + VertexCount * 4L > end) throw new InvalidDataException($"ADT MCNK {cellX},{cellY} points outside its chunk for MCVT.");
            stream.Position = nested; stream.ReadExactly(header); var nestedId = Decode(header.AsSpan(0, 4)); var nestedSize = BitConverter.ToUInt32(header, 4); if (nestedId != "MCVT" || nestedSize < VertexCount * 4 || stream.Position + nestedSize > end) throw new InvalidDataException($"ADT MCNK {cellX},{cellY} does not point to a complete MCVT height grid.");
            var bytes = new byte[VertexCount * 4]; stream.ReadExactly(bytes); var vertexStart = vertices.Count; var triangleStart = indices.Count; var min = float.PositiveInfinity; var max = float.NegativeInfinity;
            for (var index = 0; index < VertexCount; index++)
            {
                var height = BitConverter.ToSingle(bytes, index * 4); if (!float.IsFinite(height)) throw new InvalidDataException($"ADT MCNK {cellX},{cellY} vertex {index:N0} is non-finite.");
                var local = AdtTerrainBrushService.VertexPosition(index); var vertex = new Vector3(origin.X - local.Y * ChunkSize, origin.Y - local.X * ChunkSize, origin.Z + height); if (!Finite(vertex)) throw new InvalidDataException($"ADT MCNK {cellX},{cellY} vertex {index:N0} produced a non-finite world coordinate.");
                vertices.Add(vertex); min = Math.Min(min, vertex.Z); max = Math.Max(max, vertex.Z);
            }
            for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
            {
                var holeBit = (y / 2) * 4 + x / 2; if ((holes & (1u << holeBit)) != 0) { culled++; continue; }
                var topLeft = vertexStart + y * 17 + x; var topRight = topLeft + 1; var center = vertexStart + y * 17 + 9 + x; var bottomLeft = vertexStart + (y + 1) * 17 + x; var bottomRight = bottomLeft + 1;
                Add(topLeft, topRight, center); Add(topRight, bottomRight, center); Add(bottomRight, bottomLeft, center); Add(bottomLeft, topLeft, center);
            }
            if (cells.Any(cell => cell.CellX == (int)cellX && cell.CellY == (int)cellY)) throw new InvalidDataException($"ADT contains duplicate scene cell {cellX},{cellY}.");
            cells.Add(new((int)cellX, (int)cellY, vertexStart, VertexCount, triangleStart, indices.Count - triangleStart, origin, min, max, holes));
            void Add(int a, int b, int c) { indices.Add(a); indices.Add(b); indices.Add(c); }
        }
    }

    private static string Decode(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
