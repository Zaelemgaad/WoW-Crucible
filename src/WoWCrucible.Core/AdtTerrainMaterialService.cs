namespace WoWCrucible.Core;

public sealed record AdtTerrainMaterialLayer(int Slot, uint TextureId, string? ClientPath, string? SourcePath,
    bool TextureResolved, bool AlphaResolved);

public sealed record AdtTerrainMaterialCell(int CellX, int CellY, RgbaTexture Composite,
    IReadOnlyList<AdtTerrainMaterialLayer> Layers, bool Complete);

public sealed record AdtTerrainMaterialSet(string AdtPath, string Provenance,
    IReadOnlyList<AdtTerrainMaterialCell> Cells, int ResolvedTexturePaths, int UnresolvedTexturePaths,
    int CompleteCells, IReadOnlyList<string> Findings);

/// <summary>
/// Composes the ordered WotLK MTEX/MCLY/MCAL terrain layers into one bounded
/// 64x64 RGBA material per MCNK. Physical texture selection is supplied by the
/// caller so provenance policy remains explicit and independently reviewable.
/// </summary>
public static class AdtTerrainMaterialService
{
    public const int CompositeSize = 64;
    public const int TextureRepeatsPerCell = 8;

    public static AdtTerrainMaterialSet Load(string adtPath, string provenance,
        IReadOnlyDictionary<string, string> resolvedTextureSources, int maximumTextureDimension = 512,
        CancellationToken cancellationToken = default)
    {
        if (maximumTextureDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumTextureDimension));
        var normalizedSources = resolvedTextureSources.ToDictionary(pair => Normalize(pair.Key), pair => Path.GetFullPath(pair.Value), StringComparer.OrdinalIgnoreCase);
        var decoded = new Dictionary<string, RgbaTexture>(StringComparer.OrdinalIgnoreCase); var findings = new List<string>();
        foreach (var pair in normalizedSources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(pair.Value)) throw new FileNotFoundException("Resolved terrain texture does not exist.", pair.Value);
                var info = BlpTextureService.Inspect(pair.Value); var mip = info.MipLevels.FirstOrDefault(value => value.Width <= maximumTextureDimension && value.Height <= maximumTextureDimension) ?? info.MipLevels[^1];
                decoded[pair.Key] = BlpTextureService.Decode(pair.Value, mip.Index);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                findings.Add($"{pair.Key}: resolved physical texture could not be decoded: {exception.Message}");
            }
        }
        return Compose(adtPath, provenance, decoded, normalizedSources, findings, cancellationToken);
    }

    public static AdtTerrainMaterialSet Compose(string adtPath, string provenance,
        IReadOnlyDictionary<string, RgbaTexture> decodedTextures,
        IReadOnlyDictionary<string, string>? resolvedTextureSources = null,
        IEnumerable<string>? initialFindings = null, CancellationToken cancellationToken = default)
    {
        adtPath = Path.GetFullPath(adtPath); provenance = string.IsNullOrWhiteSpace(provenance) ? "Unspecified" : provenance.Trim();
        var textures = AdtTextureLayerService.Inspect(adtPath); var alpha = AdtAlphaMapService.Inspect(adtPath); var sourceBytes = File.ReadAllBytes(adtPath);
        var normalizedTextures = decodedTextures.ToDictionary(pair => Normalize(pair.Key), pair => ValidateTexture(pair.Key, pair.Value), StringComparer.OrdinalIgnoreCase);
        var normalizedSources = (resolvedTextureSources ?? new Dictionary<string, string>()).ToDictionary(pair => Normalize(pair.Key), pair => Path.GetFullPath(pair.Value), StringComparer.OrdinalIgnoreCase);
        var alphaByCellSlot = alpha.Maps.ToDictionary(value => (value.CellX, value.CellY, value.Slot));
        var findings = new List<string>(initialFindings ?? []); findings.AddRange(textures.Findings); findings.AddRange(alpha.Findings);
        var missingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var cells = new List<AdtTerrainMaterialCell>();
        foreach (var group in textures.Layers.GroupBy(value => (value.CellX, value.CellY)).OrderBy(value => value.Key.CellY).ThenBy(value => value.Key.CellX))
        {
            cancellationToken.ThrowIfCancellationRequested(); var ordered = group.OrderBy(value => value.Slot).ToArray(); if (ordered.Length == 0) continue;
            var pixels = new byte[CompositeSize * CompositeSize * 4]; var materialLayers = new List<AdtTerrainMaterialLayer>(); var complete = true;
            for (var layerIndex = 0; layerIndex < ordered.Length; layerIndex++)
            {
                var layer = ordered[layerIndex]; var clientPath = string.IsNullOrWhiteSpace(layer.TexturePath) ? null : Normalize(layer.TexturePath!); RgbaTexture? texture = null;
                var textureResolved = clientPath is not null && normalizedTextures.TryGetValue(clientPath, out texture); byte[]? alphaPixels = null; var alphaResolved = layer.Slot == 0;
                if (layer.Slot > 0 && alphaByCellSlot.TryGetValue((layer.CellX, layer.CellY, layer.Slot), out var alphaMap))
                {
                    var encoded = sourceBytes.AsSpan(checked((int)alphaMap.DataOffset), alphaMap.Capacity); alphaPixels = AdtAlphaMapCodec.Decode(alphaMap.Encoding, encoded); alphaResolved = true;
                }
                if (!textureResolved)
                {
                    complete = false; var display = clientPath ?? $"missing MTEX index {layer.TextureId}"; missingPaths.Add(display);
                    findings.Add($"Terrain texture {display} is absent or undecodable in provenance {provenance}.");
                }
                if (!alphaResolved)
                {
                    complete = false; findings.Add($"MCNK {layer.CellX},{layer.CellY} layer {layer.Slot} has no decodable MCAL alpha map; that additional layer was not invented.");
                }
                normalizedSources.TryGetValue(clientPath ?? string.Empty, out var physicalSource); materialLayers.Add(new(layer.Slot, layer.TextureId, clientPath, physicalSource, textureResolved, alphaResolved));
                if (layer.Slot > 0 && !alphaResolved) continue;
                for (var y = 0; y < CompositeSize; y++) for (var x = 0; x < CompositeSize; x++)
                {
                    var alphaValue = layer.Slot == 0 ? (byte)255 : alphaPixels![y * CompositeSize + x]; if (alphaValue == 0) continue;
                    var color = textureResolved ? Sample(texture!, x, y) : MissingColor(x, y); Blend(pixels, (y * CompositeSize + x) * 4, color, alphaValue);
                }
            }
            cells.Add(new(group.Key.CellX, group.Key.CellY, new(CompositeSize, CompositeSize, pixels), materialLayers, complete));
        }
        var distinctPaths = textures.Layers.Select(value => value.TexturePath).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => Normalize(value!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var resolvedCount = distinctPaths.Count(normalizedTextures.ContainsKey); var completeCells = cells.Count(value => value.Complete);
        findings.Add($"Composed {cells.Count:N0} MCNK material(s) at {CompositeSize}x{CompositeSize} from ordered MCLY layers and decoded MCAL alpha using {TextureRepeatsPerCell:N0} terrain-texture repeats per cell.");
        return new(adtPath, provenance, cells, resolvedCount, distinctPaths.Length - resolvedCount, completeCells, findings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static RgbaTexture ValidateTexture(string path, RgbaTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture); if (texture.Width <= 0 || texture.Height <= 0 || texture.Pixels.Length != texture.ByteLength) throw new InvalidDataException($"Decoded terrain texture {path} has invalid RGBA dimensions or storage."); return texture;
    }

    private static (byte R, byte G, byte B, byte A) Sample(RgbaTexture texture, int x, int y)
    {
        var u = x * TextureRepeatsPerCell / (double)CompositeSize; var v = y * TextureRepeatsPerCell / (double)CompositeSize;
        var sx = Math.Clamp((int)Math.Floor((u - Math.Floor(u)) * texture.Width), 0, texture.Width - 1); var sy = Math.Clamp((int)Math.Floor((v - Math.Floor(v)) * texture.Height), 0, texture.Height - 1); var offset = (sy * texture.Width + sx) * 4;
        return (texture.Pixels[offset], texture.Pixels[offset + 1], texture.Pixels[offset + 2], texture.Pixels[offset + 3]);
    }

    private static (byte R, byte G, byte B, byte A) MissingColor(int x, int y)
        => ((x / 8 + y / 8) & 1) == 0 ? ((byte)255, (byte)0, (byte)180, (byte)255) : ((byte)35, (byte)15, (byte)35, (byte)255);

    private static void Blend(byte[] target, int offset, (byte R, byte G, byte B, byte A) source, byte layerAlpha)
    {
        var alpha = layerAlpha / 255d * source.A / 255d; var inverse = 1d - alpha;
        target[offset] = (byte)Math.Clamp((int)Math.Round(source.R * alpha + target[offset] * inverse), 0, 255);
        target[offset + 1] = (byte)Math.Clamp((int)Math.Round(source.G * alpha + target[offset + 1] * inverse), 0, 255);
        target[offset + 2] = (byte)Math.Clamp((int)Math.Round(source.B * alpha + target[offset + 2] * inverse), 0, 255);
        target[offset + 3] = 255;
    }

    private static string Normalize(string path) => PatchInputMapper.NormalizeArchivePath(path.Trim().Replace('/', '\\'));
}
