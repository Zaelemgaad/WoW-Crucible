using System.Numerics;

namespace WoWCrucible.Core;

internal sealed record M2ParticleAnimation(
    M2TrackHeader Speed, M2TrackHeader Variation, M2TrackHeader VerticalRange, M2TrackHeader HorizontalRange,
    M2TrackHeader Gravity, M2TrackHeader Lifespan, M2TrackHeader EmissionRate,
    M2TrackHeader AreaLength, M2TrackHeader AreaWidth, M2TrackHeader ZSource, M2TrackHeader Enabled, bool CompressedGravity);

internal sealed record M2ParticleClip(
    M2ScalarTrack Speed, M2ScalarTrack Variation, M2ScalarTrack VerticalRange, M2ScalarTrack HorizontalRange,
    M2VectorTrack Gravity, M2ScalarTrack Lifespan, M2ScalarTrack EmissionRate,
    M2ScalarTrack AreaLength, M2ScalarTrack AreaWidth, M2ScalarTrack ZSource, M2ScalarTrack Enabled);

internal sealed record M2ParticleLifeRamp(float[] ColorTimes, Vector3[] Colors, float[] OpacityTimes, float[] Opacities, float[] SizeTimes, Vector2[] Sizes);

internal sealed class M2ParticleRig(IReadOnlyList<M2PreviewParticleEmitter> emitters, M2ParticleAnimation[] animations, M2ParticleLifeRamp[] lifeRamps, M2AnimationRig animationRig)
{
    public IReadOnlyList<M2PreviewParticleEmitter> Emitters { get; } = emitters;
    public M2ParticleAnimation[] Animations { get; } = animations;
    public M2ParticleLifeRamp[] LifeRamps { get; } = lifeRamps;
    public M2AnimationRig AnimationRig { get; } = animationRig;
    public Dictionary<int, M2ParticleClip[]> ClipCache { get; } = [];
}

public sealed record M2PreviewParticleSprite(Vector3 Position, Vector4 Color, float Size, float Rotation,
    int TextureDefinitionIndex, byte BlendMode, int TileIndex, ushort Rows, ushort Columns, int EmitterIndex)
{
    public IReadOnlyList<int> TextureDefinitionIndices { get; init; } = [TextureDefinitionIndex];
    public float Width { get; init; } = Size;
    public float Height { get; init; } = Size;
}

public static class M2ParticlePreviewService
{
    private const int ParticleStride = 476;
    private const int MaximumEmitters = 65_536;
    private const int MaximumLifeKeys = 4_096;
    private const uint MultiTextureFlag = 0x1000_0000;
    private const uint CompressedGravityFlag = 0x0080_0000;
    private const uint WorldSpaceFlag = 0x8;

