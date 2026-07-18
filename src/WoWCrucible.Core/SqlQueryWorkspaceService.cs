using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record SqlQueryBatchResult(int Index, string Statement, SqlQueryResult Result, bool Truncated)
{
    public string Display => $"Result {Index:N0} · {Result.Rows.Count:N0} row(s) · {Result.Columns.Count:N0} column(s){(Truncated ? " · TRUNCATED" : string.Empty)}";
}

public sealed record SqlQueryBatch(IReadOnlyList<SqlQueryBatchResult> Results, TimeSpan Duration)
{
    public int TotalRows => Results.Sum(result => result.Result.Rows.Count);
}

public sealed record SqlQueryHistoryEntry(string Id, string Database, string Sql, DateTimeOffset ExecutedUtc, bool Bookmarked,
    string Label, int ResultSets, int Rows, double DurationMs)
{
    public string Display => $"{(Bookmarked ? "★ " : string.Empty)}{(string.IsNullOrWhiteSpace(Label) ? FirstLine(Sql) : Label)} · {Database} · {ExecutedUtc.LocalDateTime:g}";
    private static string FirstLine(string value)
    {
        var line = value.Replace('\r', ' ').Split('\n', 2)[0].Trim();
        return line.Length <= 90 ? line : line[..90] + "…";
    }
}

public static partial class SqlReadBatchParser
{
    public const int MaximumStatements = 32;
    public const int MaximumTextLength = 1024 * 1024;

