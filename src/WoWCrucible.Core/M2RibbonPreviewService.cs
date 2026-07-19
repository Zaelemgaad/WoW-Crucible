using System.Numerics;

namespace WoWCrucible.Core;

internal sealed record M2RibbonAnimation(M2TrackHeader Color, M2TrackHeader Opacity, M2TrackHeader Above, M2TrackHeader Below);
internal sealed record M2RibbonClip(M2VectorTrack Color, M2ScalarTrack Opacity, M2ScalarTrack Above, M2ScalarTrack Below);

internal sealed class M2RibbonRig(IReadOnlyList<M2PreviewRibbonEmitter> emitters, M2RibbonAnimation[] animations, M2AnimationRig animationRig)
{
    public IReadOnlyList<M2PreviewRibbonEmitter> Emitters { get; } = emitters;
    public M2RibbonAnimation[] Animations { get; } = animations;
    public M2AnimationRig AnimationRig { get; } = animationRig;
    public Dictionary<int, M2RibbonClip[]> ClipCache { get; } = [];
}

public sealed record M2PreviewRibbonSection(Vector3 Center, Vector3 Up, float Above, float Below, float TextureU);
public sealed record M2PreviewRibbonTrail(int EmitterIndex, int TextureDefinitionIndex, ushort BlendMode, Vector4 Color, IReadOnlyList<M2PreviewRibbonSection> Sections);

public static class M2RibbonPreviewService
{
    private const int RibbonStride = 176;
    private const int MaximumEmitters = 65_536;
    private const int MaximumBindings = 4_096;

    internal static M2RibbonRig Parse(byte[] model, M2AnimationRig animationRig, int boneCount, int textureCount, IReadOnlyList<M2PreviewRenderFlag> renderFlags)
    {
        var count = CheckedCount(ReadUInt(model, 0x120), MaximumEmitters, "M2 ribbon emitter");
        var offset = CheckedOffset(ReadUInt(model, 0x124), "M2 ribbon emitter");
        Require(model, offset, count, RibbonStride, "M2 ribbon emitters");
        var emitters = new M2PreviewRibbonEmitter[count]; var animations = new M2RibbonAnimation[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * RibbonStride; var bone = ReadInt(model, item + 4); var position = ReadVector(model, item + 8);
            if (bone < -1 || bone >= boneCount) throw new InvalidDataException($"M2 ribbon emitter {index:N0} references bone {bone:N0}, but only {boneCount:N0} bones exist.");
            if (!Finite(position)) throw new InvalidDataException($"M2 ribbon emitter {index:N0} contains a non-finite position.");
            var textures = ReadBindings(model, item + 20, item + 24, "texture", index);
            var materials = ReadBindings(model, item + 28, item + 32, "material", index);
            var texture = textures.Count == 0 ? -1 : textures[0]; var material = materials.Count == 0 ? -1 : materials[0];
            if (texture < -1 || texture >= textureCount) throw new InvalidDataException($"M2 ribbon emitter {index:N0} references texture definition {texture:N0}, but only {textureCount:N0} textures exist.");
            if (material < -1 || material >= renderFlags.Count) throw new InvalidDataException($"M2 ribbon emitter {index:N0} references render flags {material:N0}, but only {renderFlags.Count:N0} records exist.");
            var resolution = ReadSingle(model, item + 116); var lifetime = ReadSingle(model, item + 120); var angle = ReadSingle(model, item + 124);
            if (!float.IsFinite(resolution) || !float.IsFinite(lifetime) || !float.IsFinite(angle) || resolution < 0 || lifetime < 0 || resolution > 100_000 || lifetime > 600)
                throw new InvalidDataException($"M2 ribbon emitter {index:N0} has invalid edge resolution, lifetime, or angle values.");
            emitters[index] = new(index, bone, position, texture, material, material < 0 ? (ushort)2 : renderFlags[material].BlendMode, resolution, lifetime, angle);
            animations[index] = new(Track(item + 36, "color"), Track(item + 56, "opacity"), Track(item + 76, "above"), Track(item + 96, "below"));
            _ = Track(item + 132, "unknown short"); _ = Track(item + 152, "unknown enabled flag");
            M2TrackHeader Track(int trackOffset, string name) => M2AnimationService.ReadTrack(model, trackOffset, animationRig.GlobalSequenceDurations.Length, $"ribbon {index:N0} {name}");
        }
        return new(emitters, animations, animationRig);
    }

