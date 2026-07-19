using System.Numerics;

namespace WoWCrucible.Core;

internal readonly record struct M2VertexSkin(byte Weight0, byte Weight1, byte Weight2, byte Weight3, byte Bone0, byte Bone1, byte Bone2, byte Bone3);
internal readonly record struct M2TrackHeader(ushort Interpolation, short GlobalSequence, uint TimestampSeriesCount, uint TimestampSeriesOffset, uint ValueSeriesCount, uint ValueSeriesOffset);
internal sealed record M2BoneAnimation(M2TrackHeader Translation, M2TrackHeader Rotation, M2TrackHeader Scale);
internal sealed record M2CameraAnimation(M2TrackHeader PositionTranslation, M2TrackHeader TargetTranslation, M2TrackHeader Roll);
internal sealed record M2LightAnimation(M2TrackHeader AmbientColor, M2TrackHeader AmbientIntensity, M2TrackHeader DiffuseColor, M2TrackHeader DiffuseIntensity, M2TrackHeader AttenuationStart, M2TrackHeader AttenuationEnd, M2TrackHeader UseAttenuation);
internal sealed class M2AnimationRig(byte[] modelData, IReadOnlyList<M2PreviewSequence> sequences, uint[] globalSequenceDurations, M2BoneAnimation[] bones, M2VertexSkin[] vertexSkin,
    M2CameraAnimation[] cameras, M2LightAnimation[] lights, IReadOnlyList<M2PreviewCamera> previewCameras, IReadOnlyList<M2PreviewLight> previewLights)
{
    public byte[] ModelData { get; } = modelData;
    public IReadOnlyList<M2PreviewSequence> Sequences { get; } = sequences;
    public uint[] GlobalSequenceDurations { get; } = globalSequenceDurations;
    public M2BoneAnimation[] Bones { get; } = bones;
    public M2VertexSkin[] VertexSkin { get; } = vertexSkin;
    public M2CameraAnimation[] Cameras { get; } = cameras;
    public M2LightAnimation[] Lights { get; } = lights;
    public IReadOnlyList<M2PreviewCamera> PreviewCameras { get; } = previewCameras;
    public IReadOnlyList<M2PreviewLight> PreviewLights { get; } = previewLights;
    public Dictionary<int, M2AnimationClip> ClipCache { get; } = [];
}

internal sealed record M2AnimationClip(int SequenceIndex, M2BoneClip[] Bones, M2CameraClip[] Cameras, M2LightClip[] Lights);
internal sealed record M2BoneClip(M2VectorTrack Translation, M2QuaternionTrack Rotation, M2VectorTrack Scale);
internal sealed record M2CameraClip(M2VectorTrack PositionTranslation, M2VectorTrack TargetTranslation, M2ScalarTrack Roll);
internal sealed record M2LightClip(M2VectorTrack AmbientColor, M2ScalarTrack AmbientIntensity, M2VectorTrack DiffuseColor, M2ScalarTrack DiffuseIntensity, M2ScalarTrack AttenuationStart, M2ScalarTrack AttenuationEnd, M2IntegerTrack UseAttenuation);
internal sealed record M2VectorTrack(ushort Interpolation, uint[] Times, Vector3[] Values, uint? GlobalDuration);
internal sealed record M2QuaternionTrack(ushort Interpolation, uint[] Times, Quaternion[] Values, uint? GlobalDuration);
internal sealed record M2ScalarTrack(ushort Interpolation, uint[] Times, float[] Values, uint? GlobalDuration);
internal sealed record M2IntegerTrack(ushort Interpolation, uint[] Times, int[] Values, uint? GlobalDuration);

public sealed record M2PreviewCameraPose(Vector3 Position, Vector3 Target, float RollRadians);
public sealed record M2PreviewLightPose(Vector3 Position, Vector3 Direction, Vector3 AmbientColor, float AmbientIntensity, Vector3 DiffuseColor, float DiffuseIntensity, float AttenuationStart, float AttenuationEnd, bool UseAttenuation);

public sealed class M2AnimationPose
{
    internal M2AnimationPose(int vertexCount, int boneCount, int attachmentCount, int cameraCount, int lightCount)
    {
        Vertices = new Vector3[vertexCount];
        Normals = new Vector3[vertexCount];
        BoneTransforms = new Matrix4x4[boneCount];
        LocalBoneTransforms = new Matrix4x4[boneCount];
        BoneTransformState = new byte[boneCount];
        AttachmentPositions = new Vector3[attachmentCount];
        Cameras = new M2PreviewCameraPose[cameraCount];
        Lights = new M2PreviewLightPose[lightCount];
    }

    public Vector3[] Vertices { get; }
    public Vector3[] Normals { get; }
    public Matrix4x4[] BoneTransforms { get; }
    public Vector3[] AttachmentPositions { get; }
    public M2PreviewCameraPose[] Cameras { get; }
    public M2PreviewLightPose[] Lights { get; }
    public Vector3 Minimum { get; internal set; }
    public Vector3 Maximum { get; internal set; }
    public int SequenceIndex { get; internal set; } = -1;
    public double TimeMilliseconds { get; internal set; }
    internal Matrix4x4[] LocalBoneTransforms { get; }
    internal byte[] BoneTransformState { get; }
}

