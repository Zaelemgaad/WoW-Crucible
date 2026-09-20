using System.Buffers.Binary;
using System.Text;

namespace WoWCrucible.Core;

public sealed record M2SkeletonEmbeddingResult(byte[] ModelData, int Bones, int Sequences, int ExternalAnimationPayloads,
    IReadOnlyList<string> Sources);

/// <summary>Embeds a standalone modern skeleton and its animation payloads without downporting geometry or materials.</summary>
public static class M2SkeletonEmbeddingService
{
    public static M2SkeletonEmbeddingResult Embed(ModelBrowserSource source, IReadOnlyDictionary<uint, string>? texturePaths = null,
        CancellationToken cancellationToken = default)
        => new Embedding(source, texturePaths, cancellationToken).Run();

    private sealed record Block(byte[] Bytes, int Base);

    private sealed class Embedding(ModelBrowserSource source, IReadOnlyDictionary<uint, string>? texturePaths, CancellationToken cancellationToken)
    {
        private readonly MemoryStream _output = new();
        private readonly Dictionary<int, uint> _pointers = [];
        private readonly Dictionary<string, Dictionary<string, byte[]>> _animations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(string File, string Part), Block> _payloads = [];
        private readonly HashSet<string> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(ushort Id, ushort Variation), uint> _animationIds = [];
        private byte[] _sequences = [];
        private int _globals;
        private string _stem = string.Empty;

        public M2SkeletonEmbeddingResult Run()
        {
            using (_output)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunks = M2ModelSource.Chunks(Read(source.ModelName));
                var modelBytes = Required(chunks, "MD21");
                if (modelBytes.Length < 0x130 || M2ModelSource.Magic(modelBytes) != "MD20" || U32(modelBytes, 4) is not (272 or 274))
                    throw new InvalidDataException("Embedding requires a modern MD21 model (version 272 or 274).");
                if (U32(modelBytes, 0x2C) != 0 || U32(modelBytes, 0x1C) != 0)
                    throw new InvalidDataException("The model already contains bones or sequences; it is not a split-skeleton model.");
                _stem = Path.GetFileNameWithoutExtension(source.ModelName);
                var skeletonId = U32(Required(chunks, "SKID"), 0);
                var skeletonName = source.Find(_stem + ".skel") ?? source.FindFileDataId(skeletonId, ".skel")
                    ?? throw new FileNotFoundException($"Missing skeleton {skeletonId} ({_stem}.skel).");
                var skeleton = M2ModelSource.Chunks(Read(skeletonName));
                if (skeleton.TryGetValue("SKPD", out var parent) && U32(parent, 8) != 0)
                    throw new NotSupportedException("Embedding a skeleton with a parent requires sequence/global-track remapping; this profile supports standalone skeletons.");
                LoadAnimationIds(skeleton);
                LoadAnimationIds(chunks);
                var sequenceData = Required(skeleton, "SKS1");
                _sequences = ArrayBytes(sequenceData, 8, 64);
                if (_sequences.Length == 0) throw new InvalidDataException("The skeleton has no animation sequences.");
                _globals = checked((int)U32(sequenceData, 0));
                var model = Append(modelBytes);
                var sequenceBlock = Append(sequenceData);
                SetHeaderArray(0x14, sequenceBlock, 0, 4);
                SetHeaderArray(0x1C, sequenceBlock, 8, 64);
                SetHeaderArray(0x24, sequenceBlock, 16, 2);
                var bones = Append(Required(skeleton, "SKB1"));
                SetHeaderArray(0x2C, bones, 0, 88);
                SetHeaderArray(0x34, bones, 8, 2);
                WalkRecords(bones, 0, 88, "AFSB", [(16, 12), (36, 8), (56, 12)]);
                if (skeleton.TryGetValue("SKA1", out var attachmentBytes))
                {
                    if (U32(modelBytes, 0xF0) != 0) throw new InvalidDataException("Both model and skeleton supply attachments.");
                    var attachments = Append(attachmentBytes);
                    SetHeaderArray(0xF0, attachments, 0, 40);
                    SetHeaderArray(0xF8, attachments, 8, 2);
                    WalkRecords(attachments, 0, 40, "AFSA", [(20, 1)]);
                }
                else WalkRecords(model, 0xF0, 40, "AFM2", [(20, 1)]);

                WalkRecords(model, 0x48, 40, "AFM2", [(0, 12), (20, 2)]);
                WalkRecords(model, 0x58, 20, "AFM2", [(0, 2)]);
                WalkRecords(model, 0x60, 60, "AFM2", [(0, 12), (20, 16), (40, 12)]);
                WalkRecords(model, 0x100, 36, "AFM2", [(24, 0)]);
                WalkRecords(model, 0x108, 156, "AFM2", [(16, 12), (36, 4), (56, 12), (76, 4), (96, 4), (116, 4), (136, 1)]);
                WalkRecords(model, 0x110, 116, "AFM2", [(12, 36), (44, 36), (76, 12), (96, 12)]);
                WalkRecords(model, 0x120, 176, "AFM2", [(36, 12), (56, 2), (76, 4), (96, 4), (132, 2), (152, 1)]);
                if (U32(modelBytes, 0x128) != 0)
                    throw new NotSupportedException("Particle-track embedding is not implemented; no particle data was discarded.");

                BindTextures(model, chunks.GetValueOrDefault("TXID"));
                var sequenceOffset = sequenceBlock.Base + checked((int)U32(sequenceData, 12));
                for (var index = 0; index < _sequences.Length / 64; index++)
                    Write32(sequenceOffset + index * 64 + 12, U32(_sequences, index * 64 + 12) | 0x20u);
                chunks["MD21"] = _output.ToArray();
                chunks.Remove("SKID");
                chunks.Remove("AFID");
                using var container = new MemoryStream();
                using (var writer = new BinaryWriter(container, Encoding.ASCII, true))
                    foreach (var chunk in chunks)
                    {
                        writer.Write(Encoding.ASCII.GetBytes(chunk.Key));
                        writer.Write(chunk.Value.Length);
                        writer.Write(chunk.Value);
                    }
                return new(container.ToArray(), checked((int)U32(bones.Bytes, 0)), _sequences.Length / 64, _payloads.Count,
                    _sources.Order(StringComparer.OrdinalIgnoreCase).ToArray());
            }
        }

