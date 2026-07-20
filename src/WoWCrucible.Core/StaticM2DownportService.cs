using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public sealed record StaticM2DownportPlan(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    string SourceModelPath,
    string SourceModelSha256,
    string? SourceSkinPath,
    string? SourceSkinSha256,
    string? SourceListfilePath,
    string? SourceListfileSha256,
    IReadOnlyList<M2ResolvedTexturePath> ResolvedTexturePaths,
    uint SourceVersion,
    uint SourceFlags,
    uint OutputFlags,
    int VertexCount,
    int TriangleCount,
    int SubmeshCount,
    int MaterialCount,
    int ShadowBatchCount,
    int ConstantColorTrackCount,
    int GlobalSequenceCount,
    int AnimationSequenceCount,
    IReadOnlyList<string> Transformations,
    IReadOnlyList<string> Losses,
    IReadOnlyList<string> Blockers)
{
    public bool Ready => Blockers.Count == 0;
    public IReadOnlyList<short> OutputTextureCoordinateLookup { get; init; } = [];
    public IReadOnlyList<ushort> OutputBlendOverrides { get; init; } = [];
    public IReadOnlyList<ushort> OutputMaterialShaderIds { get; init; } = [];
    public IReadOnlyList<ushort> OutputTransparencyLookup { get; init; } = [];
    public IReadOnlyList<ushort> OutputTextureAnimationLookup { get; init; } = [];
    public IReadOnlyList<string> OutputMaterialCombiners { get; init; } = [];
    public IReadOnlyList<M2ParticleZSourceTranslation> ParticleZSourceTranslations { get; init; } = [];
    public int ParticleEmitterCount { get; init; }
    public bool UsesBlendOverrides => OutputBlendOverrides.Count > 0;
    public bool TranslatesMaterials => OutputMaterialCombiners.Count > 0;
}

public sealed record M2ResolvedTexturePath(int TextureIndex, uint FileDataId, string ClientPath);
public sealed record M2ParticleZSourceTranslation(int ParticleIndex, float Value);

public sealed record StaticM2DownportResult(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    StaticM2DownportPlan Plan,
    string OutputDirectory,
    string OutputModelPath,
    string OutputModelSha256,
    string OutputSkinPath,
    string OutputSkinSha256,
    string ReceiptPath,
    int ValidatedVertices,
    int ValidatedTriangles,
    int ValidatedSubmeshes,
    int ValidatedMaterials);

public enum StaticM2DownportScanStatus { ConversionReady, AlreadyWotlk335, Blocked, Failed }

public sealed record StaticM2DownportScanEntry(string Path, StaticM2DownportScanStatus Status, StaticM2DownportPlan? Plan, string? Error)
{
    public bool Ready => Status == StaticM2DownportScanStatus.ConversionReady;
}

public sealed record StaticM2DownportScanResult(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<StaticM2DownportScanEntry> Entries,
    int Ready,
    int AlreadyWotlk335,
    int Blocked,
    int Failed);

/// <summary>
/// Clean-room, loss-accounted conversion for a deliberately conservative modern M2 profile.
/// Unsupported structures are refused instead of discarded.
/// </summary>
public static class StaticM2DownportService
{
    private const int PlanFormatVersion = 8;
    private const int ReceiptFormatVersion = 1;
    private const uint ModernVersion = 274;
    private const uint WotlkVersion = 264;
    private const uint RequiredModernFlags = 0x2080;
    private const uint NewExporterLayoutFlag = 0x200000;
    private const uint WotlkPassthroughFlagMask = 0x10;
    private const uint ClearedModernFlagMask = RequiredModernFlags | NewExporterLayoutFlag;
    private const uint SupportedSourceFlagMask = ClearedModernFlagMask | WotlkPassthroughFlagMask;
    private const int MaximumArrayCount = 20_000_000;
    private const int ModernParticleStride = 492;
    private const int WotlkParticleStride = 476;
    private const uint ParticleMultiTextureFlag = 0x1000_0000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static StaticM2DownportPlan Plan(string sourceModelPath, string? sourceSkinPath = null, string? listfilePath = null, CancellationToken cancellationToken = default)
        => PlanCore(sourceModelPath, sourceSkinPath, listfilePath, null, cancellationToken);

    public static FileDataIdListfileSnapshot PrepareListfile(string listfilePath, IEnumerable<string> modelPaths, CancellationToken cancellationToken = default)
    {
        return FileDataIdListfileService.Resolve(listfilePath, RequiredExternalTextureIds(modelPaths, cancellationToken), cancellationToken);
    }

    public static IReadOnlyList<uint> RequiredExternalTextureIds(IEnumerable<string> modelPaths, CancellationToken cancellationToken = default)
    {
        var ids = new HashSet<uint>();
        foreach (var modelPath in modelPaths) { cancellationToken.ThrowIfCancellationRequested(); foreach (var id in ExternalTextureIds(modelPath)) ids.Add(id); }
        return ids.Order().ToArray();
    }

    public static StaticM2DownportPlan PlanWithListfileSnapshot(string sourceModelPath, string? sourceSkinPath, FileDataIdListfileSnapshot snapshot, CancellationToken cancellationToken = default)
        => PlanCore(sourceModelPath, sourceSkinPath, snapshot.SourcePath, snapshot, cancellationToken);

