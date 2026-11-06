using WoWCrucible.Core;

internal static class WireProtocolTestSuite
{
    private const string Definition = """
        // A small definition using every field kind. The real one lives with the server module.
        {
          "schema_format": 1,
          "generator_version": 1,
          "name": "sample",
          "protocol": 7,
          "cpp": { "namespace": "Sample::Wire", "header": "SampleWire.h", "dump_header": "SampleWireDump.h" },
          "rust": { "file": "src/sample_wire.rs", "pin_dir": "protocol" },
          "fixtures": "sample.fixtures.tsv",
          "merged_id": { "shift": 28, "sources": [1, 2] },
          "constants": [
            { "name": "PROTO", "type": "u8", "value": 7 },
            { "name": "MAX_ITEMS", "type": "usize", "value": 4 },
            { "name": "FLAG", "type": "u8", "value": "0x10", "doc": "A flag." }
          ],
          "aliases": { "items": { "type": "list", "count": "u8", "max": "MAX_ITEMS", "of": "item", "strict": ["ascending:key"] } },
          "structs": [
            { "name": "item", "rust_tuple": true, "fields": [
              { "name": "key", "type": "u32", "strict": ["merged_id"] },
              { "name": "value", "type": "u16" } ] },
            { "name": "group", "fields": [
              { "name": "form", "type": "u8", "strict": ["max:1", "zero_unless:other"] },
              { "name": "own", "type": "items" },
              { "name": "other", "type": "optional", "flag": "has_other", "of": "items" } ] }
          ],
          "messages": [
            { "name": "ping", "opcode": "0x7F00", "direction": "c2s", "validate": "strict", "fields": [
              { "name": "proto", "type": "u8", "const": "PROTO" },
              { "name": "on", "type": "bool8" },
              { "name": "ids", "type": "list", "count": "u16", "of": "u64", "strict": ["min:1", "unique"], "item_strict": ["nonzero"] },
              { "name": "raw", "type": "bytes", "len": 3 } ] },
            { "name": "pong", "opcode": "0x7F01", "direction": "s2c", "validate": "lenient", "fields": [
              { "name": "group", "type": "group" } ] },
            { "name": "block", "direction": "c2s", "validate": "strict", "fields": [
              { "name": "tag", "magic": "BLOK" },
              { "name": "group", "type": "group" } ] },
            { "name": "tail", "direction": "c2s", "validate": "strict", "fields": [
              { "name": "block", "type": "tagged", "of": "block" } ] }
          ]
        }
        """;

    public static void Run()
    {
        var protocol = WireProtocolGenerator.Parse(Definition);
        var first = WireProtocolGenerator.Generate(protocol, Definition, "sample.json");
        var second = WireProtocolGenerator.Generate(WireProtocolGenerator.Parse(Definition), Definition, "sample.json");
        Check(first == second, "The same definition generates the same bytes.");
        Check(WireProtocolGenerator.Generate(protocol, Definition.Replace("\n", "\r\n"), "sample.json") == first, "A checkout's line endings do not change the output.");
        Check(!first.CppCodec.Contains('\r') && !first.Rust.Contains('\r') && first.CppCodec.EndsWith('\n'), "Output is LF with a final newline.");

        var hash = WireProtocolGenerator.TextHash(Definition);
        foreach (var text in new[] { first.CppCodec, first.CppDump, first.Rust })
        {
            Check(text.Contains($"// definition-sha256: {hash}\n", StringComparison.Ordinal), "Every file records the definition's hash.");
            Check(text.Contains($"// schema-format: 1, generator: {WireProtocolGenerator.GeneratorVersion}, protocol: 7\n", StringComparison.Ordinal), "Every file records its versions.");
            var marker = text.IndexOf(WireProtocolGenerator.BodyHashMarker, StringComparison.Ordinal);
            var bodyStart = text.IndexOf('\n', marker) + 1;
            Check(text[(marker + WireProtocolGenerator.BodyHashMarker.Length)..(bodyStart - 1)] == WireProtocolGenerator.TextHash(text[bodyStart..]), "The body hash covers everything under it.");
        }

        // The sizes a server registers as limits: 1 + 1 + (2 + 65535 * 8) + 3, and 1 + (1 + 4 * 6) + 1 + (1 + 4 * 6).
        Check(protocol.MaxSize(protocol.Find("ping")) == 524287, "ping's largest body.");
        Check(protocol.MaxSize(protocol.Find("pong")) == 52, "pong's largest body.");
        Check(protocol.MaxSize(protocol.Find("tail")) == 56, "A tagged block counts its magic.");

        foreach (var needle in new[] { "inline constexpr std::uint16_t OpPing = 0x7F00;", "PingMaxBody = 524287", "DecodePing(std::span", "if (c.StartsWith(\"BLOK\", 4))", "IsMergedId(v.Key)", "inline constexpr std::uint8_t Flag = 0x10;", "v.Form != 0 && !v.Other" })
            Check(first.CppCodec.Contains(needle, StringComparison.Ordinal), $"C++ has {needle}.");
        foreach (var needle in new[] { "pub const OP_PING: u16 = 0x7F00;", "pub type Item = (u32, u16);", "pub fn decode_pong(", "pub const FLAG: u8 = 0x10;", "read_ping(&mut c, true)", "read_pong(&mut c, false)", "c.0.starts_with(b\"BLOK\")" })
            Check(first.Rust.Contains(needle, StringComparison.Ordinal), $"Rust has {needle}.");

        Refused("\"schema_format\": 1", "\"schema_format\": 2", "A definition of another schema format stops.");
        Refused("\"generator_version\": 1", "\"generator_version\": 99", "A definition pinned to another generator stops.");
        Refused("\"name\": \"sample\",", "\"name\": \"sample\", \"surprise\": 1,", "An unknown key stops.");
        Refused("\"opcode\": \"0x7F01\"", "\"opcode\": \"0x7F00\"", "A repeated opcode stops.");
        Refused("\"type\": \"items\"", "\"type\": \"nothing\"", "An unknown type stops.");
        Refused("\"max\": \"MAX_ITEMS\"", "\"max\": \"MAX_NOTHING\"", "An unknown limit stops.");
        Refused("\"ascending:key\"", "\"ascending:nothing\"", "A sort key that is not a field stops.");
        Refused("\"zero_unless:other\"", "\"zero_unless:own\"", "A condition on a field that is not optional stops.");
        Refused("\"const\": \"PROTO\"", "\"const\": \"NOTHING\"", "An unknown const stops.");
        Refused("\"of\": \"block\"", "\"of\": \"group\"", "A tagged block needs a magic.");

        Outputs();
        Console.WriteLine("Wire protocol generator checks passed.");
    }

