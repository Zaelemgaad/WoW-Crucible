using System.Numerics;

namespace WoWCrucible.Core;

public sealed record M2TextureSlot(int Index, uint Type, uint Flags, string? EmbeddedPath);
public sealed record M2PreviewRenderFlag(int Index, ushort Flags, ushort BlendMode)
{
    public bool Unlit => (Flags & 0x1) != 0;
    public bool Unfogged => (Flags & 0x2) != 0;
    public bool TwoSided => (Flags & 0x4) != 0;
    public bool NoDepthTest => (Flags & 0x10) != 0;
}
public sealed record M2PreviewBone(int Index, uint Flags, short ParentIndex, ushort SubmeshId, Vector3 Pivot);
public sealed record M2PreviewSequence(int Index, ushort AnimationId, ushort SubAnimationId, uint DurationMilliseconds, float MoveSpeed, uint Flags, short Probability, uint MinimumRepetitions, uint MaximumRepetitions, uint BlendMilliseconds, short NextSequence, ushort AliasSequence)
{
    public bool Loops => (Flags & 0x20) != 0;
    public bool IsAlias => (Flags & 0x40) != 0;
    public override string ToString() => $"{AnimationId:N0}:{SubAnimationId:N0} · {DurationMilliseconds:N0} ms";
}
public sealed record M2PreviewAttachment(int Index, uint Id, string Name, int BoneIndex, Vector3 Position, IReadOnlyList<int> LookupSlots)
{
    public override string ToString() => $"{Id:N0} · {Name} · bone {BoneIndex:N0}";
}
public sealed record M2PreviewCamera(int Index, int Type, float FieldOfViewRaw, float FarClip, float NearClip, Vector3 BasePosition, Vector3 BaseTarget, IReadOnlyList<int> LookupSlots)
{
    public float FieldOfViewDegrees => FieldOfViewRaw * 35f;
    public float FieldOfViewRadians => FieldOfViewDegrees * MathF.PI / 180f;
    public string Name => Type switch { 0 => "Portrait camera", 1 => "Character-info camera", _ => $"Camera type {Type:N0}" };
    public override string ToString() => $"{Name} · {FieldOfViewDegrees:0.#}°";
}
public sealed record M2PreviewLight(int Index, short Type, short BoneIndex, Vector3 Position)
{
    public string Name => Type switch { 0 => "Directional light", 1 => "Point light", _ => $"Light type {Type:N0}" };
    public override string ToString() => $"{Name} · bone {(BoneIndex < 0 ? "none" : BoneIndex.ToString("N0"))}";
}
public sealed record M2PreviewParticleEmitter(int Index, uint Flags, Vector3 Position, short BoneIndex, int TextureDefinitionIndex,
    byte BlendMode, byte EmitterType, ushort Rows, ushort Columns, IReadOnlyList<Vector4> LifeColors, IReadOnlyList<float> LifeSizes, float Rotation)
{
    public IReadOnlyList<int> TextureDefinitionIndices { get; init; } = [TextureDefinitionIndex];
    public bool UsesMultipleTextures => TextureDefinitionIndices.Count > 1;
    public string EmitterName => EmitterType switch { 1 => "Plane", 2 => "Sphere", 3 => "Spline", _ => $"Unknown {EmitterType:N0}" };
    public override string ToString() => $"{EmitterName} particle · bone {(BoneIndex < 0 ? "none" : BoneIndex.ToString("N0"))} · texture{(UsesMultipleTextures ? "s" : string.Empty)} {string.Join(',', TextureDefinitionIndices)}";
}
public sealed record M2PreviewRibbonEmitter(int Index, int BoneIndex, Vector3 Position, int TextureDefinitionIndex, int RenderFlagsIndex,
    ushort BlendMode, float EdgesPerSecond, float EdgeLifetimeSeconds, float EmissionAngle)
{
    public override string ToString() => $"Ribbon · bone {BoneIndex:N0} · texture {TextureDefinitionIndex:N0} · {EdgesPerSecond:0.##} edges/s for {EdgeLifetimeSeconds:0.###} s";
}
public enum M2PreviewVisibilityMode { BaseAppearance, AllGeosets }
public enum M2PreviewTextureCoordinateSource { Primary, Secondary, Environment, Unsupported }
public enum M2PreviewTextureStageBlend { Source, Modulate, Modulate2X, Add, AddNoAlpha, Unsupported }
public enum M2PreviewTextureCombinerKind { Standard, ExplicitOpaqueMod2xNaAlpha, ExplicitOpaqueAddAlpha, ExplicitModAddAlpha, ExplicitOpaqueMod2xNaAlphaAdd, Unsupported }
public sealed record M2PreviewTextureStage(int StageIndex, int TextureLookupIndex, int TextureDefinitionIndex,
    short TextureCoordinateLookup, M2PreviewTextureCoordinateSource CoordinateSource,
    int? TransparencyDefinitionIndex, int? TextureAnimationDefinitionIndex, M2PreviewTextureStageBlend Blend);
