using System.Net;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record KnowledgeSection(int Index, string Heading, int Level, string Markdown, string PlainText);
public sealed record KnowledgeArticle(string SourcePath, string RelativePath, string Locale, string Title, IReadOnlyList<KnowledgeSection> Sections);
public sealed record KnowledgeSearchHit(string SourcePath, string RelativePath, string Locale, string Title, string Heading,
    string Excerpt, int Score, int SectionIndex, string PlainText);
public sealed record KnowledgeReferenceIndex(string RootPath, DateTimeOffset IndexedUtc, IReadOnlyList<KnowledgeArticle> Articles,
    IReadOnlyList<string> Locales, int SectionCount, long SourceBytes);

/// <summary>
/// Builds a bounded, offline search index over the local Markdown wiki corpus.
/// The provider never executes site generators, scripts, HTML, or remote links.
/// </summary>
public sealed partial class KnowledgeReferenceService
{
    private sealed record IndexedSection(KnowledgeArticle Article, KnowledgeSection Section, IReadOnlySet<string> Tokens);
    private readonly List<IndexedSection> _sections = [];
    private readonly Dictionary<string, List<int>> _postings = new(StringComparer.OrdinalIgnoreCase);
    public KnowledgeReferenceIndex? Index { get; private set; }

    public KnowledgeReferenceIndex Build(string rootPath, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(rootPath);
        var articles = new List<KnowledgeArticle>();
        long sourceBytes = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                     .Where(path => !IsExcluded(root, path)).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > 8 * 1024 * 1024) continue;
            var markdown = File.ReadAllText(path); sourceBytes += info.Length;
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var locale = DetectLocale(relative, markdown);
            var title = ReadTitle(markdown, Path.GetFileNameWithoutExtension(path));
            var sections = SplitSections(markdown, title);
            articles.Add(new(path, relative, locale, title, sections));
        }
        if (articles.Count == 0) throw new InvalidDataException($"No Markdown reference documents were found under {root}.");

        _sections.Clear(); _postings.Clear();
        foreach (var article in articles)
            foreach (var section in article.Sections)
            {
                var tokens = Tokenize($"{article.Title} {article.RelativePath} {section.Heading} {section.PlainText}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                var index = _sections.Count; _sections.Add(new(article, section, tokens));
                foreach (var token in tokens)
                {
                    if (!_postings.TryGetValue(token, out var posting)) _postings[token] = posting = [];
                    posting.Add(index);
                }
            }
        var locales = articles.Select(article => article.Locale).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return Index = new(root, DateTimeOffset.UtcNow, articles, locales, _sections.Count, sourceBytes);
    }

    public IReadOnlyList<KnowledgeSearchHit> Search(string? query, string? locale = null, int limit = 200)
    {
        if (Index is null) throw new InvalidOperationException("Build the knowledge index before searching it.");
        limit = Math.Clamp(limit, 1, 500);
        var terms = Tokenize(query ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        IEnumerable<int> candidates;
        if (terms.Length == 0) candidates = Enumerable.Range(0, _sections.Count);
        else
        {
            HashSet<int>? intersection = null;
            foreach (var term in terms)
            {
                var matches = PostingFor(term);
                if (intersection is null) intersection = matches;
                else intersection.IntersectWith(matches);
                if (intersection.Count == 0) break;
            }
            candidates = intersection ?? [];
        }

        var normalizedLocale = string.IsNullOrWhiteSpace(locale) || locale.Equals("all", StringComparison.OrdinalIgnoreCase) ? null : locale.Trim();
        return candidates.Select(index => (Indexed: _sections[index], Score: Score(_sections[index], terms)))
            .Where(value => normalizedLocale is null || value.Indexed.Article.Locale.Equals(normalizedLocale, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Indexed.Article.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Indexed.Section.Index)
            .Take(limit)
            .Select(value => new KnowledgeSearchHit(value.Indexed.Article.SourcePath, value.Indexed.Article.RelativePath,
                value.Indexed.Article.Locale, value.Indexed.Article.Title, value.Indexed.Section.Heading,
                Excerpt(value.Indexed.Section.PlainText, terms), value.Score, value.Indexed.Section.Index, value.Indexed.Section.PlainText))
            .ToArray();
    }

    public static string? FindWikiRoot(string startPath)
    {
        var current = File.Exists(startPath) ? Path.GetDirectoryName(Path.GetFullPath(startPath)) : Path.GetFullPath(startPath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            foreach (var candidate in new[] { current, Path.Combine(current, "wiki") })
                if (Directory.Exists(Path.Combine(candidate, "docs")) && File.Exists(Path.Combine(candidate, "_config.yml"))) return candidate;
            current = Directory.GetParent(current)?.FullName;
        }
        return null;
    }

    private HashSet<int> PostingFor(string term)
    {
        if (_postings.TryGetValue(term, out var exact)) return exact.ToHashSet();
        var result = new HashSet<int>();
        foreach (var pair in _postings)
            if (pair.Key.Contains(term, StringComparison.OrdinalIgnoreCase)) result.UnionWith(pair.Value);
        return result;
    }

    private static int Score(IndexedSection indexed, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return indexed.Section.Index == 0 ? 10 : 1;
        var title = indexed.Article.Title; var heading = indexed.Section.Heading; var path = indexed.Article.RelativePath; var body = indexed.Section.PlainText;
        var score = 0;
        for (var termIndex = 0; termIndex < terms.Count; termIndex++)
        {
            var term = terms[termIndex]; var headingWeight = termIndex + 1;
            if (title.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 300;
            else if (title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 120;
            if (heading.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 180 * headingWeight;
            else if (heading.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 75 * headingWeight;
            if (path.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 45;
            score += Math.Min(30, Count(body, term) * 3);
        }
        return score;
    }

    private static IReadOnlyList<KnowledgeSection> SplitSections(string markdown, string title)
    {
        markdown = RemoveFrontMatter(markdown).Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = markdown.Split('\n'); var result = new List<KnowledgeSection>(); var body = new List<string>(); var heading = title; var level = 1;
        void Flush()
        {
            var source = string.Join(Environment.NewLine, body).Trim();
            var plain = ToPlainText(source);
            if (plain.Length > 0 || result.Count == 0) result.Add(new(result.Count, heading, level, source, plain));
            body.Clear();
        }
        var fenced = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fenced = !fenced; body.Add(line); continue; }
            var match = fenced ? Match.Empty : HeadingRegex().Match(line);
            if (match.Success)
            {
                Flush(); level = match.Groups[1].Value.Length; heading = ToPlainText(match.Groups[2].Value).Trim();
            }
            else body.Add(line);
        }
        Flush(); return result;
    }

    private static string ToPlainText(string markdown)
    {
        var text = ImageRegex().Replace(markdown, match => match.Groups[1].Value);
        text = LinkRegex().Replace(text, match => match.Groups[1].Value);
        text = HtmlRegex().Replace(text, string.Empty);
        text = FenceRegex().Replace(text, string.Empty);
        text = DecorationRegex().Replace(text, string.Empty);
        text = MarkdownEscapeRegex().Replace(text, "$1");
        text = TableSeparatorRegex().Replace(text, string.Empty);
        text = Regex.Replace(text, @"^[ \t]*[-*+]\s+", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\n{3,}", Environment.NewLine + Environment.NewLine);
        return WebUtility.HtmlDecode(text).Trim();
    }

    private static string Excerpt(string text, IReadOnlyList<string> terms)
    {
        if (text.Length <= 280) return text;
        var position = terms.Select(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase)).Where(index => index >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Max(0, position - 90); var length = Math.Min(280, text.Length - start);
        return (start > 0 ? "…" : string.Empty) + text.Substring(start, length).ReplaceLineEndings(" ").Trim() + (start + length < text.Length ? "…" : string.Empty);
    }

    private static int Count(string text, string term)
    {
        var count = 0; var offset = 0;
        while ((offset = text.IndexOf(term, offset, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; offset += term.Length; }
        return count;
    }

    private static string NormalizeRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Select the local wiki root.", nameof(rootPath));
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Knowledge root does not exist: {root}");
        return root;
    }
    private static bool IsExcluded(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) || part.Equals("_site", StringComparison.OrdinalIgnoreCase) || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
    }
    private static string DetectLocale(string relative, string markdown)
    {
        var parts = relative.Split('/');
        var docs = Array.FindIndex(parts, part => part.Equals("docs", StringComparison.OrdinalIgnoreCase));
        if (docs >= 0 && docs + 1 < parts.Length && parts[docs + 1].Length is 2 or 5) return parts[docs + 1].ToLowerInvariant();
        var match = Regex.Match(markdown, @"(?mi)^\s*(?:lang|locale):\s*['""]?([a-z]{2}(?:-[a-z]{2})?)");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : "en";
    }
    private static string ReadTitle(string markdown, string fallback)
    {
        var front = Regex.Match(markdown, @"(?mi)^\s*title:\s*['""]?(.+?)['""]?\s*$");
        if (front.Success) return ToPlainText(front.Groups[1].Value.Trim().Trim('"', '\''));
        var heading = HeadingRegex().Match(RemoveFrontMatter(markdown));
        return heading.Success ? ToPlainText(heading.Groups[2].Value) : fallback.Replace('-', ' ').Replace('_', ' ');
    }
    private static string RemoveFrontMatter(string markdown)
    {
        if (!markdown.StartsWith("---", StringComparison.Ordinal)) return markdown;
        var end = markdown.IndexOf("\n---", 3, StringComparison.Ordinal);
        return end < 0 ? markdown : markdown[(end + 4)..];
    }
    private static IEnumerable<string> Tokenize(string text) => TokenRegex().Matches(text).Select(match => match.Value.ToLowerInvariant()).Where(token => token.Length > 1 || char.IsDigit(token[0]));

    [GeneratedRegex(@"(?m)^(#{1,6})\s+(.+?)\s*$")]
    private static partial Regex HeadingRegex();
    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex ImageRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex LinkRegex();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlRegex();
    [GeneratedRegex(@"(?m)^```[^\r\n]*$|`([^`]*)`")]
    private static partial Regex FenceRegex();
    [GeneratedRegex(@"\*\*|__|~~|(?<!\w)[*_](?!\w)")]
    private static partial Regex DecorationRegex();
    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+.!|>-])")]
    private static partial Regex MarkdownEscapeRegex();
    [GeneratedRegex(@"(?m)^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$")]
    private static partial Regex TableSeparatorRegex();
    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex TokenRegex();
}