    internal static M2ParticleRig Parse(byte[] model, M2AnimationRig animationRig, int boneCount, int textureCount)
    {
        var count = CheckedCount(ReadUInt(model, 0x128), MaximumEmitters, "M2 particle emitter");
        var offset = CheckedOffset(ReadUInt(model, 0x12C), "M2 particle emitter");
        Require(model, offset, count, ParticleStride, "M2 particle emitters");
        var emitters = new M2PreviewParticleEmitter[count];
        var animations = new M2ParticleAnimation[count];
        var lifeRamps = new M2ParticleLifeRamp[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * ParticleStride;
            var flags = ReadUInt(model, item + 4);
            var position = ReadVector(model, item + 8);
            var bone = ReadShort(model, item + 20);
            var packedTexture = ReadUShort(model, item + 22);
            var textures = (flags & MultiTextureFlag) != 0
                ? new[] { packedTexture & 0x1F, (packedTexture >> 5) & 0x1F, (packedTexture >> 10) & 0x1F }
                : [packedTexture];
            var texture = textures[0];
            var blend = model[item + 40];
            var type = model[item + 41];
            var rows = ReadUShort(model, item + 48); if (rows == 0) rows = 1;
            var columns = ReadUShort(model, item + 50); if (columns == 0) columns = 1;
            if (!Finite(position)) throw new InvalidDataException($"M2 particle emitter {index:N0} contains a non-finite position.");
            if (bone < -1 || bone >= boneCount) throw new InvalidDataException($"M2 particle emitter {index:N0} references bone {bone:N0}, but only {boneCount:N0} bones exist.");
            var invalidTexture = textures.FirstOrDefault(value => value >= textureCount, -1);
            if (invalidTexture >= 0) throw new InvalidDataException($"M2 particle emitter {index:N0} references texture definition {invalidTexture:N0} in its {(textures.Length == 1 ? "texture field" : "packed multi-texture field")}, but only {textureCount:N0} textures exist.");
            if (blend > 7) throw new InvalidDataException($"M2 particle emitter {index:N0} uses invalid blend mode {blend:N0}.");
            if ((long)rows * columns > 65_536) throw new InvalidDataException($"M2 particle emitter {index:N0} declares an excessive {rows:N0} x {columns:N0} sprite sheet.");

            var colors = ReadLifeColors(model, item + 260, index);
            var opacity = ReadLifeOpacity(model, item + 276, index);
            var sizes = ReadLifeSizes(model, item + 292, index);
            var lifeColors = new Vector4[3];
            var legacyColors = Three(colors.Values, Vector3.One); var legacyOpacity = Three(opacity.Values, 1f); var legacySizes = Three(sizes.Values, new Vector2(0.1f));
            for (var key = 0; key < 3; key++) lifeColors[key] = new(legacyColors[key], legacyOpacity[key]);
            var rotation = ReadSingle(model, item + 384);
            if (!float.IsFinite(rotation)) throw new InvalidDataException($"M2 particle emitter {index:N0} contains a non-finite sprite rotation.");
            emitters[index] = new(index, flags, position, bone, texture, blend, type, rows, columns, lifeColors, legacySizes.Select(value => Math.Max(Math.Abs(value.X), Math.Abs(value.Y))).ToArray(), rotation) { TextureDefinitionIndices = textures };
            lifeRamps[index] = new(colors.Times, colors.Values, opacity.Times, opacity.Values, sizes.Times, sizes.Values);
            animations[index] = new(
                Track(item + 52, "speed"), Track(item + 72, "variation"), Track(item + 92, "vertical range"), Track(item + 112, "horizontal range"),
                Track(item + 132, "gravity"), Track(item + 152, "lifespan"), Track(item + 176, "emission rate"),
                Track(item + 200, "area length"), Track(item + 220, "area width"), Track(item + 240, "Z source"), Track(item + 456, "enabled"), (flags & CompressedGravityFlag) != 0);

            M2TrackHeader Track(int trackOffset, string name) => M2AnimationService.ReadTrack(model, trackOffset, animationRig.GlobalSequenceDurations.Length, $"particle {index:N0} {name}");
        }
        return new(emitters, animations, lifeRamps, animationRig);
    }

