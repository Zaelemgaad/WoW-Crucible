using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

public enum FileEditorKind { Table, Model, Texture, Image, Map, Cache, Archive, NativeAsset, Sql, Project, Quest, Talents, StructuredRows, Unknown }
public sealed record FileOpenDetection(FileEditorKind Kind, string Description);

/// <summary>Read-only file identification. Identification never edits the source or executes its contents.</summary>
public static class FileOpenCatalog
{
    public static FileOpenDetection Detect(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[16];
        var read = stream.Read(header);
        var magic = Encoding.ASCII.GetString(header[..Math.Min(4, read)]);
        if (magic is "WDBC" or "WDB2" or "WDB5" or "WDB6" or "WDC1" or "WDC2" or "WDC3" or "WDC4" or "WDC5") return new(FileEditorKind.Table, "Client data table");
        if (magic is "BLP1" or "BLP2") return new(FileEditorKind.Texture, "BLP texture");
        if (magic is "MD20" or "MD21") return new(FileEditorKind.Model, "M2 model");
        if (magic is "MPQ\u001a" or "MPQ\u001b") return new(FileEditorKind.Archive, "MPQ archive");
        if (read >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            read >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255 || magic.StartsWith("BM", StringComparison.Ordinal))
            return new(FileEditorKind.Image, "Image texture");

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".json" && stream.Length <= 32 * 1024 * 1024)
        {
            stream.Position = 0;
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("IdRegistryFile", out _) && root.TryGetProperty("TargetProfile", out _)) return new(FileEditorKind.Project, "Crucible project");
                if (root.TryGetProperty("Values", out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty("LogTitle", out _)) return new(FileEditorKind.Quest, "Quest draft");
                if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == "twc-workspace" ||
                    root.TryGetProperty("trees", out _) && root.TryGetProperty("classId", out _) ||
                    root.EnumerateObject().Any(property => property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("trees", out _) && property.Value.TryGetProperty("classId", out _)))
                    return new(FileEditorKind.Talents, "Talent layout");
            }
            return new(FileEditorKind.StructuredRows, "JSON data — choose its destination");
        }
        return extension switch
        {
            ".dbc" or ".db2" => new(FileEditorKind.Table, "Client data table"),
            ".m2" => new(FileEditorKind.Model, "M2 model"),
            ".blp" => new(FileEditorKind.Texture, "BLP texture"),
            ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" => new(FileEditorKind.Image, "Image texture"),
            ".adt" or ".wdt" or ".wdl" => new(FileEditorKind.Map, "World map"),
            ".wdb" or ".adb" => new(FileEditorKind.Cache, "Client cache"),
            ".mpq" => new(FileEditorKind.Archive, "MPQ archive"),
            ".wmo" => new(FileEditorKind.NativeAsset, "Native model asset"),
            ".sql" => new(FileEditorKind.Sql, "SQL script"),
            ".csv" or ".json" or ".jsonl" or ".ndjson" => new(FileEditorKind.StructuredRows, "Structured rows — choose a destination"),
            _ => new(FileEditorKind.Unknown, "Unrecognized file type")
        };
    }
}
