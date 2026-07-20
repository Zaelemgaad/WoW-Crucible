using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace WoWCrucible.Core;

public enum SqlDatabaseDependencyTargetKind { Table, View, Trigger, Procedure, Function, Event }

public sealed record SqlDatabaseObjectReference(
    SqlDatabaseObjectType SourceType,
    string SourceDatabase,
    string SourceName,
    SqlDatabaseDependencyTargetKind TargetKind,
    string TargetDatabase,
    string TargetName,
    string Evidence)
{
    public string SourceIdentity => $"{SourceDatabase}\u001f{SourceType}\u001f{SourceName}";
    public string TargetIdentity => $"{TargetDatabase}\u001f{TargetKind}\u001f{TargetName}";
    public string Display => $"{SourceType} {SourceDatabase}.{SourceName} → {TargetKind} {TargetDatabase}.{TargetName} · {Evidence}";
}

public sealed record SqlDatabaseObjectImpactReport(
    SqlDatabaseObjectType Type,
    string Database,
    string Name,
    bool Exists,
    string? BeforeDeclaration,
    string ProposedDeclaration,
    bool DeclarationChanged,
    IReadOnlyList<SqlDatabaseObjectReference> OutgoingBefore,
    IReadOnlyList<SqlDatabaseObjectReference> OutgoingProposed,
    IReadOnlyList<SqlDatabaseObjectReference> Incoming,
    IReadOnlyList<SqlDatabaseObjectReference> AddedOutgoing,
    IReadOnlyList<SqlDatabaseObjectReference> RemovedOutgoing,
    IReadOnlyList<string> Findings,
    string LiveGraphSha256);

/// <summary>
/// Builds an object graph from exact server metadata where MySQL exposes it and
/// from bounded definition parsing against known live identities everywhere else.
/// Identifier matches outside SQL relationship positions are never treated as edges.
/// </summary>
public sealed class SqlDatabaseObjectDependencyService
{
    private sealed record Target(SqlDatabaseDependencyTargetKind Kind, string Database, string Name);
    private sealed record Token(string Value, bool Identifier, char Symbol = '\0');

    public async Task<SqlDatabaseObjectImpactReport> AnalyzeAsync(DatabaseConnectionProfile profile, SqlDatabaseObjectType type, string name,
        string proposedCreateSql, CancellationToken cancellationToken = default)
    {
        name = name.Trim(); proposedCreateSql = SqlDatabaseObjectService.ValidateCreateDefinition(type, profile.Database, name, proposedCreateSql);
        var objectService = new SqlDatabaseObjectService(); var objects = await objectService.ListAsync(profile, cancellationToken);
        var definitions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var definitionFailures = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(objects, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 }, async (item, token) =>
        {
            try { definitions[item.Identity] = (await objectService.ShowCreateAsync(profile, item, token)).CreateSql; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { definitionFailures.Add($"Exact {item.Type} definition unavailable for {item.Database}.{item.Name}: {exception.Message}"); }
        });

        await using var connection = new MySqlConnection(DatabaseCapabilityService.BuildConnectionString(profile)); await connection.OpenAsync(cancellationToken);
        var targets = await LoadTargetsAsync(connection, profile.Database, objects, cancellationToken); var findings = definitionFailures.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        var live = new List<SqlDatabaseObjectReference>();
        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (!definitions.TryGetValue(item.Identity, out var definition)) continue;
            live.AddRange(ParseReferences(item.Type, item.Database, item.Name, definition, targets));
        }
        await AddMetadataReferencesAsync(connection, profile.Database, targets, live, findings, cancellationToken);
        live = Distinct(live);

