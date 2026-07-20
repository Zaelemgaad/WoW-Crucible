using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum CppItemGrantConfidence
{
    DirectLiteral,
    SameFileConstant,
    UniqueSourceConstant
}

public sealed record CppItemGrant(
    uint ItemId,
    uint? Count,
    string Api,
    string SourcePath,
    int Line,
    string ItemToken,
    CppItemGrantConfidence Confidence);

public sealed record CppItemGrantAuditReport(
    string Root,
    int DiscoveredFiles,
    int ScannedFiles,
    int SkippedFiles,
    int UnresolvedCalls,
    IReadOnlyList<CppItemGrant> Grants,
    IReadOnlyList<string> Findings);

/// <summary>
/// Performs a deliberately narrow, non-executing source inspection for exact
/// player item-grant calls in AzerothCore/TrinityCore script and module trees.
/// A call site is useful review evidence, but does not prove that the enclosing
/// script is registered or reachable in a particular world database.
/// </summary>
public sealed partial class CppItemGrantAuditService
{
    private const long MaximumSourceBytes = 16L * 1024 * 1024;
    private const int MaximumSourceFiles = 50_000;
    private static readonly string[] Extensions = [".cpp", ".cc", ".cxx", ".h", ".hpp"];
    private static readonly string[] SkippedDirectoryNames = [".git", ".vs", "bin", "obj", "build", "deps", "doc", "docs", "test", "tests", "var"];

    private sealed record SourceFile(string FullPath, string RelativePath, string Sanitized,
        IReadOnlyDictionary<string, IReadOnlySet<uint>> Constants);

    public Task<CppItemGrantAuditReport> ScanAsync(string coreSourceRoot, CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(coreSourceRoot, cancellationToken), cancellationToken);

    public CppItemGrantAuditReport Scan(string coreSourceRoot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(coreSourceRoot)) throw new ArgumentException("Core source folder is required.", nameof(coreSourceRoot));
        var root = Path.GetFullPath(coreSourceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Core source folder was not found: {root}");

        var scanRoots = ResolveScanRoots(root);
        var discovered = new List<string>();
        var skipped = 0;
        foreach (var scanRoot in scanRoots)
        {
            foreach (var path in Directory.EnumerateFiles(scanRoot, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (discovered.Count >= MaximumSourceFiles)
                    throw new InvalidDataException($"Core source scan exceeded the safety limit of {MaximumSourceFiles:N0} files.");
                if (ShouldSkip(root, path) || !Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) { skipped++; continue; }
                discovered.Add(path);
            }
        }

        var files = new List<SourceFile>(discovered.Count);
        foreach (var path in discovered.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > MaximumSourceBytes) { skipped++; continue; }
            var sanitized = Sanitize(File.ReadAllText(path));
            files.Add(new(path, Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), sanitized, ReadConstants(sanitized)));
        }

