namespace WoWCrucible.Core;

public sealed record CliCommandResolution(
    bool Success,
    IReadOnlyList<string> Arguments,
    string? Message = null,
    IReadOnlyList<string>? MatchingChoices = null)
{
    public IReadOnlyList<string> Choices => MatchingChoices ?? [];
}

/// <summary>
/// Resolves CLI group and operation abbreviations before the normal dispatcher runs.
/// Exact spellings remain compatible; hyphenated operation names may also be entered
/// as separate words. A prefix is accepted only when it identifies one command.
/// </summary>
public static class CliCommandAbbreviationService
{
    private static readonly string[] Groups =
    [
        "workspace", "asset", "project", "tools", "knowledge", "cache", "client",
        "server", "db", "dbc", "mpq", "casc", "manifest"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Operations =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["workspace"] = Words("discover init show"),
            ["tools"] = Words("commands inventory"),
            ["knowledge"] = Words("search show"),
            ["cache"] = Words("info rows export server-plan server-apply server-rollback"),
            ["client"] = Words("install-patch clear-cache publisher-key release-create release-sign release-verify release-plan release-apply release-rollback index corpus extract show fusion fusion-dbc-plan fusion-dbc-apply fusion-dbc-remap-plan fusion-dbc-remap-apply fusion-stage"),
            ["server"] = Words("detect inspect bindings dbc-audit dbc-apply dbc-rollback dbc-module-export client-plan"),
            ["project"] = Words("create status run-create artifact-register cleanup reserve-ids occupancy reserve-live class-plan class-build race-plan race-build"),
            ["mpq"] = Words("list tree extract extract-folder create update put merge"),
            ["casc"] = Words("list tree extract extract-folder"),
            ["manifest"] = Words("create list validate build"),
            ["db"] = Words("draft-template schemas inspect favorites rows pet-curve pet-compare pet-preview pet-graph table-admin table-design process-list user-list account join index query export import dependency-snapshot content-plan snapshot snapshot-inspect recovery-audit recovery-inspect reference-search item-audit item-inspect item-clone spell-inspect object-compose objects object-show object-dependencies object-export object-drop object-set object-rollback view-set event-state sync-bridge sync-bridge-inspect sync-plan sync-inspect sync-apply sync-rollback"),
            ["dbc"] =
            [
                .. Words("info rows export import find validate compare stage-create stage-info stage-query stage-mutate stage-diff stage-apply dbd-info schema-audit lighting lighting-scene lighting-band-set spell-tooltip item-display item-equipped clone-dependency copy-row set-row"),
                "promote apply", "promote additions", "clone-remap where", "itemset inspect", "itemset clone", "itemset effects"
            ],
            ["asset"] = Words("layer-stack-index layer-stack-query layer-merge layer-prune-previews texture-consumers-build texture-consumers texture-info texture-decode texture-proof texture-compose texture-mask texture-brush texture-encode texture-validate inspect m2-material-audit m2-downport-plan m2-downport-scan m2-downport m2-downport-batch-plan m2-downport-batch dependency-graph gameobject-index-plan indexed-snapshot-verify gameobject-bulk-plan gameobject-bulk-apply creature-display-catalog creature-appearance-port-plan npc-chr-plan npc-chr-apply item-client-plan item-client-apply creature-appearance-port-apply creature-appearance-patch-plan creature-appearance-patch-manifest creature-appearances preview-info model-export wmo-preview-info path-candidates appearance-info appearance-render appearance-compose models definitive-status definitive-stage workspace library-plan library-run library-import library-repair library-artifacts library-layout library-consolidate library-catalog library-status compare-folders compare-files map-info wdt-create wdt-tiles-plan wdt-tiles-apply adt-height-plan adt-height-apply adt-brush-plan adt-brush-apply liquid-type-catalog adt-liquid-info adt-liquid-plan adt-liquid-apply adt-texture-info adt-texture-plan adt-texture-apply ground-effect-catalog adt-ground-effect-plan adt-ground-effect-apply adt-texture-add-plan adt-texture-add-apply adt-alpha-info adt-alpha-plan adt-alpha-apply adt-placement-plan adt-placement-apply adt-placement-add-plan adt-placement-delete-plan adt-placement-lifecycle-apply adt-placement-multi-add-plan adt-placement-multi-delete-plan adt-placement-multi-apply adt-placement-multi-transform-plan adt-placement-multi-transform-apply")
        };