public static class M2AnimationService
{
    private const int SequenceStride = 64;
    private const int BoneStride = 88;
    private const int VertexStride = 48;
    private const int MaximumSequences = 65_536;
    private const int MaximumGlobalSequences = 65_536;
    private const int MaximumCameras = 4_096;
    private const int MaximumLights = 4_096;
    private const int MaximumKeysPerTrack = 5_000_000;
    private const int MaximumCachedClips = 8;

    internal static M2AnimationRig ParseRig(string modelPath, byte[] model, int vertexOffset, int vertexCount, IReadOnlyList<M2PreviewBone> previewBones)
    {
        var sequenceCount = Count(model, 0x1C, MaximumSequences, "M2 animation sequence");
        var sequenceOffset = Offset(model, 0x20, "M2 animation sequence");
        Require(model, sequenceOffset, sequenceCount, SequenceStride, "M2 animation sequences");
        var sequences = new M2PreviewSequence[sequenceCount];
        for (var index = 0; index < sequenceCount; index++)
        {
            var item = sequenceOffset + index * SequenceStride;
            var speed = Single(model, item + 8);
            if (!float.IsFinite(speed)) throw new InvalidDataException($"M2 animation sequence {index:N0} has a non-finite move speed.");
            sequences[index] = new(index, UShort(model, item), UShort(model, item + 2), UInt(model, item + 4), speed, UInt(model, item + 12), Short(model, item + 16),
                UInt(model, item + 20), UInt(model, item + 24), UInt(model, item + 28), Short(model, item + 60), UShort(model, item + 62));
        }

        var globalCount = Count(model, 0x14, MaximumGlobalSequences, "M2 global sequence");
        var globalOffset = Offset(model, 0x18, "M2 global sequence");
        Require(model, globalOffset, globalCount, 4, "M2 global sequences");
        var globals = new uint[globalCount];
        for (var index = 0; index < globals.Length; index++) globals[index] = UInt(model, globalOffset + index * 4);

        var boneCount = Count(model, 0x2C, 65_536, "M2 bone");
        var boneOffset = Offset(model, 0x30, "M2 bone");
        if (boneCount != previewBones.Count) throw new InvalidDataException("M2 bone metadata changed while the animation rig was being loaded.");
        Require(model, boneOffset, boneCount, BoneStride, "M2 bones");
        ValidateHierarchy(previewBones);
        var bones = new M2BoneAnimation[boneCount];
        for (var index = 0; index < boneCount; index++)
        {
            var item = boneOffset + index * BoneStride;
            bones[index] = new(ReadTrack(model, item + 16, globals.Length, $"bone {index:N0} translation"),
                ReadTrack(model, item + 36, globals.Length, $"bone {index:N0} rotation"),
                ReadTrack(model, item + 56, globals.Length, $"bone {index:N0} scale"));
        }

        Require(model, vertexOffset, vertexCount, VertexStride, "M2 vertices");
        var skin = new M2VertexSkin[vertexCount];
        for (var index = 0; index < vertexCount; index++)
        {
            var item = vertexOffset + index * VertexStride;
            var value = new M2VertexSkin(model[item + 12], model[item + 13], model[item + 14], model[item + 15], model[item + 16], model[item + 17], model[item + 18], model[item + 19]);
            ValidateInfluence(index, value.Weight0, value.Bone0); ValidateInfluence(index, value.Weight1, value.Bone1);
            ValidateInfluence(index, value.Weight2, value.Bone2); ValidateInfluence(index, value.Weight3, value.Bone3);
            skin[index] = value;
        }
        var (cameras, previewCameras) = ParseCameras(model, globals.Length);
        var (lights, previewLights) = ParseLights(model, globals.Length, boneCount);
        return new(model, sequences, globals, bones, skin, cameras, lights, previewCameras, previewLights);

        void ValidateInfluence(int vertex, byte weight, byte bone)
        {
            if (weight != 0 && bone >= boneCount) throw new InvalidDataException($"M2 vertex {vertex:N0} assigns weight {weight:N0} to missing bone {bone:N0}; only {boneCount:N0} bones exist.");
        }
    }

    public static M2AnimationPose CreatePose(M2PreviewGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return new(geometry.Vertices.Count, geometry.Bones.Count, geometry.Attachments.Count, geometry.Cameras.Count, geometry.Lights.Count);
    }