    public static IReadOnlyList<M2PreviewParticleSprite> BuildSprites(M2PreviewGeometry geometry, M2AnimationPose? pose, int maximumSprites = 2_000)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (maximumSprites < 0) throw new ArgumentOutOfRangeException(nameof(maximumSprites));
        var particles = geometry.ParticleRig;
        if (particles is null || particles.Emitters.Count == 0 || maximumSprites == 0) return [];
        var sequenceIndex = pose?.SequenceIndex ?? (geometry.Sequences.Count > 0 ? 0 : -1);
        var resolved = sequenceIndex >= 0 ? M2AnimationService.ResolveAlias(geometry.Sequences, sequenceIndex) : -1;
        var clips = resolved >= 0 ? GetClips(geometry.ModelPath, particles, resolved) : null;
        var sequenceTime = pose?.TimeMilliseconds ?? 0;
        var elapsedTime = sequenceTime;
        var timeSeconds = (float)(sequenceTime / 1000d);
        var result = new List<M2PreviewParticleSprite>(Math.Min(maximumSprites, 512));
        for (var emitterIndex = 0; emitterIndex < particles.Emitters.Count && result.Count < maximumSprites; emitterIndex++)
        {
            var emitter = particles.Emitters[emitterIndex];
            if (emitter.EmitterType is not (1 or 2)) continue;
            var clip = clips?[emitterIndex];
            if (Value(clip?.Enabled, 1) <= 0) continue;
            var speed = Value(clip?.Speed, 0);
            var variation = Math.Clamp(Value(clip?.Variation, 0), 0, 4);
            var vertical = Value(clip?.VerticalRange, 0);
            var horizontal = Value(clip?.HorizontalRange, MathF.PI * 2);
            var gravity = clip is null ? Vector3.Zero : M2AnimationService.Sample(clip.Gravity, sequenceTime, elapsedTime, Vector3.Zero);
            var lifespan = Math.Clamp(Value(clip?.Lifespan, 0), 0, 120);
            var rate = Math.Clamp(Value(clip?.EmissionRate, 0), 0, 20_000);
            var areaLength = Math.Abs(Value(clip?.AreaLength, 0));
            var areaWidth = Math.Abs(Value(clip?.AreaWidth, 0));
            var zSource = Value(clip?.ZSource, 0);
            if (lifespan <= 0.0001f || rate <= 0.0001f) continue;
            var desired = Math.Clamp((int)MathF.Ceiling(lifespan * rate), 1, 512);
            desired = Math.Min(desired, maximumSprites - result.Count);
            for (var slot = 0; slot < desired; slot++)
            {
                var interval = 1f / rate;
                var age = PositiveModulo(timeSeconds - slot * interval, lifespan);
                var cycle = (int)MathF.Floor((timeSeconds - age) / Math.Max(interval, 0.00001f));
                var seed = Hash((uint)emitterIndex * 0x9E3779B9u ^ (uint)slot * 0x85EBCA6Bu ^ (uint)cycle);
                var r0 = Unit(ref seed); var r1 = Unit(ref seed); var r2 = Unit(ref seed); var r3 = Unit(ref seed);
                Vector3 origin; Vector3 direction;
                if (emitter.EmitterType == 2)
                {
                    var azimuth = r0 * MathF.PI * 2;
                    var z = r1 * 2 - 1;
                    var radial = MathF.Sqrt(Math.Max(0, 1 - z * z));
                    direction = new(radial * MathF.Cos(azimuth), radial * MathF.Sin(azimuth), z);
                    origin = emitter.Position + new Vector3(direction.X * areaLength, direction.Y * areaWidth, direction.Z * Math.Max(areaLength, areaWidth)) * r2;
                }
                else
                {
                    origin = emitter.Position + new Vector3((r0 * 2 - 1) * areaLength, (r1 * 2 - 1) * areaWidth, 0);
                    var azimuth = (r2 * 2 - 1) * horizontal * 0.5f;
                    var inclination = (r3 * 2 - 1) * vertical * 0.5f;
                    direction = Vector3.Normalize(new(MathF.Sin(inclination) * MathF.Cos(azimuth), MathF.Sin(inclination) * MathF.Sin(azimuth), MathF.Cos(inclination)));
                }
                if (Math.Abs(zSource) > 0.0001f)
                {
                    var sourceDirection = origin - new Vector3(0, 0, zSource);
                    if (sourceDirection.LengthSquared() > 0.0000001f) direction = Vector3.Normalize(sourceDirection);
                }
                var velocity = direction * (speed * (1 + (r3 * 2 - 1) * variation));
                if (pose is not null && emitter.BoneIndex >= 0)
                {
                    var transform = pose.BoneTransforms[emitter.BoneIndex];
                    origin = Vector3.Transform(origin, transform);
                    if ((emitter.Flags & WorldSpaceFlag) == 0) velocity = Vector3.TransformNormal(velocity, transform);
                }
                var position = origin + velocity * age + 0.5f * gravity * age * age;
                if (!Finite(position)) continue;
                var life = Math.Clamp(age / lifespan, 0, 1);
                var ramp = particles.LifeRamps[emitterIndex];
                var rgb = Life(ramp.ColorTimes, ramp.Colors, life, Vector3.One); var alpha = Life(ramp.OpacityTimes, ramp.Opacities, life, 1f); var sizeVector = Life(ramp.SizeTimes, ramp.Sizes, life, new Vector2(0.1f));
                var color = new Vector4(rgb, alpha); var width = Math.Abs(sizeVector.X); var height = Math.Abs(sizeVector.Y); var size = Math.Max(width, height);
                if (!Finite(color) || !float.IsFinite(width) || !float.IsFinite(height) || size <= 0.000001f || color.W <= 0.00001f) continue;
                var tileCount = emitter.Rows * emitter.Columns;
                var tile = tileCount <= 1 ? 0 : (int)(seed % tileCount);
                result.Add(new(position, Vector4.Clamp(color, Vector4.Zero, Vector4.One), size, emitter.Rotation, emitter.TextureDefinitionIndex, emitter.BlendMode, tile, emitter.Rows, emitter.Columns, emitterIndex)
                {
                    TextureDefinitionIndices = emitter.TextureDefinitionIndices,
                    Width = width,
                    Height = height
                });
            }
        }
        return result;