        private void WalkRecords(Block owner, int array, int stride, string part, (int Offset, int Width)[] tracks)
        {
            var records = ArrayBytes(owner.Bytes, array, stride);
            var offset = checked((int)U32(owner.Bytes, array + 4));
            for (var index = 0; index < records.Length / stride; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var track in tracks) RelocateTrack(owner, offset + index * stride + track.Offset, track.Width, part);
            }
        }

        private void RelocateTrack(Block owner, int track, int width, string part)
        {
            Require(owner.Bytes, track, width == 0 ? 12 : 20);
            var global = BinaryPrimitives.ReadInt16LittleEndian(owner.Bytes.AsSpan(track + 2));
            if (global < -1 || global >= _globals) throw new InvalidDataException($"Invalid global sequence {global} at track {track}.");
            RelocateSeries(track + 4, 4);
            if (width != 0) RelocateSeries(track + 12, width);

            void RelocateSeries(int array, int elementWidth)
            {
                var descriptors = ArrayBytes(owner.Bytes, array, 8);
                var count = descriptors.Length / 8;
                var descriptorOffset = checked((int)U32(owner.Bytes, array + 4));
                if (count > (global >= 0 ? 1 : _sequences.Length / 64))
                    throw new InvalidDataException($"Track {track} has more series than its sequence table.");
                Pointer(owner.Base + array + 4, count == 0 ? 0u : checked((uint)(owner.Base + descriptorOffset)));
                for (var index = 0; index < count; index++)
                {
                    var keys = U32(descriptors, index * 8);
                    var keyOffset = U32(descriptors, index * 8 + 4);
                    var pointer = owner.Base + descriptorOffset + index * 8 + 4;
                    if (keys == 0) { Pointer(pointer, 0); continue; }
                    var payload = owner;
                    if (global < 0 && (U32(_sequences, index * 64 + 12) & 0x60u) == 0)
                        payload = Animation(index, part);
                    Require(payload.Bytes, keyOffset, checked((long)keys * elementWidth));
                    Pointer(pointer, checked((uint)payload.Base + keyOffset));
                }
            }
        }

        private Block Animation(int sequence, string part)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(_sequences.AsSpan(sequence * 64));
            var variation = BinaryPrimitives.ReadUInt16LittleEndian(_sequences.AsSpan(sequence * 64 + 2));
            var file = _animationIds.TryGetValue((id, variation), out var fileId) ? source.FindFileDataId(fileId, ".anim") : null;
            file ??= source.Find($"{_stem}{id:D4}-{variation:D2}.anim");
            if (file is null) throw new FileNotFoundException($"Missing animation {id}:{variation} ({fileId}) for {part} tracks.");
            if (_payloads.TryGetValue((file, part), out var existing)) return existing;
            if (!_animations.TryGetValue(file, out var chunks))
            {
                var bytes = Read(file);
                chunks = M2ModelSource.Magic(bytes) is "AFM2" or "AFSB" or "AFSA" ? M2ModelSource.Chunks(bytes)
                    : throw new InvalidDataException($"Split-skeleton animation {file} does not identify its payload address spaces.");
                _animations.Add(file, chunks);
            }
            var block = Append(Required(chunks, part));
            _payloads.Add((file, part), block);
            return block;
        }

        private void BindTextures(Block model, byte[]? ids)
        {
            if (texturePaths is null) return;
            var records = ArrayBytes(model.Bytes, 0x50, 16);
            var offset = checked((int)U32(model.Bytes, 0x54));
            for (var index = 0; index < records.Length / 16; index++)
            {
                var texture = offset + index * 16;
                if (U32(model.Bytes, texture) != 0 || U32(model.Bytes, texture + 8) != 0) continue;
                var id = ids is not null && index * 4 < ids.Length ? U32(ids, index * 4) : 0;
                if (id == 0 || !texturePaths.TryGetValue(id, out var path))
                    throw new InvalidDataException($"Hardcoded texture {index} (FileDataID {id}) has no supplied path.");
                path = PatchInputMapper.NormalizeArchivePath(path);
                var name = Encoding.UTF8.GetBytes(path + '\0');
                var bytes = Append(name);
                Write32(texture + 8, (uint)name.Length);
                Write32(texture + 12, (uint)bytes.Base);
            }
        }

        private void LoadAnimationIds(Dictionary<string, byte[]> chunks)
        {
            foreach (var (key, value) in M2ModelSource.AnimationIds(chunks))
            {
                if (value == 0) continue;
                if (_animationIds.TryGetValue(key, out var previous) && previous != value)
                    throw new InvalidDataException($"Model and skeleton use different files for animation {key}.");
                _animationIds[key] = value;
            }
        }

        private void SetHeaderArray(int destination, Block owner, int sourceArray, int stride)
        {
            var bytes = ArrayBytes(owner.Bytes, sourceArray, stride);
            Write32(destination, checked((uint)(bytes.Length / stride)));
            Write32(destination + 4, bytes.Length == 0 ? 0u : checked((uint)owner.Base + U32(owner.Bytes, sourceArray + 4)));
        }

        private Block Append(byte[] bytes)
        {
            _output.Position = _output.Length;
            while ((_output.Position & 3) != 0) _output.WriteByte(0);
            var position = checked((int)_output.Position);
            if ((long)position + bytes.Length > 256L * 1024 * 1024) throw new InvalidDataException("Embedded model exceeds the 256 MB model limit.");
            _output.Write(bytes);
            return new(bytes, position);
        }

        private byte[] Read(string name) { _sources.Add(name); return source.Read(name); }
        private void Pointer(int address, uint value)
        {
            if (_pointers.TryGetValue(address, out var previous) && previous != value)
                throw new InvalidDataException($"Shared track descriptor at {address} would address different animation payloads.");
            _pointers[address] = value;
            Write32(address, value);
        }
        private void Write32(int offset, uint value)
        {
            if (offset < 0 || (long)offset + 4 > _output.Length) throw new InvalidDataException("Embedding write is outside the model.");
            _output.Position = offset;
            Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); _output.Write(bytes);
        }
    }

    private static byte[] Required(Dictionary<string, byte[]> chunks, string tag) => chunks.GetValueOrDefault(tag)
        ?? throw new InvalidDataException($"Missing {tag} payload.");
    private static uint U32(byte[] bytes, int offset) => M2ModelSource.U32(bytes, offset);
    private static byte[] ArrayBytes(byte[] bytes, int array, int stride)
    {
        var count = U32(bytes, array); var offset = U32(bytes, array + 4);
        if (count == 0) return [];
        var length = checked((long)count * stride);
        Require(bytes, offset, length);
        return bytes.AsSpan(checked((int)offset), checked((int)length)).ToArray();
    }
    private static void Require(byte[] bytes, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > bytes.LongLength || length > bytes.LongLength - offset)
            throw new InvalidDataException($"Model range {offset}+{length} exceeds its {bytes.Length}-byte address space.");
    }
}
