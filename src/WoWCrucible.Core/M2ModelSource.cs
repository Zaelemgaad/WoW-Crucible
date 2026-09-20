using System.Buffers.Binary;
using System.Text;

namespace WoWCrucible.Core;

internal sealed record M2SkeletonSource(byte[] Sequences, byte[] Bones, byte[]? Attachments,
    IReadOnlyDictionary<(ushort Animation, ushort Variation), uint> AnimationIds);

/// <summary>Separates model, skeleton and animation address spaces without rewriting any source bytes.</summary>
internal sealed class M2ModelSource
{
    public byte[] Model { get; }
    public byte[] Skin { get; }
    public string SkinName { get; }
    public uint Version { get; }
    public M2SkeletonSource? Skeleton { get; }
    public byte[]? ModelSequences { get; }
    public uint[] TextureIds { get; }
    private readonly ModelBrowserSource _source;
    private readonly IReadOnlyDictionary<(ushort Animation, ushort Variation), uint> _animationIds;
    private readonly Dictionary<(ushort Animation, ushort Variation), (int Index, uint Flags)> _modelSequences = [];

    public M2ModelSource(ModelBrowserSource source, string? skinName = null)
    {
        _source = source;
        var bytes = source.Read(source.ModelName);
        var chunks = Magic(bytes) == "MD21" ? Chunks(bytes) : new Dictionary<string, byte[]>();
        Model = chunks.Count > 0 ? Required(chunks, "MD21") : bytes;
        if (Model.Length < 0x130 || Magic(Model) != "MD20") throw new InvalidDataException("Model has no complete MD20 payload.");
        Version = U32(Model, 4);
        if (Version is not 264 and not 272 and not 274) throw new NotSupportedException($"M2 version {Version} is not supported by the model viewer yet.");
        TextureIds = chunks.TryGetValue("TXID", out var txid) ? UInts(txid, "TXID") : [];
        _animationIds = AnimationIds(chunks);
        var stem = Path.GetFileNameWithoutExtension(source.ModelName);
        if (chunks.TryGetValue("SKID", out var skid) && U32(skid, 0) is var skeletonId && skeletonId != 0)
        {
            var skeletonName = source.Find(stem + ".skel") ?? source.FindFileDataId(skeletonId, ".skel")
                ?? throw new FileNotFoundException($"Could not resolve skeleton {skeletonId} ({stem}.skel).");
            var skeleton = Chunks(source.Read(skeletonName));
            ModelSequences = Required(skeleton, "SKS1");
            var sequenceCount = U32(ModelSequences, 8); var sequenceOffset = U32(ModelSequences, 12);
            if (sequenceCount > 65536 || (ulong)sequenceOffset + sequenceCount * 64UL > (ulong)ModelSequences.Length)
                throw new InvalidDataException("Invalid model skeleton sequence array.");
            for (var index = 0; index < sequenceCount; index++)
            {
                var offset = checked((int)sequenceOffset + index * 64);
                _modelSequences[(BinaryPrimitives.ReadUInt16LittleEndian(ModelSequences.AsSpan(offset)), BinaryPrimitives.ReadUInt16LittleEndian(ModelSequences.AsSpan(offset + 2)))] = (index, U32(ModelSequences, offset + 12));
            }
            var attachments = skeleton.GetValueOrDefault("SKA1");
            var visited = new HashSet<uint> { skeletonId };
            while (skeleton.TryGetValue("SKPD", out var parent) && U32(parent, 8) is var parentId && parentId != 0)
            {
                if (!visited.Add(parentId) || visited.Count > 16) throw new InvalidDataException("Skeleton parent cycle or excessive parent depth.");
                var parentName = source.FindFileDataId(parentId, ".skel")
                    ?? throw new FileNotFoundException($"Could not resolve parent skeleton {parentId} referenced by {skeletonName}.");
                skeleton = Chunks(source.Read(parentName));
                attachments ??= skeleton.GetValueOrDefault("SKA1");
            }
            Skeleton = new(Required(skeleton, "SKS1"), Required(skeleton, "SKB1"), attachments, AnimationIds(skeleton));
        }
        var sfid = chunks.TryGetValue("SFID", out var ids) ? UInts(ids, "SFID") : [];
        SkinName = (string.IsNullOrWhiteSpace(skinName) ? null : skinName) ?? source.Find(stem + "00.skin")
            ?? (sfid.Length > 0 ? source.FindFileDataId(sfid[0], ".skin") : null)
            ?? throw new FileNotFoundException($"Missing companion {stem}00.skin.");
        Skin = source.Read(SkinName);
    }