public sealed record M2PreviewTextureCombiner(string Name, bool Supported, bool Exact)
{
    public static M2PreviewTextureCombiner None { get; } = new("No textures", true, true);
    public M2PreviewTextureCombinerKind Kind { get; init; } = M2PreviewTextureCombinerKind.Standard;
}
public sealed record M2GeosetSelection(IReadOnlyDictionary<int, int> GroupVariants, string Source);
public sealed record M2PreviewSubmesh(int Index, ushort GeosetId, ushort Level, int VertexStart, int VertexCount, int TriangleStart, int TriangleIndexCount, bool Visible)
{
    public int GeosetGroup => M2GeosetCatalog.Group(GeosetId);
    public int GeosetVariant => M2GeosetCatalog.Variant(GeosetId);
    public string GeosetGroupName => M2GeosetCatalog.GroupName(GeosetGroup);
}
public sealed record M2PreviewMaterialUnit(int Index, byte Flags, sbyte PriorityPlane, ushort ShaderId, ushort SubmeshIndex, ushort SecondarySubmeshIndex, short ColorIndex, ushort RenderFlagsIndex, ushort TextureUnitLookupIndex, ushort TextureCount, ushort TextureLookupIndex, int TextureDefinitionIndex, ushort SecondaryTextureUnitLookupIndex, ushort TransparencyLookupIndex, ushort TextureAnimationLookupIndex)
{
    public IReadOnlyList<M2PreviewTextureStage> TextureStages { get; init; } = [];
    public M2PreviewTextureCombiner Combiner { get; init; } = M2PreviewTextureCombiner.None;
}
public sealed record M2PreviewBatch(int SubmeshIndex, ushort GeosetId, int TriangleStart, int TriangleIndexCount, int? MaterialUnitIndex, int? TextureDefinitionIndex)
{
    public ushort RenderFlags { get; init; }
    public ushort BlendMode { get; init; }
    public sbyte PriorityPlane { get; init; }
    public IReadOnlyList<M2PreviewTextureStage> TextureStages { get; init; } = [];
    public M2PreviewTextureCombiner Combiner { get; init; } = M2PreviewTextureCombiner.None;
}
public sealed record M2PreviewGeometry(string ModelPath, string SkinPath, IReadOnlyList<Vector3> Vertices, IReadOnlyList<Vector3> Normals, IReadOnlyList<Vector2> TextureCoordinates, IReadOnlyList<int> TriangleIndices, Vector3 Minimum, Vector3 Maximum, IReadOnlyList<M2TextureSlot> TextureSlots)
{
    public IReadOnlyList<M2PreviewSubmesh> Submeshes { get; init; } = [];
    public IReadOnlyList<M2PreviewMaterialUnit> MaterialUnits { get; init; } = [];
    public IReadOnlyList<M2PreviewRenderFlag> RenderFlags { get; init; } = [];
    public IReadOnlyList<M2PreviewBatch> Batches { get; init; } = [];
    public IReadOnlyList<M2PreviewBone> Bones { get; init; } = [];
    public IReadOnlyList<M2PreviewAttachment> Attachments { get; init; } = [];
    public IReadOnlyList<M2PreviewCamera> Cameras { get; init; } = [];
    public IReadOnlyList<M2PreviewLight> Lights { get; init; } = [];
    public IReadOnlyList<M2PreviewParticleEmitter> ParticleEmitters { get; init; } = [];
    public IReadOnlyList<M2PreviewRibbonEmitter> RibbonEmitters { get; init; } = [];
    public IReadOnlyList<int> UsedTextureDefinitionIndices { get; init; } = [];
    public IReadOnlyList<M2PreviewSequence> Sequences { get; init; } = [];
    public IReadOnlyList<Vector2> SecondaryTextureCoordinates { get; init; } = [];
    public int TotalTriangleIndices { get; init; } = TriangleIndices.Count;
    public M2PreviewVisibilityMode VisibilityMode { get; init; } = M2PreviewVisibilityMode.BaseAppearance;
    public M2GeosetSelection? GeosetSelection { get; init; }
    internal M2AnimationRig? AnimationRig { get; init; }
    internal M2ParticleRig? ParticleRig { get; init; }
    internal M2RibbonRig? RibbonRig { get; init; }
}

public static class M2PreviewGeometryService
{
    private const int VertexStride = 48;
    private const int MaximumVertices = 5_000_000;
    private const int MaximumTriangleIndices = 15_000_000;