    private static readonly IReadOnlyDictionary<string, string> ExactGroupAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["database"] = "db",
            ["assets"] = "asset",
            ["projects"] = "project",
            ["work"] = "workspace"
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OperationAliases =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["asset"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["library validate"] = "texture-validate",
                ["libval"] = "texture-validate",
                ["texture library validate"] = "texture-validate",
                ["texlib validate"] = "texture-validate"
            },
            ["client"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["corp"] = "corpus"
            }
        };

    public static CliCommandResolution Resolve(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || arguments[0].StartsWith("-", StringComparison.Ordinal))
            return Success(arguments);

        var groupResult = ResolveGroup(arguments[0]);
        if (!groupResult.Success) return groupResult;
        var group = groupResult.Arguments[0];
        if (arguments.Count == 1) return Success([group]);

        var tail = arguments.Skip(1).ToArray();
        if (tail[0] is "help" or "--help" or "-h") return Success([group, .. tail]);

        // The user's common texture-library shorthand is a complete invocation macro:
        // `as li v t` and `as libval texlib` both retain a usable directory operand.
        if (group.Equals("asset", StringComparison.OrdinalIgnoreCase) && MatchesSegments(tail, ["li", "v", "t"]))
            return Success([group, "texture-validate", "texture-library", .. tail[3..]]);
        if (group.Equals("asset", StringComparison.OrdinalIgnoreCase) && tail.Length >= 2 &&
            tail[0].Equals("libval", StringComparison.OrdinalIgnoreCase) && tail[1].Equals("texlib", StringComparison.OrdinalIgnoreCase))
            return Success([group, "texture-validate", "texture-library", .. tail[2..]]);

        if (!Operations.TryGetValue(group, out var operations)) return Success([group, .. tail]);
        var candidates = operations.Select(operation => new Candidate(operation, operation)).ToList();
        if (OperationAliases.TryGetValue(group, out var aliases))
            candidates.AddRange(aliases.Select(alias => new Candidate(alias.Key, alias.Value)));

        var bestConsumed = 0;
        Candidate[] best = [];
        for (var consumed = 1; consumed <= Math.Min(6, tail.Length); consumed++)
        {
            if (tail.Take(consumed).Any(value => value.StartsWith("-", StringComparison.Ordinal))) break;
            var input = SplitSegments(tail.Take(consumed));
            var matching = candidates.Where(candidate => SegmentPrefixMatch(input, SplitSegments([candidate.Phrase]))).ToArray();
            if (matching.Length == 0) break;
            var exact = matching.Where(candidate => SplitSegments([candidate.Phrase]).SequenceEqual(input, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (exact.Length > 0) matching = exact;
            bestConsumed = consumed;
            best = matching;
        }

        if (bestConsumed == 0) return Success([group, .. tail]);
        var canonicalMatches = best.Select(candidate => candidate.Canonical).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (canonicalMatches.Length != 1)
            return new(false, arguments.ToArray(), $"Ambiguous command '{group} {string.Join(' ', tail.Take(bestConsumed))}'. Keep typing one of:", canonicalMatches.Select(value => $"{group} {value}").ToArray());

        return Success([group, .. canonicalMatches[0].Split(' ', StringSplitOptions.RemoveEmptyEntries), .. tail[bestConsumed..]]);
    }

    private static CliCommandResolution ResolveGroup(string input)
    {
        if (ExactGroupAliases.TryGetValue(input, out var alias)) return Success([alias]);
        var exact = Groups.FirstOrDefault(group => group.Equals(input, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return Success([exact]);
        var matches = Groups.Where(group => group.StartsWith(input, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1) return Success(matches);
        if (matches.Length > 1)
            return new(false, [input], $"Ambiguous command group '{input}'. Keep typing one of:", matches);
        return Success([input]);
    }

    private static bool MatchesSegments(IReadOnlyList<string> input, IReadOnlyList<string> prefixes) =>
        input.Count >= prefixes.Count && prefixes.Select((prefix, index) => input[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).All(value => value);

    private static bool SegmentPrefixMatch(IReadOnlyList<string> input, IReadOnlyList<string> candidate) =>
        input.Count <= candidate.Count && input.Select((segment, index) => candidate[index].StartsWith(segment, StringComparison.OrdinalIgnoreCase)).All(value => value);

    private static string[] SplitSegments(IEnumerable<string> values) => values
        .SelectMany(value => value.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(value => value.ToLowerInvariant()).ToArray();

    private static string[] Words(string value) => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static CliCommandResolution Success(IReadOnlyList<string> arguments) => new(true, arguments.ToArray());
    private sealed record Candidate(string Phrase, string Canonical);
}
