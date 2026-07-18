namespace WoWCrucible.Core;

public sealed record CharacterAppearanceSource(string Provenance, string FullPath, bool ByteEquivalent)
{
    public override string ToString() => $"{Provenance}{(ByteEquivalent ? " · byte-identical alternatives" : string.Empty)}";
}

public sealed record CharacterAppearancePreviewPlan(
    CharacterAppearanceIdentity Identity,
    IReadOnlyList<CharacterBaseSkin> Skins,
    CharacterBaseSkin SelectedSkin,
    IReadOnlyList<CharacterSection> Faces,
    CharacterSection? SelectedFace,
    IReadOnlyList<CharacterSection> FacialHair,
    CharacterSection? SelectedFacialHair,
    IReadOnlyList<CharacterSection> Hair,
    CharacterSection? SelectedHair,
    CharacterSection? Underwear,
    IReadOnlyList<CharacterAppearanceSource> Sources,
    CharacterAppearanceSource? SelectedSource,
    CharacterAppearanceGeosetPlan Geosets,
    string Message);

public sealed record ComposedCharacterAppearance(RgbaTexture Body, RgbaTexture? Hair, IReadOnlyList<string> Missing);

public static class CharacterAppearancePreviewService
{
    public static CharacterAppearancePreviewPlan Build(AssetComparisonIndex index, string dbcFolder, CharacterAppearanceIdentity identity,
        uint? skinId = null, uint? faceId = null, uint? facialHairId = null, uint? hairId = null, string? sourcePath = null,
        string? preferredProvenance = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index); ArgumentNullException.ThrowIfNull(identity);
        var charSections = Path.Combine(Path.GetFullPath(dbcFolder), "CharSections.dbc");
        if (!File.Exists(charSections)) throw new FileNotFoundException("CharSections.dbc is required for a decoded playable-character appearance.", charSections);
        var sections = CharacterAppearanceService.LoadSections(charSections, identity); cancellationToken.ThrowIfCancellationRequested();
        var skins = sections.Where(section => section.Kind == CharacterSectionKind.Skin && section.Texture0 is not null)
            .Select(section => new CharacterBaseSkin(section.Id, section.RaceId, section.SexId, section.Flags, section.VariationIndex, section.ColorIndex, section.Texture0!)).ToArray();
        if (skins.Length == 0) throw new InvalidDataException($"CharSections.dbc has no base skins for {identity.RaceName} {identity.SexName}.");
        var selectedSkin = skins.FirstOrDefault(item => item.Id == skinId) ?? skins[0];
        var faces = sections.Where(section => section.Kind == CharacterSectionKind.Face && section.ColorIndex == selectedSkin.ColorIndex).ToArray();
        var facialHair = sections.Where(section => section.Kind == CharacterSectionKind.FacialHair).ToArray();
        var hair = sections.Where(section => section.Kind == CharacterSectionKind.Hair).ToArray();
        var underwear = sections.FirstOrDefault(section => section.Kind == CharacterSectionKind.Underwear && section.ColorIndex == selectedSkin.ColorIndex);
        var selectedFace = Pick(faces, faceId); var selectedFacialHair = Pick(facialHair, facialHairId); var selectedHair = Pick(hair, hairId);
        var geosets = CharacterAppearanceService.ResolveGeosets(dbcFolder, identity, selectedHair?.VariationIndex, selectedFacialHair?.VariationIndex);

        var baseResolution = AssetDependencyGraphService.ResolveClientAsset(index, preferredProvenance ?? "__appearance_discovery__", selectedSkin.TexturePath);
        var paths = (baseResolution.SourcePath is not null ? [baseResolution.SourcePath] : baseResolution.Candidates).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var equivalent = paths.Length > 1 && paths.Skip(1).All(path => AssetComparisonService.FilesAreIdentical(paths[0], path, cancellationToken));
        var sources = paths.Select(path => new CharacterAppearanceSource(SourceName(index, path), path, equivalent)).OrderBy(item => item.Provenance, StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedSource = sources.FirstOrDefault(item => item.FullPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase));
        selectedSource ??= sources.Length == 1 || equivalent ? sources.FirstOrDefault() : null;
        var message = sources.Length == 0 ? $"The selected skin texture is missing: {selectedSkin.TexturePath}"
            : selectedSource is null ? $"{sources.Length:N0} non-identical base-skin sources exist. Choose one explicitly; Crucible will not mix them."
            : $"{identity.RaceName} {identity.SexName} · skin {selectedSkin.ColorIndex:N0} · {selectedSource.Provenance}.";
        if (geosets.Warnings.Count > 0) message += " " + string.Join(" ", geosets.Warnings);
        return new(identity, skins, selectedSkin, faces, selectedFace, facialHair, selectedFacialHair, hair, selectedHair, underwear, sources, selectedSource, geosets, message);

        static CharacterSection? Pick(IReadOnlyList<CharacterSection> choices, uint? id) => id is null ? choices.FirstOrDefault() : choices.FirstOrDefault(item => item.Id == id) ?? choices.FirstOrDefault();
    }

    public static ComposedCharacterAppearance Compose(AssetComparisonIndex index, CharacterAppearancePreviewPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index); ArgumentNullException.ThrowIfNull(plan);
        if (plan.SelectedSource is null) throw new InvalidOperationException("Choose an explicit character appearance source before composition.");
        var missing = new List<string>(); var layers = new List<CharacterTextureLayer>();
        var body = Decode(plan.SelectedSource.FullPath); cancellationToken.ThrowIfCancellationRequested();
        Add(plan.Underwear?.Texture1, CharacterTextureRegion.Torso); Add(plan.Underwear?.Texture0, CharacterTextureRegion.Pelvis);
        Add(plan.SelectedFace?.Texture1, CharacterTextureRegion.FaceUpper); Add(plan.SelectedFace?.Texture0, CharacterTextureRegion.FaceLower);
        Add(plan.SelectedFacialHair?.Texture1, CharacterTextureRegion.FaceUpper); Add(plan.SelectedFacialHair?.Texture0, CharacterTextureRegion.FaceLower);
        Add(plan.SelectedHair?.Texture2, CharacterTextureRegion.FaceUpper); Add(plan.SelectedHair?.Texture1, CharacterTextureRegion.FaceLower);
        var hairPath = Resolve(plan.SelectedHair?.Texture0); var hair = hairPath is null ? null : Decode(hairPath);
        return new(CharacterTextureComposer.Compose(body, layers), hair, missing);

        void Add(string? clientPath, CharacterTextureRegion region) { var path = Resolve(clientPath); if (path is not null) layers.Add(new(Decode(path), region)); cancellationToken.ThrowIfCancellationRequested(); }
        string? Resolve(string? clientPath)
        {
            if (string.IsNullOrWhiteSpace(clientPath)) return null;
            var resolution = AssetDependencyGraphService.ResolveClientAsset(index, plan.SelectedSource.Provenance, clientPath);
            if (resolution.SourcePath is not null) return resolution.SourcePath;
            missing.Add(Path.GetFileName(clientPath)); return null;
        }
    }

    private static RgbaTexture Decode(string path) => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase) ? BlpTextureService.Decode(path) : BlpTextureService.DecodeImage(path);
    private static string SourceName(AssetComparisonIndex index, string path)
        => index.LooseContentRoot is { } loose && Path.GetRelativePath(loose, path) is var relative && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)
            ? "Loose" : Directory.GetParent(path)?.Name ?? "Unknown";
}