    public static M2AnimationPose SnapshotPose(M2PreviewGeometry geometry, M2AnimationPose pose)
    {
        ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(pose);
        if (pose.Vertices.Length != geometry.Vertices.Count || pose.Normals.Length != geometry.Vertices.Count || pose.BoneTransforms.Length != geometry.Bones.Count || pose.AttachmentPositions.Length != geometry.Attachments.Count || pose.Cameras.Length != geometry.Cameras.Count || pose.Lights.Length != geometry.Lights.Count || pose.SequenceIndex < 0)
            throw new ArgumentException("The sampled animation pose does not belong to this geometry or has not been sampled.", nameof(pose));
        var snapshot = new M2AnimationPose(pose.Vertices.Length, pose.BoneTransforms.Length, pose.AttachmentPositions.Length, pose.Cameras.Length, pose.Lights.Length)
        {
            Minimum = pose.Minimum,
            Maximum = pose.Maximum,
            SequenceIndex = pose.SequenceIndex,
            TimeMilliseconds = pose.TimeMilliseconds
        };
        pose.Vertices.CopyTo(snapshot.Vertices, 0); pose.Normals.CopyTo(snapshot.Normals, 0); pose.BoneTransforms.CopyTo(snapshot.BoneTransforms, 0); pose.AttachmentPositions.CopyTo(snapshot.AttachmentPositions, 0); pose.Cameras.CopyTo(snapshot.Cameras, 0); pose.Lights.CopyTo(snapshot.Lights, 0);
        return snapshot;
    }