    public static M2PreviewGeometry Load(string modelPath, string? skinPath = null, M2PreviewVisibilityMode visibilityMode = M2PreviewVisibilityMode.BaseAppearance,
        M2GeosetSelection? geosetSelection = null)
    {
        modelPath = Path.GetFullPath(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("The M2 model does not exist.", modelPath);
        var model = File.ReadAllBytes(modelPath);
        if (model.Length < 0x130 || FourCc(model, 0) != "MD20") throw new InvalidDataException("Embedded preview currently requires a complete unwrapped MD20 model header.");
        var version = ReadUInt(model, 4);
        if (version != 264) throw new NotSupportedException($"Embedded preview currently supports Wrath M2 version 264; this model is version {version}.");
        var vertexCount = CheckedCount(ReadUInt(model, 0x3C), MaximumVertices, "M2 vertex");
        var vertexOffset = CheckedOffset(ReadUInt(model, 0x40), "M2 vertex");
        RequireRange(model, vertexOffset, vertexCount, VertexStride, "M2 vertices");
        var vertices = new Vector3[vertexCount]; var normals = new Vector3[vertexCount]; var textureCoordinates = new Vector2[vertexCount]; var secondaryTextureCoordinates = new Vector2[vertexCount];
        var minimum = new Vector3(float.PositiveInfinity); var maximum = new Vector3(float.NegativeInfinity);
        for (var index = 0; index < vertexCount; index++)
        {
            var offset = vertexOffset + index * VertexStride;
            var vertex = ReadVector(model, offset); var normal = ReadVector(model, offset + 20);
            if (!Finite(vertex) || !Finite(normal)) throw new InvalidDataException($"M2 vertex {index:N0} contains a non-finite coordinate.");
            var uv = new Vector2(BitConverter.ToSingle(model, offset + 32), BitConverter.ToSingle(model, offset + 36));
            var secondaryUv = new Vector2(BitConverter.ToSingle(model, offset + 40), BitConverter.ToSingle(model, offset + 44));
            vertices[index] = vertex; normals[index] = normal; textureCoordinates[index] = Finite(uv) ? uv : Vector2.Zero; secondaryTextureCoordinates[index] = Finite(secondaryUv) ? secondaryUv : Vector2.Zero; minimum = Vector3.Min(minimum, vertex); maximum = Vector3.Max(maximum, vertex);
        }

        var bones = ReadBones(model);
        var animationRig = M2AnimationService.ParseRig(modelPath, model, vertexOffset, vertexCount, bones);
        var attachments = ReadAttachments(model, bones.Count);
        var textureSlots = ReadTextureSlots(model);
        var particleRig = M2ParticlePreviewService.Parse(model, animationRig, bones.Count, textureSlots.Count);
        var renderFlags = ReadRenderFlags(model);
        var ribbonRig = M2RibbonPreviewService.Parse(model, animationRig, bones.Count, textureSlots.Count, renderFlags);
        var textureLookup = ReadTextureLookup(model, textureSlots.Count);
        var textureCoordinateLookup = ReadSignedLookup(model, 0x88, 0x8C, "M2 texture-coordinate lookup");
        var transparencyLookup = ReadUnsignedLookup(model, 0x90, 0x94, "M2 transparency lookup");
        var textureAnimationLookup = ReadUnsignedLookup(model, 0x98, 0x9C, "M2 texture-animation lookup");
        skinPath = ResolveSkin(modelPath, skinPath);
        var skin = File.ReadAllBytes(skinPath);
        if (skin.Length < 48 || FourCc(skin, 0) != "SKIN") throw new InvalidDataException("The companion file is not a valid SKIN container.");
        var lookupCount = CheckedCount(ReadUInt(skin, 4), MaximumVertices, "skin vertex lookup"); var lookupOffset = CheckedOffset(ReadUInt(skin, 8), "skin vertex lookup");
        var triangleIndexCount = CheckedCount(ReadUInt(skin, 12), MaximumTriangleIndices, "skin triangle index"); var triangleOffset = CheckedOffset(ReadUInt(skin, 16), "skin triangle");
        if (triangleIndexCount % 3 != 0) throw new InvalidDataException($"SKIN triangle index count {triangleIndexCount:N0} is not divisible by three.");
        RequireRange(skin, lookupOffset, lookupCount, 2, "SKIN vertex lookup"); RequireRange(skin, triangleOffset, triangleIndexCount, 2, "SKIN triangles");
        var lookup = new ushort[lookupCount]; for (var index = 0; index < lookup.Length; index++) lookup[index] = ReadUShort(skin, lookupOffset + index * 2);
        var allTriangles = new int[triangleIndexCount];
        for (var index = 0; index < allTriangles.Length; index++)
        {
            var lookupIndex = ReadUShort(skin, triangleOffset + index * 2);
            if (lookupIndex >= lookup.Length) throw new InvalidDataException($"SKIN triangle entry {index:N0} references lookup {lookupIndex:N0}, but only {lookup.Length:N0} entries exist.");
            var vertexIndex = lookup[lookupIndex];
            if (vertexIndex >= vertices.Length) throw new InvalidDataException($"SKIN lookup {lookupIndex:N0} references M2 vertex {vertexIndex:N0}, but only {vertices.Length:N0} vertices exist.");
            allTriangles[index] = vertexIndex;
        }
        var materialUnits = ReadMaterialUnits(skin, textureLookup, textureCoordinateLookup, transparencyLookup, textureAnimationLookup, textureSlots.Count, renderFlags.Count);
        var (submeshes, triangles, batches) = ReadVisibleSubmeshes(skin, allTriangles, materialUnits, renderFlags, visibilityMode, geosetSelection);
        if (triangles.Length > 0)
        {
            minimum = new Vector3(float.PositiveInfinity); maximum = new Vector3(float.NegativeInfinity);
            foreach (var vertexIndex in triangles) { minimum = Vector3.Min(minimum, vertices[vertexIndex]); maximum = Vector3.Max(maximum, vertices[vertexIndex]); }
        }
        return new(modelPath, skinPath, vertices, normals, textureCoordinates, triangles, minimum, maximum, textureSlots)
        {
            Submeshes = submeshes,
            MaterialUnits = materialUnits,
            RenderFlags = renderFlags,
            Batches = batches,
            Bones = bones,
            Attachments = attachments,
            Cameras = animationRig.PreviewCameras,
            Lights = animationRig.PreviewLights,
            ParticleEmitters = particleRig.Emitters,
            RibbonEmitters = ribbonRig.Emitters,
            UsedTextureDefinitionIndices = batches.SelectMany(batch => batch.TextureStages).Where(stage => stage.TextureDefinitionIndex >= 0).Select(stage => stage.TextureDefinitionIndex)
                .Concat(batches.Where(batch => batch.TextureStages.Count == 0 && batch.TextureDefinitionIndex is not null).Select(batch => batch.TextureDefinitionIndex!.Value))
                .Concat(particleRig.Emitters.SelectMany(emitter => emitter.TextureDefinitionIndices)).Concat(ribbonRig.Emitters.Where(emitter => emitter.TextureDefinitionIndex >= 0).Select(emitter => emitter.TextureDefinitionIndex)).Distinct().Order().ToArray(),
            Sequences = animationRig.Sequences,
            SecondaryTextureCoordinates = secondaryTextureCoordinates,
            TotalTriangleIndices = allTriangles.Length,
            VisibilityMode = visibilityMode,
            GeosetSelection = geosetSelection,
            AnimationRig = animationRig,
            ParticleRig = particleRig,
            RibbonRig = ribbonRig
        };
    }

    public static IReadOnlyList<M2TextureSlot> InspectTextureSlots(string modelPath)
    {
        modelPath = Path.GetFullPath(modelPath); if (!File.Exists(modelPath)) throw new FileNotFoundException("The M2 model does not exist.", modelPath);
        var model = File.ReadAllBytes(modelPath); if (model.Length < 8 || FourCc(model, 0) != "MD20" || ReadUInt(model, 4) != 264) throw new InvalidDataException("Texture-slot inspection requires an unwrapped Wrath MD20 version 264 model.");
        return ReadTextureSlots(model);
    }

    private static IReadOnlyList<M2TextureSlot> ReadTextureSlots(byte[] model)
    {
        const int CountOffset = 0x50; const int DataOffset = 0x54; const int TextureStride = 16; const int MaximumTextures = 4096;
        if (model.Length < DataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, CountOffset), MaximumTextures, "M2 texture"); var offset = CheckedOffset(ReadUInt(model, DataOffset), "M2 texture");
        RequireRange(model, offset, count, TextureStride, "M2 textures"); var result = new M2TextureSlot[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * TextureStride; var type = ReadUInt(model, item); var flags = ReadUInt(model, item + 4); var nameLength = ReadUInt(model, item + 8); var nameOffset = ReadUInt(model, item + 12); string? path = null;
            if (type == 0 && nameLength > 0)
            {
                var length = CheckedCount(nameLength, 1024 * 1024, "M2 texture name"); var start = CheckedOffset(nameOffset, "M2 texture name"); RequireRange(model, start, length, 1, "M2 texture name");
                path = System.Text.Encoding.UTF8.GetString(model, start, length).TrimEnd('\0');
            }
            result[index] = new(index, type, flags, path);
        }
        return result;
    }