    public byte[] Animation(M2PreviewSequence sequence, bool skeleton)
    {
        var owner = skeleton ? Skeleton?.Bones ?? Model : Model;
        var flags = !skeleton && ModelSequences is not null
            ? _modelSequences.TryGetValue((sequence.AnimationId, sequence.SubAnimationId), out var local) ? local.Flags : 0x20
            : sequence.Flags;
        // Embedded tracks and aliases must not be replaced by an unrelated external file.
        if ((flags & 0x20) != 0 || (flags & 0x40) != 0) return owner;
        var ids = skeleton ? Skeleton?.AnimationIds ?? _animationIds : _animationIds;
        var file = ids.TryGetValue((sequence.AnimationId, sequence.SubAnimationId), out var id) ? _source.FindFileDataId(id, ".anim") : null;
        file ??= _source.Find($"{Path.GetFileNameWithoutExtension(_source.ModelName)}{sequence.AnimationId:D4}-{sequence.SubAnimationId:D2}.anim");
        if (file is null) return owner;
        var data = _source.Read(file);
        if (Magic(data) is not ("AFM2" or "AFSA" or "AFSB")) return data;
        var chunks = Chunks(data);
        return Required(chunks, skeleton ? "AFSB" : "AFM2");
    }

    public int ModelSequenceIndex(M2PreviewSequence sequence) => ModelSequences is null ? sequence.Index
        : _modelSequences.TryGetValue((sequence.AnimationId, sequence.SubAnimationId), out var local) ? local.Index : int.MaxValue;

    internal static Dictionary<string, byte[]> Chunks(byte[] bytes)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            if (bytes.Length - cursor < 8) throw new InvalidDataException("Truncated M2 chunk header.");
            var id = Encoding.ASCII.GetString(bytes, cursor, 4); var length = U32(bytes, cursor + 4);
            if (length > int.MaxValue || (long)cursor + 8 + length > bytes.Length) throw new InvalidDataException($"Invalid {id} chunk length.");
            if (!result.TryAdd(id, bytes.AsSpan(cursor + 8, (int)length).ToArray())) throw new InvalidDataException($"Duplicate {id} chunk.");
            cursor = checked(cursor + 8 + (int)length);
        }
        return result;
    }

    private static IReadOnlyDictionary<(ushort, ushort), uint> AnimationIds(Dictionary<string, byte[]> chunks)
    {
        var result = new Dictionary<(ushort, ushort), uint>();
        if (!chunks.TryGetValue("AFID", out var data)) return result;
        if (data.Length % 8 != 0) throw new InvalidDataException("AFID record is truncated.");
        for (var offset = 0; offset < data.Length; offset += 8)
            result[(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)), BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2)))] = U32(data, offset + 4);
        return result;
    }

    private static byte[] Required(Dictionary<string, byte[]> chunks, string name) => chunks.TryGetValue(name, out var data)
        ? data : throw new InvalidDataException($"Missing {name} chunk.");
    private static uint[] UInts(byte[] data, string name)
    {
        if (data.Length % 4 != 0) throw new InvalidDataException($"Truncated {name} ID array.");
        return Enumerable.Range(0, data.Length / 4).Select(index => U32(data, index * 4)).ToArray();
    }
    internal static string Magic(byte[] data) => data.Length >= 4 ? Encoding.ASCII.GetString(data, 0, 4) : string.Empty;
    internal static uint U32(byte[] bytes, int offset) => offset >= 0 && (long)offset + 4 <= bytes.Length
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) : throw new InvalidDataException("Truncated model structure.");
}
