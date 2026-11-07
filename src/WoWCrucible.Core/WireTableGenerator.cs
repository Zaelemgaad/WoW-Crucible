using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WoWCrucible.Core;

/// <summary>
/// Generates matching C++ and Rust readers for tab-separated data tables from one definition. A reader
/// checks a table's header and each row's cells. Whether rows agree with each other or with other tables,
/// and every database schema, stay ordinary reviewed code.
/// </summary>
public static class WireTableGenerator
{
    public static bool IsTableDefinition(string definitionText)
    {
        using var document = JsonDocument.Parse(definitionText, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return document.RootElement.TryGetProperty("tables", out _);
    }

    public static WireTableSet Parse(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        Only(root, "definition", "schema_format", "generator_version", "name", "catalog_version", "cpp", "rust", "fixtures", "merged_id", "tables");
        if (root.GetProperty("schema_format").GetInt32() != WireProtocolGenerator.SchemaFormat)
            throw new InvalidDataException($"This generator reads schema format {WireProtocolGenerator.SchemaFormat}.");
        var pinned = root.GetProperty("generator_version").GetInt32();
        if (pinned != WireProtocolGenerator.GeneratorVersion)
            throw new InvalidDataException($"The definition pins generator {pinned}; this is generator {WireProtocolGenerator.GeneratorVersion}. Change the pin on purpose, and review the regenerated output.");
        var cpp = root.GetProperty("cpp");
        Only(cpp, "cpp", "namespace", "header", "dump_header");
        var rust = root.GetProperty("rust");
        Only(rust, "rust", "file", "pin_dir");
        var merged = root.GetProperty("merged_id");
        Only(merged, "merged_id", "shift", "sources");
        var tables = root.GetProperty("tables").EnumerateArray().Select(ParseTable).ToArray();
        if (tables.Select(table => table.Name).Distinct(StringComparer.Ordinal).Count() != tables.Length || tables.Select(table => table.File).Distinct(StringComparer.Ordinal).Count() != tables.Length)
            throw new InvalidDataException("A table name or file is repeated.");
        return new WireTableSet(root.GetProperty("name").GetString()!, root.GetProperty("catalog_version").GetInt32(),
            cpp.GetProperty("namespace").GetString()!, cpp.GetProperty("header").GetString()!, cpp.GetProperty("dump_header").GetString()!,
            rust.GetProperty("file").GetString()!, rust.GetProperty("pin_dir").GetString()!, root.GetProperty("fixtures").GetString()!,
            merged.GetProperty("shift").GetInt32(), merged.GetProperty("sources").EnumerateArray().Select(item => item.GetInt32()).ToArray(), tables);
    }

    private static WireTable ParseTable(JsonElement element)
    {
        Only(element, "table", "name", "file", "doc", "rows", "columns", "row_checks");
        var name = element.GetProperty("name").GetString()!;
        var rows = element.TryGetProperty("rows", out var rowsElement) ? rowsElement.GetString()! : "any";
        if (rows is not ("any" or "some" or "one")) throw new InvalidDataException($"{name}: rows is any, some or one.");
        var columns = element.GetProperty("columns").EnumerateArray().Select(column =>
        {
            Only(column, "column", "name", "type", "check", "doc");
            var type = column.GetProperty("type").GetString()!;
            if (type is not ("u32" or "text")) throw new InvalidDataException($"{name}: column type {type} is not known.");
            var check = column.TryGetProperty("check", out var checkElement) ? checkElement.GetString() : null;
            if (check is not (null or "merged_id" or "nonzero") || (check is not null && type != "u32")) throw new InvalidDataException($"{name}: check {check} does not apply.");
            return new WireColumn(column.GetProperty("name").GetString()!, type, check, column.TryGetProperty("doc", out var doc) ? doc.GetString()! : "");
        }).ToArray();
        if (columns.Length == 0 || columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != columns.Length)
            throw new InvalidDataException($"{name}: columns must be present and distinct.");
        var checks = element.TryGetProperty("row_checks", out var checksElement) ? checksElement.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
        foreach (var check in checks)
        {
            var parts = check.Split(':', '=');
            if (parts.Length != 3 || parts[0] != "namespace" || columns.All(column => column.Name != parts[1] || column.Type != "u32") || columns.All(column => column.Name != parts[2] || column.Type != "u32"))
                throw new InvalidDataException($"{name}: row check {check} is not namespace:<id column>=<source column>.");
        }
        return new WireTable(name, element.GetProperty("file").GetString()!, element.TryGetProperty("doc", out var tableDoc) ? tableDoc.GetString()! : "", rows, columns, checks);
    }

    private static void Only(JsonElement element, string what, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException($"Unknown {what} key: {property.Name}.");
    }

    public static WireGeneratedSet Generate(WireTableSet set, string definitionText, string definitionFileName)
    {
        var hash = WireProtocolGenerator.TextHash(definitionText);
        return new WireGeneratedSet(
            WireProtocolGenerator.Seal(Cpp(set), $"catalog: {set.Version}", hash, definitionFileName),
            WireProtocolGenerator.Seal(CppDump(set), $"catalog: {set.Version}", hash, definitionFileName),
            WireProtocolGenerator.Seal(Rust(set), $"catalog: {set.Version}", hash, definitionFileName));
    }

    private static string Pascal(string name) => WireNames.Pascal(name);

    private static string Guard(string file) =>
        string.Concat(file.Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')) + "_INCLUDED";

    private const string MergedMessage = "expected source-qualified ID from Epsilon or retail";

    private static string Cpp(WireTableSet set)
    {
        var o = new StringBuilder();
        var guard = Guard(set.CppHeader);
        o.Append($"#ifndef {guard}\n#define {guard}\n\n");
        o.Append("#include <algorithm>\n#include <charconv>\n#include <cstddef>\n#include <cstdint>\n#include <initializer_list>\n#include <istream>\n#include <string>\n#include <string_view>\n#include <vector>\n\n");
        o.Append("// One table's header and each row's cells. Duplicates and references between tables stay ordinary code.\n");
        o.Append($"namespace {set.CppNamespace}\n{{\n");
        o.Append("namespace Detail\n{\n");
        o.Append("// Tab separated, with a header. Fields may be quoted, with doubled quotes and line breaks inside.\n");
        o.Append("// CRLF and a leading UTF-8 byte-order mark are accepted. No field may be blank.\n");
        o.Append("class Table\n{\npublic:\n    Table(std::istream& stream, char const* name, std::string& error)\n        : _stream(stream), _name(name), _error(error) { }\n\n");
        o.Append("    bool Header(std::initializer_list<std::string_view> expected)\n    {\n        if (!Next(expected.size()))\n            return Fail(\"missing header\");\n");
        o.Append("        return std::equal(expected.begin(), expected.end(), _fields.begin()) || Fail(\"unexpected header\");\n    }\n\n");
        o.Append("    bool Next(std::size_t count)\n    {\n        if (!std::getline(_stream, _line))\n        {\n            if (!_stream.eof() || _stream.bad())\n                Fail(\"read failed\");\n            return false;\n        }\n");
        o.Append("        ++_number;\n        if (_number == 1 && _line.starts_with(\"\\xEF\\xBB\\xBF\"))\n            _line.erase(0, 3);\n        _fields.clear();\n        _fields.emplace_back();\n        bool quoted = false;\n        bool closed = false;\n");
        o.Append("        while (true)\n        {\n            if (!_line.empty() && _line.back() == '\\r')\n                _line.pop_back();\n            for (std::size_t i = 0; i < _line.size(); ++i)\n            {\n                char const c = _line[i];\n");
        o.Append("                if (quoted)\n                {\n                    if (c != '\"')\n                        _fields.back() += c;\n                    else if (i + 1 < _line.size() && _line[i + 1] == '\"')\n                    {\n                        _fields.back() += '\"';\n                        ++i;\n                    }\n                    else\n                    {\n                        quoted = false;\n                        closed = true;\n                    }\n                }\n");
        o.Append("                else if (c == '\\t')\n                {\n                    _fields.emplace_back();\n                    closed = false;\n                }\n                else if (closed)\n                    return Fail(\"unexpected character after quoted field\");\n");
        o.Append("                else if (c == '\"' && _fields.back().empty())\n                    quoted = true;\n                else\n                    _fields.back() += c;\n            }\n");
        o.Append("            if (!quoted)\n                break;\n            if (!std::getline(_stream, _line))\n                return Fail(\"unterminated quoted field or read failed\");\n            ++_number;\n            _fields.back() += '\\n';\n        }\n");
        o.Append("        if (_fields.size() != count || std::any_of(_fields.begin(), _fields.end(), [](auto const& field)\n            { return field.find_first_not_of(\" \\t\\r\\n\") == std::string_view::npos; }))\n            return Fail(\"incomplete or malformed row\");\n        return true;\n    }\n\n");
        o.Append("    bool Number(std::size_t index, std::uint32_t& value)\n    {\n        auto const& field = _fields[index];\n        auto const result = std::from_chars(field.data(), field.data() + field.size(), value);\n");
        o.Append("        if (result.ec != std::errc{} || result.ptr != field.data() + field.size())\n            return Fail(\"expected decimal uint32\");\n        return true;\n    }\n\n");
        o.Append("    std::string const& Text(std::size_t index) const { return _fields[index]; }\n\n");
        o.Append("    bool Fail(std::string_view reason)\n    {\n        if (_error.empty())\n            _error = _name + \":\" + std::to_string(_number) + \": \" + std::string(reason);\n        return false;\n    }\n\n");
        o.Append("private:\n    std::istream& _stream;\n    std::string _name;\n    std::string& _error;\n    std::string _line;\n    std::size_t _number = 0;\n    std::vector<std::string> _fields;\n};\n\n");
        var sources = string.Join(" || ", set.MergedSources.Select(source => $"source == {source}"));
        o.Append($"constexpr bool IsMergedId(std::uint32_t id)\n{{\n    auto const source = id >> {set.MergedShift};\n    return ({sources}) && (id & 0x{(1UL << set.MergedShift) - 1:X8}) != 0;\n}}\n}} // namespace Detail\n\n");

        foreach (var table in set.Tables)
        {
            var pascal = Pascal(table.Name);
            WireNames.Comment(o, table.Doc, "");
            o.Append($"inline constexpr char {pascal}File[] = \"{table.File}\";\n\nstruct {pascal}Row\n{{\n");
            foreach (var column in table.Columns)
            {
                WireNames.Comment(o, column.Doc, "    ");
                o.Append(column.Type == "u32" ? $"    std::uint32_t {Pascal(column.Name)} = 0;\n" : $"    std::string {Pascal(column.Name)};\n");
            }
            o.Append($"    bool operator==({pascal}Row const&) const = default;\n}};\n\n");
            o.Append($"// False leaves error set and is all or nothing for the caller to treat as a failed load.\ninline bool Read{pascal}(std::istream& stream, std::vector<{pascal}Row>& rows, std::string& error)\n{{\n");
            o.Append($"    Detail::Table table(stream, {pascal}File, error);\n    rows.clear();\n");
            o.Append($"    if (!table.Header({{{string.Join(", ", table.Columns.Select(column => $"\"{column.Name}\""))}}}))\n        return false;\n");
            o.Append($"    while (table.Next({table.Columns.Count}))\n    {{\n");
            if (table.Rows == "one") o.Append($"        if (!rows.empty())\n            return table.Fail(\"expected exactly one {table.Name} row\");\n");
            o.Append($"        {pascal}Row row;\n");
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                var field = $"row.{Pascal(column.Name)}";
                if (column.Type == "text") { o.Append($"        {field} = table.Text({index});\n"); continue; }
                o.Append($"        if (!table.Number({index}, {field}))\n            return false;\n");
                if (column.Check == "merged_id") o.Append($"        if (!Detail::IsMergedId({field}))\n            return table.Fail(\"{MergedMessage}\");\n");
                if (column.Check == "nonzero") o.Append($"        if ({field} == 0)\n            return table.Fail(\"{column.Name} must not be zero\");\n");
            }
            foreach (var check in table.RowChecks)
            {
                var parts = check.Split(':', '=');
                o.Append($"        if (row.{Pascal(parts[2])} != (row.{Pascal(parts[1])} >> {set.MergedShift}))\n            return table.Fail(\"{parts[2]} does not match {parts[1]}'s namespace\");\n");
            }
            o.Append("        rows.push_back(std::move(row));\n    }\n    if (!error.empty())\n        return false;\n");
            if (table.Rows == "one") o.Append($"    if (rows.empty())\n        return table.Fail(\"expected exactly one {table.Name} row\");\n");
            if (table.Rows == "some") o.Append("    if (rows.empty())\n        return table.Fail(\"no rows\");\n");
            o.Append("    return true;\n}\n\n");
        }
        o.Append($"}} // namespace {set.CppNamespace}\n\n#endif\n");
        return o.ToString();
    }

    private static string CppDump(WireTableSet set)
    {
        var o = new StringBuilder();
        var guard = Guard(set.CppDumpHeader);
        o.Append($"#ifndef {guard}\n#define {guard}\n\n#include \"{set.CppHeader}\"\n\n#include <sstream>\n\n");
        o.Append("// Test support: canonical text of a table's rows, compared with the fixtures and across languages.\n");
        o.Append($"namespace {set.CppNamespace}\n{{\n");
        o.Append("// The rows' text, or \"error: \" and the reason. An unknown table reads as empty text.\ninline std::string DumpByName(std::string_view table, std::string const& text)\n{\n    std::istringstream stream(text);\n    std::string error;\n    std::string s;\n");
        foreach (var table in set.Tables)
        {
            var pascal = Pascal(table.Name);
            o.Append($"    if (table == \"{table.Name}\")\n    {{\n        std::vector<{pascal}Row> rows;\n        if (!Read{pascal}(stream, rows, error))\n            return \"error: \" + error;\n        s += \"[\";\n        for (std::size_t i = 0; i < rows.size(); ++i)\n        {{\n            s += i ? \",{{\" : \"{{\";\n");
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                var value = column.Type == "u32" ? $"std::to_string(rows[i].{Pascal(column.Name)})" : $"rows[i].{Pascal(column.Name)}";
                o.Append($"            s += \"{(index == 0 ? "" : ",")}{column.Name}=\" + {value};\n");
            }
            o.Append("            s += \"}\";\n        }\n        s += \"]\";\n    }\n");
        }
        o.Append($"    return s;\n}}\n}} // namespace {set.CppNamespace}\n\n#endif\n");
        return o.ToString();
    }

    private static string Rust(WireTableSet set)
    {
        var o = new StringBuilder();
        o.Append("//! One table's header and each row's cells, generated from its definition. Duplicates and references\n//! between tables stay ordinary code.\n\n");
        o.Append("#![allow(clippy::all)]\n\n");
        o.Append(CultureInfo.InvariantCulture, $"pub const CATALOG_VERSION: u32 = {set.Version};\npub const GENERATOR_VERSION: u32 = {WireProtocolGenerator.GeneratorVersion};\n\n");
        o.Append("/// Tab separated, with a header. Fields may be quoted, with doubled quotes and line breaks inside. CRLF\n/// and a leading UTF-8 byte-order mark are accepted. No field may be blank.\n");
        o.Append("struct Table<'a> {\n    lines: Option<std::str::Split<'a, char>>,\n    file: &'static str,\n    number: usize,\n    fields: Vec<Vec<u8>>,\n    error: Option<String>,\n}\n\n");
        o.Append("impl<'a> Table<'a> {\n    fn new(text: &'a str, file: &'static str) -> Self {\n        let lines = (!text.is_empty()).then(|| text.strip_suffix('\\n').unwrap_or(text).split('\\n'));\n        Self { lines, file, number: 0, fields: Vec::new(), error: None }\n    }\n\n");
        o.Append("    fn line(&mut self) -> Option<&'a str> {\n        self.lines.as_mut()?.next()\n    }\n\n");
        o.Append("    fn fail(&mut self, reason: &str) -> bool {\n        if self.error.is_none() {\n            self.error = Some(format!(\"{}:{}: {reason}\", self.file, self.number));\n        }\n        false\n    }\n\n");
        o.Append("    fn header(&mut self, expected: &[&str]) -> bool {\n        if !self.next(expected.len()) {\n            return self.fail(\"missing header\");\n        }\n        expected.iter().zip(&self.fields).all(|(a, b)| a.as_bytes() == b.as_slice()) || self.fail(\"unexpected header\")\n    }\n\n");
        o.Append("    fn next(&mut self, count: usize) -> bool {\n        let Some(mut line) = self.line() else {\n            return false;\n        };\n        self.number += 1;\n        if self.number == 1 {\n            line = line.strip_prefix('\\u{FEFF}').unwrap_or(line);\n        }\n        self.fields.clear();\n        self.fields.push(Vec::new());\n        let (mut quoted, mut closed) = (false, false);\n");
        o.Append("        loop {\n            let bytes = line.strip_suffix('\\r').unwrap_or(line).as_bytes();\n            let mut i = 0;\n            while i < bytes.len() {\n                let c = bytes[i];\n                let field = self.fields.last_mut().expect(\"a field\");\n");
        o.Append("                if quoted {\n                    if c != b'\"' {\n                        field.push(c);\n                    } else if bytes.get(i + 1) == Some(&b'\"') {\n                        field.push(b'\"');\n                        i += 1;\n                    } else {\n                        quoted = false;\n                        closed = true;\n                    }\n                } else if c == b'\\t' {\n                    self.fields.push(Vec::new());\n                    closed = false;\n                } else if closed {\n                    return self.fail(\"unexpected character after quoted field\");\n                } else if c == b'\"' && field.is_empty() {\n                    quoted = true;\n                } else {\n                    field.push(c);\n                }\n                i += 1;\n            }\n");
        o.Append("            if !quoted {\n                break;\n            }\n            let Some(more) = self.line() else {\n                return self.fail(\"unterminated quoted field or read failed\");\n            };\n            line = more;\n            self.number += 1;\n            self.fields.last_mut().expect(\"a field\").push(b'\\n');\n        }\n");
        o.Append("        if self.fields.len() != count || self.fields.iter().any(|f| f.iter().all(|c| matches!(c, b' ' | b'\\t' | b'\\r' | b'\\n'))) {\n            return self.fail(\"incomplete or malformed row\");\n        }\n        true\n    }\n\n");
        o.Append("    fn number_cell(&mut self, index: usize) -> Option<u32> {\n        let field = &self.fields[index];\n        let value = (field.iter().all(u8::is_ascii_digit))\n            .then(|| std::str::from_utf8(field).ok()?.parse().ok())\n            .flatten();\n        if value.is_none() {\n            self.fail(\"expected decimal uint32\");\n        }\n        value\n    }\n\n");
        o.Append("    fn text_cell(&self, index: usize) -> String {\n        String::from_utf8_lossy(&self.fields[index]).into_owned()\n    }\n\n    fn finish<T>(&mut self, rows: Vec<T>) -> Result<Vec<T>, String> {\n        match self.error.take() {\n            Some(error) => Err(error),\n            None => Ok(rows),\n        }\n    }\n}\n\n");
        var sources = string.Join(" | ", set.MergedSources);
        o.Append($"const fn is_merged_id(id: u32) -> bool {{\n    matches!(id >> {set.MergedShift}, {sources}) && id & 0x{(1UL << set.MergedShift) - 1:X8} != 0\n}}\n\n");

        foreach (var table in set.Tables)
        {
            var pascal = Pascal(table.Name);
            var upper = table.Name.ToUpperInvariant();
            if (table.Doc.Length > 0) o.Append($"/// {table.Doc}\n");
            o.Append($"pub const {upper}_FILE: &str = \"{table.File}\";\n\n#[derive(Debug, Clone, PartialEq, Eq, Default)]\npub struct {pascal}Row {{\n");
            foreach (var column in table.Columns)
            {
                if (column.Doc.Length > 0) o.Append($"    /// {column.Doc}\n");
                o.Append($"    pub {column.Name}: {(column.Type == "u32" ? "u32" : "String")},\n");
            }
            o.Append("}\n\n");
            o.Append($"/// An error fails the whole load: no rows come back with it.\npub fn read_{table.Name}(text: &str) -> Result<Vec<{pascal}Row>, String> {{\n");
            o.Append($"    let mut t = Table::new(text, {upper}_FILE);\n    let mut rows = Vec::new();\n");
            o.Append($"    if !t.header(&[{string.Join(", ", table.Columns.Select(column => $"\"{column.Name}\""))}]) {{\n        return t.finish(rows);\n    }}\n");
            o.Append($"    while t.next({table.Columns.Count}) {{\n");
            if (table.Rows == "one") o.Append($"        if !rows.is_empty() {{\n            t.fail(\"expected exactly one {table.Name} row\");\n            return t.finish(Vec::new());\n        }}\n");
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                if (column.Type == "text") { o.Append($"        let {column.Name} = t.text_cell({index});\n"); continue; }
                o.Append($"        let Some({column.Name}) = t.number_cell({index}) else {{\n            return t.finish(Vec::new());\n        }};\n");
                if (column.Check == "merged_id") o.Append($"        if !is_merged_id({column.Name}) {{\n            t.fail(\"{MergedMessage}\");\n            return t.finish(Vec::new());\n        }}\n");
                if (column.Check == "nonzero") o.Append($"        if {column.Name} == 0 {{\n            t.fail(\"{column.Name} must not be zero\");\n            return t.finish(Vec::new());\n        }}\n");
            }
            foreach (var check in table.RowChecks)
            {
                var parts = check.Split(':', '=');
                o.Append($"        if {parts[2]} != {parts[1]} >> {set.MergedShift} {{\n            t.fail(\"{parts[2]} does not match {parts[1]}'s namespace\");\n            return t.finish(Vec::new());\n        }}\n");
            }
            o.Append($"        rows.push({pascal}Row {{ {string.Join(", ", table.Columns.Select(column => column.Name))} }});\n    }}\n    if t.error.is_some() {{\n        return t.finish(Vec::new());\n    }}\n");
            if (table.Rows == "one") o.Append($"    if rows.is_empty() {{\n        t.fail(\"expected exactly one {table.Name} row\");\n    }}\n");
            if (table.Rows == "some") o.Append("    if rows.is_empty() {\n        t.fail(\"no rows\");\n    }\n");
            o.Append("    t.finish(rows)\n}\n\n");

            // The client's reader: one bad row costs that row, and is named. `file` is what its message says,
            // as other files share a table's shape.
            o.Append($"/// The rows that read, and each bad one's `file:line: reason`, for a reader that must survive a typo.\n/// A bad header costs the file's rows. The strict [`read_{table.Name}`] is what a server loads with.\npub fn read_{table.Name}_rows(text: &str, file: &'static str) -> (Vec<{pascal}Row>, Vec<String>) {{\n");
            o.Append("    let mut t = Table::new(text, file);\n    let (mut rows, mut errors) = (Vec::new(), Vec::new());\n");
            o.Append($"    if !t.header(&[{string.Join(", ", table.Columns.Select(column => $"\"{column.Name}\""))}]) {{\n        errors.extend(t.error.take());\n        return (rows, errors);\n    }}\n");
            o.Append($"    loop {{\n        if !t.next({table.Columns.Count}) {{\n            match t.error.take() {{\n                Some(error) => {{\n                    errors.push(error);\n                    continue;\n                }}\n                None => break,\n            }}\n        }}\n");
            if (table.Rows == "one") o.Append($"        if !rows.is_empty() {{\n            t.fail(\"expected exactly one {table.Name} row\");\n            errors.extend(t.error.take());\n            continue;\n        }}\n");
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = table.Columns[index];
                if (column.Type == "text") { o.Append($"        let {column.Name} = t.text_cell({index});\n"); continue; }
                o.Append($"        let Some({column.Name}) = t.number_cell({index}) else {{\n            errors.extend(t.error.take());\n            continue;\n        }};\n");
                if (column.Check == "merged_id") o.Append($"        if !is_merged_id({column.Name}) {{\n            t.fail(\"{MergedMessage}\");\n            errors.extend(t.error.take());\n            continue;\n        }}\n");
                if (column.Check == "nonzero") o.Append($"        if {column.Name} == 0 {{\n            t.fail(\"{column.Name} must not be zero\");\n            errors.extend(t.error.take());\n            continue;\n        }}\n");
            }
            foreach (var check in table.RowChecks)
            {
                var parts = check.Split(':', '=');
                o.Append($"        if {parts[2]} != {parts[1]} >> {set.MergedShift} {{\n            t.fail(\"{parts[2]} does not match {parts[1]}'s namespace\");\n            errors.extend(t.error.take());\n            continue;\n        }}\n");
            }
            o.Append($"        rows.push({pascal}Row {{ {string.Join(", ", table.Columns.Select(column => column.Name))} }});\n    }}\n");
            if (table.Rows is "one" or "some") o.Append($"    if rows.is_empty() {{\n        t.fail(\"{(table.Rows == "one" ? $"expected exactly one {table.Name} row" : "no rows")}\");\n        errors.extend(t.error.take());\n    }}\n");
            o.Append("    (rows, errors)\n}\n\n");
        }
        o.Append("/// Test support: canonical text of a table's rows, compared with the fixtures and across languages.\n#[doc(hidden)]\npub mod dump {\n    use super::*;\n\n");
        o.Append("    /// The rows' text, or \"error: \" and the reason. An unknown table reads as empty text.\n    pub fn by_name(table: &str, text: &str) -> String {\n        match table {\n");
        foreach (var table in set.Tables)
        {
            var format = string.Join(",", table.Columns.Select(column => $"{column.Name}={{}}"));
            var arguments = string.Join(", ", table.Columns.Select(column => $"row.{column.Name}"));
            o.Append($"            \"{table.Name}\" => match read_{table.Name}(text) {{\n                Err(error) => format!(\"error: {{error}}\"),\n                Ok(rows) => {{\n                    let rows: Vec<String> = rows.iter().map(|row| format!(\"{{{{{format}}}}}\", {arguments})).collect();\n                    format!(\"[{{}}]\", rows.join(\",\"))\n                }}\n            }},\n");
        }
        o.Append("            _ => String::new(),\n        }\n    }\n}\n");
        return o.ToString();
    }
}

public sealed record WireColumn(string Name, string Type, string? Check, string Doc);

public sealed record WireTable(string Name, string File, string Doc, string Rows, IReadOnlyList<WireColumn> Columns, IReadOnlyList<string> RowChecks);

public sealed record WireTableSet(
    string Name, int Version, string CppNamespace, string CppHeader, string CppDumpHeader,
    string RustFile, string RustPinDir, string Fixtures, int MergedShift, IReadOnlyList<int> MergedSources,
    IReadOnlyList<WireTable> Tables);