    private static IReadOnlyList<M2PreviewRenderFlag> ReadRenderFlags(byte[] model)
    {
        const int CountOffset = 0x70; const int DataOffset = 0x74; const int RenderFlagStride = 4; const int MaximumRenderFlags = 65_536;
        if (model.Length < DataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, CountOffset), MaximumRenderFlags, "M2 render flag"); var offset = CheckedOffset(ReadUInt(model, DataOffset), "M2 render flag");
        RequireRange(model, offset, count, RenderFlagStride, "M2 render flags"); var result = new M2PreviewRenderFlag[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * RenderFlagStride; var blendMode = ReadUShort(model, item + 2);
            if (blendMode > 7) throw new InvalidDataException($"M2 render flag {index:N0} uses unsupported blend mode {blendMode:N0}.");
            result[index] = new(index, ReadUShort(model, item), blendMode);
        }
        return result;
    }

    private static IReadOnlyList<M2PreviewBone> ReadBones(byte[] model)
    {
        const int CountOffset = 0x2C; const int DataOffset = 0x30; const int BoneStride = 88; const int MaximumBones = 65_536;
        if (model.Length < DataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, CountOffset), MaximumBones, "M2 bone");
        var offset = CheckedOffset(ReadUInt(model, DataOffset), "M2 bone");
        RequireRange(model, offset, count, BoneStride, "M2 bones");
        var result = new M2PreviewBone[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * BoneStride;
            var parent = ReadShort(model, item + 8);
            if (parent < -1 || parent >= count) throw new InvalidDataException($"M2 bone {index:N0} has invalid parent {parent:N0}; the model contains {count:N0} bones.");
            var pivot = ReadVector(model, item + 76);
            if (!Finite(pivot)) throw new InvalidDataException($"M2 bone {index:N0} contains a non-finite pivot.");
            result[index] = new(index, ReadUInt(model, item + 4), parent, ReadUShort(model, item + 10), pivot);
        }
        return result;
    }

    private static IReadOnlyList<M2PreviewAttachment> ReadAttachments(byte[] model, int boneCount)
    {
        const int CountOffset = 0xF0; const int DataOffset = 0xF4; const int AttachmentStride = 40; const int MaximumAttachments = 4096;
        const int LookupCountOffset = 0xF8; const int LookupDataOffset = 0xFC; const int MaximumLookups = 65_536;
        if (model.Length < LookupDataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, CountOffset), MaximumAttachments, "M2 attachment");
        var offset = CheckedOffset(ReadUInt(model, DataOffset), "M2 attachment");
        RequireRange(model, offset, count, AttachmentStride, "M2 attachments");
        var lookupCount = CheckedCount(ReadUInt(model, LookupCountOffset), MaximumLookups, "M2 attachment lookup");
        var lookupOffset = CheckedOffset(ReadUInt(model, LookupDataOffset), "M2 attachment lookup");
        RequireRange(model, lookupOffset, lookupCount, 2, "M2 attachment lookup");
        var lookupSlots = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (var slot = 0; slot < lookupCount; slot++)
        {
            var attachmentIndex = ReadShort(model, lookupOffset + slot * 2);
            if (attachmentIndex == -1) continue;
            if (attachmentIndex < 0 || attachmentIndex >= count)
                throw new InvalidDataException($"M2 attachment lookup slot {slot:N0} references attachment {attachmentIndex:N0}, but only {count:N0} records exist.");
            lookupSlots[attachmentIndex].Add(slot);
        }
        var result = new M2PreviewAttachment[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * AttachmentStride;
            var id = ReadUInt(model, item); var rawBone = ReadUInt(model, item + 4);
            if (rawBone >= boneCount) throw new InvalidDataException($"M2 attachment {index:N0} ({M2AttachmentCatalog.Name(id)}) references bone {rawBone:N0}, but only {boneCount:N0} bones exist.");
            var position = ReadVector(model, item + 8);
            if (!Finite(position)) throw new InvalidDataException($"M2 attachment {index:N0} contains a non-finite position.");
            result[index] = new(index, id, M2AttachmentCatalog.Name(id), checked((int)rawBone), position, lookupSlots[index].ToArray());
        }
        return result;
    }

    private static ushort[] ReadTextureLookup(byte[] model, int textureCount)
    {
        const int CountOffset = 0x80; const int DataOffset = 0x84; const int MaximumLookups = 65_536;
        if (model.Length < DataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, CountOffset), MaximumLookups, "M2 texture lookup");
        var offset = CheckedOffset(ReadUInt(model, DataOffset), "M2 texture lookup");
        RequireRange(model, offset, count, 2, "M2 texture lookup");
        var result = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            var value = ReadUShort(model, offset + index * 2);
            if (value >= textureCount) throw new InvalidDataException($"M2 texture lookup {index:N0} references texture definition {value:N0}, but only {textureCount:N0} definitions exist.");
            result[index] = value;
        }
        return result;
    }

    private static short[] ReadSignedLookup(byte[] model, int countOffset, int dataOffset, string label)
    {
        const int MaximumLookups = 65_536;
        if (model.Length < dataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, countOffset), MaximumLookups, label);
        var offset = CheckedOffset(ReadUInt(model, dataOffset), label);
        RequireRange(model, offset, count, 2, label);
        var result = new short[count];
        for (var index = 0; index < count; index++) result[index] = ReadShort(model, offset + index * 2);
        return result;
    }

    private static ushort[] ReadUnsignedLookup(byte[] model, int countOffset, int dataOffset, string label)
    {
        const int MaximumLookups = 65_536;
        if (model.Length < dataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(model, countOffset), MaximumLookups, label);
        var offset = CheckedOffset(ReadUInt(model, dataOffset), label);
        RequireRange(model, offset, count, 2, label);
        var result = new ushort[count];
        for (var index = 0; index < count; index++) result[index] = ReadUShort(model, offset + index * 2);
        return result;
    }

    private static IReadOnlyList<M2PreviewMaterialUnit> ReadMaterialUnits(byte[] skin, IReadOnlyList<ushort> textureLookup,
        IReadOnlyList<short> textureCoordinateLookup, IReadOnlyList<ushort> transparencyLookup,
        IReadOnlyList<ushort> textureAnimationLookup, int textureCount, int renderFlagCount)
    {
        const int CountOffset = 36; const int DataOffset = 40; const int MaterialStride = 24; const int MaximumMaterials = 131_072;
        if (skin.Length < DataOffset + 4) return [];
        var count = CheckedCount(ReadUInt(skin, CountOffset), MaximumMaterials, "SKIN material unit");
        var offset = CheckedOffset(ReadUInt(skin, DataOffset), "SKIN material unit");
        RequireRange(skin, offset, count, MaterialStride, "SKIN material units");
        var result = new M2PreviewMaterialUnit[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * MaterialStride;
            var textureLookupIndex = ReadUShort(skin, item + 16);
            var renderFlagsIndex = ReadUShort(skin, item + 10);
            var stageCount = ReadUShort(skin, item + 14);
            if (stageCount > 64) throw new InvalidDataException($"SKIN material unit {index:N0} declares {stageCount:N0} texture stages; the WotLK preview safety limit is 64.");
            var shaderId = ReadUShort(skin, item + 2);
            var textureCoordinateLookupIndex = ReadUShort(skin, item + 18);
            var transparencyLookupIndex = ReadUShort(skin, item + 20);
            var textureAnimationLookupIndex = ReadUShort(skin, item + 22);
            if (renderFlagCount > 0 && renderFlagsIndex >= renderFlagCount) throw new InvalidDataException($"SKIN material unit {index:N0} references render flag {renderFlagsIndex:N0}, but only {renderFlagCount:N0} records exist.");
            var combiner = DescribeCombiner(shaderId, stageCount);
            var stages = new M2PreviewTextureStage[stageCount];
            for (var stage = 0; stage < stages.Length; stage++)
            {
                var lookupIndex = textureLookupIndex + stage;
                var definitionIndex = lookupIndex < textureLookup.Count ? textureLookup[lookupIndex] : textureLookup.Count == 0 && lookupIndex < textureCount ? lookupIndex : -1;
                var coordinateIndex = textureCoordinateLookupIndex + stage;
                var coordinate = coordinateIndex < textureCoordinateLookup.Count ? textureCoordinateLookup[coordinateIndex] : short.MinValue;
                var source = coordinate switch
                {
                    0 => M2PreviewTextureCoordinateSource.Primary,
                    1 => M2PreviewTextureCoordinateSource.Secondary,
                    -1 => M2PreviewTextureCoordinateSource.Environment,
                    _ => M2PreviewTextureCoordinateSource.Unsupported
                };
                source = ExplicitCoordinateSource(combiner.Kind, stage, source);
                int? transparencyIndex = transparencyLookupIndex + stage < transparencyLookup.Count && transparencyLookup[transparencyLookupIndex + stage] != ushort.MaxValue
                    ? transparencyLookup[transparencyLookupIndex + stage]
                    : null;
                int? animationIndex = textureAnimationLookupIndex + stage < textureAnimationLookup.Count && textureAnimationLookup[textureAnimationLookupIndex + stage] != ushort.MaxValue
                    ? textureAnimationLookup[textureAnimationLookupIndex + stage]
                    : null;
                stages[stage] = new(stage, lookupIndex, definitionIndex, coordinate, source, transparencyIndex, animationIndex,
                    StageBlend(combiner, stage));
            }
            var textureDefinitionIndex = stages.FirstOrDefault()?.TextureDefinitionIndex ?? -1;
            var supported = combiner.Supported && stages.All(stage => stage.TextureDefinitionIndex >= 0 && stage.CoordinateSource is M2PreviewTextureCoordinateSource.Primary or M2PreviewTextureCoordinateSource.Secondary or M2PreviewTextureCoordinateSource.Environment);
            if (supported != combiner.Supported) combiner = combiner with { Supported = false, Exact = false };
            result[index] = new(index, skin[item], unchecked((sbyte)skin[item + 1]), shaderId, ReadUShort(skin, item + 4), ReadUShort(skin, item + 6),
                ReadShort(skin, item + 8), renderFlagsIndex, ReadUShort(skin, item + 12), stageCount, textureLookupIndex,
                textureDefinitionIndex, textureCoordinateLookupIndex, transparencyLookupIndex, textureAnimationLookupIndex)
            {
                TextureStages = stages,
                Combiner = combiner
            };
        }
        return result;
    }

    internal static M2PreviewTextureCombiner DescribeCombiner(ushort shaderId, int textureCount)
    {
        if (textureCount <= 0) return M2PreviewTextureCombiner.None;
        if ((shaderId & 0x8000) != 0)
        {
            if ((shaderId & 0x7FFF) == 3 && textureCount == 3)
                return new("Opaque_Mod2xNA_Alpha_Add", true, false) { Kind = M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlphaAdd };
            if (textureCount == 2)
                return (shaderId & 0x7FFF) switch
                {
                    0 => new("Opaque_Mod2xNA_Alpha", true, false) { Kind = M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlpha },
                    1 => new("Opaque_AddAlpha", true, false) { Kind = M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlpha },
                    6 => new("Mod_AddAlpha", true, false) { Kind = M2PreviewTextureCombinerKind.ExplicitModAddAlpha },
                    _ => new($"Explicit shader {shaderId & 0x7FFF}", false, false) { Kind = M2PreviewTextureCombinerKind.Unsupported }
                };
            return new($"Explicit shader {shaderId & 0x7FFF} ({textureCount:N0} stages)", false, false) { Kind = M2PreviewTextureCombinerKind.Unsupported };
        }
        var first = (shaderId & 0x70) != 0 ? "Mod" : "Opaque";
        if (textureCount == 1) return new(first, true, true);
        if (textureCount != 2) return new($"{first} + {textureCount - 1:N0} legacy stage(s)", false, false);
        var second = (shaderId & 0x7) switch
        {
            0 => "Opaque",
            3 => "Add",
            4 => "Mod2x",
            6 => "Mod2xNA",
            7 => first == "Opaque" ? "AddAlpha" : "AddNA",
            _ => "Mod"
        };
        // The isolated Skia passes reproduce the legacy RGB operation and declared UV route,
        // but several client combiners choose alpha from one specific stage rather than using
        // the canvas operator's alpha result. Keep the public fidelity label conservative until
        // those per-combiner alpha equations are implemented as one runtime shader.
        return new($"{first}_{second}", true, false);
    }

    private static M2PreviewTextureCoordinateSource ExplicitCoordinateSource(M2PreviewTextureCombinerKind kind, int stage, M2PreviewTextureCoordinateSource fallback) => kind switch
    {
        M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlpha or M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlpha => stage == 0 ? M2PreviewTextureCoordinateSource.Primary : M2PreviewTextureCoordinateSource.Environment,
        M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlphaAdd => stage == 1 ? M2PreviewTextureCoordinateSource.Environment : M2PreviewTextureCoordinateSource.Primary,
        M2PreviewTextureCombinerKind.ExplicitModAddAlpha => M2PreviewTextureCoordinateSource.Primary,
        _ => fallback
    };

    private static M2PreviewTextureStageBlend StageBlend(M2PreviewTextureCombiner combiner, int stage)
    {
        if (stage == 0) return M2PreviewTextureStageBlend.Source;
        if (combiner.Kind == M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlphaAdd) return stage switch { 1 => M2PreviewTextureStageBlend.Modulate2X, 2 => M2PreviewTextureStageBlend.Add, _ => M2PreviewTextureStageBlend.Unsupported };
        if (stage > 1) return M2PreviewTextureStageBlend.Unsupported;
        if (combiner.Kind == M2PreviewTextureCombinerKind.ExplicitOpaqueMod2xNaAlpha) return M2PreviewTextureStageBlend.Modulate2X;
        if (combiner.Kind is M2PreviewTextureCombinerKind.ExplicitOpaqueAddAlpha or M2PreviewTextureCombinerKind.ExplicitModAddAlpha) return M2PreviewTextureStageBlend.Add;
        var suffix = combiner.Name[(combiner.Name.LastIndexOf('_') + 1)..];
        return suffix switch
        {
            "Opaque" or "Mod" => M2PreviewTextureStageBlend.Modulate,
            "Mod2x" => M2PreviewTextureStageBlend.Modulate2X,
            "Mod2xNA" => M2PreviewTextureStageBlend.Modulate2X,
            "Add" or "AddAlpha" => M2PreviewTextureStageBlend.Add,
            "AddNA" => M2PreviewTextureStageBlend.AddNoAlpha,
            _ => M2PreviewTextureStageBlend.Unsupported
        };
    }

    private static (IReadOnlyList<M2PreviewSubmesh> Submeshes, int[] Triangles, IReadOnlyList<M2PreviewBatch> Batches) ReadVisibleSubmeshes(byte[] skin, int[] allTriangles, IReadOnlyList<M2PreviewMaterialUnit> materialUnits, IReadOnlyList<M2PreviewRenderFlag> renderFlags, M2PreviewVisibilityMode visibilityMode, M2GeosetSelection? geosetSelection)
    {
        const int CountOffset = 28; const int DataOffset = 32; const int SubmeshStride = 48; const int MaximumSubmeshes = 131_072;
        if (skin.Length < DataOffset + 4) return ([], allTriangles, [new(0, 0, 0, allTriangles.Length, null, null)]);
        var count = CheckedCount(ReadUInt(skin, CountOffset), MaximumSubmeshes, "SKIN submesh");
        var offset = CheckedOffset(ReadUInt(skin, DataOffset), "SKIN submesh");
        if (count == 0) return ([], allTriangles, [new(0, 0, 0, allTriangles.Length, null, null)]);
        RequireRange(skin, offset, count, SubmeshStride, "SKIN submeshes");

        var raw = new (ushort Id, ushort Level, int VertexStart, int VertexCount, int TriangleStart, int TriangleCount)[count];
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * SubmeshStride;
            var triangleStart = ReadUShort(skin, item + 8); var triangleCount = ReadUShort(skin, item + 10);
            if (triangleStart + triangleCount > allTriangles.Length)
                throw new InvalidDataException($"SKIN submesh {index:N0} triangle range ({triangleStart:N0}..{triangleStart + triangleCount:N0}) exceeds the {allTriangles.Length:N0}-index triangle array.");
            if (triangleCount % 3 != 0) throw new InvalidDataException($"SKIN submesh {index:N0} triangle index count {triangleCount:N0} is not divisible by three.");
            raw[index] = (ReadUShort(skin, item), ReadUShort(skin, item + 2), ReadUShort(skin, item + 4), ReadUShort(skin, item + 6), triangleStart, triangleCount);
        }

        var visible = visibilityMode == M2PreviewVisibilityMode.AllGeosets
            ? Enumerable.Repeat(true, count).ToArray()
            : raw.Select(section => IsBaseAppearanceGeoset(section.Id)).ToArray();
        if (visibilityMode == M2PreviewVisibilityMode.BaseAppearance && geosetSelection is not null)
        {
            foreach (var (group, variant) in geosetSelection.GroupVariants)
            {
                if (group < 0 || group > ushort.MaxValue / 100 || variant < 0 || variant > 99) continue;
                for (var index = 0; index < raw.Length; index++)
                {
                    var id = raw[index].Id;
                    if (group == 0 ? id is > 0 and < 100 : id / 100 == group) visible[index] = false;
                }
                if (variant == 0) continue;
                var selectedId = group == 0 ? variant : checked(group * 100 + variant);
                for (var index = 0; index < raw.Length; index++) if (raw[index].Id == selectedId) visible[index] = true;
            }
        }
        if (visibilityMode == M2PreviewVisibilityMode.BaseAppearance && geosetSelection?.GroupVariants.ContainsKey(7) != true &&
            !raw.Where((section, index) => section.Id / 100 == 7 && visible[index]).Any())
        {
            // Wrath has race/sex models whose fixed built-in ears are not variant 01
            // (for example an HD replacement may contain 702 but no 701). When no
            // DBC/manual ear choice drives group 7, show the lowest real variant.
            var fixedEarId = raw.Where(section => section.Id / 100 == 7 && section.Id % 100 > 0).Select(section => section.Id).DefaultIfEmpty().Min();
            if (fixedEarId > 0) for (var index = 0; index < raw.Length; index++) if (raw[index].Id == fixedEarId) visible[index] = true;
        }
        if (!visible.Any(value => value))
        {
            var fallback = Array.FindIndex(raw, section => section.Id == 0);
            visible[fallback >= 0 ? fallback : 0] = true;
        }

        var submeshes = new M2PreviewSubmesh[count]; var triangles = new List<int>(allTriangles.Length); var batches = new List<M2PreviewBatch>(count);
        for (var index = 0; index < count; index++)
        {
            var section = raw[index];
            submeshes[index] = new(index, section.Id, section.Level, section.VertexStart, section.VertexCount, section.TriangleStart, section.TriangleCount, visible[index]);
            if (visible[index] && section.TriangleCount > 0)
            {
                var compactStart = triangles.Count;
                triangles.AddRange(allTriangles.AsSpan(section.TriangleStart, section.TriangleCount).ToArray());
                var materials = materialUnits.Where(unit => unit.SubmeshIndex == index).OrderBy(unit => unit.PriorityPlane).ThenBy(unit => unit.Index).ToArray();
                if (materials.Length == 0) batches.Add(new(index, section.Id, compactStart, section.TriangleCount, null, null));
                else foreach (var material in materials)
                {
                    var flags = material.RenderFlagsIndex < renderFlags.Count ? renderFlags[material.RenderFlagsIndex] : null;
                    batches.Add(new(index, section.Id, compactStart, section.TriangleCount, material.Index, material.TextureDefinitionIndex >= 0 ? material.TextureDefinitionIndex : null)
                    {
                        RenderFlags = flags?.Flags ?? 0,
                        BlendMode = flags?.BlendMode ?? 0,
                        PriorityPlane = material.PriorityPlane,
                        TextureStages = material.TextureStages,
                        Combiner = material.Combiner
                    });
                }
            }
        }
        return (submeshes, triangles.ToArray(), batches);
    }

    internal static bool IsBaseAppearanceGeoset(ushort geosetId)
    {
        if (geosetId == 0) return true;
        if (geosetId < 100) return false;
        var group = geosetId / 100;
        // These groups are driven by equipped items or explicit customization data.
        // Showing their *01 fallback on a naked character produces phantom cloaks,
        // belts, helms, attachments, and stacked modern customization geometry.
        if (group is 12 or 15 or 17 or 18 or 23 or 24 or 25 or 26 or 27 or 28 or 32 or 35) return false;
        return geosetId % 100 == 1;
    }

    private static string ResolveSkin(string modelPath, string? selected)
    {
        if (!string.IsNullOrWhiteSpace(selected))
        {
            var path = Path.IsPathFullyQualified(selected)
                ? Path.GetFullPath(selected)
                : Path.GetFullPath(selected, Path.GetDirectoryName(modelPath)!);
            if (!File.Exists(path)) throw new FileNotFoundException("The selected SKIN file does not exist.", path);
            return path;
        }
        var directory = Path.GetDirectoryName(modelPath)!; var stem = Path.GetFileNameWithoutExtension(modelPath);
        var exact = Path.Combine(directory, stem + "00.skin"); if (File.Exists(exact)) return exact;
        return Directory.EnumerateFiles(directory, stem + "*.skin", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            ?? throw new FileNotFoundException($"No companion {stem}00.skin file was found beside the model.");
    }

    private static void RequireRange(byte[] data, int offset, int count, int stride, string label)
    {
        var end = (long)offset + (long)count * stride;
        if (offset < 0 || end > data.LongLength) throw new InvalidDataException($"{label} range ({offset:N0}..{end:N0}) exceeds the {data.LongLength:N0}-byte file.");
    }
    private static int CheckedCount(uint value, int maximum, string label) => value > maximum ? throw new InvalidDataException($"{label} count {value:N0} exceeds the safety limit {maximum:N0}.") : checked((int)value);
    private static int CheckedOffset(uint value, string label) => value > int.MaxValue ? throw new InvalidDataException($"{label} offset {value:N0} is unsupported.") : (int)value;
    private static uint ReadUInt(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);
    private static ushort ReadUShort(byte[] data, int offset) => BitConverter.ToUInt16(data, offset);
    private static short ReadShort(byte[] data, int offset) => BitConverter.ToInt16(data, offset);
    private static Vector3 ReadVector(byte[] data, int offset) => new(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4), BitConverter.ToSingle(data, offset + 8));
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static string FourCc(byte[] data, int offset) => System.Text.Encoding.ASCII.GetString(data, offset, 4);
}