        var globalConstants = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var pair in file.Constants)
            {
                if (!globalConstants.TryGetValue(pair.Key, out var values)) globalConstants[pair.Key] = values = [];
                values.UnionWith(pair.Value);
            }

        var grants = new List<CppItemGrant>();
        var unresolved = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in GrantCallRegex().Matches(file.Sanitized))
            {
                if (!IsPlayerReceiver(file.Sanitized, match.Index)) continue;
                if (!TryReadArguments(file.Sanitized, match.Index + match.Length, out var arguments)) { unresolved++; continue; }
                var api = match.Groups["api"].Value;
                var itemIndex = api.Equals("StoreNewItem", StringComparison.Ordinal) ? 1 : 0;
                if (arguments.Count <= itemIndex || !TryResolve(arguments[itemIndex], file.Constants, globalConstants, out var item, out var token, out var confidence) || item == 0)
                {
                    unresolved++;
                    continue;
                }

                uint? count = null;
                if (api.Equals("AddItem", StringComparison.Ordinal))
                {
                    if (arguments.Count < 2 || !TryResolve(arguments[1], file.Constants, globalConstants, out var parsedCount, out _, out _) || parsedCount == 0)
                    {
                        unresolved++;
                        continue;
                    }
                    count = parsedCount;
                }
                grants.Add(new(item, count, api, file.RelativePath, LineNumber(file.Sanitized, match.Index), token, confidence));
            }
        }

        var unique = grants
            .DistinctBy(grant => (grant.ItemId, grant.Count, grant.Api, grant.SourcePath.ToUpperInvariant(), grant.Line))
            .OrderBy(grant => grant.ItemId).ThenBy(grant => grant.SourcePath, StringComparer.OrdinalIgnoreCase).ThenBy(grant => grant.Line)
            .ToArray();
        var findings = new List<string>
        {
            $"Read-only source scan covered {files.Count:N0} C/C++ file(s) beneath {string.Join(", ", scanRoots.Select(path => Path.GetRelativePath(root, path)))}.",
            $"Resolved {unique.Length:N0} exact player item-grant call(s); {unresolved:N0} player-like call(s) used runtime/ambiguous values and were not inferred."
        };
        if (skipped > 0) findings.Add($"Skipped {skipped:N0} non-source, excluded-directory, inaccessible, or oversized file(s).");
        return new(root, discovered.Count, files.Count, skipped, unresolved, unique, findings);
    }

    private static IReadOnlyList<string> ResolveScanRoots(string root)
    {
        var candidates = new[] { Path.Combine(root, "src", "server", "scripts"), Path.Combine(root, "modules") }
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.Length > 0 ? candidates : [root];
    }

    private static bool ShouldSkip(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => SkippedDirectoryNames.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<uint>> ReadConstants(string source)
    {
        var values = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        void Add(Match match)
        {
            if (!TryParseUnsignedLiteral(match.Groups["value"].Value, out var value)) return;
            var name = match.Groups["name"].Value;
            if (!values.TryGetValue(name, out var entries)) values[name] = entries = [];
            entries.Add(value);
        }
        foreach (Match match in DefineRegex().Matches(source)) Add(match);
        foreach (Match match in AssignmentRegex().Matches(source)) Add(match);
        return values.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<uint>)pair.Value, StringComparer.Ordinal);
    }

    private static bool TryResolve(string expression, IReadOnlyDictionary<string, IReadOnlySet<uint>> local,
        IReadOnlyDictionary<string, HashSet<uint>> global, out uint value, out string token, out CppItemGrantConfidence confidence)
    {
        token = NormalizeExpression(expression);
        if (TryParseUnsignedLiteral(token, out value)) { confidence = CppItemGrantConfidence.DirectLiteral; return true; }
        if (!IdentifierRegex().IsMatch(token)) { confidence = default; value = 0; return false; }
        if (local.TryGetValue(token, out var localValues) && localValues.Count == 1)
        {
            value = localValues.Single(); confidence = CppItemGrantConfidence.SameFileConstant; return true;
        }
        if (global.TryGetValue(token, out var globalValues) && globalValues.Count == 1)
        {
            value = globalValues.Single(); confidence = CppItemGrantConfidence.UniqueSourceConstant; return true;
        }
        confidence = default; value = 0; return false;
    }

    private static string NormalizeExpression(string expression)
    {
        var value = expression.Trim();
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && ParenthesesWrap(value)) value = value[1..^1].Trim();
        return value;
    }

    private static bool ParenthesesWrap(string value)
    {
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '(') depth++;
            else if (value[index] == ')' && --depth == 0) return index == value.Length - 1;
        }
        return false;
    }

    private static bool TryParseUnsignedLiteral(string text, out uint value)
    {
        var candidate = text.Trim().Replace("'", string.Empty, StringComparison.Ordinal);
        while (candidate.Length > 0 && "uUlL".Contains(candidate[^1])) candidate = candidate[..^1];
        return candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(candidate.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadArguments(string source, int openingParenthesis, out IReadOnlyList<string> arguments)
    {
        arguments = [];
        if (openingParenthesis >= source.Length || source[openingParenthesis] != '(') return false;
        var depth = 0; var start = openingParenthesis + 1; var values = new List<string>();
        for (var index = openingParenthesis; index < source.Length; index++)
        {
            switch (source[index])
            {
                case '(':
                case '[':
                case '{': depth++; break;
                case ')':
                    depth--;
                    if (depth == 0) { values.Add(source[start..index]); arguments = values; return true; }
                    break;
                case ']':
                case '}': depth--; break;
                case ',' when depth == 1: values.Add(source[start..index]); start = index + 1; break;
            }
        }
        return false;
    }

    private static bool IsPlayerReceiver(string source, int arrowIndex)
    {
        var lineStart = source.LastIndexOf('\n', Math.Max(0, arrowIndex - 1));
        var prefix = source[(lineStart + 1)..arrowIndex];
        var receiver = ReceiverRegex().Match(prefix);
        if (!receiver.Success) return false;
        var value = receiver.Groups["receiver"].Value;
        return value.Contains("player", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("caster", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("target", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("invoker", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("ToPlayer", StringComparison.Ordinal);
    }

    private static int LineNumber(string source, int offset)
    {
        var line = 1;
        for (var index = 0; index < offset; index++) if (source[index] == '\n') line++;
        return line;
    }

    internal static string Sanitize(string source)
    {
        var output = new StringBuilder(source.Length);
        var state = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var current = source[index]; var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (state == 0)
            {
                if (current == '/' && next == '/') { output.Append("  "); index++; state = 1; }
                else if (current == '/' && next == '*') { output.Append("  "); index++; state = 2; }
                else if (current == '"') { output.Append(' '); state = 3; }
                else if (current == '\'') { output.Append(' '); state = 4; }
                else output.Append(current);
            }
            else if (state == 1)
            {
                if (current == '\n') { output.Append('\n'); state = 0; } else output.Append(' ');
            }
            else if (state == 2)
            {
                if (current == '*' && next == '/') { output.Append("  "); index++; state = 0; }
                else output.Append(current is '\r' or '\n' ? current : ' ');
            }
            else
            {
                if (current == '\\' && next != '\0') { output.Append("  "); index++; }
                else if ((state == 3 && current == '"') || (state == 4 && current == '\'')) { output.Append(' '); state = 0; }
                else output.Append(current is '\r' or '\n' ? current : ' ');
            }
        }
        return output.ToString();
    }

    [GeneratedRegex(@"->\s*(?<api>AddItem|StoreNewItem)\s*(?=\()", RegexOptions.CultureInvariant)]
    private static partial Regex GrantCallRegex();
    [GeneratedRegex(@"(?m)^\s*#\s*define\s+(?<name>[A-Z][A-Z0-9_]{2,})\s+(?<value>0[xX][0-9A-Fa-f]+|[0-9][0-9']*)[uUlL]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex DefineRegex();
    [GeneratedRegex(@"\b(?<name>[A-Z][A-Z0-9_]{2,})\s*=\s*(?<value>0[xX][0-9A-Fa-f]+|[0-9][0-9']*)[uUlL]*\b", RegexOptions.CultureInvariant)]
    private static partial Regex AssignmentRegex();
    [GeneratedRegex(@"^[A-Z_][A-Z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
    [GeneratedRegex(@"(?<receiver>(?:[A-Za-z_][A-Za-z0-9_]*(?:\s*\(\s*\))?)(?:\s*->\s*[A-Za-z_][A-Za-z0-9_]*\s*\(\s*\))*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReceiverRegex();
}