        var rootIdentity = $"{profile.Database}\u001f{type}\u001f{name}"; var current = objects.FirstOrDefault(item => item.Identity.Equals(rootIdentity, StringComparison.OrdinalIgnoreCase));
        var proposed = Distinct(ParseReferences(type, profile.Database, name, proposedCreateSql, targets));
        var outgoingBefore = live.Where(reference => reference.SourceIdentity.Equals(rootIdentity, StringComparison.OrdinalIgnoreCase)).OrderBy(reference => reference.TargetIdentity, StringComparer.OrdinalIgnoreCase).ToArray();
        var incoming = live.Where(reference => TargetMatchesObject(reference, type, profile.Database, name)).OrderBy(reference => reference.SourceIdentity, StringComparer.OrdinalIgnoreCase).ToArray();
        var beforeTargets = outgoingBefore.Select(reference => reference.TargetIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase); var proposedTargets = proposed.Select(reference => reference.TargetIdentity).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = proposed.Where(reference => !beforeTargets.Contains(reference.TargetIdentity)).ToArray(); var removed = outgoingBefore.Where(reference => !proposedTargets.Contains(reference.TargetIdentity)).ToArray();
        var beforeSql = current is null ? null : definitions.GetValueOrDefault(current.Identity); var beforeDeclaration = beforeSql is null ? null : Declaration(type, beforeSql); var proposedDeclaration = Declaration(type, proposedCreateSql); var declarationChanged = beforeDeclaration is not null && !beforeDeclaration.Equals(proposedDeclaration, StringComparison.OrdinalIgnoreCase);