    public static void SampleInto(M2PreviewGeometry geometry, int sequenceIndex, double elapsedMilliseconds, M2AnimationPose pose)
    {
        ArgumentNullException.ThrowIfNull(geometry); ArgumentNullException.ThrowIfNull(pose);
        var rig = geometry.AnimationRig ?? throw new InvalidOperationException("This model does not contain a parsed Wrath animation rig.");
        if ((uint)sequenceIndex >= (uint)rig.Sequences.Count) throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
        if (pose.Vertices.Length != geometry.Vertices.Count || pose.BoneTransforms.Length != geometry.Bones.Count || pose.AttachmentPositions.Length != geometry.Attachments.Count || pose.Cameras.Length != geometry.Cameras.Count || pose.Lights.Length != geometry.Lights.Count)
            throw new ArgumentException("The reusable pose belongs to a different model.", nameof(pose));
        if (!double.IsFinite(elapsedMilliseconds)) throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));

        var resolved = ResolveAlias(rig.Sequences, sequenceIndex);
        var sequence = rig.Sequences[resolved];
        var clip = GetClip(geometry.ModelPath, rig, resolved);
        var duration = Math.Max(1u, sequence.DurationMilliseconds);
        var sequenceTime = sequence.Loops ? PositiveModulo(elapsedMilliseconds, duration) : Math.Clamp(elapsedMilliseconds, 0, duration);
        SampleBones(geometry, clip, sequenceTime, elapsedMilliseconds, pose.BoneTransforms, pose.LocalBoneTransforms, pose.BoneTransformState);

        for (var index = 0; index < geometry.Vertices.Count; index++)
        {
            var sourceVertex = geometry.Vertices[index]; var sourceNormal = geometry.Normals[index]; var skin = rig.VertexSkin[index];
            var weightTotal = skin.Weight0 + skin.Weight1 + skin.Weight2 + skin.Weight3;
            if (weightTotal == 0) { pose.Vertices[index] = sourceVertex; pose.Normals[index] = sourceNormal; continue; }
            var vertex = Vector3.Zero; var normal = Vector3.Zero;
            Add(skin.Weight0, skin.Bone0); Add(skin.Weight1, skin.Bone1); Add(skin.Weight2, skin.Bone2); Add(skin.Weight3, skin.Bone3);
            pose.Vertices[index] = vertex;
            pose.Normals[index] = normal.LengthSquared() > 0.0000001f ? Vector3.Normalize(normal) : sourceNormal;
            void Add(byte weight, byte bone)
            {
                if (weight == 0) return;
                var amount = weight / (float)weightTotal;
                vertex += Vector3.Transform(sourceVertex, pose.BoneTransforms[bone]) * amount;
                normal += Vector3.TransformNormal(sourceNormal, pose.BoneTransforms[bone]) * amount;
            }
        }

        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        foreach (var vertexIndex in geometry.TriangleIndices) { minimum = Vector3.Min(minimum, pose.Vertices[vertexIndex]); maximum = Vector3.Max(maximum, pose.Vertices[vertexIndex]); }
        if (geometry.TriangleIndices.Count == 0) { minimum = geometry.Minimum; maximum = geometry.Maximum; }
        for (var index = 0; index < geometry.Attachments.Count; index++)
        {
            var attachment = geometry.Attachments[index]; pose.AttachmentPositions[index] = Vector3.Transform(attachment.Position, pose.BoneTransforms[attachment.BoneIndex]);
        }
        for (var index = 0; index < geometry.Cameras.Count; index++)
        {
            var camera = geometry.Cameras[index]; var tracks = clip.Cameras[index];
            var position = camera.BasePosition + Sample(tracks.PositionTranslation, sequenceTime, elapsedMilliseconds, Vector3.Zero);
            var target = camera.BaseTarget + Sample(tracks.TargetTranslation, sequenceTime, elapsedMilliseconds, Vector3.Zero);
            var roll = Sample(tracks.Roll, sequenceTime, elapsedMilliseconds, 0f);
            if (!Finite(position) || !Finite(target) || !float.IsFinite(roll)) throw new InvalidDataException($"Animation produced a non-finite camera {index:N0} pose.");
            pose.Cameras[index] = new(position, target, roll);
        }
        for (var index = 0; index < geometry.Lights.Count; index++)
        {
            var light = geometry.Lights[index]; var tracks = clip.Lights[index];
            var position = light.BoneIndex >= 0 ? Vector3.Transform(light.Position, pose.BoneTransforms[light.BoneIndex]) : light.Position;
            var direction = light.BoneIndex >= 0 ? Vector3.TransformNormal(Vector3.UnitY, pose.BoneTransforms[light.BoneIndex]) : Vector3.UnitY;
            if (direction.LengthSquared() > 0.0000001f) direction = Vector3.Normalize(direction); else direction = Vector3.UnitY;
            var ambientColor = Sample(tracks.AmbientColor, sequenceTime, elapsedMilliseconds, Vector3.Zero);
            var ambientIntensity = Sample(tracks.AmbientIntensity, sequenceTime, elapsedMilliseconds, 0f);
            var diffuseColor = Sample(tracks.DiffuseColor, sequenceTime, elapsedMilliseconds, Vector3.One);
            var diffuseIntensity = Sample(tracks.DiffuseIntensity, sequenceTime, elapsedMilliseconds, 0f);
            var attenuationStart = Sample(tracks.AttenuationStart, sequenceTime, elapsedMilliseconds, 0f);
            var attenuationEnd = Sample(tracks.AttenuationEnd, sequenceTime, elapsedMilliseconds, 0f);
            var useAttenuation = Sample(tracks.UseAttenuation, sequenceTime, elapsedMilliseconds, 0) != 0;
            if (!Finite(position) || !Finite(direction) || !Finite(ambientColor) || !Finite(diffuseColor) || !float.IsFinite(ambientIntensity) || !float.IsFinite(diffuseIntensity) || !float.IsFinite(attenuationStart) || !float.IsFinite(attenuationEnd))
                throw new InvalidDataException($"Animation produced a non-finite light {index:N0} pose.");
            pose.Lights[index] = new(position, direction, ambientColor, ambientIntensity, diffuseColor, diffuseIntensity, attenuationStart, attenuationEnd, useAttenuation);
        }
        pose.Minimum = minimum; pose.Maximum = maximum; pose.SequenceIndex = sequenceIndex; pose.TimeMilliseconds = sequenceTime;
    }

    internal static double SampleBoneTransforms(M2PreviewGeometry geometry, int sequenceIndex, double elapsedMilliseconds,
        Matrix4x4[] worldTransforms, Matrix4x4[] localTransforms, byte[] transformState)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var rig = geometry.AnimationRig ?? throw new InvalidOperationException("This model does not contain a parsed Wrath animation rig.");
        if ((uint)sequenceIndex >= (uint)rig.Sequences.Count) throw new ArgumentOutOfRangeException(nameof(sequenceIndex));
        if (worldTransforms.Length != geometry.Bones.Count || localTransforms.Length != geometry.Bones.Count || transformState.Length != geometry.Bones.Count)
            throw new ArgumentException("The reusable bone-transform buffers do not match this model.");
        if (!double.IsFinite(elapsedMilliseconds)) throw new ArgumentOutOfRangeException(nameof(elapsedMilliseconds));
        var resolved = ResolveAlias(rig.Sequences, sequenceIndex); var sequence = rig.Sequences[resolved]; var duration = Math.Max(1u, sequence.DurationMilliseconds);
        var sequenceTime = sequence.Loops ? PositiveModulo(elapsedMilliseconds, duration) : Math.Clamp(elapsedMilliseconds, 0, duration);
        SampleBones(geometry, GetClip(geometry.ModelPath, rig, resolved), sequenceTime, elapsedMilliseconds, worldTransforms, localTransforms, transformState);
        return sequenceTime;
    }

    private static void SampleBones(M2PreviewGeometry geometry, M2AnimationClip clip, double sequenceTime, double elapsedMilliseconds,
        Matrix4x4[] worldTransforms, Matrix4x4[] localTransforms, byte[] transformState)
    {
        Array.Clear(transformState);
        for (var index = 0; index < geometry.Bones.Count; index++)
        {
            var bone = geometry.Bones[index]; var tracks = clip.Bones[index];
            var translation = Sample(tracks.Translation, sequenceTime, elapsedMilliseconds, Vector3.Zero);
            var rotation = Sample(tracks.Rotation, sequenceTime, elapsedMilliseconds, Quaternion.Identity);
            var scale = Sample(tracks.Scale, sequenceTime, elapsedMilliseconds, Vector3.One);
            localTransforms[index] = Matrix4x4.CreateTranslation(-bone.Pivot) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation + bone.Pivot);
        }
        for (var index = 0; index < geometry.Bones.Count; index++) BuildWorldTransform(index);

        Matrix4x4 BuildWorldTransform(int index)
        {
            if (transformState[index] == 2) return worldTransforms[index];
            if (transformState[index] == 1) throw new InvalidDataException($"M2 bone hierarchy contains a cycle at bone {index:N0}.");
            transformState[index] = 1;
            var parent = geometry.Bones[index].ParentIndex;
            var world = parent >= 0 ? localTransforms[index] * BuildWorldTransform(parent) : localTransforms[index];
            if (!Finite(world)) throw new InvalidDataException($"Animation produced a non-finite transform for bone {index:N0}.");
            worldTransforms[index] = world; transformState[index] = 2; return world;
        }
    }

    private static M2AnimationClip GetClip(string modelPath, M2AnimationRig rig, int sequenceIndex)
    {
        if (rig.ClipCache.TryGetValue(sequenceIndex, out var cached)) return cached;
        var sequence = rig.Sequences[sequenceIndex];
        var sequenceData = ResolveSequenceData(modelPath, rig, sequence);
        var bones = new M2BoneClip[rig.Bones.Length];
        for (var index = 0; index < bones.Length; index++)
        {
            var bone = rig.Bones[index];
            bones[index] = new(ParseVectorTrack(rig, bone.Translation, sequenceIndex, sequenceData, $"bone {index:N0} translation"),
                ParseQuaternionTrack(rig, bone.Rotation, sequenceIndex, sequenceData, $"bone {index:N0} rotation"),
                ParseVectorTrack(rig, bone.Scale, sequenceIndex, sequenceData, $"bone {index:N0} scale"));
        }
        var cameras = new M2CameraClip[rig.Cameras.Length];
        for (var index = 0; index < cameras.Length; index++)
        {
            var camera = rig.Cameras[index]; cameras[index] = new(ParseVectorTrack(rig, camera.PositionTranslation, sequenceIndex, sequenceData, $"camera {index:N0} position"), ParseVectorTrack(rig, camera.TargetTranslation, sequenceIndex, sequenceData, $"camera {index:N0} target"), ParseScalarTrack(rig, camera.Roll, sequenceIndex, sequenceData, $"camera {index:N0} roll"));
        }
        var lights = new M2LightClip[rig.Lights.Length];
        for (var index = 0; index < lights.Length; index++)
        {
            var light = rig.Lights[index]; lights[index] = new(ParseVectorTrack(rig, light.AmbientColor, sequenceIndex, sequenceData, $"light {index:N0} ambient color"), ParseScalarTrack(rig, light.AmbientIntensity, sequenceIndex, sequenceData, $"light {index:N0} ambient intensity"), ParseVectorTrack(rig, light.DiffuseColor, sequenceIndex, sequenceData, $"light {index:N0} diffuse color"), ParseScalarTrack(rig, light.DiffuseIntensity, sequenceIndex, sequenceData, $"light {index:N0} diffuse intensity"), ParseScalarTrack(rig, light.AttenuationStart, sequenceIndex, sequenceData, $"light {index:N0} attenuation start"), ParseScalarTrack(rig, light.AttenuationEnd, sequenceIndex, sequenceData, $"light {index:N0} attenuation end"), ParseIntegerTrack(rig, light.UseAttenuation, sequenceIndex, sequenceData, $"light {index:N0} attenuation flag"));
        }
        var clip = new M2AnimationClip(sequenceIndex, bones, cameras, lights);
        if (rig.ClipCache.Count >= MaximumCachedClips) rig.ClipCache.Remove(rig.ClipCache.Keys.First());
        rig.ClipCache[sequenceIndex] = clip;
        return clip;
    }

    internal static byte[] ResolveSequenceData(string modelPath, M2AnimationRig rig, M2PreviewSequence sequence)
    {
        var externalPath = Path.Combine(Path.GetDirectoryName(modelPath)!, $"{Path.GetFileNameWithoutExtension(modelPath)}{sequence.AnimationId:D4}-{sequence.SubAnimationId:D2}.anim");
        return File.Exists(externalPath) ? File.ReadAllBytes(externalPath) : rig.ModelData;
    }

    private static (M2CameraAnimation[] Animations, IReadOnlyList<M2PreviewCamera> Preview) ParseCameras(byte[] model, int globalCount)
    {
        var count = Count(model, 0x110, MaximumCameras, "M2 camera"); var offset = Offset(model, 0x114, "M2 camera");
        Require(model, offset, count, 100, "M2 cameras");
        var lookupCount = Count(model, 0x118, MaximumCameras * 16, "M2 camera lookup"); var lookupOffset = Offset(model, 0x11C, "M2 camera lookup");
        Require(model, lookupOffset, lookupCount, 2, "M2 camera lookup");
        var slots = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (var index = 0; index < lookupCount; index++)
        {
            var camera = Short(model, lookupOffset + index * 2); if (camera == -1) continue;
            if (camera < 0 || camera >= count) throw new InvalidDataException($"M2 camera lookup slot {index:N0} references camera {camera:N0}, but only {count:N0} records exist.");
            slots[camera].Add(index);
        }
        var animations = new M2CameraAnimation[count]; var preview = new M2PreviewCamera[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * 100; var fov = Single(model, item + 4); var far = Single(model, item + 8); var near = Single(model, item + 12); var position = Vector(model, item + 36); var target = Vector(model, item + 68);
            if (!float.IsFinite(fov) || !float.IsFinite(far) || !float.IsFinite(near) || fov <= 0 || fov >= MathF.PI || near <= 0 || far <= near || !Finite(position) || !Finite(target))
                throw new InvalidDataException($"M2 camera {index:N0} has an invalid field of view, clipping range, position, or target.");
            animations[index] = new(ReadTrack(model, item + 16, globalCount, $"camera {index:N0} position"), ReadTrack(model, item + 48, globalCount, $"camera {index:N0} target"), ReadTrack(model, item + 80, globalCount, $"camera {index:N0} roll"));
            preview[index] = new(index, BitConverter.ToInt32(model, item), fov, far, near, position, target, slots[index].ToArray());
        }
        return (animations, preview);
    }

    private static (M2LightAnimation[] Animations, IReadOnlyList<M2PreviewLight> Preview) ParseLights(byte[] model, int globalCount, int boneCount)
    {
        var count = Count(model, 0x108, MaximumLights, "M2 light"); var offset = Offset(model, 0x10C, "M2 light");
        Require(model, offset, count, 156, "M2 lights"); var animations = new M2LightAnimation[count]; var preview = new M2PreviewLight[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * 156; var type = Short(model, item); var bone = Short(model, item + 2); var position = Vector(model, item + 4);
            if (bone < -1 || bone >= boneCount) throw new InvalidDataException($"M2 light {index:N0} references bone {bone:N0}, but only {boneCount:N0} bones exist.");
            if (!Finite(position)) throw new InvalidDataException($"M2 light {index:N0} contains a non-finite position.");
            animations[index] = new(ReadTrack(model, item + 16, globalCount, $"light {index:N0} ambient color"), ReadTrack(model, item + 36, globalCount, $"light {index:N0} ambient intensity"), ReadTrack(model, item + 56, globalCount, $"light {index:N0} diffuse color"), ReadTrack(model, item + 76, globalCount, $"light {index:N0} diffuse intensity"), ReadTrack(model, item + 96, globalCount, $"light {index:N0} attenuation start"), ReadTrack(model, item + 116, globalCount, $"light {index:N0} attenuation end"), ReadTrack(model, item + 136, globalCount, $"light {index:N0} attenuation flag"));
            preview[index] = new(index, type, bone, position);
        }
        return (animations, preview);
    }

    internal static M2VectorTrack ParseVectorTrack(M2AnimationRig rig, M2TrackHeader header, int sequenceIndex, byte[] sequenceData, string label)
    {
        var (times, valueCount, valueOffset, globalDuration, data) = ReadSeries(rig, header, sequenceIndex, sequenceData, label); data = ValueData(rig, data, valueOffset, valueCount, 12);
        Require(data, valueOffset, valueCount, 12, label + " values");
        var values = new Vector3[valueCount];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = new(Single(data, valueOffset + index * 12), Single(data, valueOffset + index * 12 + 4), Single(data, valueOffset + index * 12 + 8));
            if (!Finite(values[index])) throw new InvalidDataException($"M2 {label} key {index:N0} is non-finite.");
        }
        MatchCounts(times, values.Length, label); return new(header.Interpolation, times, values, globalDuration);
    }

    private static M2QuaternionTrack ParseQuaternionTrack(M2AnimationRig rig, M2TrackHeader header, int sequenceIndex, byte[] sequenceData, string label)
    {
        var (times, valueCount, valueOffset, globalDuration, data) = ReadSeries(rig, header, sequenceIndex, sequenceData, label); data = ValueData(rig, data, valueOffset, valueCount, 8);
        Require(data, valueOffset, valueCount, 8, label + " values");
        var values = new Quaternion[valueCount];
        for (var index = 0; index < values.Length; index++)
        {
            var offset = valueOffset + index * 8;
            var value = new Quaternion(Unpack(UShort(data, offset)), Unpack(UShort(data, offset + 2)), Unpack(UShort(data, offset + 4)), Unpack(UShort(data, offset + 6)));
            values[index] = value.LengthSquared() > 0.0000001f ? Quaternion.Normalize(value) : Quaternion.Identity;
        }
        MatchCounts(times, values.Length, label); return new(header.Interpolation, times, values, globalDuration);
    }

    internal static M2ScalarTrack ParseScalarTrack(M2AnimationRig rig, M2TrackHeader header, int sequenceIndex, byte[] sequenceData, string label)
    {
        var (times, valueCount, valueOffset, globalDuration, data) = ReadSeries(rig, header, sequenceIndex, sequenceData, label); data = ValueData(rig, data, valueOffset, valueCount, 4);
        Require(data, valueOffset, valueCount, 4, label + " values"); var values = new float[valueCount];
        for (var index = 0; index < values.Length; index++) { values[index] = Single(data, valueOffset + index * 4); if (!float.IsFinite(values[index])) throw new InvalidDataException($"M2 {label} key {index:N0} is non-finite."); }
        MatchCounts(times, values.Length, label); return new(header.Interpolation, times, values, globalDuration);
    }

    private static M2IntegerTrack ParseIntegerTrack(M2AnimationRig rig, M2TrackHeader header, int sequenceIndex, byte[] sequenceData, string label)
    {
        var (times, valueCount, valueOffset, globalDuration, data) = ReadSeries(rig, header, sequenceIndex, sequenceData, label); data = ValueData(rig, data, valueOffset, valueCount, 4);
        Require(data, valueOffset, valueCount, 4, label + " values"); var values = new int[valueCount];
        for (var index = 0; index < values.Length; index++) values[index] = BitConverter.ToInt32(data, valueOffset + index * 4);
        MatchCounts(times, values.Length, label); return new(header.Interpolation, times, values, globalDuration);
    }

    internal static M2ScalarTrack ParseUnsignedShortScalarTrack(M2AnimationRig rig, M2TrackHeader header, int sequenceIndex, byte[] sequenceData, string label)
    {
        var (times, valueCount, valueOffset, globalDuration, data) = ReadSeries(rig, header, sequenceIndex, sequenceData, label); data = ValueData(rig, data, valueOffset, valueCount, 2);
        Require(data, valueOffset, valueCount, 2, label + " values"); var values = new float[valueCount];
        for (var index = 0; index < values.Length; index++) values[index] = UShort(data, valueOffset + index * 2);
        MatchCounts(times, values.Length, label); return new(header.Interpolation, times, values, globalDuration);
    }

    private static (uint[] Times, int ValueCount, int ValueOffset, uint? GlobalDuration, byte[] Data) ReadSeries(M2AnimationRig rig, M2TrackHeader header, int sequenceIndex, byte[] sequenceData, string label)
    {
        if (header.Interpolation > 1) throw new NotSupportedException($"M2 {label} uses interpolation {header.Interpolation}; the safe Wrath preview currently supports none and linear tracks.");
        var series = header.GlobalSequence >= 0 ? 0 : sequenceIndex;
        var data = header.GlobalSequence >= 0 ? rig.ModelData : sequenceData;
        if ((uint)series >= header.TimestampSeriesCount || (uint)series >= header.ValueSeriesCount) return ([], 0, 0, GlobalDuration(), data);
        var timeEntry = checked((int)header.TimestampSeriesOffset + series * 8); var valueEntry = checked((int)header.ValueSeriesOffset + series * 8);
        Require(rig.ModelData, timeEntry, 1, 8, label + " timestamp series"); Require(rig.ModelData, valueEntry, 1, 8, label + " value series");
        var timeCount = CheckedKeyCount(UInt(rig.ModelData, timeEntry), label + " timestamps"); var timeOffset = checked((int)UInt(rig.ModelData, timeEntry + 4));
        var valueCount = CheckedKeyCount(UInt(rig.ModelData, valueEntry), label + " values"); var valueOffset = checked((int)UInt(rig.ModelData, valueEntry + 4));
        if (!HasRange(data, timeOffset, timeCount, 4) && !ReferenceEquals(data, rig.ModelData) && HasRange(rig.ModelData, timeOffset, timeCount, 4)) data = rig.ModelData;
        Require(data, timeOffset, timeCount, 4, label + " timestamps");
        var times = new uint[timeCount]; for (var index = 0; index < times.Length; index++) times[index] = UInt(data, timeOffset + index * 4);
        for (var index = 1; index < times.Length; index++) if (times[index] < times[index - 1]) throw new InvalidDataException($"M2 {label} timestamps are not sorted.");
        return (times, valueCount, valueOffset, GlobalDuration(), data);
        uint? GlobalDuration() => header.GlobalSequence >= 0 ? rig.GlobalSequenceDurations[header.GlobalSequence] : null;
    }

    internal static Vector3 Sample(M2VectorTrack track, double sequenceTime, double elapsedTime, Vector3 fallback)
    {
        if (track.Values.Length == 0) return fallback;
        var time = TrackTime(track.GlobalDuration, sequenceTime, elapsedTime); var (left, right, amount) = Keys(track.Times, time, track.Interpolation);
        return left == right ? track.Values[left] : Vector3.Lerp(track.Values[left], track.Values[right], amount);
    }

    private static Quaternion Sample(M2QuaternionTrack track, double sequenceTime, double elapsedTime, Quaternion fallback)
    {
        if (track.Values.Length == 0) return fallback;
        var time = TrackTime(track.GlobalDuration, sequenceTime, elapsedTime); var (left, right, amount) = Keys(track.Times, time, track.Interpolation);
        return left == right ? track.Values[left] : Quaternion.Normalize(Quaternion.Slerp(track.Values[left], track.Values[right], amount));
    }

    internal static float Sample(M2ScalarTrack track, double sequenceTime, double elapsedTime, float fallback)
    {
        if (track.Values.Length == 0) return fallback;
        var time = TrackTime(track.GlobalDuration, sequenceTime, elapsedTime); var (left, right, amount) = Keys(track.Times, time, track.Interpolation);
        return left == right ? track.Values[left] : track.Values[left] + (track.Values[right] - track.Values[left]) * amount;
    }

    internal static int Sample(M2IntegerTrack track, double sequenceTime, double elapsedTime, int fallback)
    {
        if (track.Values.Length == 0) return fallback;
        var time = TrackTime(track.GlobalDuration, sequenceTime, elapsedTime); var (left, _, _) = Keys(track.Times, time, 0); return track.Values[left];
    }

    private static byte[] ValueData(M2AnimationRig rig, byte[] selected, int offset, int count, int stride)
        => !HasRange(selected, offset, count, stride) && !ReferenceEquals(selected, rig.ModelData) && HasRange(rig.ModelData, offset, count, stride) ? rig.ModelData : selected;

    private static bool HasRange(byte[] data, int offset, int count, int stride) => offset >= 0 && count >= 0 && (long)offset + (long)count * stride <= data.LongLength;

    private static double TrackTime(uint? globalDuration, double sequenceTime, double elapsedTime) => globalDuration is { } duration && duration > 0 ? PositiveModulo(elapsedTime, duration) : sequenceTime;
    private static (int Left, int Right, float Amount) Keys(uint[] times, double time, ushort interpolation)
    {
        if (times.Length <= 1 || time <= times[0]) return (0, 0, 0);
        var right = Array.BinarySearch(times, (uint)Math.Min(uint.MaxValue, Math.Floor(time)));
        if (right >= 0) return (right, right, 0);
        right = ~right; if (right >= times.Length) return (times.Length - 1, times.Length - 1, 0);
        var left = right - 1; if (interpolation == 0 || times[right] == times[left]) return (left, left, 0);
        return (left, right, Math.Clamp((float)((time - times[left]) / (times[right] - times[left])), 0, 1));
    }

    internal static int ResolveAlias(IReadOnlyList<M2PreviewSequence> sequences, int index)
    {
        var visited = new HashSet<int>();
        while (sequences[index].IsAlias)
        {
            if (!visited.Add(index)) throw new InvalidDataException($"M2 animation alias chain contains a cycle at sequence {index:N0}.");
            var next = sequences[index].AliasSequence;
            if (next >= sequences.Count) throw new InvalidDataException($"M2 animation sequence {index:N0} aliases missing sequence {next:N0}.");
            index = next;
        }
        return index;
    }

    internal static M2TrackHeader ReadTrack(byte[] model, int offset, int globalCount, string label)
    {
        Require(model, offset, 1, 20, "M2 " + label);
        var header = new M2TrackHeader(UShort(model, offset), Short(model, offset + 2), UInt(model, offset + 4), UInt(model, offset + 8), UInt(model, offset + 12), UInt(model, offset + 16));
        if (header.TimestampSeriesCount == 0 && header.ValueSeriesCount == 0) header = header with { GlobalSequence = -1 };
        else if (header.GlobalSequence < -1 || header.GlobalSequence >= globalCount) throw new InvalidDataException($"M2 {label} references invalid global sequence {header.GlobalSequence:N0}.");
        if (header.TimestampSeriesCount > MaximumSequences || header.ValueSeriesCount > MaximumSequences) throw new InvalidDataException($"M2 {label} contains too many nested animation series.");
        Require(model, checked((int)header.TimestampSeriesOffset), checked((int)header.TimestampSeriesCount), 8, "M2 " + label + " timestamp series");
        Require(model, checked((int)header.ValueSeriesOffset), checked((int)header.ValueSeriesCount), 8, "M2 " + label + " value series");
        return header;
    }

    private static void ValidateHierarchy(IReadOnlyList<M2PreviewBone> bones)
    {
        for (var start = 0; start < bones.Count; start++)
        {
            var visited = new HashSet<int>(); var current = start;
            while (current >= 0) { if (!visited.Add(current)) throw new InvalidDataException($"M2 bone hierarchy contains a cycle at bone {current:N0}."); current = bones[current].ParentIndex; }
        }
    }

    private static void MatchCounts(uint[] times, int valueCount, string label) { if (times.Length != valueCount) throw new InvalidDataException($"M2 {label} has {times.Length:N0} timestamps but {valueCount:N0} values."); }
    private static int CheckedKeyCount(uint value, string label) => value > MaximumKeysPerTrack ? throw new InvalidDataException($"M2 {label} count {value:N0} exceeds the safety limit.") : checked((int)value);
    private static double PositiveModulo(double value, uint divisor) { var result = value % divisor; return result < 0 ? result + divisor : result; }
    private static float Unpack(ushort value) => Math.Clamp(value / 32767f - 1f, -1f, 1f);
    private static int Count(byte[] data, int offset, int maximum, string label) { var value = UInt(data, offset); return value > maximum ? throw new InvalidDataException($"{label} count {value:N0} exceeds {maximum:N0}.") : checked((int)value); }
    private static int Offset(byte[] data, int offset, string label) { var value = UInt(data, offset); return value > int.MaxValue ? throw new InvalidDataException($"{label} offset is unsupported.") : (int)value; }
    private static void Require(byte[] data, int offset, int count, int stride, string label) { var end = (long)offset + (long)count * stride; if (offset < 0 || count < 0 || end > data.LongLength) throw new InvalidDataException($"{label} range ({offset:N0}..{end:N0}) exceeds the {data.LongLength:N0}-byte source."); }
    private static uint UInt(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort UShort(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
    private static short Short(byte[] data, int offset) => BitConverter.ToInt16(data, offset);
    private static float Single(byte[] data, int offset) => BitConverter.ToSingle(data, offset);
    private static Vector3 Vector(byte[] data, int offset) => new(Single(data, offset), Single(data, offset + 4), Single(data, offset + 8));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Matrix4x4 value) => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
