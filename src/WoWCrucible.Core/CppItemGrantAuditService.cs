using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public enum CppItemGrantConfidence
{
    DirectLiteral,
    SameFileConstant,
    UniqueSourceConstant
}

public enum CppItemGrantScriptKind
{
    Unknown,
    ItemScript,
    CreatureScript,
    SpellScript,
    AuraScript
}

public sealed record CppItemGrant(
    uint ItemId,
    uint? Count,
    string Api,
    string SourcePath,
    int Line,
    string ItemToken,
    CppItemGrantConfidence Confidence,
    string? ScriptClass,
    string? ScriptName,
    CppItemGrantScriptKind ScriptKind,
    string? Callback,
    bool RegisteredInSource,
    string? LoaderFunction,
    bool LoaderInvoked);

public sealed record CppItemGrantAuditReport(
    string Root,
    int DiscoveredFiles,
    int ScannedFiles,
    int SkippedFiles,
    int UnresolvedCalls,
    string? SourceRevision,
    bool? WorktreeClean,
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

    private sealed record SourceFile(string FullPath, string RelativePath, string Original, string Sanitized,
        IReadOnlyDictionary<string, IReadOnlySet<uint>> Constants);
    private sealed record ScriptContext(string Class, string Name, CppItemGrantScriptKind Kind, string? Callback,
        bool Registered, string? LoaderFunction, bool LoaderInvoked);

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
            var original = File.ReadAllText(path); var sanitized = Sanitize(original);
            files.Add(new(path, Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), original, sanitized, ReadConstants(sanitized)));
        }

        var globalConstants = new Dictionary<string, HashSet<uint>>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var pair in file.Constants)
            {
                if (!globalConstants.TryGetValue(pair.Key, out var values)) globalConstants[pair.Key] = values = [];
                values.UnionWith(pair.Value);
            }
        var loaderCalls = CountLoaderInvocations(files);

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
                var script = FindScriptContext(file, match.Index, loaderCalls);
                grants.Add(new(item, count, api, file.RelativePath, LineNumber(file.Sanitized, match.Index), token, confidence,
                    script?.Class, script?.Name, script?.Kind ?? CppItemGrantScriptKind.Unknown, script?.Callback, script?.Registered == true,
                    script?.LoaderFunction, script?.LoaderInvoked == true));
            }
        }

        var unique = grants
            .DistinctBy(grant => (grant.ItemId, grant.Count, grant.Api, grant.SourcePath.ToUpperInvariant(), grant.Line))
            .OrderBy(grant => grant.ItemId).ThenBy(grant => grant.SourcePath, StringComparer.OrdinalIgnoreCase).ThenBy(grant => grant.Line)
            .ToArray();
        var git = ReadGitIdentity(root);
        var findings = new List<string>
        {
            $"Read-only source scan covered {files.Count:N0} C/C++ file(s) beneath {string.Join(", ", scanRoots.Select(path => Path.GetRelativePath(root, path)))}.",
            $"Resolved {unique.Length:N0} exact player item-grant call(s); {unresolved:N0} player-like call(s) used runtime/ambiguous values and were not inferred."
        };
        if (git.Revision is not null) findings.Add($"Source Git revision {git.Revision}; worktree {(git.Clean == true ? "clean" : git.Clean == false ? "modified/untracked" : "cleanliness unavailable")}.");
        else findings.Add("Source Git revision was unavailable; runtime source identity cannot be proven.");
        if (skipped > 0) findings.Add($"Skipped {skipped:N0} non-source, excluded-directory, inaccessible, or oversized file(s).");
        return new(root, discovered.Count, files.Count, skipped, unresolved, git.Revision, git.Clean, unique, findings);
    }

    private static (string? Revision, bool? Clean) ReadGitIdentity(string root)
    {
        try
        {
            var revision = RunGit(root, "rev-parse", "HEAD");
            if (revision.ExitCode != 0 || !Regex.IsMatch(revision.Output.Trim(), "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)) return (null, null);
            var status = RunGit(root, "status", "--porcelain", "--untracked-files=normal");
            return (revision.Output.Trim().ToLowerInvariant(), status.ExitCode == 0 ? string.IsNullOrWhiteSpace(status.Output) : null);
        }
        catch { return (null, null); }
    }

    private static (int ExitCode, string Output) RunGit(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root
            }
        };
        process.StartInfo.ArgumentList.Add("-C"); process.StartInfo.ArgumentList.Add(root);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return (-1, string.Empty);
        var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("Git source identity check exceeded 10 seconds."); }
        Task.WaitAll(outputTask, errorTask);
        return (process.ExitCode, outputTask.Result);
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

    private static ScriptContext? FindScriptContext(SourceFile file, int callOffset, IReadOnlyDictionary<string, int> loaderCalls)
    {
        Match? selected = null; var selectedClose = -1;
        foreach (Match match in ScriptClassRegex().Matches(file.Sanitized))
        {
            if (match.Index >= callOffset) break;
            var opening = file.Sanitized.IndexOf('{', match.Index + match.Length);
            if (opening < 0) continue;
            var closing = FindClosingBrace(file.Sanitized, opening);
            if (closing < callOffset || callOffset <= opening) continue;
            if (selected is null || match.Index > selected.Index) { selected = match; selectedClose = closing; }
        }
        if (selected is null) return null;

        var className = selected.Groups["class"].Value;
        var kind = Enum.TryParse<CppItemGrantScriptKind>(selected.Groups["base"].Value, out var parsed) ? parsed : CppItemGrantScriptKind.Unknown;
        var callback = FindContainingMethod(file.Sanitized, callOffset, selected.Index, selectedClose);
        var scriptName = className;
        if (kind is CppItemGrantScriptKind.ItemScript or CppItemGrantScriptKind.CreatureScript)
        {
            var opening = file.Original.IndexOf('{', selected.Index + selected.Length);
            var body = opening >= 0 && selectedClose > opening ? file.Original[opening..(selectedClose + 1)] : string.Empty;
            var constructor = NamedScriptConstructorRegex().Match(body);
            if (constructor.Success) scriptName = constructor.Groups["name"].Value;
        }

        var registration = Regex.Match(file.Sanitized,
            $@"(?:\bnew\s+{Regex.Escape(className)}\s*\(|\bRegister[A-Za-z0-9_]*\s*\([^;\r\n]*\b{Regex.Escape(className)}\b)",
            RegexOptions.CultureInvariant);
        if (!registration.Success) return new(className, scriptName, kind, callback, false, null, false);

        string? loader = null;
        foreach (Match candidate in LoaderFunctionRegex().Matches(file.Sanitized))
        {
            var opening = file.Sanitized.IndexOf('{', candidate.Index + candidate.Length);
            if (opening < 0) continue;
            var closing = FindClosingBrace(file.Sanitized, opening);
            if (registration.Index > opening && registration.Index < closing) { loader = candidate.Groups["loader"].Value; break; }
        }
        var invoked = loader is not null && loaderCalls.TryGetValue(loader, out var calls) && calls > 0;
        return new(className, scriptName, kind, callback, true, loader, invoked);
    }

    private static IReadOnlyDictionary<string, int> CountLoaderInvocations(IReadOnlyList<SourceFile> files)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (Match match in LoaderCallRegex().Matches(file.Sanitized))
            {
                var lineStart = file.Sanitized.LastIndexOf('\n', Math.Max(0, match.Index - 1));
                var prefix = file.Sanitized[(lineStart + 1)..match.Index].TrimEnd();
                if (Regex.IsMatch(prefix, @"\bvoid\s*$", RegexOptions.CultureInvariant)) continue;
                var loader = match.Groups["loader"].Value;
                result[loader] = result.GetValueOrDefault(loader) + 1;
            }
        return result;
    }

    private static string? FindContainingMethod(string source, int callOffset, int classStart, int classEnd)
    {
        Match? selected = null;
        foreach (Match match in MethodRegex().Matches(source, classStart))
        {
            if (match.Index >= callOffset || match.Index > classEnd) break;
            var opening = source.IndexOf('{', match.Index + match.Length - 1);
            if (opening < 0 || opening > callOffset) continue;
            var closing = FindClosingBrace(source, opening);
            if (closing >= callOffset && (selected is null || match.Index > selected.Index)) selected = match;
        }
        return selected?.Groups["method"].Value;
    }

    private static int FindClosingBrace(string source, int opening)
    {
        var depth = 0;
        for (var index = opening; index < source.Length; index++)
        {
            if (source[index] == '{') depth++;
            else if (source[index] == '}' && --depth == 0) return index;
        }
        return -1;
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
    [GeneratedRegex(@"\b(?:class|struct)\s+(?<class>[A-Za-z_][A-Za-z0-9_]*)\s*:\s*public\s+(?<base>ItemScript|CreatureScript|SpellScript|AuraScript)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptClassRegex();
    [GeneratedRegex(@"\b(?:ItemScript|CreatureScript)\s*\(\s*""(?<name>[^""]+)""\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedScriptConstructorRegex();
    [GeneratedRegex(@"\bvoid\s+(?<loader>(?:AddSC_|Addmod_)[A-Za-z0-9_]+)\s*\(\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex LoaderFunctionRegex();
    [GeneratedRegex(@"\b(?<loader>(?:AddSC_|Addmod_)[A-Za-z0-9_]+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LoaderCallRegex();
    [GeneratedRegex(@"\b(?:bool|void|Item\s*|SpellCastResult|int(?:8|16|32|64)?|uint(?:8|16|32|64)?|[A-Za-z_][A-Za-z0-9_:<>]*\s*[&*]?)\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*(?:const\s*)?(?:override\s*)?\{", RegexOptions.CultureInvariant)]
    private static partial Regex MethodRegex();
}