    public static IReadOnlyList<string> Split(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return [];
        if (sql.Length > MaximumTextLength) throw new InvalidDataException($"SQL text exceeds the {MaximumTextLength / 1024:N0} KiB safety limit.");
        var statements = new List<string>(); var current = new StringBuilder(); var state = State.Normal;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index]; var next = index + 1 < sql.Length ? sql[index + 1] : '\0';
            current.Append(character);
            switch (state)
            {
                case State.Normal:
                    if (character == '\'' ) state = State.SingleQuote;
                    else if (character == '"') state = State.DoubleQuote;
                    else if (character == '`') state = State.Backtick;
                    else if (character == '#') state = State.LineComment;
                    else if (character == '-' && next == '-') { current.Append(next); index++; state = State.LineComment; }
                    else if (character == '/' && next == '*') { current.Append(next); index++; state = State.BlockComment; }
                    else if (character == ';') AddStatement(statements, current);
                    break;
                case State.SingleQuote:
                case State.DoubleQuote:
                    var quote = state == State.SingleQuote ? '\'' : '"';
                    if (character == '\\' && next != '\0') { current.Append(next); index++; }
                    else if (character == quote && next == quote) { current.Append(next); index++; }
                    else if (character == quote) state = State.Normal;
                    break;
                case State.Backtick:
                    if (character == '`' && next == '`') { current.Append(next); index++; }
                    else if (character == '`') state = State.Normal;
                    break;
                case State.LineComment:
                    if (character is '\r' or '\n') state = State.Normal;
                    break;
                case State.BlockComment:
                    if (character == '*' && next == '/') { current.Append(next); index++; state = State.Normal; }
                    break;
            }
        }
        if (state is State.SingleQuote or State.DoubleQuote or State.Backtick or State.BlockComment)
            throw new InvalidDataException("SQL text ends inside a quoted value, quoted identifier, or block comment.");
        AddStatement(statements, current);
        if (statements.Count > MaximumStatements) throw new InvalidDataException($"A read batch may contain at most {MaximumStatements:N0} statements.");
        return statements;
    }

    public static bool IsReadOnlyStatement(string? sql)
    {
        IReadOnlyList<string> statements;
        try { statements = Split(sql); } catch { return false; }
        return statements.Count == 1 && IsSingleReadOnly(statements[0]);
    }

    public static bool IsReadOnlyBatch(string? sql)
    {
        IReadOnlyList<string> statements;
        try { statements = Split(sql); } catch { return false; }
        return statements.Count > 0 && statements.All(IsSingleReadOnly);
    }

    private static bool IsSingleReadOnly(string statement)
    {
        var scrubbed = Scrub(statement).TrimStart();
        var token = new string(scrubbed.TakeWhile(char.IsLetter).ToArray()).ToUpperInvariant();
        if (token is not ("SELECT" or "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN")) return false;
        if (token == "SELECT" && FileOutputPattern().IsMatch(scrubbed)) return false;
        return true;
    }

    private static string Scrub(string text)
    {
        var result = new StringBuilder(text.Length); var state = State.Normal;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index]; var next = index + 1 < text.Length ? text[index + 1] : '\0';
            switch (state)
            {
                case State.Normal:
                    if (character == '\'') { result.Append(' '); state = State.SingleQuote; }
                    else if (character == '"') { result.Append(' '); state = State.DoubleQuote; }
                    else if (character == '`') { result.Append(' '); state = State.Backtick; }
                    else if (character == '#') { result.Append(' '); state = State.LineComment; }
                    else if (character == '-' && next == '-') { result.Append("  "); index++; state = State.LineComment; }
                    else if (character == '/' && next == '*') { result.Append("  "); index++; state = State.BlockComment; }
                    else result.Append(character);
                    break;
                case State.SingleQuote:
                case State.DoubleQuote:
                    result.Append(character is '\r' or '\n' ? character : ' '); var quote = state == State.SingleQuote ? '\'' : '"';
                    if (character == '\\' && next != '\0') { result.Append(' '); index++; }
                    else if (character == quote && next == quote) { result.Append(' '); index++; }
                    else if (character == quote) state = State.Normal;
                    break;
                case State.Backtick:
                    result.Append(' '); if (character == '`' && next == '`') { result.Append(' '); index++; } else if (character == '`') state = State.Normal;
                    break;
                case State.LineComment:
                    result.Append(character is '\r' or '\n' ? character : ' '); if (character is '\r' or '\n') state = State.Normal;
                    break;
                case State.BlockComment:
                    result.Append(character is '\r' or '\n' ? character : ' '); if (character == '*' && next == '/') { result.Append(' '); index++; state = State.Normal; }
                    break;
            }
        }
        return result.ToString();
    }

    private static void AddStatement(ICollection<string> statements, StringBuilder current)
    {
        var value = current.ToString().Trim(); current.Clear();
        if (value.EndsWith(';')) value = value[..^1].TrimEnd();
        if (value.Length > 0 && Scrub(value).Any(char.IsLetterOrDigit)) statements.Add(value);
    }

    [GeneratedRegex(@"\bINTO\s+(?:OUTFILE|DUMPFILE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileOutputPattern();

    private enum State { Normal, SingleQuote, DoubleQuote, Backtick, LineComment, BlockComment }
}

public sealed class SqlQueryHistoryStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly int _maximumUnbookmarked;

    public SqlQueryHistoryStore(string? path = null, int maximumUnbookmarked = 100)
    {
        _path = Path.GetFullPath(path ?? CruciblePaths.SqlQueryHistoryFile);
        _maximumUnbookmarked = Math.Clamp(maximumUnbookmarked, 10, 1000);
    }

    public IReadOnlyList<SqlQueryHistoryEntry> Load()
    {
        lock (Gate) return LoadUnlocked().ToArray();
    }

    public IReadOnlyList<SqlQueryHistoryEntry> Record(string database, string sql, SqlQueryBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch); database = Required(database, nameof(database)); sql = Required(sql, nameof(sql));
        lock (Gate)
        {
            var entries = LoadUnlocked(); var existing = entries.FirstOrDefault(entry => entry.Database.Equals(database, StringComparison.OrdinalIgnoreCase) && entry.Sql.Equals(sql, StringComparison.Ordinal));
            var entry = new SqlQueryHistoryEntry(existing?.Id ?? Guid.NewGuid().ToString("N"), database, sql, DateTimeOffset.UtcNow, existing?.Bookmarked == true,
                existing?.Label ?? string.Empty, batch.Results.Count, batch.TotalRows, batch.Duration.TotalMilliseconds);
            entries.RemoveAll(item => item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)); entries.Insert(0, entry); Trim(entries); SaveUnlocked(entries); return entries.ToArray();
        }
    }

    public IReadOnlyList<SqlQueryHistoryEntry> Bookmark(string database, string sql, string? label)
    {
        database = Required(database, nameof(database)); sql = Required(sql, nameof(sql));
        lock (Gate)
        {
            var entries = LoadUnlocked(); var existing = entries.FirstOrDefault(entry => entry.Database.Equals(database, StringComparison.OrdinalIgnoreCase) && entry.Sql.Equals(sql, StringComparison.Ordinal));
            var entry = existing is null
                ? new(Guid.NewGuid().ToString("N"), database, sql, DateTimeOffset.UtcNow, true, label?.Trim() ?? string.Empty, 0, 0, 0)
                : existing with { Bookmarked = true, Label = label?.Trim() ?? existing.Label };
            entries.RemoveAll(item => item.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase)); entries.Insert(0, entry); Trim(entries); SaveUnlocked(entries); return entries.ToArray();
        }
    }

    public IReadOnlyList<SqlQueryHistoryEntry> Remove(string id)
    {
        lock (Gate) { var entries = LoadUnlocked(); entries.RemoveAll(entry => entry.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); SaveUnlocked(entries); return entries.ToArray(); }
    }

    public IReadOnlyList<SqlQueryHistoryEntry> ClearUnbookmarked()
    {
        lock (Gate) { var entries = LoadUnlocked().Where(entry => entry.Bookmarked).ToList(); SaveUnlocked(entries); return entries.ToArray(); }
    }

    private List<SqlQueryHistoryEntry> LoadUnlocked()
    {
        if (!File.Exists(_path)) return [];
        try { return JsonSerializer.Deserialize<List<SqlQueryHistoryEntry>>(File.ReadAllText(_path)) ?? []; }
        catch (Exception exception)
        {
            var preserved = _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
            try { File.Move(_path, preserved, false); }
            catch (Exception preserveException) { throw new InvalidDataException($"SQL query history is corrupt and could not be preserved before recovery: {_path}", new AggregateException(exception, preserveException)); }
            return [];
        }
    }

    private void Trim(List<SqlQueryHistoryEntry> entries)
    {
        var keep = entries.Where(entry => entry.Bookmarked).Concat(entries.Where(entry => !entry.Bookmarked).Take(_maximumUnbookmarked)).DistinctBy(entry => entry.Id).OrderByDescending(entry => entry.ExecutedUtc).ToList();
        entries.Clear(); entries.AddRange(keep);
    }

    private void SaveUnlocked(IReadOnlyList<SqlQueryHistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions)); File.Move(temporary, _path, true); }
        catch { try { File.Delete(temporary); } catch { } throw; }
    }

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim();
}