    public static IReadOnlyList<M2PreviewRibbonTrail> BuildTrails(M2PreviewGeometry geometry, M2AnimationPose? pose, int maximumSections = 2_048)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (maximumSections < 0) throw new ArgumentOutOfRangeException(nameof(maximumSections));
        var ribbons = geometry.RibbonRig;
        if (ribbons is null || ribbons.Emitters.Count == 0 || maximumSections == 0) return [];
        var sequenceIndex = pose?.SequenceIndex ?? (geometry.Sequences.Count > 0 ? 0 : -1);
        if (sequenceIndex < 0) return [];
        var resolved = M2AnimationService.ResolveAlias(geometry.Sequences, sequenceIndex);
        var clips = GetClips(geometry.ModelPath, ribbons, resolved);
        var elapsed = pose?.TimeMilliseconds ?? 0;
        var currentSequenceTime = elapsed;
        var world = new Matrix4x4[geometry.Bones.Count]; var local = new Matrix4x4[geometry.Bones.Count]; var state = new byte[geometry.Bones.Count];
        var result = new List<M2PreviewRibbonTrail>(ribbons.Emitters.Count);
        var usedSections = 0;
        for (var emitterIndex = 0; emitterIndex < ribbons.Emitters.Count && usedSections < maximumSections; emitterIndex++)
        {
            var emitter = ribbons.Emitters[emitterIndex]; var clip = clips[emitterIndex];
            if (emitter.TextureDefinitionIndex < 0 || emitter.EdgesPerSecond <= 0.0001f || emitter.EdgeLifetimeSeconds <= 0.0001f) continue;
            var desired = Math.Clamp((int)MathF.Ceiling(emitter.EdgesPerSecond * emitter.EdgeLifetimeSeconds) + 1, 2, 128);
            desired = Math.Min(desired, maximumSections - usedSections); if (desired < 2) break;
            var color = Vector3.Clamp(M2AnimationService.Sample(clip.Color, currentSequenceTime, elapsed, Vector3.One), Vector3.Zero, Vector3.One);
            var opacity = Math.Clamp(M2AnimationService.Sample(clip.Opacity, currentSequenceTime, elapsed, 32767) / 32767f, 0, 1);
            var above = Math.Abs(M2AnimationService.Sample(clip.Above, currentSequenceTime, elapsed, 0));
            var below = Math.Abs(M2AnimationService.Sample(clip.Below, currentSequenceTime, elapsed, 0));
            if (!Finite(color) || !float.IsFinite(opacity) || !float.IsFinite(above) || !float.IsFinite(below)) throw new InvalidDataException($"M2 ribbon emitter {emitterIndex:N0} produced non-finite visual values.");
            var sections = new M2PreviewRibbonSection[desired];
            for (var section = 0; section < desired; section++)
            {
                var age = Math.Min(emitter.EdgeLifetimeSeconds, section / emitter.EdgesPerSecond);
                Matrix4x4 transform;
                if (section == 0 && pose is not null && emitter.BoneIndex >= 0) transform = pose.BoneTransforms[emitter.BoneIndex];
                else if (emitter.BoneIndex >= 0)
                {
                    M2AnimationService.SampleBoneTransforms(geometry, sequenceIndex, elapsed - age * 1000d, world, local, state);
                    transform = world[emitter.BoneIndex];
                }
                else transform = Matrix4x4.Identity;
                var center = Vector3.Transform(emitter.Position, transform); var up = Vector3.TransformNormal(Vector3.UnitZ, transform);
                if (up.LengthSquared() <= 0.0000001f || !Finite(up)) up = Vector3.UnitZ; else up = Vector3.Normalize(up);
                if (!Finite(center)) throw new InvalidDataException($"M2 ribbon emitter {emitterIndex:N0} produced a non-finite trail position.");
                sections[section] = new(center, up, above, below, emitter.EdgeLifetimeSeconds <= 0 ? 0 : age / emitter.EdgeLifetimeSeconds);
            }
            result.Add(new(emitterIndex, emitter.TextureDefinitionIndex, emitter.BlendMode, new(color, opacity), sections)); usedSections += desired;
        }
        return result;
    }

    private static M2RibbonClip[] GetClips(string modelPath, M2RibbonRig ribbons, int sequenceIndex)
    {
        if (ribbons.ClipCache.TryGetValue(sequenceIndex, out var cached)) return cached;
        var sequence = ribbons.AnimationRig.Sequences[sequenceIndex]; var data = M2AnimationService.ResolveSequenceData(modelPath, ribbons.AnimationRig, sequence);
        var result = new M2RibbonClip[ribbons.Animations.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var value = ribbons.Animations[index];
            result[index] = new(
                M2AnimationService.ParseVectorTrack(ribbons.AnimationRig, value.Color, sequenceIndex, data, $"ribbon {index:N0} color"),
                M2AnimationService.ParseUnsignedShortScalarTrack(ribbons.AnimationRig, value.Opacity, sequenceIndex, data, $"ribbon {index:N0} opacity"),
                M2AnimationService.ParseScalarTrack(ribbons.AnimationRig, value.Above, sequenceIndex, data, $"ribbon {index:N0} above"),
                M2AnimationService.ParseScalarTrack(ribbons.AnimationRig, value.Below, sequenceIndex, data, $"ribbon {index:N0} below"));
        }
        if (ribbons.ClipCache.Count >= 8) ribbons.ClipCache.Remove(ribbons.ClipCache.Keys.First()); ribbons.ClipCache[sequenceIndex] = result; return result;
    }

    private static IReadOnlyList<int> ReadBindings(byte[] data, int countOffset, int dataOffset, string label, int emitter)
    {
        var count = CheckedCount(ReadUInt(data, countOffset), MaximumBindings, $"M2 ribbon {emitter:N0} {label} binding");
        var offset = CheckedOffset(ReadUInt(data, dataOffset), $"M2 ribbon {emitter:N0} {label} binding"); Require(data, offset, count, 4, $"M2 ribbon {emitter:N0} {label} bindings");
        var values = new int[count]; for (var index = 0; index < count; index++) values[index] = ReadInt(data, offset + index * 4); return values;
    }

    private static int CheckedCount(uint value, int maximum, string label) => value > maximum ? throw new InvalidDataException($"{label} count {value:N0} exceeds {maximum:N0}.") : checked((int)value);
    private static int CheckedOffset(uint value, string label) => value > int.MaxValue ? throw new InvalidDataException($"{label} offset is unsupported.") : (int)value;
    private static void Require(byte[] data, int offset, int count, int stride, string label) { var end = (long)offset + (long)count * stride; if (offset < 0 || count < 0 || end > data.LongLength) throw new InvalidDataException($"{label} range ({offset:N0}..{end:N0}) exceeds the {data.LongLength:N0}-byte model."); }
    private static uint ReadUInt(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static int ReadInt(byte[] data, int offset) => BitConverter.ToInt32(data, offset);
    private static float ReadSingle(byte[] data, int offset) => BitConverter.ToSingle(data, offset);
    private static Vector3 ReadVector(byte[] data, int offset) => new(ReadSingle(data, offset), ReadSingle(data, offset + 4), ReadSingle(data, offset + 8));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