        if (incoming.Length > 0) findings.Add($"{incoming.Length:N0} live database object reference(s) depend on this identity and may be affected by replacement.");
        if (declarationChanged) findings.Add($"The reviewed {type} declaration/signature changes{(incoming.Length == 0 ? "." : " while live dependents exist; review every incoming edge.")}");
        if (removed.Length > 0) findings.Add($"The draft removes {removed.Length:N0} known outgoing dependency edge(s).");
        if (added.Length > 0) findings.Add($"The draft adds {added.Length:N0} known outgoing dependency edge(s).");
        if (current is null) findings.Add($"{type} {profile.Database}.{name} does not currently exist; incoming references normally remain empty until another object names that future identity.");
        if (findings.Count == 0) findings.Add("No declaration or known dependency-edge change was detected. The exact object preimage remains independently hash-bound.");
        findings.Add("Static dependency coverage excludes dynamically constructed SQL and unresolved names that are not present as live schema identities; review dynamic EXECUTE/PREPARE logic manually.");
        var graphHash = GraphHash(type, profile.Database, name, current is not null, outgoingBefore, incoming);
        return new(type, profile.Database, name, current is not null, beforeDeclaration, proposedDeclaration, declarationChanged, outgoingBefore, proposed, incoming, added, removed, findings, graphHash);
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<Target>>> LoadTargetsAsync(MySqlConnection connection, string database,
        IReadOnlyList<SqlDatabaseObjectInfo> objects, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, List<Target>>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new MySqlCommand("SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA=@database", connection))
        {
            command.Parameters.AddWithValue("@database", database); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) Add(new(reader.GetString(1).Equals("VIEW", StringComparison.OrdinalIgnoreCase) ? SqlDatabaseDependencyTargetKind.View : SqlDatabaseDependencyTargetKind.Table, database, reader.GetString(0)));
        }
        foreach (var item in objects) Add(new(ToTargetKind(item.Type), item.Database, item.Name));
        return targets.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Target>)pair.Value.Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);

        void Add(Target target) { if (!targets.TryGetValue(target.Name, out var list)) targets[target.Name] = list = []; list.Add(target); }
    }

    private static async Task AddMetadataReferencesAsync(MySqlConnection connection, string database, IReadOnlyDictionary<string, IReadOnlyList<Target>> targets,
        ICollection<SqlDatabaseObjectReference> output, ICollection<string> findings, CancellationToken cancellationToken)
    {
        await TryReadAsync("SELECT VIEW_NAME, TABLE_SCHEMA, TABLE_NAME FROM information_schema.VIEW_TABLE_USAGE WHERE VIEW_SCHEMA=@database", reader =>
        {
            var target = ResolveKnown(targets, reader.GetString(2), reader.GetString(1), [SqlDatabaseDependencyTargetKind.Table, SqlDatabaseDependencyTargetKind.View]);
            if (target is not null) output.Add(Reference(SqlDatabaseObjectType.View, database, reader.GetString(0), target, "server VIEW_TABLE_USAGE"));
        }, "VIEW_TABLE_USAGE");
        await TryReadAsync("SELECT TABLE_NAME, SPECIFIC_SCHEMA, SPECIFIC_NAME FROM information_schema.VIEW_ROUTINE_USAGE WHERE TABLE_SCHEMA=@database", reader =>
        {
            var target = ResolveKnown(targets, reader.GetString(2), reader.GetString(1), [SqlDatabaseDependencyTargetKind.Function, SqlDatabaseDependencyTargetKind.Procedure]);
            if (target is not null) output.Add(Reference(SqlDatabaseObjectType.View, database, reader.GetString(0), target, "server VIEW_ROUTINE_USAGE"));
        }, "VIEW_ROUTINE_USAGE");
        await TryReadAsync("SELECT TRIGGER_NAME, EVENT_OBJECT_SCHEMA, EVENT_OBJECT_TABLE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@database", reader =>
        {
            var target = ResolveKnown(targets, reader.GetString(2), reader.GetString(1), [SqlDatabaseDependencyTargetKind.Table, SqlDatabaseDependencyTargetKind.View]);
            if (target is not null) output.Add(Reference(SqlDatabaseObjectType.Trigger, database, reader.GetString(0), target, "server trigger target"));
        }, "TRIGGERS");

        async Task TryReadAsync(string sql, Action<MySqlDataReader> add, string source)
        {
            try
            {
                await using var command = new MySqlCommand(sql, connection); command.Parameters.AddWithValue("@database", database); await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) add(reader);
            }
            catch (MySqlException exception) { findings.Add($"Server metadata {source} is unavailable ({exception.Number}: {exception.Message}); known-identity definition parsing remains active."); }
        }
    }

    private static IReadOnlyList<SqlDatabaseObjectReference> ParseReferences(SqlDatabaseObjectType sourceType, string database, string sourceName, string sql,
        IReadOnlyDictionary<string, IReadOnlyList<Target>> targets)
    {
        var tokens = Tokenize(sql); var result = new List<SqlDatabaseObjectReference>();
        if (sourceType == SqlDatabaseObjectType.Trigger && TriggerTarget(tokens, database, targets) is { } triggerTarget)
            result.Add(Reference(sourceType, database, sourceName, triggerTarget, "definition trigger ON"));
        for (var index = 0; index < tokens.Count; index++)
        {
            var keyword = tokens[index].Value.ToUpperInvariant();
            SqlDatabaseDependencyTargetKind[]? expected = keyword switch
            {
                "FROM" or "JOIN" or "UPDATE" or "INTO" or "REFERENCES" => [SqlDatabaseDependencyTargetKind.Table, SqlDatabaseDependencyTargetKind.View],
                "CALL" => [SqlDatabaseDependencyTargetKind.Procedure],
                "TABLE" when index > 0 && tokens[index - 1].Value.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase) => [SqlDatabaseDependencyTargetKind.Table],
                _ => null
            };
            if (expected is not null && ResolveTokenTarget(tokens, index + 1, database, targets, expected) is { } target)
                result.Add(Reference(sourceType, database, sourceName, target, $"definition {keyword}"));
            if (tokens[index].Identifier && index + 1 < tokens.Count && tokens[index + 1].Symbol == '(' && ResolveTokenTarget(tokens, index, database, targets, [SqlDatabaseDependencyTargetKind.Function]) is { } function &&
                !(function.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase) && function.Database.Equals(database, StringComparison.OrdinalIgnoreCase)))
                result.Add(Reference(sourceType, database, sourceName, function, "definition function call"));
        }
        return Distinct(result);
    }

    private static Target? TriggerTarget(IReadOnlyList<Token> tokens, string database, IReadOnlyDictionary<string, IReadOnlyList<Target>> targets)
    {
        var trigger = -1; for (var index = 0; index < tokens.Count; index++) if (tokens[index].Value.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)) { trigger = index; break; }
        if (trigger < 0) return null;
        for (var index = trigger + 1; index < tokens.Count; index++)
        {
            if (tokens[index].Value.Equals("FOR", StringComparison.OrdinalIgnoreCase)) return null;
            if (tokens[index].Value.Equals("ON", StringComparison.OrdinalIgnoreCase))
                return ResolveTokenTarget(tokens, index + 1, database, targets, [SqlDatabaseDependencyTargetKind.Table, SqlDatabaseDependencyTargetKind.View]);
        }
        return null;
    }

    private static Target? ResolveTokenTarget(IReadOnlyList<Token> tokens, int index, string defaultDatabase, IReadOnlyDictionary<string, IReadOnlyList<Target>> targets,
        IReadOnlyList<SqlDatabaseDependencyTargetKind> expected)
    {
        if (index >= tokens.Count || !tokens[index].Identifier) return null; var database = defaultDatabase; var name = tokens[index].Value;
        if (index + 2 < tokens.Count && tokens[index + 1].Symbol == '.' && tokens[index + 2].Identifier) { database = name; name = tokens[index + 2].Value; }
        return ResolveKnown(targets, name, database, expected);
    }

    private static Target? ResolveKnown(IReadOnlyDictionary<string, IReadOnlyList<Target>> targets, string name, string database, IReadOnlyList<SqlDatabaseDependencyTargetKind> expected)
    {
        if (!targets.TryGetValue(name, out var candidates)) return null;
        return candidates.FirstOrDefault(candidate => candidate.Database.Equals(database, StringComparison.OrdinalIgnoreCase) && expected.Contains(candidate.Kind));
    }

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        for (var index = 0; index < sql.Length;)
        {
            var character = sql[index]; if (char.IsWhiteSpace(character)) { index++; continue; }
            if (character == '#' || character == '-' && index + 2 < sql.Length && sql[index + 1] == '-' && char.IsWhiteSpace(sql[index + 2])) { var end = sql.IndexOf('\n', index); index = end < 0 ? sql.Length : end + 1; continue; }
            if (character == '/' && index + 1 < sql.Length && sql[index + 1] == '*') { var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal); index = end < 0 ? sql.Length : end + 2; continue; }
            if (character is '\'' or '"') { var quote = character; index++; while (index < sql.Length) { if (sql[index] == '\\' && index + 1 < sql.Length) { index += 2; continue; } if (sql[index] != quote) { index++; continue; } if (index + 1 < sql.Length && sql[index + 1] == quote) { index += 2; continue; } index++; break; } continue; }
            if (character == '`') { var builder = new StringBuilder(); index++; while (index < sql.Length) { if (sql[index] != '`') { builder.Append(sql[index++]); continue; } if (index + 1 < sql.Length && sql[index + 1] == '`') { builder.Append('`'); index += 2; continue; } index++; break; } tokens.Add(new(builder.ToString(), true)); continue; }
            if (char.IsLetter(character) || character is '_' or '$') { var start = index++; while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] is '_' or '$')) index++; tokens.Add(new(sql[start..index], true)); continue; }
            if (character is '(' or ')' or '.' or ',') tokens.Add(new(character.ToString(), false, character)); index++;
        }
        return tokens;
    }

    private static string Declaration(SqlDatabaseObjectType type, string sql)
    {
        var normalized = Regex.Replace(sql.Trim().TrimEnd(';'), @"\s+", " ");
        return type switch
        {
            SqlDatabaseObjectType.Trigger => MatchOrWhole(normalized, @"^CREATE\s+.*?\bTRIGGER\s+.*?\s+(?:BEFORE|AFTER)\s+(?:INSERT|UPDATE|DELETE)\s+ON\s+.*?\s+FOR\s+EACH\s+ROW\b"),
            SqlDatabaseObjectType.Procedure => RoutineDeclaration(normalized, "PROCEDURE", false),
            SqlDatabaseObjectType.Function => RoutineDeclaration(normalized, "FUNCTION", true),
            SqlDatabaseObjectType.Event => MatchOrWhole(normalized, @"^CREATE\s+.*?\bEVENT\s+.*?\s+ON\s+SCHEDULE\s+.*?\s+ON\s+COMPLETION\s+(?:NOT\s+)?PRESERVE\s+(?:ENABLE|DISABLE|DISABLE\s+ON\s+SLAVE)\s+DO\b"),
            SqlDatabaseObjectType.View => MatchOrWhole(normalized, @"^CREATE\s+.*?\bVIEW\s+[^\s]+(?:\s*\([^)]*\))?\s+AS\b"),
            _ => normalized
        };
    }

    private static string RoutineDeclaration(string sql, string keyword, bool includeReturns)
    {
        var match = Regex.Match(sql, $@"^CREATE\s+.*?\b{keyword}\s+[^\s(]+\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); if (!match.Success) return sql;
        var open = sql.IndexOf('(', match.Index + match.Length - 1); var depth = 0; var quote = '\0'; var close = -1;
        for (var index = open; index < sql.Length; index++) { var character = sql[index]; if (quote != '\0') { if (character == quote) quote = '\0'; continue; } if (character is '\'' or '"' or '`') { quote = character; continue; } if (character == '(') depth++; else if (character == ')' && --depth == 0) { close = index; break; } }
        if (close < 0) return sql; var declaration = sql[..(close + 1)]; if (!includeReturns) return declaration;
        var returns = Regex.Match(sql[(close + 1)..], @"^\s*RETURNS\s+(?<type>.+?)(?=\s+(?:DETERMINISTIC|NOT\s+DETERMINISTIC|NO\s+SQL|CONTAINS\s+SQL|READS\s+SQL\s+DATA|MODIFIES\s+SQL\s+DATA|SQL\s+SECURITY|COMMENT|LANGUAGE|RETURN|BEGIN)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return returns.Success ? declaration + " RETURNS " + returns.Groups["type"].Value.Trim() : declaration;
    }

    private static string MatchOrWhole(string sql, string pattern) { var match = Regex.Match(sql, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); return match.Success ? match.Value : sql; }
    private static SqlDatabaseObjectReference Reference(SqlDatabaseObjectType sourceType, string sourceDatabase, string sourceName, Target target, string evidence) => new(sourceType, sourceDatabase, sourceName, target.Kind, target.Database, target.Name, evidence);
    private static bool TargetMatchesObject(SqlDatabaseObjectReference reference, SqlDatabaseObjectType type, string database, string name) => reference.TargetKind == ToTargetKind(type) && reference.TargetDatabase.Equals(database, StringComparison.OrdinalIgnoreCase) && reference.TargetName.Equals(name, StringComparison.OrdinalIgnoreCase);
    private static SqlDatabaseDependencyTargetKind ToTargetKind(SqlDatabaseObjectType type) => type switch { SqlDatabaseObjectType.View => SqlDatabaseDependencyTargetKind.View, SqlDatabaseObjectType.Trigger => SqlDatabaseDependencyTargetKind.Trigger, SqlDatabaseObjectType.Procedure => SqlDatabaseDependencyTargetKind.Procedure, SqlDatabaseObjectType.Function => SqlDatabaseDependencyTargetKind.Function, SqlDatabaseObjectType.Event => SqlDatabaseDependencyTargetKind.Event, _ => throw new ArgumentOutOfRangeException(nameof(type)) };
    private static List<SqlDatabaseObjectReference> Distinct(IEnumerable<SqlDatabaseObjectReference> references) => references.GroupBy(reference => $"{reference.SourceIdentity}\u001f{reference.TargetIdentity}", StringComparer.OrdinalIgnoreCase).Select(group => group.OrderBy(reference => reference.Evidence.StartsWith("server", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First()).OrderBy(reference => reference.SourceIdentity, StringComparer.OrdinalIgnoreCase).ThenBy(reference => reference.TargetIdentity, StringComparer.OrdinalIgnoreCase).ToList();
    internal static string GraphHash(SqlDatabaseObjectType type, string database, string name, bool exists, IReadOnlyList<SqlDatabaseObjectReference> outgoing, IReadOnlyList<SqlDatabaseObjectReference> incoming)
    {
        var lines = new List<string> { $"ROOT\t{database}\t{type}\t{name}\t{exists}" };
        lines.AddRange(outgoing.Select(reference => "OUT\t" + reference.TargetIdentity).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        lines.AddRange(incoming.Select(reference => "IN\t" + reference.SourceIdentity).OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))).ToLowerInvariant();
    }
}
