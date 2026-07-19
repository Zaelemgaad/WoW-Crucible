using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum MapAssetKind { Adt, Wdt, Wdl }
public sealed record MapChunkSummary(string Id, int Occurrences, long PayloadBytes);
public sealed record MapTileCell(int X, int Y, bool Present, uint Flags, uint AsyncId, uint? AreaId, uint? Holes, float? MinimumHeight, float? MaximumHeight)
{
    public override string ToString() => $"{X:N0},{Y:N0}" + (AreaId is { } area ? $" · area {area:N0}" : string.Empty);
}
public sealed record MapWmoPlacement(int Index, uint NameId, uint UniqueId, string? ClientPath, Vector3 Position, Vector3 Orientation,
    Vector3 MinimumExtent, Vector3 MaximumExtent, ushort Flags, ushort DoodadSet, ushort NameSet, ushort ScaleRaw)
{
    public float Scale => ScaleRaw == 0 ? 1f : ScaleRaw / 1024f;
    public override string ToString() => $"UID {UniqueId:N0} · {ClientPath ?? $"unresolved name {NameId:N0}"} · pos {Position.X:0.##}, {Position.Y:0.##}, {Position.Z:0.##}";
}
public sealed record MapAssetInspection(string Path, MapAssetKind Kind, uint Version, int GridWidth, int GridHeight, IReadOnlyList<MapTileCell> Cells,
    IReadOnlyList<MapChunkSummary> Chunks, IReadOnlyList<string> TexturePaths, IReadOnlyList<string> ModelPaths, IReadOnlyList<string> WmoPaths,
    uint HeaderFlags, int? TileX, int? TileY, IReadOnlyList<MapWmoPlacement> WmoPlacements, IReadOnlyList<string> Findings)
{
    public int PresentCells => Cells.Count(cell => cell.Present);
    public float? MinimumHeight => Cells.Where(cell => cell.MinimumHeight is not null).Select(cell => cell.MinimumHeight!.Value).DefaultIfEmpty(float.NaN).Min() is var value && float.IsFinite(value) ? value : null;
    public float? MaximumHeight => Cells.Where(cell => cell.MaximumHeight is not null).Select(cell => cell.MaximumHeight!.Value).DefaultIfEmpty(float.NaN).Max() is var value && float.IsFinite(value) ? value : null;
}

public static partial class MapAssetInspectionService
{
    private const int MaximumCapturedChunkBytes = 64 * 1024 * 1024;
    private const int WdtCells = 64 * 64;
    private const int AdtCells = 16 * 16;
    private const int MaximumWmoPlacements = 100_000;