    private static StaticM2DownportPlan PlanCore(string sourceModelPath, string? sourceSkinPath, string? listfilePath, FileDataIdListfileSnapshot? preparedListfile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sourceModelPath = Path.GetFullPath(sourceModelPath);
        if (!File.Exists(sourceModelPath)) throw new FileNotFoundException("The modern M2 source does not exist.", sourceModelPath);
        var source = File.ReadAllBytes(sourceModelPath);
        var blockers = new List<string>(); var transformations = new List<string>(); var losses = new List<string>();
        var modelHash = Hash(source); uint version = 0, flags = 0; int vertices = 0, triangles = 0, submeshes = 0, materials = 0, shadows = 0, globalSequenceCount = 0, animationSequenceCount = 0, particleEmitterCount = 0;
        byte[]? payload = null; ModernSkin? skin = null; string? skinHash = null; var omittedEmptyTxac = false; var omittedSingleProfileLdv1 = false; var omittedNeutralExp2 = false; var constantColorTracks = 0;
        IReadOnlyList<M2ParticleZSourceTranslation> particleZSourceTranslations = [];
        var materialTranslation = MaterialTranslation.None;
        FileDataIdListfileSnapshot? listfile = null; var resolvedTextures = new List<M2ResolvedTexturePath>();

        if (source.Length < 8 || FourCc(source, 0) != "MD21") blockers.Add("Input is not a modern MD21 chunk container.");
        else
        {
            var chunks = ReadChunks(source, blockers);
            var txac = chunks.Where(chunk => chunk.Id == "TXAC").ToArray();
            if (txac.Length > 1) blockers.Add($"Expected at most one TXAC chunk; found {txac.Length:N0}.");
            else if (txac.Length == 1)
            {
                if (txac[0].Size == 0 || source.AsSpan(checked((int)txac[0].DataOffset), checked((int)txac[0].Size)).IndexOfAnyExcept((byte)0) < 0) omittedEmptyTxac = true;
                else blockers.Add("TXAC contains nonzero extended texture-animation data that has no verified Wrath translation yet.");
            }
            var ldv1 = chunks.Where(chunk => chunk.Id == "LDV1").ToArray();
            if (ldv1.Length > 1) blockers.Add($"Expected at most one LDV1 chunk; found {ldv1.Length:N0}.");
            else if (ldv1.Length == 1)
            {
                var dataOffset = checked((int)ldv1[0].DataOffset);
                if (ldv1[0].Size != 16)
                    blockers.Add($"LDV1 has {ldv1[0].Size:N0} bytes; the verified single-profile structure is exactly 16 bytes.");
                else
                {
                    var threshold = BitConverter.ToSingle(source, dataOffset + 4);
                    if (U16(source, dataOffset) != 0 || U16(source, dataOffset + 2) != 1 || !float.IsFinite(threshold) || threshold <= 0 || U32(source, dataOffset + 8) != 0x02010000 || U32(source, dataOffset + 12) != 0)
                        blockers.Add("LDV1 does not match the verified one-SKIN LOD-selection structure; it will not be discarded.");
                    else omittedSingleProfileLdv1 = true;
                }
            }
            var exp2 = chunks.Where(chunk => chunk.Id == "EXP2").ToArray();
            if (exp2.Length > 1) blockers.Add($"Expected at most one EXP2 chunk; found {exp2.Length:N0}.");
            var unexpected = chunks.Where(chunk => chunk.Id is not "MD21" and not "SFID" and not "TXID" and not "TXAC" and not "LDV1" and not "EXP2").Select(chunk => chunk.Id).Distinct().Order().ToArray();
            if (unexpected.Length > 0) blockers.Add($"Modern semantic chunk(s) are outside the static profile: {string.Join(", ", unexpected)}.");
            var md21 = chunks.Where(chunk => chunk.Id == "MD21").ToArray();
            if (md21.Length != 1) blockers.Add($"Expected exactly one MD21 payload chunk; found {md21.Length:N0}.");
            else if (md21[0].Size > int.MaxValue || md21[0].DataOffset + md21[0].Size > source.LongLength) blockers.Add("MD21 payload range is invalid.");
            else
            {
                payload = source.AsSpan(checked((int)md21[0].DataOffset), checked((int)md21[0].Size)).ToArray();
                if (payload.Length < 0x130 || FourCc(payload, 0) != "MD20") blockers.Add("MD21 does not contain a complete unwrapped MD20 header.");
                else
                {
                    version = U32(payload, 4); flags = U32(payload, 0x10);
                    if (version != ModernVersion) blockers.Add($"Static downport currently requires modern M2 version {ModernVersion}; found {version}.");
                    if ((flags & RequiredModernFlags) != RequiredModernFlags || (flags & ~SupportedSourceFlagMask) != 0)
                        blockers.Add($"Static downport requires flags 0x{RequiredModernFlags:X}; it accepts only the optional newer-exporter bit 0x{NewExporterLayoutFlag:X} and native WotLK passthrough bit 0x{WotlkPassthroughFlagMask:X}. Found 0x{flags:X}.");
                    vertices = Count(payload, 0x3C, "vertex", blockers);
                    ValidateArray(payload, 0x3C, 0x40, 48, "vertices", blockers);
                    var viewCount = Count(payload, 0x44, "skin profile", blockers);
                    if (viewCount != 1) blockers.Add($"Static profile requires exactly one skin profile; found {viewCount:N0}.");
                    globalSequenceCount = Count(payload, 0x14, "global sequence", blockers);
                    ValidateArray(payload, 0x14, 0x18, 4, "global sequences", blockers);
                    animationSequenceCount = Count(payload, 0x1C, "animation", blockers);
                    ValidateArray(payload, 0x1C, 0x20, 64, "animations", blockers);
                    ValidateAnimationSequences(payload, animationSequenceCount, blockers);
                    ValidateArray(payload, 0x24, 0x28, 2, "animation lookup", blockers);
                    ValidateAnimationLookup(payload, animationSequenceCount, blockers);
                    ValidateEmbeddedAnimationOwnership(sourceModelPath, payload, animationSequenceCount, blockers);
                    var boneCount = Count(payload, 0x2C, "bone", blockers);
                    ValidateArray(payload, 0x2C, 0x30, 88, "bones", blockers);
                    var colorCount = Count(payload, 0x48, "color track", blockers);
                    if (colorCount > 0 && ValidateConstantColorTracks(payload, colorCount, blockers)) constantColorTracks = colorCount;
                    RequireZero(payload, 0x60, "texture transforms", blockers);
                    RequireZero(payload, 0xF0, "attachments", blockers);
                    RequireZero(payload, 0x100, "events", blockers);
                    RequireZero(payload, 0x108, "lights", blockers);
                    RequireZero(payload, 0x110, "cameras", blockers);
                    RequireZero(payload, 0x120, "ribbon emitters", blockers);
                    particleEmitterCount = Count(payload, 0x128, "particle emitter", blockers);
                    if (Count(payload, 0x88, "texture-coordinate lookup", blockers) != 0) blockers.Add("Modern texture-coordinate lookup is not empty; the static profile only synthesizes the verified primary-UV lookup.");

                    var textureCount = Count(payload, 0x50, "texture", blockers);
                    ValidateArray(payload, 0x50, 0x54, 16, "textures", blockers);
                    ValidateModernParticles(payload, particleEmitterCount, boneCount, textureCount, globalSequenceCount, blockers);
                    if (exp2.Length == 1)
                    {
                        var exp2Analysis = AnalyzeExp2(source, exp2[0], particleEmitterCount, animationSequenceCount, blockers);
                        omittedNeutralExp2 = exp2Analysis.Valid && exp2Analysis.ZSourceTranslations.Count == 0;
                        particleZSourceTranslations = exp2Analysis.ZSourceTranslations;
                    }
                    ValidateArray(payload, 0x58, 0x5C, 20, "transparency tracks", blockers);
                    var textureOffset = Offset(payload, 0x54, "textures", blockers);
                    ValidateArray(payload, 0x70, 0x74, 4, "render flags", blockers);
                    var renderCount = Count(payload, 0x70, "render flag", blockers); var renderOffset = Offset(payload, 0x74, "render flags", blockers);
                    if (renderOffset >= 0)
                        for (var index = 0; index < renderCount && renderOffset + index * 4 + 4 <= payload.Length; index++)
                            if (U16(payload, renderOffset + index * 4 + 2) > 7) blockers.Add($"Render flag {index:N0} uses unsupported blend mode {U16(payload, renderOffset + index * 4 + 2)}.");
                    ValidateArray(payload, 0x80, 0x84, 2, "texture lookup", blockers);

                    var txid = chunks.Where(chunk => chunk.Id == "TXID").ToArray(); var textureIds = new uint[textureCount];
                    if (txid.Length != 1) blockers.Add($"Expected exactly one TXID chunk; found {txid.Length:N0}.");
                    else
                    {
                        if (txid[0].Size != checked((uint)textureCount * 4u)) blockers.Add($"TXID has {txid[0].Size / 4:N0} entries but the model has {textureCount:N0} texture definitions.");
                        else for (var index = 0; index < textureCount; index++) textureIds[index] = U32(source, checked((int)txid[0].DataOffset + index * 4));
                    }
                    var requiredIds = textureIds.Where(value => value != 0).Distinct().ToArray();
                    if (requiredIds.Length > 0)
                    {
                        if (string.IsNullOrWhiteSpace(listfilePath)) blockers.Add($"TXID contains {requiredIds.Length:N0} external texture FileDataID(s); supply a semicolon/comma/tab id-to-path listfile.");
                        else
                        {
                            listfile = preparedListfile ?? FileDataIdListfileService.Resolve(listfilePath, requiredIds, cancellationToken);
                            var resolved = listfile.ResolvedById;
                            foreach (var missing in requiredIds.Where(id => !resolved.ContainsKey(id) && !listfile.AmbiguousIds.ContainsKey(id)).Order()) blockers.Add($"FileDataID {missing} is missing from the selected listfile.");
                            foreach (var ambiguous in requiredIds.Where(id => listfile.AmbiguousIds.ContainsKey(id)).Order()) blockers.Add($"FileDataID {ambiguous} maps to multiple distinct client paths: {string.Join(" | ", listfile.AmbiguousIds[ambiguous])}");
                            for (var index = 0; index < textureIds.Length; index++) if (textureIds[index] != 0 && resolved.TryGetValue(textureIds[index], out var clientPath)) resolvedTextures.Add(new(index, textureIds[index], clientPath));
                        }
                    }
                    if (textureOffset >= 0)
                    {
                        for (var index = 0; index < textureCount && textureOffset + index * 16 + 16 <= payload.Length; index++)
                        {
                            var item = textureOffset + index * 16; var type = U32(payload, item); var nameLength = U32(payload, item + 8); var nameOffset = U32(payload, item + 12); var fileDataId = textureIds[index];
                            if (type != 0 && fileDataId != 0) blockers.Add($"Texture {index:N0} is replaceable type {type} but also carries FileDataID {fileDataId}; this mixed binding is not translated automatically.");
                            if (type != 0) continue;
                            if (fileDataId != 0) continue;
                            if (nameLength == 0) { blockers.Add($"Hardcoded texture {index:N0} has neither an embedded filename nor a FileDataID mapping."); continue; }
                            if (nameOffset > int.MaxValue || nameLength > int.MaxValue || nameOffset + (ulong)nameLength > (ulong)payload.Length) blockers.Add($"Embedded texture {index:N0} filename range is invalid.");
                        }
                    }
                    var sfid = chunks.Where(chunk => chunk.Id == "SFID").ToArray();
                    if (sfid.Length != 1 || sfid[0].Size != 4) blockers.Add("Static profile requires one four-byte SFID skin reference.");
                }
            }
        }

        sourceSkinPath = ResolveSkin(sourceModelPath, sourceSkinPath);
        if (sourceSkinPath is null) blockers.Add("The conventional companion <model>00.skin file was not found; pass it explicitly.");
        else
        {
            var skinBytes = File.ReadAllBytes(sourceSkinPath); skinHash = Hash(skinBytes);
            skin = ParseModernSkin(skinBytes, blockers);
            if (skin is not null)
            {
                triangles = skin.TriangleIndexCount / 3; submeshes = skin.SubmeshCount; materials = skin.MaterialCount; shadows = skin.ShadowCount;
                if (payload is not null && vertices > 0) materialTranslation = ValidateSkinReferences(skin, skinBytes, payload, vertices, blockers);
                if (shadows > 0) losses.Add($"Wrath SKIN v2 has no shadow-batch array; {shadows:N0} modern shadow-batch record(s) will be omitted. Vertex, triangle, submesh, and material arrays remain byte-preserved.");
            }
        }

        transformations.Add("Unwrap the MD21 container into its embedded MD20 payload.");
        transformations.Add($"Translate M2 version {ModernVersion} to Wrath version {WotlkVersion} after structural validation.");
        transformations.Add("Clear verified modern FileDataID/LOD flags only after resolving every external texture ID and proving exactly one SKIN is present.");
        if ((flags & WotlkPassthroughFlagMask) != 0) transformations.Add($"Preserve native WotLK model flag 0x{WotlkPassthroughFlagMask:X}; it is not part of the modern container translation mask.");
        if ((flags & NewExporterLayoutFlag) != 0) transformations.Add("Clear the newer-exporter layout flag after validating every translated array by its absolute offset; physical record order is not copied into Wrath semantics.");
        if (globalSequenceCount > 0) transformations.Add($"Preserve {globalSequenceCount:N0} native global-sequence duration(s) byte-for-byte, including zero-duration clock entries.");
        if (animationSequenceCount > 1) transformations.Add($"Preserve {animationSequenceCount:N0} fully embedded animation sequence record(s) and their lookup table byte-for-byte; every staged sequence must independently parse and sample without an external .anim file.");
        transformations.Add(materialTranslation.Enabled
            ? materialTranslation.BlendOverrides.Count > 0
                ? "Synthesize primary/environment texture-coordinate routes and a WotLK global blend-override table for verified packed shader-14 materials."
                : "Synthesize primary/environment texture-coordinate routes while preserving verified native WotLK explicit shader IDs."
            : "Append the missing primary-UV texture-coordinate lookup used by the verified single-stage SKIN batches.");
        if (materialTranslation.BlendOverrides.Count > 0)
        {
            transformations.Add("Relocate the model-name bytes that occupy the WotLK blend-override header fields, after proving no other live array overlaps those bytes.");
            transformations.Add($"Rewrite {materialTranslation.MaterialShaderIds.Count:N0} SKIN shader index(es) against {materialTranslation.BlendOverrides.Count:N0} explicit blend-stage entries.");
        }
        if (materialTranslation.Enabled)
        {
            transformations.Add("Pad material transparency/texture-animation lookup spans with explicit none values; dangling references to absent definitions are canonicalized to none.");
        }
        if (constantColorTracks > 0) transformations.Add($"Preserve {constantColorTracks:N0} single-key constant color track(s) after validating both nested RGB and opacity series.");
        if (particleEmitterCount > 0) transformations.Add($"Repack {particleEmitterCount:N0} validated modern particle emitter record(s) from 492 to Wrath's 476-byte stride while preserving every untranslated legacy field and referenced animation/lifetime payload byte-for-byte.");
        if (particleZSourceTranslations.Count > 0) transformations.Add($"Translate {particleZSourceTranslations.Count:N0} EXP2 Z-source override(s) into exact one-key Wrath particle tracks for every embedded animation sequence.");
        if (resolvedTextures.Count > 0) transformations.Add($"Embed {resolvedTextures.Count:N0} listfile-resolved texture path(s) into the Wrath M2 payload and remove the external FileDataID dependency.");
        transformations.Add("Repack modern SKIN v3 common arrays into the Wrath SKIN v2 header without changing their contents.");
        if (omittedEmptyTxac) transformations.Add("Omit the proven zero-filled TXAC extension chunk; it contains no texture-animation values to translate.");
        if (omittedSingleProfileLdv1)
        {
            transformations.Add("Omit the verified single-profile LDV1 selection chunk after proving the model exposes exactly one SKIN.");
            losses.Add("Wrath's chunkless M2 container has no LDV1 distance-selection metadata; its finite modern threshold is omitted while the sole validated SKIN geometry remains unchanged.");
        }
        if (omittedNeutralExp2)
        {
            transformations.Add("Omit the verified neutral EXP2 particle extension after proving every Z-source offset is zero, both multipliers are one, and every alpha-cutoff curve is empty.");
            losses.Add("Wrath has no EXP2 particle-extension chunk; only identity-valued EXP2 metadata with no visual effect is omitted.");
        }
        else if (particleZSourceTranslations.Count > 0)
        {
            transformations.Add("Omit EXP2 only after migrating every nonzero Z-source override and proving its remaining color/alpha multipliers and alpha-cutoff curves are neutral.");
        }
        var outputFlags = flags & ~ClearedModernFlagMask;
        if (materialTranslation.BlendOverrides.Count > 0) outputFlags |= 0x8;
        return new(PlanFormatVersion, DateTimeOffset.UtcNow, sourceModelPath, modelHash, sourceSkinPath, skinHash, listfile?.SourcePath, listfile?.SourceSha256, resolvedTextures, version, flags,
            outputFlags, vertices, triangles, submeshes, materials, shadows, constantColorTracks, globalSequenceCount, animationSequenceCount, transformations, losses, blockers.Distinct().ToArray())
        {
            OutputTextureCoordinateLookup = materialTranslation.TextureCoordinates,
            OutputBlendOverrides = materialTranslation.BlendOverrides,
            OutputMaterialShaderIds = materialTranslation.MaterialShaderIds,
            OutputTransparencyLookup = materialTranslation.TransparencyLookup,
            OutputTextureAnimationLookup = materialTranslation.TextureAnimationLookup,
            OutputMaterialCombiners = materialTranslation.MaterialCombiners,
            ParticleZSourceTranslations = particleZSourceTranslations,
            ParticleEmitterCount = particleEmitterCount
        };
    }