        float Value(M2ScalarTrack? track, float fallback) => track is null ? fallback : M2AnimationService.Sample(track, sequenceTime, elapsedTime, fallback);
    }

    private static M2ParticleClip[] GetClips(string modelPath, M2ParticleRig particles, int sequenceIndex)
    {
        if (particles.ClipCache.TryGetValue(sequenceIndex, out var cached)) return cached;
        var sequence = particles.AnimationRig.Sequences[sequenceIndex];
        var data = M2AnimationService.ResolveSequenceData(modelPath, particles.AnimationRig, sequence);
        var result = new M2ParticleClip[particles.Animations.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var value = particles.Animations[index];
            result[index] = new(
                Scalar(value.Speed, "speed"), Scalar(value.Variation, "variation"), Scalar(value.VerticalRange, "vertical range"), Scalar(value.HorizontalRange, "horizontal range"),
                M2AnimationService.ParseParticleGravityTrack(particles.AnimationRig, value.Gravity, sequenceIndex, data, value.CompressedGravity, $"particle {index:N0} gravity"), Scalar(value.Lifespan, "lifespan"), Scalar(value.EmissionRate, "emission rate"),
                Scalar(value.AreaLength, "area length"), Scalar(value.AreaWidth, "area width"), Scalar(value.ZSource, "Z source"), M2AnimationService.ParseByteScalarTrack(particles.AnimationRig, value.Enabled, sequenceIndex, data, $"particle {index:N0} enabled"));
            M2ScalarTrack Scalar(M2TrackHeader header, string name) => M2AnimationService.ParseScalarTrack(particles.AnimationRig, header, sequenceIndex, data, $"particle {index:N0} {name}");
        }
        if (particles.ClipCache.Count >= 8) particles.ClipCache.Remove(particles.ClipCache.Keys.First());
        particles.ClipCache[sequenceIndex] = result;
        return result;
    }

    internal static void ValidateAllSequences(M2PreviewGeometry geometry)
    {
        var rig = geometry.ParticleRig;
        if (rig is null || rig.Emitters.Count == 0) return;
        if (geometry.Sequences.Count == 0) { _ = BuildSprites(geometry, null, 1); return; }
        foreach (var sequence in geometry.Sequences)
        {
            var resolved = M2AnimationService.ResolveAlias(geometry.Sequences, sequence.Index);
            _ = GetClips(geometry.ModelPath, rig, resolved);
        }
    }

    private static (float[] Times, Vector3[] Values) ReadLifeColors(byte[] data, int block, int emitter)
    {
        var values = ReadFakeBlock(data, block, 12, emitter, "colors"); var result = new Vector3[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var offset = values.ValueOffset + index * 12;
            var value = ReadVector(data, offset) / 255f;
            if (!Finite(value)) throw new InvalidDataException($"M2 particle emitter {emitter:N0} contains a non-finite life color.");
            result[index] = Vector3.Clamp(value, Vector3.Zero, Vector3.One);
        }
        return (values.Times, result);
    }

    private static (float[] Times, float[] Values) ReadLifeOpacity(byte[] data, int block, int emitter)
    {
        var values = ReadFakeBlock(data, block, 2, emitter, "opacity"); var result = new float[values.Count];
        for (var index = 0; index < values.Count; index++) result[index] = Math.Clamp(ReadShort(data, values.ValueOffset + index * 2) / 32767f, 0, 1);
        return (values.Times, result);
    }

    private static (float[] Times, Vector2[] Values) ReadLifeSizes(byte[] data, int block, int emitter)
    {
        var values = ReadFakeBlock(data, block, 8, emitter, "sizes"); var result = new Vector2[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            var value = new Vector2(ReadSingle(data, values.ValueOffset + index * 8), ReadSingle(data, values.ValueOffset + index * 8 + 4));
            if (!float.IsFinite(value.X) || !float.IsFinite(value.Y)) throw new InvalidDataException($"M2 particle emitter {emitter:N0} contains a non-finite life size.");
            result[index] = value;
        }
        return (values.Times, result);
    }

    private static (int Count, float[] Times, int ValueOffset) ReadFakeBlock(byte[] data, int block, int stride, int emitter, string label)
    {
        Require(data, block, 1, 16, $"M2 particle {emitter:N0} {label} block");
        var timeCount = CheckedCount(ReadUInt(data, block), MaximumLifeKeys, $"M2 particle {emitter:N0} {label} timestamps"); var timeOffset = CheckedOffset(ReadUInt(data, block + 4), $"M2 particle {emitter:N0} {label} timestamps");
        var valueCount = CheckedCount(ReadUInt(data, block + 8), MaximumLifeKeys, $"M2 particle {emitter:N0} {label} values"); var valueOffset = CheckedOffset(ReadUInt(data, block + 12), $"M2 particle {emitter:N0} {label} values");
        if (timeCount != valueCount) throw new InvalidDataException($"M2 particle emitter {emitter:N0} {label} has {timeCount:N0} timestamps but {valueCount:N0} values.");
        Require(data, timeOffset, timeCount, 2, $"M2 particle {emitter:N0} {label} timestamps"); Require(data, valueOffset, valueCount, stride, $"M2 particle {emitter:N0} {label} values");
        var times = new float[timeCount];
        for (var index = 0; index < times.Length; index++)
        {
            times[index] = Math.Clamp(ReadUShort(data, timeOffset + index * 2) / 32767f, 0, 1);
            if (index > 0 && times[index] < times[index - 1]) throw new InvalidDataException($"M2 particle emitter {emitter:N0} {label} timestamps are not sorted.");
        }
        return (valueCount, times, valueOffset);
    }

    private static T Life<T>(float[] times, IReadOnlyList<T> values, float life, T fallback) where T : struct
    {
        if (values.Count == 0 || times.Length == 0) return fallback;
        var count = Math.Min(times.Length, values.Count); if (count == 1 || life <= times[0]) return values[0]; if (life >= times[count - 1]) return values[count - 1];
        var right = Array.BinarySearch(times, 0, count, life); if (right >= 0) return values[right]; right = ~right; var left = right - 1;
        var amount = times[right] == times[left] ? 0 : Math.Clamp((life - times[left]) / (times[right] - times[left]), 0, 1);
        if (typeof(T) == typeof(float)) { var a = (float)(object)values[left]; var b = (float)(object)values[right]; return (T)(object)(a + (b - a) * amount); }
        if (typeof(T) == typeof(Vector2)) return (T)(object)Vector2.Lerp((Vector2)(object)values[left], (Vector2)(object)values[right], amount);
        if (typeof(T) == typeof(Vector3)) return (T)(object)Vector3.Lerp((Vector3)(object)values[left], (Vector3)(object)values[right], amount);
        throw new NotSupportedException($"Unsupported particle life-ramp value {typeof(T).Name}.");
    }

    private static T[] Three<T>(IReadOnlyList<T> values, T fallback)
    {
        if (values.Count == 0) return [fallback, fallback, fallback];
        return [values[0], values[Math.Min(1, values.Count - 1)], values[Math.Min(2, values.Count - 1)]];
    }

    private static float PositiveModulo(float value, float divisor) { var result = value % divisor; return result < 0 ? result + divisor : result; }
    private static uint Hash(uint value) { value ^= value >> 16; value *= 0x7FEB352Du; value ^= value >> 15; value *= 0x846CA68Bu; return value ^ (value >> 16); }
    private static float Unit(ref uint state) { state = Hash(state + 0x9E3779B9u); return (state & 0x00FF_FFFFu) / 16_777_216f; }
    private static int CheckedCount(uint value, int maximum, string label) => value > maximum ? throw new InvalidDataException($"{label} count {value:N0} exceeds {maximum:N0}.") : checked((int)value);
    private static int CheckedOffset(uint value, string label) => value > int.MaxValue ? throw new InvalidDataException($"{label} offset is unsupported.") : (int)value;
    private static void Require(byte[] data, int offset, int count, int stride, string label) { var end = (long)offset + (long)count * stride; if (offset < 0 || count < 0 || end > data.LongLength) throw new InvalidDataException($"{label} range ({offset:N0}..{end:N0}) exceeds the {data.LongLength:N0}-byte model."); }
    private static uint ReadUInt(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadUShort(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
    private static short ReadShort(byte[] data, int offset) => BitConverter.ToInt16(data, offset);
    private static float ReadSingle(byte[] data, int offset) => BitConverter.ToSingle(data, offset);
    private static Vector3 ReadVector(byte[] data, int offset) => new(ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
