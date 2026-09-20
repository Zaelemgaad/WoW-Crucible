using System.Globalization;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public static class ModelBrowserTextureService
{
    public static string SlotName(uint type) => type switch
    {
        0 => "Model texture", 1 => "Body and clothing", 2 => "Item / cape", 3 => "Weapon blade", 4 => "Weapon handle",
        5 => "Environment reflection", 6 => "Hair", 7 => "Facial hair", 8 => "Skin details", 9 => "Inventory artwork", 10 => "Mane",
        11 => "Creature skin 1", 12 => "Creature skin 2", 13 => "Creature skin 3", 14 => "Item icon",
        15 => "Guild background", 16 => "Guild emblem color", 17 => "Guild border", 18 => "Guild emblem",
        19 => "Eyes", 20 => "Jewelry / accessories", _ => $"Unidentified material ({type})"
    };

    private enum TextureRole
    {
        Unknown, Body, Hair, FacialHair, SkinDetails, FaceUpper, FaceLower, Scalp, Underwear,
        Cape, Equipment, Blade, Handle, Reflection, Icon, Mane, CreatureSkin,
        GuildBackground, GuildColor, GuildBorder, GuildEmblem, Eyes, Accessories
    }

    private sealed record Candidate(string Path, string Leaf, string Stem, string Family, TextureRole Role, string? Character);

    public static IReadOnlyDictionary<int, IReadOnlyList<string>> BuildChoices(M2PreviewGeometry geometry, IEnumerable<string> paths, bool includeOtherModels = false)
    {
        var candidates = paths.Where(path => Path.GetExtension(path).Equals(".blp", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                var leaf = Path.GetFileName(path.Replace('\\', '/'));
                var stem = Path.GetFileNameWithoutExtension(leaf);
                return new Candidate(path, leaf, Compact(stem), Family(stem), Classify(path), CharacterOwner(path));
            }).ToArray();
        var modelStem = Compact(Path.GetFileNameWithoutExtension(geometry.ModelPath.Replace('\\', '/')));
        if (modelStem.EndsWith("hd", StringComparison.Ordinal) || modelStem.EndsWith("sdr", StringComparison.Ordinal))
            modelStem = modelStem[..^(modelStem.EndsWith("sdr", StringComparison.Ordinal) ? 3 : 2)];
        var result = new Dictionary<int, IReadOnlyList<string>>();
        var character = CharacterOwner(geometry.ModelPath);
        foreach (var slot in geometry.TextureSlots)
        {
            var declared = candidates.Where(candidate => MatchesReference(slot, candidate)).ToArray();
            var declaredPaths = declared.Select(candidate => candidate.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var roles = declared.Select(candidate => candidate.Role).Where(role => role != TextureRole.Unknown).ToHashSet();
            if (!string.IsNullOrWhiteSpace(slot.EmbeddedPath))
            {
                var role = Classify(slot.EmbeddedPath);
                if (role != TextureRole.Unknown) roles.Add(role);
            }
            var family = Family(Path.GetFileNameWithoutExtension((slot.EmbeddedPath ?? "").Replace('\\', '/')));
            result[slot.Index] = candidates.Where(candidate => declaredPaths.Contains(candidate.Path)
                    || (includeOtherModels || character is null || candidate.Character is null || candidate.Character == character) && (slot.Type == 0
                    ? roles.Contains(candidate.Role) || family.Length >= 3 && candidate.Family == family
                    : MatchesRole(slot.Type, candidate.Role)))
                .OrderBy(candidate => declaredPaths.Contains(candidate.Path) ? 0 : SameFolder(geometry.ModelPath, candidate.Path) ? 1
                    : modelStem.Length > 0 && candidate.Stem.StartsWith(modelStem, StringComparison.Ordinal) ? 2 : 3)
                .ThenBy(candidate => candidate.Leaf, StringComparer.OrdinalIgnoreCase).ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Path).ToArray();
        }
        return result;
    }

    private static bool MatchesRole(uint type, TextureRole role) => type switch
    {
        1 => role == TextureRole.Body,
        2 => role is TextureRole.Cape or TextureRole.Equipment or TextureRole.Blade or TextureRole.Handle,
        3 => role == TextureRole.Blade,
        4 => role == TextureRole.Handle,
        5 => role == TextureRole.Reflection,
        6 => role == TextureRole.Hair,
        7 => role == TextureRole.FacialHair,
        8 => role is TextureRole.SkinDetails or TextureRole.FaceUpper or TextureRole.FaceLower or TextureRole.Scalp or TextureRole.Underwear,
        9 or 14 => role == TextureRole.Icon,
        10 => role == TextureRole.Mane,
        11 or 12 or 13 => role == TextureRole.CreatureSkin,
        15 => role == TextureRole.GuildBackground,
        16 => role == TextureRole.GuildColor,
        17 => role == TextureRole.GuildBorder,
        18 => role == TextureRole.GuildEmblem,
        19 => role == TextureRole.Eyes,
        20 => role == TextureRole.Accessories,
        _ => false
    };

    private static TextureRole Classify(string path)
    {
        var logical = "/" + path.Replace('\\', '/').ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(logical);
        var name = Compact(stem);
        // Partial character overlays and non-color maps are not complete body atlases.
        if (Regex.IsMatch(stem, @"(^|[_ -])(normal|specular|roughness|metallic|ao)([_ -]|$)", RegexOptions.CultureInvariant)) return TextureRole.Unknown;
        if (logical.Contains("/icons/", StringComparison.Ordinal) || stem.StartsWith("inv_", StringComparison.Ordinal)) return TextureRole.Icon;
        if (logical.Contains("/guildemblems/", StringComparison.Ordinal) || name.StartsWith("guild", StringComparison.Ordinal) || name.StartsWith("tabard", StringComparison.Ordinal))
        {
            if (name.Contains("background", StringComparison.Ordinal)) return TextureRole.GuildBackground;
            if (name.Contains("border", StringComparison.Ordinal)) return TextureRole.GuildBorder;
            if (name.Contains("color", StringComparison.Ordinal)) return TextureRole.GuildColor;
            if (name.Contains("emblem", StringComparison.Ordinal)) return TextureRole.GuildEmblem;
        }
        if (name.Contains("reflect", StringComparison.Ordinal)) return TextureRole.Reflection;
        if (name.Contains("faceupper", StringComparison.Ordinal)) return TextureRole.FaceUpper;
        if (name.Contains("facelower", StringComparison.Ordinal)) return TextureRole.FaceLower;
        if (name.Contains("scalp", StringComparison.Ordinal)) return TextureRole.Scalp;
        if (name.Contains("nakedtorso", StringComparison.Ordinal) || name.Contains("nakedpelvis", StringComparison.Ordinal)
            || name.Contains("underclothes", StringComparison.Ordinal) || name.Contains("underwear", StringComparison.Ordinal)) return TextureRole.Underwear;
        if (name.Contains("facialhair", StringComparison.Ordinal) || name.Contains("beard", StringComparison.Ordinal)
            || name.Contains("moustache", StringComparison.Ordinal) || name.Contains("mustache", StringComparison.Ordinal)) return TextureRole.FacialHair;
        if (name.Contains("tattoo", StringComparison.Ordinal) || name.Contains("eyebrow", StringComparison.Ordinal)
            || name.Contains("runes", StringComparison.Ordinal) || name.Contains("skinextra", StringComparison.Ordinal)
            || logical.Contains("/texturecomponents/", StringComparison.Ordinal)) return TextureRole.SkinDetails;
        if (HasPart(stem, logical, "hair")) return TextureRole.Hair;
        if (HasPart(stem, logical, "eye") || name.Contains("eyeglow", StringComparison.Ordinal)) return TextureRole.Eyes;
        if (name.Contains("jewelry", StringComparison.Ordinal) || name.Contains("jewellery", StringComparison.Ordinal)
            || name.Contains("bracelet", StringComparison.Ordinal) || name.Contains("earring", StringComparison.Ordinal)
            || name.Contains("necklace", StringComparison.Ordinal) || name.Contains("accessor", StringComparison.Ordinal)) return TextureRole.Accessories;
        if (HasPart(stem, logical, "cloak") || HasPart(stem, logical, "cape")) return TextureRole.Cape;
        if (HasPart(stem, logical, "blade")) return TextureRole.Blade;
        if (HasPart(stem, logical, "handle") || HasPart(stem, logical, "hilt")) return TextureRole.Handle;
        if (HasPart(stem, logical, "mane")) return TextureRole.Mane;
        if (logical.Contains("/creature/", StringComparison.Ordinal) && name.StartsWith(Compact(Path.GetFileName(ModelBrowserSource.DirectoryName(logical))), StringComparison.Ordinal)) return TextureRole.CreatureSkin;
        if (logical.Contains("/objectcomponents/", StringComparison.Ordinal)) return TextureRole.Equipment;
        if (HasPart(stem, logical, "skin") && Regex.IsMatch(name, @"skin(\d|color)", RegexOptions.CultureInvariant) && !name.EndsWith("extra", StringComparison.Ordinal)
            || Regex.IsMatch(stem, @"(^|[_ -])(body|clothing)([_ -]|$)", RegexOptions.CultureInvariant)) return TextureRole.Body;
        return TextureRole.Unknown;
    }

    private static bool HasPart(string stem, string logicalPath, string part)
    {
        var index = stem.IndexOf(part, StringComparison.Ordinal);
        if (index < 0) return false;
        if (index == 0 || !char.IsLetter(stem[index - 1])) return true;
        var prefix = stem[..index];
        return prefix.EndsWith("male", StringComparison.Ordinal)
            || logicalPath.Split('/').Any(folder => Compact(folder) == Compact(prefix));
    }

    private static bool MatchesReference(M2TextureSlot slot, Candidate candidate)
    {
        if (slot.FileDataId != 0)
        {
            var id = slot.FileDataId.ToString(CultureInfo.InvariantCulture);
            var stem = Path.GetFileNameWithoutExtension(candidate.Leaf);
            if (stem == id || stem.EndsWith("_" + id, StringComparison.Ordinal)) return true;
        }
        return !string.IsNullOrWhiteSpace(slot.EmbeddedPath)
            && candidate.Leaf.Equals(Path.GetFileName(slot.EmbeddedPath.Replace('\\', '/')), StringComparison.OrdinalIgnoreCase);
    }

    private static string Compact(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Family(string name) => Compact(Regex.Replace(name, @"\d+", "", RegexOptions.CultureInvariant));
    private static string? CharacterOwner(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = Compact(Path.GetFileNameWithoutExtension(normalized));
        var named = Regex.Match(name, @"^[a-z]+?(?:female|male)", RegexOptions.CultureInvariant);
        if (named.Success && named.Value is not "female" and not "male") return named.Value;
        var folders = ModelBrowserSource.DirectoryName(normalized).Split('/');
        for (var index = folders.Length - 1; index > 0; index--)
        {
            var sex = Compact(folders[index]);
            if (sex is "female" or "male") return Compact(folders[index - 1]) + sex;
        }
        return null;
    }

    private static bool SameFolder(string model, string texture)
    {
        var modelFolder = ModelBrowserSource.DirectoryName(model.Replace('\\', '/'));
        var textureFolder = ModelBrowserSource.DirectoryName(texture.Replace('\\', '/'));
        return textureFolder.Length == 0 || modelFolder.Equals(textureFolder, StringComparison.OrdinalIgnoreCase)
            || modelFolder.EndsWith("/" + textureFolder, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyDictionary<int, string> SuggestBindings(ModelBrowserSource source, M2PreviewGeometry geometry)
    {
        var result = new Dictionary<int, string>();
        var nearby = source.Nearby(".blp");
        var stem = Path.GetFileNameWithoutExtension(source.ModelName).Replace("_hd", "", StringComparison.OrdinalIgnoreCase).Replace("_sdr", "", StringComparison.OrdinalIgnoreCase);
        foreach (var slot in geometry.TextureSlots)
        {
            var path = !string.IsNullOrWhiteSpace(slot.EmbeddedPath) ? source.Find(slot.EmbeddedPath) : null;
            path ??= source.FindFileDataId(slot.FileDataId, ".blp");
            if (path is null && slot.Type == 1)
                path = nearby.FirstOrDefault(path => Path.GetFileName(path).StartsWith(stem + "skin00_00", StringComparison.OrdinalIgnoreCase))
                    ?? nearby.FirstOrDefault(path => Path.GetFileName(path).StartsWith(stem + "skin00", StringComparison.OrdinalIgnoreCase));
            if (path is null && slot.Type == 6)
                path = nearby.FirstOrDefault(path => Path.GetFileName(path).StartsWith(stem + "hair00_00", StringComparison.OrdinalIgnoreCase));
            if (path is not null) result[slot.Index] = path;
        }
        return result;
    }

    public static RgbaTexture Decode(ModelBrowserSource source, string path)
    {
        using var stream = new MemoryStream(source.Read(path), false);
        return BlpTextureService.Decode(stream, path);
    }
}