    public static MapAssetInspection Inspect(string path)
    {
        path = Path.GetFullPath(path); if (!File.Exists(path)) throw new FileNotFoundException("The map asset does not exist.", path);
        var kind = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".adt" => MapAssetKind.Adt,
            ".wdt" => MapAssetKind.Wdt,
            ".wdl" => MapAssetKind.Wdl,
            _ => throw new NotSupportedException("Native map inspection accepts WotLK ADT, WDT, or WDL files.")
        };
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.RandomAccess);
        var chunks = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal); var captures = new Dictionary<string, List<(long HeaderOffset, byte[] Data)>>(StringComparer.Ordinal);
        var header = new byte[8];
        while (stream.Position < stream.Length)
        {
            var headerOffset = stream.Position; if (stream.Read(header) != header.Length) throw new InvalidDataException($"Map chunk header is truncated at byte {headerOffset:N0}.");
            var id = DecodeChunkId(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header.AsSpan(4, 4)); var payloadOffset = stream.Position; var end = payloadOffset + size;
            if (size > int.MaxValue || end > stream.Length) throw new InvalidDataException($"Map chunk {id} at byte {headerOffset:N0} extends beyond the {stream.Length:N0}-byte file.");
            chunks.TryGetValue(id, out var summary); chunks[id] = (summary.Count + 1, summary.Bytes + size);
            var captureBytes = id switch
            {
                "MVER" => Math.Min(4, (int)size),
                "MPHD" => Math.Min(32, (int)size),
                "MAIN" => (int)size,
                "MAOF" => (int)size,
                "MCNK" => Math.Min(128, (int)size),
                "MTEX" or "MMDX" or "MWMO" or "MWID" or "MODF" => (int)size,
                _ => 0
            };
            if (captureBytes > MaximumCapturedChunkBytes) throw new InvalidDataException($"Map chunk {id} is too large to inspect safely ({captureBytes:N0} bytes).");
            if (captureBytes > 0)
            {
                var data = new byte[captureBytes]; stream.ReadExactly(data); if (!captures.TryGetValue(id, out var values)) captures[id] = values = []; values.Add((headerOffset, data));
            }
            stream.Position = end;
        }

        var version = Captures("MVER").FirstOrDefault().Data is { Length: >= 4 } versionData ? BitConverter.ToUInt32(versionData) : throw new InvalidDataException("Map asset has no complete MVER chunk.");
        var findings = new List<string>(); if (version != 18) findings.Add($"MVER is {version:N0}; the verified Wrath terrain version is 18.");
        var texturePaths = Strings("MTEX").Where(value => Path.GetExtension(value).Equals(".blp", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var modelPaths = Strings("MMDX").Where(value => Path.GetExtension(value) is ".m2" or ".M2" or ".mdx" or ".MDX").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var wmoNames = StringOffsets("MWMO");
        var wmoPaths = wmoNames.Values.Where(value => Path.GetExtension(value).Equals(".wmo", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        uint headerFlags = 0; if (Captures("MPHD").FirstOrDefault().Data is { Length: >= 4 } mphd) headerFlags = BitConverter.ToUInt32(mphd);
        int? tileX = null, tileY = null; var match = AdtName().Match(Path.GetFileName(path));
        if (match.Success) { tileX = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); tileY = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture); }
        IReadOnlyList<MapTileCell> cells = kind switch
        {
            MapAssetKind.Wdt => ReadWdtCells(),
            MapAssetKind.Wdl => ReadWdlCells(),
            _ => ReadAdtCells()
        };
        var expectedCells = kind == MapAssetKind.Adt ? AdtCells : WdtCells;
        if (cells.Count != expectedCells) findings.Add($"Expected {expectedCells:N0} grid cells but decoded {cells.Count:N0}.");
        if (kind == MapAssetKind.Adt && (tileX is null || tileY is null)) findings.Add("The ADT filename does not end in _<tileX>_<tileY>.adt, so its world-tile coordinate is unknown.");
        if (kind == MapAssetKind.Wdt && cells.All(cell => !cell.Present) && (headerFlags & 1) != 0) findings.Add("This WDT uses a global WMO rather than terrain ADT tiles.");
        var wmoPlacements = ReadWmoPlacements();
        return new(path, kind, version, kind == MapAssetKind.Adt ? 16 : 64, kind == MapAssetKind.Adt ? 16 : 64, cells,
            chunks.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new MapChunkSummary(pair.Key, pair.Value.Count, pair.Value.Bytes)).ToArray(),
            texturePaths, modelPaths, wmoPaths, headerFlags, tileX, tileY, wmoPlacements, findings);

        IReadOnlyList<MapWmoPlacement> ReadWmoPlacements()
        {
            var modfCaptures = Captures("MODF").ToArray(); if (modfCaptures.Length == 0) return [];
            if (modfCaptures.Length != 1) throw new InvalidDataException($"Map asset contains {modfCaptures.Length:N0} MODF chunks; one placement table is expected.");
            var modf = modfCaptures[0].Data; if (modf.Length % 64 != 0) throw new InvalidDataException($"MODF size {modf.Length:N0} is not divisible by its 64-byte placement record size.");
            if (modf.Length / 64 > MaximumWmoPlacements) throw new InvalidDataException($"MODF declares {modf.Length / 64:N0} placements, above the bounded {MaximumWmoPlacements:N0}-instance inspection limit.");
            var mwidCaptures = Captures("MWID").ToArray(); if (mwidCaptures.Length > 1) throw new InvalidDataException($"Map asset contains {mwidCaptures.Length:N0} MWID chunks; one name-index table is expected.");
            var mwid = mwidCaptures.FirstOrDefault().Data ?? [];
            if (mwid.Length % 4 != 0) throw new InvalidDataException($"MWID size {mwid.Length:N0} is not divisible by four bytes.");
            var offsets = Enumerable.Range(0, mwid.Length / 4).Select(index => BitConverter.ToUInt32(mwid, index * 4)).ToArray(); var result = new MapWmoPlacement[modf.Length / 64];
            for (var index = 0; index < result.Length; index++)
            {
                var offset = index * 64; var nameId = BitConverter.ToUInt32(modf, offset); string? clientPath = null;
                if (nameId < (uint)offsets.Length) wmoNames.TryGetValue(offsets[checked((int)nameId)], out clientPath);
                else if (offsets.Length == 0 && nameId == 0 && wmoNames.Count == 1) clientPath = wmoNames.Values.Single();
                if (clientPath is null) findings.Add($"MODF placement {index:N0} UID {BitConverter.ToUInt32(modf, offset + 4):N0} references unresolved MWID name {nameId:N0}.");
                var position = Vector(offset + 8, $"MODF placement {index:N0} position"); var orientation = Vector(offset + 20, $"MODF placement {index:N0} orientation");
                var minimum = Vector(offset + 32, $"MODF placement {index:N0} minimum extent"); var maximum = Vector(offset + 44, $"MODF placement {index:N0} maximum extent");
                if (minimum.X > maximum.X || minimum.Y > maximum.Y || minimum.Z > maximum.Z) findings.Add($"MODF placement {index:N0} UID {BitConverter.ToUInt32(modf, offset + 4):N0} has reversed extent bounds.");
                result[index] = new(index, nameId, BitConverter.ToUInt32(modf, offset + 4), clientPath, position, orientation, minimum, maximum,
                    BitConverter.ToUInt16(modf, offset + 56), BitConverter.ToUInt16(modf, offset + 58), BitConverter.ToUInt16(modf, offset + 60), BitConverter.ToUInt16(modf, offset + 62));
            }
            var duplicate = result.GroupBy(value => value.UniqueId).FirstOrDefault(group => group.Count() > 1); if (duplicate is not null) findings.Add($"MODF unique ID {duplicate.Key:N0} occurs {duplicate.Count():N0} times.");
            return result;
            Vector3 Vector(int offset, string label)
            {
                var value = new Vector3(BitConverter.ToSingle(modf, offset), BitConverter.ToSingle(modf, offset + 4), BitConverter.ToSingle(modf, offset + 8));
                if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException($"{label} contains a non-finite coordinate."); return value;
            }
        }

        IReadOnlyList<MapTileCell> ReadWdtCells()
        {
            var main = Captures("MAIN").SingleOrDefault().Data ?? throw new InvalidDataException("WDT has no MAIN tile table.");
            if (main.Length != WdtCells * 8) throw new InvalidDataException($"WDT MAIN must be {WdtCells * 8:N0} bytes; this file declares {main.Length:N0}.");
            var result = new MapTileCell[WdtCells];
            for (var index = 0; index < result.Length; index++) { var flags = BitConverter.ToUInt32(main, index * 8); result[index] = new(index % 64, index / 64, (flags & 1) != 0, flags, BitConverter.ToUInt32(main, index * 8 + 4), null, null, null, null); }
            return result;
        }

        IReadOnlyList<MapTileCell> ReadWdlCells()
        {
            var maof = Captures("MAOF").SingleOrDefault().Data ?? throw new InvalidDataException("WDL has no MAOF tile-offset table.");
            if (maof.Length != WdtCells * 4) throw new InvalidDataException($"WDL MAOF must be {WdtCells * 4:N0} bytes; this file declares {maof.Length:N0}.");
            var result = new MapTileCell[WdtCells];
            for (var index = 0; index < result.Length; index++)
            {
                var offset = BitConverter.ToUInt32(maof, index * 4); float? minimum = null, maximum = null;
                if (offset != 0)
                {
                    if (offset > int.MaxValue || offset + 8L > stream.Length) throw new InvalidDataException($"WDL MAOF tile {index:N0} points outside the file at {offset:N0}.");
                    stream.Position = offset; stream.ReadExactly(header); var id = DecodeChunkId(header.AsSpan(0, 4)); var size = BitConverter.ToUInt32(header.AsSpan(4, 4));
                    if (id != "MARE" || size < 1090 || stream.Position + size > stream.Length) throw new InvalidDataException($"WDL tile {index:N0} does not point to a complete 1,090-byte MARE height grid.");
                    var heightBytes = new byte[1090]; stream.ReadExactly(heightBytes); short min = short.MaxValue, max = short.MinValue;
                    for (var height = 0; height < 545; height++) { var value = BitConverter.ToInt16(heightBytes, height * 2); min = Math.Min(min, value); max = Math.Max(max, value); }
                    minimum = min; maximum = max;
                }
                result[index] = new(index % 64, index / 64, offset != 0, offset, 0, null, null, minimum, maximum);
            }
            return result;
        }

        IReadOnlyList<MapTileCell> ReadAdtCells()
        {
            var result = Enumerable.Range(0, AdtCells).Select(index => new MapTileCell(index % 16, index / 16, false, 0, 0, null, null, null, null)).ToArray();
            foreach (var capture in Captures("MCNK"))
            {
                if (capture.Data.Length < 128) throw new InvalidDataException($"ADT MCNK at byte {capture.HeaderOffset:N0} is shorter than its 128-byte Wrath header.");
                var x = BitConverter.ToUInt32(capture.Data, 4); var y = BitConverter.ToUInt32(capture.Data, 8);
                if (x >= 16 || y >= 16) throw new InvalidDataException($"ADT MCNK at byte {capture.HeaderOffset:N0} uses invalid grid coordinate {x:N0},{y:N0}.");
                var slot = checked((int)(y * 16 + x)); if (result[slot].Present) throw new InvalidDataException($"ADT contains duplicate MCNK grid coordinate {x:N0},{y:N0}.");
                var flags = BitConverter.ToUInt32(capture.Data); var area = BitConverter.ToUInt32(capture.Data, 0x34); var holes = BitConverter.ToUInt32(capture.Data, 0x3C); var baseHeight = BitConverter.ToSingle(capture.Data, 0x70);
                if (!float.IsFinite(baseHeight)) throw new InvalidDataException($"ADT MCNK {x:N0},{y:N0} has a non-finite base height.");
                float? minimum = baseHeight, maximum = baseHeight; var heightOffset = BitConverter.ToUInt32(capture.Data, 0x14);
                if (heightOffset != 0)
                {
                    var nested = capture.HeaderOffset + heightOffset;
                    if (nested < capture.HeaderOffset + 8 || nested + 8 > stream.Length) throw new InvalidDataException($"ADT MCNK {x:N0},{y:N0} points outside the file for MCVT.");
                    stream.Position = nested; stream.ReadExactly(header); var nestedId = DecodeChunkId(header.AsSpan(0, 4)); var nestedSize = BitConverter.ToUInt32(header.AsSpan(4, 4));
                    if (nestedId != "MCVT" || nestedSize < 145 * 4 || stream.Position + nestedSize > stream.Length) throw new InvalidDataException($"ADT MCNK {x:N0},{y:N0} does not point to a complete MCVT height grid.");
                    var heightBytes = new byte[145 * 4]; stream.ReadExactly(heightBytes); var min = float.PositiveInfinity; var max = float.NegativeInfinity;
                    for (var height = 0; height < 145; height++) { var value = BitConverter.ToSingle(heightBytes, height * 4) + baseHeight; if (!float.IsFinite(value)) throw new InvalidDataException($"ADT MCNK {x:N0},{y:N0} contains a non-finite terrain height."); min = Math.Min(min, value); max = Math.Max(max, value); }
                    minimum = min; maximum = max;
                }
                result[slot] = new((int)x, (int)y, true, flags, 0, area, holes, minimum, maximum);
            }
            return result;
        }

        IEnumerable<(long HeaderOffset, byte[] Data)> Captures(string id) => captures.TryGetValue(id, out var values) ? values : [];
        IEnumerable<string> Strings(string id)
        {
            foreach (var (_, data) in Captures(id))
            {
                var start = 0; for (var index = 0; index <= data.Length; index++) if (index == data.Length || data[index] == 0)
                { if (index > start) { var value = Encoding.UTF8.GetString(data, start, index - start).Trim(); if (value.Length > 0) yield return value; } start = index + 1; }
            }
        }
        IReadOnlyDictionary<uint, string> StringOffsets(string id)
        {
            var result = new Dictionary<uint, string>(); var chunk = Captures(id).SingleOrDefault().Data; if (chunk is null) return result;
            var start = 0; for (var index = 0; index <= chunk.Length; index++) if (index == chunk.Length || chunk[index] == 0)
            { if (index > start) { var value = Encoding.UTF8.GetString(chunk, start, index - start).Trim(); if (value.Length > 0) result[(uint)start] = value; } start = index + 1; }
            return result;
        }
    }

    private static string DecodeChunkId(ReadOnlySpan<byte> raw) => new string(Encoding.ASCII.GetString(raw).Reverse().ToArray());
    [GeneratedRegex(@"_(\d+)_(\d+)\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex AdtName();
}