    private static void Outputs()
    {
        var root = Path.Combine(Path.GetTempPath(), "cwp" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var definition = Path.Combine(root, "sample.json");
            File.WriteAllText(definition, Definition);
            var fixtures = Path.Combine(root, "sample.fixtures.tsv");
            File.WriteAllText(fixtures, "# a fixture\n");
            var paths = new WireProtocolPaths(definition, Path.Combine(root, "cpp"), Path.Combine(root, "crate"));

            Check(WireProtocolOutputs.Check(paths).All(state => state.State == "missing"), "Nothing generated yet reads as missing.");
            Check(WireProtocolOutputs.Write(paths).Count == 5, "Two C++ files, the Rust file, and the pinned definition and fixtures.");
            Check(WireProtocolOutputs.Check(paths).All(state => state.State == "current"), "Everything written is current.");
            Check(WireProtocolOutputs.Write(paths).Count == 0, "Regenerating unchanged writes nothing.");
            Check(File.Exists(Path.Combine(root, "crate", "protocol", "sample.json")) && File.Exists(Path.Combine(root, "crate", "protocol", "sample.fixtures.tsv")), "The client's pin is beside its crate.");
            Check(!File.ReadAllBytes(Path.Combine(root, "cpp", "SampleWire.h")).AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "No byte-order mark.");

            var header = Path.Combine(root, "cpp", "SampleWire.h");
            File.AppendAllText(header, "// a hand edit\n");
            Check(WireProtocolOutputs.Check(paths).Single(state => state.Path == Path.GetFullPath(header)).State == "stale", "A hand edit reads as stale.");
            WireProtocolOutputs.Write(paths);

            File.WriteAllText(definition, Definition.Replace("\"value\": 7", "\"value\": 8"));
            var stale = WireProtocolOutputs.Check(paths);
            Check(stale.Count(state => state.State == "stale") == 4 && stale.Single(state => state.State == "current").Path.EndsWith("sample.fixtures.tsv", StringComparison.Ordinal), "A changed definition stales every output and the pin.");

            File.WriteAllText(definition, Definition);
            WireProtocolOutputs.Write(paths);
            File.WriteAllText(fixtures, "# a changed fixture\n");
            Check(WireProtocolOutputs.Check(paths).Single(state => state.State == "stale").Path.EndsWith("sample.fixtures.tsv", StringComparison.Ordinal), "A changed fixture stales only its pin.");

            var cppOnly = WireProtocolOutputs.Expected(paths with { ClientRoot = null });
            Check(cppOnly.Count == 2 && cppOnly.All(file => file.Path.EndsWith(".h", StringComparison.Ordinal)), "The server side alone needs no client path.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Refused(string find, string replacement, string message)
    {
        if (!Definition.Contains(find, StringComparison.Ordinal)) throw new InvalidOperationException($"The test's anchor is gone: {find}");
        try
        {
            WireProtocolGenerator.Parse(Definition.Replace(find, replacement));
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