    public static StaticM2DownportScanResult Scan(IEnumerable<string> inputs, string? listfilePath = null, CancellationToken cancellationToken = default)
    {
        var normalizedInputs = inputs.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalizedInputs.Length == 0) throw new ArgumentException("Add at least one M2 file or folder to scan.", nameof(inputs));
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in normalizedInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(input))
            {
                if (!Path.GetExtension(input).Equals(".m2", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Static M2 scan input is not an M2 file: {input}");
                files.Add(input); continue;
            }
            if (!Directory.Exists(input)) throw new FileNotFoundException("Static M2 scan input does not exist.", input);
            foreach (var file in Directory.EnumerateFiles(input, "*.m2", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                cancellationToken.ThrowIfCancellationRequested(); files.Add(Path.GetFullPath(file));
            }
        }
        if (files.Count == 0) throw new InvalidOperationException("The selected input contains no M2 files.");
        FileDataIdListfileSnapshot? listfile = null;
        if (!string.IsNullOrWhiteSpace(listfilePath)) listfile = PrepareListfile(listfilePath, files, cancellationToken);
        var entries = new List<StaticM2DownportScanEntry>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (IsWotlkM2(file)) { entries.Add(new(file, StaticM2DownportScanStatus.AlreadyWotlk335, null, null)); continue; }
                var plan = listfile is null ? Plan(file, cancellationToken: cancellationToken) : PlanWithListfileSnapshot(file, null, listfile, cancellationToken); entries.Add(new(file, plan.Ready ? StaticM2DownportScanStatus.ConversionReady : StaticM2DownportScanStatus.Blocked, plan, null));
            }
            catch (Exception exception) { entries.Add(new(file, StaticM2DownportScanStatus.Failed, null, exception.Message)); }
        }
        return new(1, DateTimeOffset.UtcNow, normalizedInputs, entries, entries.Count(value => value.Status == StaticM2DownportScanStatus.ConversionReady),
            entries.Count(value => value.Status == StaticM2DownportScanStatus.AlreadyWotlk335), entries.Count(value => value.Status == StaticM2DownportScanStatus.Blocked), entries.Count(value => value.Status == StaticM2DownportScanStatus.Failed));
    }

    public static StaticM2DownportResult Convert(StaticM2DownportPlan plan, string outputDirectory, CancellationToken cancellationToken = default)
        => ConvertCore(plan, outputDirectory, null, cancellationToken);

    internal static StaticM2DownportResult ConvertPrepared(StaticM2DownportPlan plan, string outputDirectory, FileDataIdListfileSnapshot preparedListfile, CancellationToken cancellationToken = default)
        => ConvertCore(plan, outputDirectory, preparedListfile, cancellationToken);

    private static StaticM2DownportResult ConvertCore(StaticM2DownportPlan plan, string outputDirectory, FileDataIdListfileSnapshot? preparedListfile, CancellationToken cancellationToken)
    {
        if (plan.FormatVersion != PlanFormatVersion) throw new InvalidDataException($"Unsupported static M2 plan version {plan.FormatVersion}.");
        cancellationToken.ThrowIfCancellationRequested();
        var current = preparedListfile is null
            ? Plan(plan.SourceModelPath, plan.SourceSkinPath, plan.SourceListfilePath, cancellationToken)
            : PlanWithListfileSnapshot(plan.SourceModelPath, plan.SourceSkinPath, preparedListfile, cancellationToken);
        if (!current.SourceModelSha256.Equals(plan.SourceModelSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.SourceSkinSha256, plan.SourceSkinSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.SourceListfileSha256, plan.SourceListfileSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Source M2, SKIN, or FileDataID listfile changed after planning; create a fresh conversion plan.");
        if (!current.Ready) throw new InvalidOperationException("Static M2 downport is blocked:\n- " + string.Join("\n- ", current.Blockers));
        if (current.SourceSkinPath is null) throw new InvalidOperationException("Conversion plan has no companion SKIN.");

        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any()) throw new IOException($"Conversion output must be new or empty: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory) ?? throw new InvalidOperationException("Conversion output has no parent folder."); Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".crucible-static-m2-{Guid.NewGuid():N}"); Directory.CreateDirectory(staging);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modelSource = File.ReadAllBytes(current.SourceModelPath); var chunks = ReadChunks(modelSource, []); var md21 = chunks.Single(chunk => chunk.Id == "MD21");
            var payload = modelSource.AsSpan(checked((int)md21.DataOffset), checked((int)md21.Size)).ToArray();
            var originalPayload = payload.ToArray();
            if (current.UsesBlendOverrides) RelocateBlendOverrideHeaderName(ref payload);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), WotlkVersion); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x10, 4), current.OutputFlags);
            AppendSignedLookup(ref payload, 0x88, 0x8C, current.OutputTextureCoordinateLookup);
            if (current.TranslatesMaterials)
            {
                AppendUnsignedLookup(ref payload, 0x90, 0x94, current.OutputTransparencyLookup);
                AppendUnsignedLookup(ref payload, 0x98, 0x9C, current.OutputTextureAnimationLookup);
            }
            if (current.UsesBlendOverrides)
            {
                AppendUnsignedLookup(ref payload, 0x130, 0x134, current.OutputBlendOverrides);
            }
            var textureOffset = checked((int)U32(payload, 0x54));
            foreach (var resolved in current.ResolvedTexturePaths)
            {
                var bytes = Encoding.UTF8.GetBytes(resolved.ClientPath + "\0"); var pathOffset = payload.Length; Array.Resize(ref payload, checked(payload.Length + bytes.Length)); bytes.CopyTo(payload, pathOffset);
                var item = checked(textureOffset + resolved.TextureIndex * 16); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(item + 8, 4), checked((uint)bytes.Length)); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(item + 12, 4), checked((uint)pathOffset));
            }
            var outputParticleOffset = AppendLegacyParticles(ref payload, originalPayload, current.ParticleEmitterCount);
            ApplyParticleZSourceTranslations(ref payload, outputParticleOffset, current.AnimationSequenceCount, current.ParticleZSourceTranslations);
            var outputColorBlockers = new List<string>(); var outputColorCount = Count(payload, 0x48, "output color track", outputColorBlockers);
            if (outputColorCount != current.ConstantColorTrackCount || (outputColorCount > 0 && !ValidateConstantColorTracks(payload, outputColorCount, outputColorBlockers)))
                throw new InvalidDataException("Converted M2 color tracks failed independent constant-track validation: " + string.Join("; ", outputColorBlockers));
            ValidateBytePreservedArray(originalPayload, payload, 0x14, 0x18, 4, "global sequences");
            ValidateBytePreservedArray(originalPayload, payload, 0x1C, 0x20, 64, "animation sequences");
            ValidateBytePreservedArray(originalPayload, payload, 0x24, 0x28, 2, "animation lookup");

            var skinSource = File.ReadAllBytes(current.SourceSkinPath); var skin = ParseModernSkin(skinSource, []) ?? throw new InvalidDataException("Companion SKIN changed into an invalid layout.");
            var outputSkin = BuildWotlkSkin(skinSource, skin, current.OutputMaterialShaderIds);
            var modelName = Path.GetFileName(current.SourceModelPath); var skinName = Path.GetFileNameWithoutExtension(current.SourceModelPath) + "00.skin";
            var stagedModel = Path.Combine(staging, modelName); var stagedSkin = Path.Combine(staging, skinName); File.WriteAllBytes(stagedModel, payload); File.WriteAllBytes(stagedSkin, outputSkin);
            cancellationToken.ThrowIfCancellationRequested();

            var inspection = NativeAssetConversionService.Inspect(stagedModel);
            if (inspection.Compatibility != AssetCompatibility.AlreadyWotlk335) throw new InvalidDataException("Converted model did not pass Crucible's Wrath M2 inspection.");
            var geometry = M2PreviewGeometryService.Load(stagedModel, stagedSkin, M2PreviewVisibilityMode.AllGeosets);
            if (geometry.Vertices.Count != current.VertexCount || geometry.TotalTriangleIndices / 3 != current.TriangleCount || geometry.Submeshes.Count != current.SubmeshCount || geometry.MaterialUnits.Count != current.MaterialCount)
                throw new InvalidDataException("Converted M2/SKIN geometry or material counts differ from the immutable plan.");
            if (geometry.Sequences.Count != current.AnimationSequenceCount || geometry.AnimationRig?.GlobalSequenceDurations.Length != current.GlobalSequenceCount)
                throw new InvalidDataException($"Converted animation metadata differs from the immutable plan. Expected {current.AnimationSequenceCount:N0} sequence(s) and {current.GlobalSequenceCount:N0} global clock(s); found {geometry.Sequences.Count:N0} and {geometry.AnimationRig?.GlobalSequenceDurations.Length ?? 0:N0}.");
            M2AnimationService.ValidateAllSequences(geometry);
            M2ParticlePreviewService.ValidateAllSequences(geometry);
            if (geometry.ParticleEmitters.Count != current.ParticleEmitterCount)
                throw new InvalidDataException($"Converted particle-emitter count differs from the immutable plan. Expected {current.ParticleEmitterCount:N0}; found {geometry.ParticleEmitters.Count:N0}.");
            if (current.OutputMaterialCombiners.Count > 0 && !geometry.MaterialUnits.Select(material => material.Combiner.Name).SequenceEqual(current.OutputMaterialCombiners))
                throw new InvalidDataException($"Converted material combiners differ from the immutable plan. Expected {string.Join(", ", current.OutputMaterialCombiners)}; found {string.Join(", ", geometry.MaterialUnits.Select(material => material.Combiner.Name))}.");
            var slots = M2PreviewGeometryService.InspectTextureSlots(stagedModel);
            foreach (var resolved in current.ResolvedTexturePaths)
                if (resolved.TextureIndex >= slots.Count || !string.Equals(PatchInputMapper.NormalizeArchivePath(slots[resolved.TextureIndex].EmbeddedPath ?? string.Empty), resolved.ClientPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Converted texture slot {resolved.TextureIndex:N0} did not retain its resolved client path.");

            var finalModel = Path.Combine(outputDirectory, modelName); var finalSkin = Path.Combine(outputDirectory, skinName); var receipt = Path.Combine(outputDirectory, "conversion-receipt.json");
            var result = new StaticM2DownportResult(ReceiptFormatVersion, DateTimeOffset.UtcNow, current, outputDirectory, finalModel, Hash(payload), finalSkin, Hash(outputSkin), receipt,
                geometry.Vertices.Count, geometry.TotalTriangleIndices / 3, geometry.Submeshes.Count, geometry.MaterialUnits.Count);
            File.WriteAllText(Path.Combine(staging, "conversion-receipt.json"), JsonSerializer.Serialize(result, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory);
            Directory.Move(staging, outputDirectory);
            return result;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static ModernSkin? ParseModernSkin(byte[] data, List<string> blockers)
    {
        if (data.Length < 56 || FourCc(data, 0) != "SKIN") { blockers.Add("Companion file is not a complete modern SKIN v3 container."); return null; }
        var result = new ModernSkin(
            Count(data, 4, "SKIN vertex lookup", blockers), Offset(data, 8, "SKIN vertex lookup", blockers),
            Count(data, 12, "SKIN triangle index", blockers), Offset(data, 16, "SKIN triangles", blockers),
            Count(data, 20, "SKIN bone property", blockers), Offset(data, 24, "SKIN bone properties", blockers),
            Count(data, 28, "SKIN submesh", blockers), Offset(data, 32, "SKIN submeshes", blockers),
            Count(data, 36, "SKIN material", blockers), Offset(data, 40, "SKIN materials", blockers),
            U32(data, 44), Count(data, 48, "SKIN shadow batch", blockers), Offset(data, 52, "SKIN shadow batches", blockers));
        ValidateRange(data, result.LookupOffset, result.LookupCount, 2, "SKIN vertex lookup", blockers);
        ValidateRange(data, result.TriangleOffset, result.TriangleIndexCount, 2, "SKIN triangles", blockers);
        ValidateRange(data, result.PropertyOffset, result.PropertyCount, 4, "SKIN bone properties", blockers);
        ValidateRange(data, result.SubmeshOffset, result.SubmeshCount, 48, "SKIN submeshes", blockers);
        ValidateRange(data, result.MaterialOffset, result.MaterialCount, 24, "SKIN materials", blockers);
        ValidateRange(data, result.ShadowOffset, result.ShadowCount, 12, "SKIN shadow batches", blockers);
        if (result.TriangleIndexCount % 3 != 0) blockers.Add($"SKIN triangle index count {result.TriangleIndexCount:N0} is not divisible by three.");
        return result;
    }

    private static MaterialTranslation ValidateSkinReferences(ModernSkin skin, byte[] data, byte[] model, int vertexCount, List<string> blockers)
    {
        if (!RangesValid(skin, data.Length)) return MaterialTranslation.None;
        for (var index = 0; index < skin.LookupCount; index++) if (U16(data, skin.LookupOffset + index * 2) >= vertexCount) { blockers.Add($"SKIN vertex lookup {index:N0} exceeds the model's {vertexCount:N0} vertices."); break; }
        for (var index = 0; index < skin.TriangleIndexCount; index++) if (U16(data, skin.TriangleOffset + index * 2) >= skin.LookupCount) { blockers.Add($"SKIN triangle index {index:N0} exceeds the {skin.LookupCount:N0} vertex-lookup entries."); break; }
        for (var index = 0; index < skin.SubmeshCount; index++)
        {
            var offset = skin.SubmeshOffset + index * 48; var vertexStart = U16(data, offset + 4); var count = U16(data, offset + 6); var triangleStart = U16(data, offset + 8); var triangleCount = U16(data, offset + 10);
            if (vertexStart + count > skin.LookupCount) blockers.Add($"SKIN submesh {index:N0} vertex range exceeds the lookup table.");
            if (triangleStart + triangleCount > skin.TriangleIndexCount) blockers.Add($"SKIN submesh {index:N0} triangle range exceeds the triangle table.");
        }
        var renderCount = Count(model, 0x70, "render flag", blockers); var textureLookupCount = Count(model, 0x80, "texture lookup", blockers);
        var materials = new List<MaterialSource>(skin.MaterialCount); var useBlendOverrides = false; var preserveExplicitMaterials = false; var hasUnsupportedMaterials = false; var requiredTransparency = 0; var requiredAnimation = 0;
        for (var index = 0; index < skin.MaterialCount; index++)
        {
            var offset = skin.MaterialOffset + index * 24; var shader = U16(data, offset + 2); var submesh = U16(data, offset + 4); var submesh2 = U16(data, offset + 6); var render = U16(data, offset + 10); var stages = U16(data, offset + 14); var textureCombo = U16(data, offset + 16); var coordinateCombo = U16(data, offset + 18); var transparencyCombo = U16(data, offset + 20); var animationCombo = U16(data, offset + 22);
            var supportedMaterial = shader switch
            {
                0 when stages == 1 => true,
                16 when stages == 1 => true,
                14 when stages == 2 => useBlendOverrides = true,
                0x8000 when stages == 2 => preserveExplicitMaterials = true,
                0x8001 when stages == 2 => preserveExplicitMaterials = true,
                _ => false
            };
            if (!supportedMaterial) { hasUnsupportedMaterials = true; blockers.Add($"SKIN material {index:N0} uses shader {shader} with {stages:N0} stage(s); verified static translation supports shader 0/16 with one stage, packed shader 14 (Opaque_Mod2xNA), and explicit shaders 32768/32769 with two stages."); }
            if (submesh >= skin.SubmeshCount || submesh2 >= skin.SubmeshCount) blockers.Add($"SKIN material {index:N0} references a missing submesh.");
            if (render >= renderCount) blockers.Add($"SKIN material {index:N0} references missing render flag {render:N0}.");
            if ((long)textureCombo + stages > textureLookupCount) blockers.Add($"SKIN material {index:N0} texture span {textureCombo:N0} + {stages:N0} exceeds the {textureLookupCount:N0}-entry texture lookup.");
            if (coordinateCombo != 0) blockers.Add($"SKIN material {index:N0} references texture-coordinate lookup {coordinateCombo:N0}; verified static routes begin at synthesized entry 0.");
            requiredTransparency = Math.Max(requiredTransparency, checked(transparencyCombo + stages)); requiredAnimation = Math.Max(requiredAnimation, checked(animationCombo + stages));
            materials.Add(new(shader, stages, transparencyCombo, animationCombo));
        }
        if (!useBlendOverrides && !preserveExplicitMaterials) return MaterialTranslation.None;
        if (hasUnsupportedMaterials) return MaterialTranslation.None;
        if (useBlendOverrides && preserveExplicitMaterials)
        {
            blockers.Add("The model mixes packed shader 14 with native explicit shader IDs. Enabling WotLK global blend overrides reinterprets every SKIN shader field, so this mixed encoding requires a verified whole-model material rewrite.");
            return MaterialTranslation.None;
        }

        if (useBlendOverrides) ValidateBlendOverrideHeaderSpace(model, blockers);
        var transparencyDefinitions = Count(model, 0x58, "transparency track", blockers); var animationDefinitions = Count(model, 0x60, "texture transform", blockers);
        var sourceTransparency = ReadUnsignedArray(model, 0x90, 0x94, "transparency lookup", blockers);
        var sourceAnimation = ReadUnsignedArray(model, 0x98, 0x9C, "texture-animation lookup", blockers);
        var outputTransparency = CanonicalizeOptionalLookup(sourceTransparency, requiredTransparency, transparencyDefinitions, "transparency", blockers);
        var outputAnimation = CanonicalizeOptionalLookup(sourceAnimation, requiredAnimation, animationDefinitions, "texture-animation", blockers);
        if (preserveExplicitMaterials)
        {
            var preservedCombiners = materials.Select(material => material.Shader switch
            {
                0 => "Opaque",
                16 => "Mod",
                0x8000 => "Opaque_Mod2xNA_Alpha",
                0x8001 => "Opaque_AddAlpha",
                _ => throw new InvalidDataException($"Unexpected material shader {material.Shader} entered explicit preservation planning.")
            }).ToArray();
            return new(true, [0, -1], [], [], outputTransparency, outputAnimation, preservedCombiners);
        }
        var blends = new List<ushort>(); var shaderIds = new List<ushort>(materials.Count); var combiners = new List<string>(materials.Count);
        foreach (var material in materials)
        {
            if (blends.Count > ushort.MaxValue) { blockers.Add("Global blend-override shader indices exceed the 16-bit SKIN material field."); break; }
            shaderIds.Add(checked((ushort)blends.Count));
            switch (material.Shader)
            {
                case 0: blends.Add(0); combiners.Add("Opaque"); break;
                case 16: blends.Add(1); combiners.Add("Mod"); break;
                case 14: blends.Add(0); blends.Add(6); combiners.Add("Opaque_Mod2xNA"); break;
            }
        }
        if (blends.Count > ushort.MaxValue + 1L) blockers.Add($"Global blend-override table requires {blends.Count:N0} entries; the verified maximum is {ushort.MaxValue + 1:N0}.");
        return new(true, [0, -1], blends.ToArray(), shaderIds.ToArray(), outputTransparency, outputAnimation, combiners.ToArray());
    }

    private static ushort[] CanonicalizeOptionalLookup(IReadOnlyList<ushort> source, int requiredCount, int definitionCount, string label, List<string> blockers)
    {
        var result = Enumerable.Repeat(ushort.MaxValue, Math.Max(requiredCount, source.Count)).ToArray();
        for (var index = 0; index < source.Count; index++)
        {
            var value = source[index];
            if (value == ushort.MaxValue || definitionCount == 0) continue;
            if (value >= definitionCount) blockers.Add($"M2 {label} lookup {index:N0} references definition {value:N0}, but only {definitionCount:N0} exist.");
            else result[index] = value;
        }
        return result;
    }

    private static ushort[] ReadUnsignedArray(byte[] model, int countOffset, int offsetOffset, string label, List<string> blockers)
    {
        var count = Count(model, countOffset, label, blockers); var offset = Offset(model, offsetOffset, label, blockers);
        if (!HasRange(model, offset, count, 2)) { blockers.Add($"{label} range ({count:N0} × 2 bytes at {offset:N0}) exceeds the containing model."); return []; }
        var result = new ushort[count]; for (var index = 0; index < count; index++) result[index] = U16(model, offset + index * 2); return result;
    }

    private static void ValidateBlendOverrideHeaderSpace(byte[] model, List<string> blockers)
    {
        if (model.Length < 0x138) { blockers.Add("Global blend-override translation requires room for the complete 0x138-byte WotLK extended header."); return; }
        var nameCount = checked((int)U32(model, 0x08)); var nameOffset = checked((int)U32(model, 0x0C));
        if (nameCount > 0 && !HasRange(model, nameOffset, nameCount, 1)) blockers.Add("Model-name range is invalid and cannot be relocated away from the blend-override header.");
        var liveArrays = new (int CountOffset, int OffsetOffset, int Stride, string Name)[]
        {
            (0x14,0x18,4,"global sequences"),(0x1C,0x20,64,"animations"),(0x24,0x28,2,"animation lookup"),(0x2C,0x30,88,"bones"),(0x34,0x38,2,"key-bone lookup"),(0x3C,0x40,48,"vertices"),
            (0x48,0x4C,40,"colors"),(0x50,0x54,16,"textures"),(0x58,0x5C,20,"transparencies"),(0x60,0x64,20,"texture transforms"),(0x68,0x6C,2,"texture replacement lookup"),(0x70,0x74,4,"render flags"),
            (0x78,0x7C,2,"bone lookup"),(0x80,0x84,2,"texture lookup"),(0x88,0x8C,2,"texture-coordinate lookup"),(0x90,0x94,2,"transparency lookup"),(0x98,0x9C,2,"texture-animation lookup"),
            (0xA0,0xA4,2,"bounding triangles"),(0xA8,0xAC,12,"bounding vertices"),(0xB0,0xB4,12,"bounding normals"),(0xF0,0xF4,40,"attachments"),(0xF8,0xFC,2,"attachment lookup"),
            (0x100,0x104,36,"events"),(0x108,0x10C,156,"lights"),(0x110,0x114,124,"cameras"),(0x118,0x11C,2,"camera lookup"),(0x120,0x124,176,"ribbons"),(0x128,0x12C,492,"particles")
        };
        foreach (var array in liveArrays)
        {
            var count = U32(model, array.CountOffset); if (count == 0) continue; var offset = U32(model, array.OffsetOffset);
            var byteLength = (ulong)count * (uint)array.Stride;
            if (RangesOverlap(offset, byteLength, 0x130, 8)) blockers.Add($"Live {array.Name} data overlaps WotLK blend-override header bytes 0x130–0x137; conversion will not move that structure implicitly.");
        }
        for (var offset = 0x130; offset < 0x138; offset++)
        {
            var ownedByName = nameCount > 0 && offset >= nameOffset && offset < (long)nameOffset + nameCount;
            if (!ownedByName && model[offset] != 0) blockers.Add($"Byte 0x{offset:X} is nonzero outside the relocatable model-name range; blend-override translation refuses to overwrite it.");
        }
    }

    private static byte[] BuildWotlkSkin(byte[] source, ModernSkin skin, IReadOnlyList<ushort> materialShaderIds)
    {
        using var stream = new MemoryStream(); stream.Write(new byte[48]);
        var lookup = CopySection(stream, source, skin.LookupOffset, checked(skin.LookupCount * 2));
        var triangles = CopySection(stream, source, skin.TriangleOffset, checked(skin.TriangleIndexCount * 2));
        var properties = CopySection(stream, source, skin.PropertyOffset, checked(skin.PropertyCount * 4));
        var submeshes = CopySection(stream, source, skin.SubmeshOffset, checked(skin.SubmeshCount * 48));
        var materials = CopySection(stream, source, skin.MaterialOffset, checked(skin.MaterialCount * 24));
        var result = stream.ToArray(); Encoding.ASCII.GetBytes("SKIN").CopyTo(result, 0);
        WritePair(result, 4, skin.LookupCount, lookup); WritePair(result, 12, skin.TriangleIndexCount, triangles); WritePair(result, 20, skin.PropertyCount, properties);
        WritePair(result, 28, skin.SubmeshCount, submeshes); WritePair(result, 36, skin.MaterialCount, materials); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44, 4), skin.BoneCountMax);
        if (materialShaderIds.Count > 0)
        {
            if (materialShaderIds.Count != skin.MaterialCount) throw new InvalidDataException($"Material translation contains {materialShaderIds.Count:N0} shader IDs for {skin.MaterialCount:N0} SKIN materials.");
            for (var index = 0; index < materialShaderIds.Count; index++) BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(materials + index * 24 + 2, 2), materialShaderIds[index]);
        }
        return result;
    }

    private static void RelocateBlendOverrideHeaderName(ref byte[] payload)
    {
        if (payload.Length < 0x138) throw new InvalidDataException("Blend-override output requires a complete 0x138-byte WotLK extended header.");
        var nameLength = checked((int)U32(payload, 0x08)); var nameOffset = checked((int)U32(payload, 0x0C));
        if (nameLength > 0 && RangesOverlap(nameOffset, nameLength, 0x130, 8))
        {
            if (!HasRange(payload, nameOffset, nameLength, 1)) throw new InvalidDataException("Model-name bytes moved outside the source after planning.");
            var copy = payload.AsSpan(nameOffset, nameLength).ToArray(); var relocated = payload.Length; Array.Resize(ref payload, checked(payload.Length + copy.Length)); copy.CopyTo(payload, relocated);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x0C, 4), checked((uint)relocated));
        }
        payload.AsSpan(0x130, 8).Clear();
    }

    private static void AppendSignedLookup(ref byte[] payload, int countOffset, int dataOffset, IReadOnlyList<short> values)
    {
        var offset = Align(payload.Length, 2); Array.Resize(ref payload, checked(offset + values.Count * 2));
        for (var index = 0; index < values.Count; index++) BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(offset + index * 2, 2), values[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(countOffset, 4), checked((uint)values.Count)); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(dataOffset, 4), values.Count == 0 ? 0u : checked((uint)offset));
    }

    private static void AppendUnsignedLookup(ref byte[] payload, int countOffset, int dataOffset, IReadOnlyList<ushort> values)
    {
        var offset = Align(payload.Length, 2); Array.Resize(ref payload, checked(offset + values.Count * 2));
        for (var index = 0; index < values.Count; index++) BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + index * 2, 2), values[index]);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(countOffset, 4), checked((uint)values.Count)); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(dataOffset, 4), values.Count == 0 ? 0u : checked((uint)offset));
    }

    private static int CopySection(MemoryStream destination, byte[] source, int offset, int length)
    {
        while (destination.Position % 16 != 0) destination.WriteByte(0); var result = checked((int)destination.Position); destination.Write(source, offset, length); return result;
    }
    private static void WritePair(byte[] data, int offset, int count, int location) { BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), checked((uint)count)); BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), checked((uint)location)); }

    private static IReadOnlyList<Chunk> ReadChunks(byte[] data, List<string> blockers)
    {
        var result = new List<Chunk>(); long offset = 0;
        while (offset < data.LongLength)
        {
            if (data.LongLength - offset < 8) { blockers.Add($"Trailing {data.LongLength - offset:N0} byte(s) cannot form a chunk header."); break; }
            var id = FourCc(data, checked((int)offset)); var size = U32(data, checked((int)offset + 4)); var end = offset + 8L + size;
            if (!id.All(character => character is >= ' ' and <= '~')) { blockers.Add($"Invalid chunk signature at byte {offset:N0}."); break; }
            if (end > data.LongLength) { blockers.Add($"Chunk {id} extends beyond end of file."); break; }
            result.Add(new(id, offset, offset + 8, size)); offset = end;
        }
        return result;
    }

    private static string? ResolveSkin(string modelPath, string? skinPath)
    {
        if (!string.IsNullOrWhiteSpace(skinPath))
        {
            var resolved = Path.IsPathRooted(skinPath) ? skinPath : Path.Combine(Path.GetDirectoryName(modelPath)!, skinPath);
            resolved = Path.GetFullPath(resolved); if (!File.Exists(resolved)) throw new FileNotFoundException("The supplied modern SKIN does not exist.", resolved); return resolved;
        }
        var expected = Path.GetFileNameWithoutExtension(modelPath) + "00.skin";
        return Directory.EnumerateFiles(Path.GetDirectoryName(modelPath)!, "*.skin", SearchOption.TopDirectoryOnly).FirstOrDefault(path => Path.GetFileName(path).Equals(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<uint> ExternalTextureIds(string modelPath)
    {
        modelPath = Path.GetFullPath(modelPath); if (!File.Exists(modelPath)) throw new FileNotFoundException("The M2 source does not exist.", modelPath);
        var data = File.ReadAllBytes(modelPath); if (data.Length < 8 || FourCc(data, 0) != "MD21") return [];
        var chunks = ReadChunks(data, []); var txid = chunks.SingleOrDefault(chunk => chunk.Id == "TXID");
        if (txid is null || txid.Size % 4 != 0 || txid.Size > int.MaxValue) return [];
        var result = new List<uint>(); for (long cursor = txid.DataOffset; cursor < txid.DataOffset + txid.Size; cursor += 4) { var id = U32(data, checked((int)cursor)); if (id != 0) result.Add(id); }
        return result;
    }

    private static bool IsWotlkM2(string path)
    {
        Span<byte> header = stackalloc byte[8]; using var stream = File.OpenRead(path); if (stream.Length < header.Length) return false; stream.ReadExactly(header);
        return header[..4].SequenceEqual("MD20"u8) && BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) == WotlkVersion;
    }

    private static void ValidateModernParticles(byte[] data, int count, int boneCount, int textureCount, int globalSequenceCount, List<string> blockers)
    {
        var offset = Offset(data, 0x12C, "particle emitters", blockers);
        if (!HasRange(data, offset, count, ModernParticleStride))
        {
            if (count > 0) blockers.Add($"Modern particle-emitter range ({count:N0} × {ModernParticleStride:N0} bytes at {offset:N0}) exceeds the containing model.");
            return;
        }
        var trackOffsets = new[] { 52, 72, 92, 112, 132, 152, 176, 200, 220, 240 };
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * ModernParticleStride;
            var flags = U32(data, item + 4); var bone = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(item + 20, 2)); var packedTexture = U16(data, item + 22);
            foreach (var floatOffset in new[] { 8,12,16,172,196,308,312,348,352,356,360,364,368,372,376,380,384,388,392,396,400,404,408,412,416,420,424,428,432,436,440,444 })
                if (!float.IsFinite(BitConverter.ToSingle(data, item + floatOffset))) blockers.Add($"Particle emitter {index:N0} contains a non-finite scalar at +0x{floatOffset:X}.");
            if (bone < -1 || bone >= boneCount) blockers.Add($"Particle emitter {index:N0} references bone {bone:N0}, but only {boneCount:N0} bones exist.");
            if ((flags & ParticleMultiTextureFlag) != 0)
                blockers.Add($"Particle emitter {index:N0} uses modern multi-texturing; its post-Cataclysm texture parameters do not yet have a verified Wrath equivalent.");
            else if (packedTexture >= textureCount) blockers.Add($"Particle emitter {index:N0} references texture definition {packedTexture:N0}, but only {textureCount:N0} textures exist.");
            if (data[item + 40] > 7) blockers.Add($"Particle emitter {index:N0} uses invalid blend mode {data[item + 40]:N0}.");
            if (data[item + 41] is not (1 or 2)) blockers.Add($"Particle emitter {index:N0} uses emitter type {data[item + 41]:N0}; the verified Wrath downport profile currently covers plane and sphere emitters.");
            var rows = U16(data, item + 48); var columns = U16(data, item + 50);
            if ((long)Math.Max(1, (int)rows) * Math.Max(1, (int)columns) > 65_536) blockers.Add($"Particle emitter {index:N0} declares an excessive {rows:N0} × {columns:N0} sprite sheet.");
            ValidateParticleArray(data, item + 24, 1, $"particle emitter {index:N0} geometry filename", blockers);
            ValidateParticleArray(data, item + 32, 1, $"particle emitter {index:N0} recursion filename", blockers);
            foreach (var trackOffset in trackOffsets) ValidateParticleTrack(data, item + trackOffset, 4, globalSequenceCount, $"particle emitter {index:N0} track at +0x{trackOffset:X}", blockers, trackOffset == 132 && (flags & 0x0080_0000) != 0);
            ValidateParticleTrack(data, item + 456, 1, globalSequenceCount, $"particle emitter {index:N0} enabled track", blockers);
            ValidateParticleLifeBlock(data, item + 260, 12, $"particle emitter {index:N0} color curve", blockers, 3);
            ValidateParticleLifeBlock(data, item + 276, 2, $"particle emitter {index:N0} opacity curve", blockers);
            ValidateParticleLifeBlock(data, item + 292, 8, $"particle emitter {index:N0} size curve", blockers, 2);
            ValidateParticleLifeBlock(data, item + 316, 2, $"particle emitter {index:N0} head-cell curve", blockers);
            ValidateParticleLifeBlock(data, item + 332, 2, $"particle emitter {index:N0} tail-cell curve", blockers);
            ValidateParticleArray(data, item + 448, 12, $"particle emitter {index:N0} spline", blockers);
            if (data.AsSpan(item + WotlkParticleStride, ModernParticleStride - WotlkParticleStride).IndexOfAnyExcept((byte)0) >= 0)
                blockers.Add($"Particle emitter {index:N0} has nonzero post-Cataclysm multi-texture parameters; they will not be silently discarded.");
        }
    }

    private static void ValidateParticleTrack(byte[] data, int offset, int valueStride, int globalSequenceCount, string label, List<string> blockers, bool packedValues = false)
    {
        if (!HasRange(data, offset, 1, 20)) { blockers.Add($"{label} header is truncated."); return; }
        var interpolation = U16(data, offset); var globalSequence = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 2, 2));
        if (interpolation > 1) blockers.Add($"{label} uses interpolation {interpolation}; the verified profile supports none and linear tracks.");
        if (globalSequence < -1 || globalSequence >= globalSequenceCount) blockers.Add($"{label} references global sequence {globalSequence:N0}, but only {globalSequenceCount:N0} exist.");
        var timeCount = checked((int)Math.Min(U32(data, offset + 4), int.MaxValue)); var timeOffset = U32(data, offset + 8);
        var valueCount = checked((int)Math.Min(U32(data, offset + 12), int.MaxValue)); var valueOffset = U32(data, offset + 16);
        if (timeCount != valueCount) { blockers.Add($"{label} has {timeCount:N0} timestamp series but {valueCount:N0} value series."); return; }
        if (!HasRange(data, UIntOffset(timeOffset), timeCount, 8) || !HasRange(data, UIntOffset(valueOffset), valueCount, 8)) { blockers.Add($"{label} outer series range is invalid."); return; }
        for (var series = 0; series < timeCount; series++)
        {
            var timeEntry = UIntOffset(timeOffset) + series * 8; var valueEntry = UIntOffset(valueOffset) + series * 8;
            var keys = U32(data, timeEntry); var values = U32(data, valueEntry);
            if (keys != values) { blockers.Add($"{label} series {series:N0} has {keys:N0} timestamps but {values:N0} values."); continue; }
            var nestedTimeOffset = UIntOffset(U32(data, timeEntry + 4)); var nestedValueOffset = UIntOffset(U32(data, valueEntry + 4));
            if (keys > MaximumArrayCount || !HasRange(data, nestedTimeOffset, (int)keys, 4) || !HasRange(data, nestedValueOffset, (int)values, valueStride)) { blockers.Add($"{label} series {series:N0} has an invalid nested key range."); continue; }
            for (var key = 1; key < keys; key++) if (U32(data, nestedTimeOffset + (key - 1) * 4) > U32(data, nestedTimeOffset + key * 4)) { blockers.Add($"{label} series {series:N0} timestamps are not sorted."); break; }
            if (!packedValues && valueStride == 4)
                for (var key = 0; key < values; key++) if (!float.IsFinite(BitConverter.ToSingle(data, nestedValueOffset + key * 4))) { blockers.Add($"{label} series {series:N0} key {key:N0} is non-finite."); break; }
        }
    }

    private static void ValidateParticleLifeBlock(byte[] data, int offset, int valueStride, string label, List<string> blockers, int floatComponents = 0)
    {
        if (!HasRange(data, offset, 1, 16)) { blockers.Add($"{label} header is truncated."); return; }
        var times = U32(data, offset); var values = U32(data, offset + 8);
        if (times != values) { blockers.Add($"{label} has {times:N0} timestamps but {values:N0} values."); return; }
        var timeOffset = UIntOffset(U32(data, offset + 4)); var valueOffset = UIntOffset(U32(data, offset + 12));
        if (times > MaximumArrayCount || !HasRange(data, timeOffset, (int)times, 2) || !HasRange(data, valueOffset, (int)values, valueStride)) { blockers.Add($"{label} range is invalid."); return; }
        for (var key = 0; key < times; key++)
        {
            var time = U16(data, timeOffset + key * 2); if (time > 32767) { blockers.Add($"{label} timestamp {key:N0} exceeds normalized fixed-16 range."); break; }
            if (key > 0 && U16(data, timeOffset + (key - 1) * 2) > time) { blockers.Add($"{label} timestamps are not sorted."); break; }
        }
        for (var key = 0; key < values && floatComponents > 0; key++)
            for (var component = 0; component < floatComponents; component++) if (!float.IsFinite(BitConverter.ToSingle(data, valueOffset + key * valueStride + component * 4))) { blockers.Add($"{label} key {key:N0} component {component:N0} is non-finite."); return; }
    }

    private static void ValidateParticleArray(byte[] data, int offset, int stride, string label, List<string> blockers)
    {
        if (!HasRange(data, offset, 1, 8)) { blockers.Add($"{label} header is truncated."); return; }
        var count = U32(data, offset); if (count > MaximumArrayCount || !HasRange(data, UIntOffset(U32(data, offset + 4)), (int)count, stride)) blockers.Add($"{label} range is invalid.");
    }

    private static Exp2Analysis AnalyzeExp2(byte[] source, Chunk chunk, int particleCount, int animationSequenceCount, List<string> blockers)
    {
        if (chunk.Size > int.MaxValue) { blockers.Add("EXP2 is too large to validate safely."); return Exp2Analysis.Invalid; }
        var start = checked((int)chunk.DataOffset); var size = checked((int)chunk.Size);
        if (size < 16) { blockers.Add("EXP2 is shorter than its verified 16-byte header."); return Exp2Analysis.Invalid; }
        var count = U32(source, start); var relativeOffset = U32(source, start + 4);
        if (count != particleCount || relativeOffset != 16) { blockers.Add($"EXP2 does not match the verified particle mapping: {count:N0} record(s) at relative offset {relativeOffset:N0} for {particleCount:N0} emitter(s)."); return Exp2Analysis.Invalid; }
        if (source.AsSpan(start + 8, 8).IndexOfAnyExcept((byte)0) >= 0 || (long)relativeOffset + (long)count * 28 > size) { blockers.Add("EXP2 has an unsupported header or record range."); return Exp2Analysis.Invalid; }
        var valid = true; var translations = new List<M2ParticleZSourceTranslation>(); var records = start + (int)relativeOffset;
        for (var index = 0; index < count; index++)
        {
            var item = records + index * 28; var z = BitConverter.ToSingle(source, item); var color = BitConverter.ToSingle(source, item + 4); var alpha = BitConverter.ToSingle(source, item + 8);
            var timeCount = U32(source, item + 12); var timeOffset = U32(source, item + 16); var valueCount = U32(source, item + 20); var valueOffset = U32(source, item + 24);
            if (!float.IsFinite(z) || !float.IsFinite(color) || !float.IsFinite(alpha))
            {
                blockers.Add($"EXP2 record {index:N0} contains a non-finite Z-source or color/alpha multiplier."); valid = false; continue;
            }
            if (color != 1 || alpha != 1 || timeCount != 0 || timeOffset != 0 || valueCount != 0 || valueOffset != 0)
            {
                blockers.Add($"EXP2 record {index:N0} carries non-neutral color/alpha multiplier or alpha-cutoff data that has no verified Wrath representation."); valid = false; continue;
            }
            if (z == 0) continue;
            if (animationSequenceCount == 0)
            {
                blockers.Add($"EXP2 record {index:N0} carries Z-source {z:R}, but the model has no animation sequence in which to author an exact Wrath track."); valid = false; continue;
            }
            translations.Add(new(index, z));
        }
        var logicalEnd = checked((int)relativeOffset + (int)count * 28);
        if (source.AsSpan(start + logicalEnd, size - logicalEnd).IndexOfAnyExcept((byte)0) >= 0) { blockers.Add("EXP2 contains nonzero trailing extension bytes outside the verified records."); valid = false; }
        return valid ? new(true, translations) : Exp2Analysis.Invalid;
    }

    private static int AppendLegacyParticles(ref byte[] payload, byte[] source, int count)
    {
        if (count == 0) return -1;
        var sourceOffset = checked((int)U32(source, 0x12C));
        if (!HasRange(source, sourceOffset, count, ModernParticleStride)) throw new InvalidDataException("Planned modern particle-emitter range is no longer valid.");
        var outputOffset = Align(payload.Length, 16); Array.Resize(ref payload, checked(outputOffset + count * WotlkParticleStride));
        for (var index = 0; index < count; index++) source.AsSpan(sourceOffset + index * ModernParticleStride, WotlkParticleStride).CopyTo(payload.AsSpan(outputOffset + index * WotlkParticleStride, WotlkParticleStride));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x128, 4), checked((uint)count)); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0x12C, 4), checked((uint)outputOffset));
        for (var index = 0; index < count; index++)
            if (!source.AsSpan(sourceOffset + index * ModernParticleStride, WotlkParticleStride).SequenceEqual(payload.AsSpan(outputOffset + index * WotlkParticleStride, WotlkParticleStride)))
                throw new InvalidDataException($"Particle emitter {index:N0} was not preserved byte-for-byte while changing record stride.");
        return outputOffset;
    }

    private static void ApplyParticleZSourceTranslations(ref byte[] payload, int particleOffset, int animationSequenceCount, IReadOnlyList<M2ParticleZSourceTranslation> translations)
    {
        if (translations.Count == 0) return;
        if (particleOffset < 0 || animationSequenceCount <= 0) throw new InvalidDataException("The immutable EXP2 Z-source plan has no valid Wrath particle/animation target.");
        foreach (var translation in translations)
        {
            if (translation.ParticleIndex < 0 || !HasRange(payload, particleOffset + translation.ParticleIndex * WotlkParticleStride, 1, WotlkParticleStride) || !float.IsFinite(translation.Value))
                throw new InvalidDataException($"The immutable EXP2 Z-source translation for emitter {translation.ParticleIndex:N0} is invalid.");
            var seriesBytes = checked(animationSequenceCount * 8);
            var outerTimes = Align(payload.Length, 4); var outerValues = checked(outerTimes + seriesBytes);
            var timeKey = checked(outerValues + seriesBytes); var valueKey = checked(timeKey + 4);
            Array.Resize(ref payload, checked(valueKey + 4));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(timeKey, 4), 0);
            BitConverter.GetBytes(translation.Value).CopyTo(payload, valueKey);
            for (var sequence = 0; sequence < animationSequenceCount; sequence++)
            {
                WriteArray(payload, outerTimes + sequence * 8, 1, timeKey);
                WriteArray(payload, outerValues + sequence * 8, 1, valueKey);
            }
            var track = checked(particleOffset + translation.ParticleIndex * WotlkParticleStride + 240);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(track, 2), 0);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(track + 2, 2), -1);
            WriteArray(payload, track + 4, animationSequenceCount, outerTimes);
            WriteArray(payload, track + 12, animationSequenceCount, outerValues);
        }

        static void WriteArray(byte[] target, int offset, int count, int dataOffset)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset, 4), checked((uint)count));
            BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(offset + 4, 4), checked((uint)dataOffset));
        }
    }

    private static int UIntOffset(uint value) => value > int.MaxValue ? -1 : (int)value;

    private static void RequireZero(byte[] data, int countOffset, string label, List<string> blockers) { var count = Count(data, countOffset, label, blockers); if (count != 0) blockers.Add($"Static profile requires zero {label}; found {count:N0}."); }
    private static void ValidateAnimationSequences(byte[] data, int count, List<string> blockers)
    {
        var offset = Offset(data, 0x20, "animations", blockers);
        if (!HasRange(data, offset, count, 64)) return;
        for (var index = 0; index < count; index++)
        {
            var item = offset + index * 64; var speed = BitConverter.ToSingle(data, item + 8); var next = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(item + 60, 2)); var flags = U32(data, item + 12); var alias = U16(data, item + 62);
            if (!float.IsFinite(speed)) blockers.Add($"Animation sequence {index:N0} has a non-finite move speed.");
            if (next < -1 || next >= count) blockers.Add($"Animation sequence {index:N0} points to next sequence {next:N0}, outside -1..{count - 1:N0}.");
            if ((flags & 0x40) != 0 && alias >= count) blockers.Add($"Alias animation sequence {index:N0} points to sequence {alias:N0}, but only {count:N0} sequence(s) exist.");
        }
    }

    private static void ValidateAnimationLookup(byte[] data, int animationCount, List<string> blockers)
    {
        var count = Count(data, 0x24, "animation lookup", blockers); var offset = Offset(data, 0x28, "animation lookup", blockers);
        if (!HasRange(data, offset, count, 2)) return;
        for (var index = 0; index < count; index++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + index * 2, 2));
            if (value < -1 || value >= animationCount) { blockers.Add($"Animation lookup slot {index:N0} points to sequence {value:N0}, but only {animationCount:N0} sequence(s) exist."); break; }
        }
    }

    private static void ValidateEmbeddedAnimationOwnership(string modelPath, byte[] data, int animationCount, List<string> blockers)
    {
        if (animationCount == 0) return;
        var rawOffset = U32(data, 0x20);
        if (rawOffset > int.MaxValue || !HasRange(data, (int)rawOffset, animationCount, 64)) return;
        var offset = (int)rawOffset; var directory = Path.GetDirectoryName(modelPath)!; var stem = Path.GetFileNameWithoutExtension(modelPath);
        for (var index = 0; index < animationCount; index++)
        {
            var item = offset + index * 64; var path = Path.Combine(directory, $"{stem}{U16(data, item):D4}-{U16(data, item + 2):D2}.anim");
            if (File.Exists(path)) { blockers.Add($"Animation sequence {index:N0} has external companion {Path.GetFileName(path)}. External .anim translation is not part of the embedded-only profile."); break; }
        }
    }

    private static void ValidateBytePreservedArray(byte[] source, byte[] output, int countOffset, int offsetOffset, int stride, string label)
    {
        var sourceCount = checked((int)U32(source, countOffset)); var outputCount = checked((int)U32(output, countOffset));
        var sourceOffset = checked((int)U32(source, offsetOffset)); var outputOffset = checked((int)U32(output, offsetOffset));
        if (sourceCount != outputCount || !HasRange(source, sourceOffset, sourceCount, stride) || !HasRange(output, outputOffset, outputCount, stride) ||
            !source.AsSpan(sourceOffset, checked(sourceCount * stride)).SequenceEqual(output.AsSpan(outputOffset, checked(outputCount * stride))))
            throw new InvalidDataException($"Converted {label} were not preserved byte-for-byte.");
    }
    private static bool ValidateConstantColorTracks(byte[] data, int count, List<string> blockers)
    {
        var valid = true; var offset = Offset(data, 0x4C, "color tracks", blockers);
        if (!HasRange(data, offset, count, 40)) { blockers.Add($"Color-track range ({count:N0} × 40 bytes at {offset:N0}) exceeds the containing model."); return false; }
        for (var index = 0; index < count; index++)
        {
            valid &= ValidateConstantTrack(data, offset + index * 40, 12, $"Color track {index:N0} RGB", blockers);
            valid &= ValidateConstantTrack(data, offset + index * 40 + 20, 2, $"Color track {index:N0} opacity", blockers);
        }
        return valid;
    }
    private static bool ValidateConstantTrack(byte[] data, int offset, int valueStride, string label, List<string> blockers)
    {
        if (!HasRange(data, offset, 1, 20)) { blockers.Add($"{label} header is truncated."); return false; }
        var interpolation = U16(data, offset); var globalSequence = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset + 2, 2));
        var timeSeriesCount = U32(data, offset + 4); var timeSeriesOffset = U32(data, offset + 8); var valueSeriesCount = U32(data, offset + 12); var valueSeriesOffset = U32(data, offset + 16);
        if (interpolation != 0 || globalSequence != -1 || timeSeriesCount != 1 || valueSeriesCount != 1)
        {
            blockers.Add($"{label} is not a standalone single-series constant (interpolation {interpolation}, global sequence {globalSequence}, timestamp series {timeSeriesCount}, value series {valueSeriesCount})."); return false;
        }
        if (timeSeriesOffset > int.MaxValue || valueSeriesOffset > int.MaxValue || !HasRange(data, (int)timeSeriesOffset, 1, 8) || !HasRange(data, (int)valueSeriesOffset, 1, 8))
        { blockers.Add($"{label} nested series array is outside the containing model."); return false; }
        var timeCount = U32(data, (int)timeSeriesOffset); var timeOffset = U32(data, (int)timeSeriesOffset + 4); var valueCount = U32(data, (int)valueSeriesOffset); var valueOffset = U32(data, (int)valueSeriesOffset + 4);
        if (timeCount != 1 || valueCount != 1 || timeOffset > int.MaxValue || valueOffset > int.MaxValue || !HasRange(data, (int)timeOffset, 1, 4) || !HasRange(data, (int)valueOffset, 1, valueStride))
        { blockers.Add($"{label} must contain exactly one in-file timestamp/value key."); return false; }
        if (U32(data, (int)timeOffset) != 0) { blockers.Add($"{label} constant key must be at timestamp zero."); return false; }
        if (valueStride == 12)
            for (var component = 0; component < 3; component++) if (!float.IsFinite(BitConverter.ToSingle(data, (int)valueOffset + component * 4))) { blockers.Add($"{label} contains a non-finite component."); return false; }
        return true;
    }
    private static void ValidateArray(byte[] data, int countOffset, int offsetOffset, int stride, string label, List<string> blockers) => ValidateRange(data, Offset(data, offsetOffset, label, blockers), Count(data, countOffset, label, blockers), stride, label, blockers);
    private static void ValidateRange(byte[] data, int offset, int count, int stride, string label, List<string> blockers)
    {
        if (offset < 0 || count < 0) return; var bytes = (long)count * stride;
        if (offset > data.Length || bytes > data.Length - (long)offset) blockers.Add($"{label} range ({count:N0} × {stride:N0} bytes at {offset:N0}) exceeds the containing file.");
    }
    private static int Count(byte[] data, int offset, string label, List<string> blockers)
    {
        if (offset < 0 || offset + 4 > data.Length) { blockers.Add($"Missing {label} count."); return 0; }
        var value = U32(data, offset); if (value > MaximumArrayCount) { blockers.Add($"{label} count {value:N0} exceeds the safety limit."); return 0; } return checked((int)value);
    }
    private static int Offset(byte[] data, int offset, string label, List<string> blockers)
    {
        if (offset < 0 || offset + 4 > data.Length) { blockers.Add($"Missing {label} offset."); return -1; }
        var value = U32(data, offset); if (value > int.MaxValue) { blockers.Add($"{label} offset exceeds the supported file range."); return -1; } return checked((int)value);
    }
    private static bool RangesValid(ModernSkin skin, int length) => new[] { (skin.LookupOffset, skin.LookupCount, 2), (skin.TriangleOffset, skin.TriangleIndexCount, 2), (skin.PropertyOffset, skin.PropertyCount, 4), (skin.SubmeshOffset, skin.SubmeshCount, 48), (skin.MaterialOffset, skin.MaterialCount, 24), (skin.ShadowOffset, skin.ShadowCount, 12) }.All(value => value.Item1 >= 0 && (long)value.Item1 + (long)value.Item2 * value.Item3 <= length);
    private static bool HasRange(byte[] data, int offset, int count, int stride) => offset >= 0 && count >= 0 && (long)offset + (long)count * stride <= data.LongLength;
    private static bool RangesOverlap(int firstOffset, int firstLength, int secondOffset, int secondLength) => (long)firstOffset < (long)secondOffset + secondLength && (long)secondOffset < (long)firstOffset + firstLength;
    private static bool RangesOverlap(uint firstOffset, ulong firstLength, uint secondOffset, uint secondLength) => (ulong)firstOffset < (ulong)secondOffset + secondLength && (ulong)secondOffset < (ulong)firstOffset + firstLength;
    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static string FourCc(byte[] data, int offset) => Encoding.ASCII.GetString(data, offset, 4);
    private static string Hash(byte[] data) => System.Convert.ToHexString(SHA256.HashData(data));
    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private sealed record Chunk(string Id, long Offset, long DataOffset, uint Size);
    private sealed record Exp2Analysis(bool Valid, IReadOnlyList<M2ParticleZSourceTranslation> ZSourceTranslations)
    {
        public static Exp2Analysis Invalid { get; } = new(false, []);
    }
    private sealed record MaterialSource(ushort Shader, ushort Stages, ushort TransparencyCombo, ushort AnimationCombo);
    private sealed record MaterialTranslation(bool Enabled, IReadOnlyList<short> TextureCoordinates, IReadOnlyList<ushort> BlendOverrides,
        IReadOnlyList<ushort> MaterialShaderIds, IReadOnlyList<ushort> TransparencyLookup, IReadOnlyList<ushort> TextureAnimationLookup,
        IReadOnlyList<string> MaterialCombiners)
    {
        public static MaterialTranslation None { get; } = new(false, [0], [], [], [], [], []);
    }
    private sealed record ModernSkin(int LookupCount, int LookupOffset, int TriangleIndexCount, int TriangleOffset, int PropertyCount, int PropertyOffset,
        int SubmeshCount, int SubmeshOffset, int MaterialCount, int MaterialOffset, uint BoneCountMax, int ShadowCount, int ShadowOffset);
}
