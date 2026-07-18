# WoW Crucible CLI reference

The CLI executable is `wowcrucible.exe`. Run `wowcrucible --help` for the short command map or `wowcrucible <group> --help` for group-specific syntax. Paths containing spaces must be quoted.

Add `--devbug` anywhere in a command to keep a detailed portable diagnostic log while preserving normal terminal output. Example: `wowcrucible --devbug mpq list patch-H.MPQ`. Logs are written under `Logs\Debug` beside the executable when that location is writable, and only the newest three CLI Devbug sessions are retained. Normal CLI use creates no routine log.

Exit codes are consistent across workflows:

- `0`: completed successfully.
- `1`: invalid input, I/O failure, or another hard error.
- `2`: missing/invalid command syntax.
- `3`: work completed but unresolved conflicts, blocked assets, warnings that require review, or partial failures remain.

## Tool consolidation inventory

```text
wowcrucible tools inventory [workspace-root] [--format=text|json] [--unassigned-only] [--no-missing]
```

Without a path, the CLI searches upward from its executable for the shared `wow edits` workspace. It inventories every top-level workspace root, every direct `Tools` package, and the explicitly audited AmarothTools/Models/Map children. Tracked, missing, and newly unassigned paths are distinct states. A new unassigned directory is printed first and returns review exit code `3`; it is never silently folded into a generic category. The desktop exposes the same catalog in the single-window **Tool inventory** workspace with search and state filters.

## Portable content projects and ID reservations

```text
wowcrucible project create <folder> <name> [--target=wotlk-12340] [--asset-library=folder]
wowcrucible project status <project-folder>
wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]
```

A content project separates Assets, DBC, SQL, Manifests, Reports, and Staging outputs and keeps `ids.crucible.json` as its durable allocation registry. Domains such as `CreatureTemplate`, `CreatureModelData`, `CreatureDisplayInfo`, `CreatureDisplayInfoExtra`, `Spell`, `Item`, `Race`, and `Class` have independent ID spaces. Supply `--occupied` from a live DBC/SQL audit whenever IDs will be deployed; omitting it still reserves against the project registry but returns warning exit code `3` because absence from the project does not prove absence from the target server.

## Asset inspection and libraries

```text
wowcrucible asset texture-info <file.blp>
wowcrucible asset texture-decode <file.blp> <output.png> [--mip=N] [--overwrite]
wowcrucible asset texture-encode <image.png|jpg|bmp|tga> <output.blp> [--format=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--overwrite]
wowcrucible asset texture-validate <file-or-folder> [--recursive]
wowcrucible asset inspect <model.m2|building.wmo>...
wowcrucible asset dependency-graph <processed-library> <root.m2|wmo|adt|wdt> [--target-index=client-index] ["--target-choice=client-path|archive"]... [--only-problems] [--manifest=patch.json] [--output-mpq=name.MPQ] [--format=text|json]
wowcrucible asset preview-info <wrath-model.m2> [--dbc=folder] [--hair=N] [--facial-hair=N] [--naked|--groups=group:variant,...|--all-geosets]
wowcrucible asset appearance-info <CharSections.dbc> <logical-path> <model-file>
wowcrucible asset appearance-render <processed-library> <dbc-folder> <logical-path> <model-file> <body.png> [--skin=N] [--face=N] [--facial-hair=N] [--hair=N] [--source=name] [--hair-output=file.png] [--overwrite]
wowcrucible asset appearance-compose <base.blp> <output.png> [--torso=BLP] [--pelvis=BLP] [--face-upper=BLP] [--face-lower=BLP] [--facial-upper=BLP] [--facial-lower=BLP] [--scalp-upper=BLP] [--scalp-lower=BLP] [--overwrite]
wowcrucible asset workspace <new-output-folder> <files/folders...>
wowcrucible asset library-plan <source-folder> <library-folder> [--max-gb=2]
wowcrucible asset library-run <library-folder> [--workers=6]
wowcrucible asset library-import <extracted-folder> <library-folder> <provenance> [--workers=6]
wowcrucible asset library-repair <library-folder> [--workers=6]
wowcrucible asset library-artifacts <library-folder> [--source-root=folder]... [--apply]
wowcrucible asset library-layout <library-folder> [--apply]
wowcrucible asset library-consolidate <library-folder> [--apply]
wowcrucible asset library-catalog <library-folder>
wowcrucible asset library-status <library-folder>
wowcrucible asset compare-folders <library-folder> [path-filter]
wowcrucible asset compare-files <library-folder> <logical-directory>
wowcrucible asset models <library-folder> <logical-directory>
wowcrucible asset definitive-status <library-folder>
wowcrucible asset definitive-stage <library-folder> <output-folder>
```

`library-plan` recursively inventories loose BLP files and MPQs, but only reads archive file tables for MPQs below `--max-gb`. The library must be outside the source tree. The plan records source paths, archive identities, logical extraction sizes, entry counts, BLP counts, and skipped/failed archives.

`dependency-graph` infers the root's logical client path and provenance from the content-first library, then follows WotLK M2 → exact view SKIN/embedded texture, WMO → group/texture/doodad model, and ADT/WDT → terrain texture/model/WMO edges recursively. Missing/corrupt dependencies and assets found only in another provenance are blocking and return exit code `3`; JSON reports every physical candidate so a caller can make an explicit choice rather than silently mixing mods. `--target-index` adds a complete index made by `client index`: Crucible models standard Wrath root/active-locale archive order, probes archives with unresolved hash-only entries using each exact dependency path, extracts the effective candidate for SHA-256 comparison, omits byte-identical target content, and keeps different bytes as patch overrides. A target path with nonstandard/ambiguous archive ordering is retained safely when a local source is staged, but it cannot satisfy an otherwise missing dependency. Use a quoted repeatable `"--target-choice=Textures\Example.blp|Data\custom.MPQ"` (or the desktop candidate picker) to make that target-archive decision explicit. `--manifest` writes only when the graph is clean and contains only the resulting minimal patch entries. When target content is inherited, the format-4 manifest records the target client/index fingerprint plus every inherited client path, effective archive, and SHA-256; an all-inherited graph reports that no patch is needed instead of producing an invalid empty manifest. The desktop MPQ workspace caches the loaded target catalog, supports selecting processed provenance and effective target-archive candidates explicitly, recomputes all downstream edges before staging, and emits a target-bound companion manifest beside a direct MPQ build.

Crucible's native managed texture codec validates and decodes BLP2 palette, raw BGRA, DXT1/DXT1A, DXT3 and DXT5 payloads plus BLP1 palette and JPEG payloads. Encoding writes Wrath-compatible BLP2 with a complete optional mip chain. `auto` selects DXT1 for opaque pixels, DXT1A for binary transparency, and DXT5 for smooth alpha. Output replacement is opt-in and written through a temporary file. Old but top-level-decodable textures with corrupt phantom/trailing mip metadata are reported as `WARN` and expose only their valid mip chain; the legacy exporter pattern with all offset entries set to `0xFFFFFFFF` and cumulative ends stored in the size table is also recovered only when those ends produce a valid bounded chain. A broken top mip remains `FAIL`. The same operations are available inside the single-window **Texture Lab**, which opens directly with `--textures` or by opening a `.blp` file.

`library-run` is resumable. It preserves each archive as provenance at the leaf of the shared content-first tree, preserves duplicate locale variants with suffixes, never overwrites an existing extracted file or PNG, copies loose BLPs directly into that same tree without modifying the source, decodes with the native codec in bounded parallel work, verifies PNG output, writes a checkpoint after every archive, and generates `asset-catalog.csv` with Maps/UI/Characters/Creatures/Items/Textures/ModelsAndWorld/Audio/Other categories. A corrupt or unsupported individual archive entry is recorded with its exact codec failure while the remaining entries continue. No BLPConverter executable is required.

`library-import` safely ingests a folder produced by another MPQ extractor while retaining a single explicit provenance label. It preserves every file type, converts staged BLPs, relocates the result directly into the shared content-first tree, uses exact byte comparisons when resuming, refuses differing destination files, and never modifies or deletes the extracted source folder. New imports do not create a parallel `Loose` tree.

Example:

```powershell
wowcrucible asset library-plan "G:\extras" "G:\Crucible-Extras-Processed" --max-gb=2
wowcrucible asset library-run "G:\Crucible-Extras-Processed" --workers=6
wowcrucible asset library-consolidate "G:\Crucible-Extras-Processed"
wowcrucible asset library-consolidate "G:\Crucible-Extras-Processed" --apply
wowcrucible asset library-catalog "G:\Crucible-Extras-Processed"
wowcrucible asset library-status "G:\Crucible-Extras-Processed"
```

Stopping `library-run` does not discard completed work. Run the same command again to resume past existing extraction/conversion outputs.

Use `library-repair` after opening a library created by an older Crucible build or after native codec support expands. It never re-extracts MPQs; it retries only BLPs whose matching PNG is absent, refreshes the per-provenance failure lists, and rebuilds the catalog.

Use `library-artifacts` when a prior extractor may have left truncated or zero-filled generated BLPs. It maps each invalid processed texture back through its provenance folder, re-extracts the exact logical path to isolated staging, and validates that source result. Archive provenance is accumulated in `asset-library-sources.json` across later plan replacements; for a library made by an older build, repeat `--source-root=folder` to reconstruct that registry from the original MPQ trees. The default is a non-mutating asset audit written to `Reports\archive-artifact-audit.json`. With `--apply`, a valid source replaces the artifact atomically; a source-invalid entry or repeatable extraction failure moves the generated artifact under `Reports\InvalidArchiveArtifacts` instead of deleting it. Unmapped files remain untouched. MPQ extraction itself always writes to a sibling temporary file now, so a native failure cannot publish a partial output or overwrite an existing good file.

Asset libraries use a content-first comparison layout. For example, `Archives\patch-Y-id\Content\Character\BloodElf\Female\hair.png` becomes `Archives\Content\Character\BloodElf\Female\patch-Y-id\hair.png`, placing every source's version of the same content directory beside the others. `library-layout` migrates the older archive-first layout: it is a non-mutating conflict/count dry run by default; add `--apply` to perform the same-volume migration and rebuild the catalog. Existing destinations are never overwritten.

`library-consolidate` is the separate one-time migration for older `Loose\Content` libraries. It canonicalizes recognizable client paths and places the former top-level package name in the provenance leaf, so `Loose\Content\classic (1)\Character\Human\...` joins `Archives\Content\Character\Human\classic (1)\...`. Run it without `--apply` first: the dry run changes nothing and reports planned moves, exact duplicates, bytes, and non-identical conflicts. Any non-identical destination conflict blocks the entire apply. With `--apply`, a duplicate source is removed only after a streaming byte-for-byte comparison proves that its destination is identical; planned and completed dispositions are retained in `Reports\loose-consolidation-journal.json`, and empty `Loose` directories are removed. A failure during file consolidation rolls moved files back instead of leaving a partial layout. Catalog generation is a second phase after the file commit: if it fails, the command reports that the files were committed, exits nonzero, and preserves the journal error. `library-catalog` safely retries only the catalog phase and clears that recovery marker after success.

Comparison is directory-first because expansions frequently rename equivalent assets. `compare-folders` searches logical content paths and reports PNG/source counts; `compare-files` lists every direct PNG from every provenance folder in the selected path without requiring filenames to match. The Avalonia **Assets & compare** workspace exposes the same model visually with paged, lazy thumbnails and two comparison slots.

`library-catalog` also writes `asset-comparison-index.json`, a compact versioned acceleration sidecar containing PNG, M2, and SKIN directory aggregates. Asset Compare validates it against the Crucible-managed CSV and falls back to the CSV or live filesystem if it is missing, stale, corrupt, unsafe, or unwritable. The sidecar is disposable metadata—not an asset manifest or trust boundary—and failure to write it never turns a successfully committed catalog into a failed library operation. Re-run `library-catalog` after deliberately editing or replacing the CSV outside Crucible.

Launch that visual workspace directly with `WoWCrucible.Desktop-latest.exe "--asset-compare=G:\Crucible-Extras-Processed"`, or choose **Assets & compare** from the main navigation. It replaces the current workspace inside the same application window instead of opening a separate window; **← Editor** returns to the previous workspace. Its directory, cards, and preview columns have draggable splitters, tool groups wrap, and the configuration headers scroll when vertical space is limited so resizing does not strand controls off-screen.

Selecting `Character\BloodElf\Female`, for example, shows all direct PNGs under every provenance leaf in that shared logical path; it does not guess that differently named files are equivalent. Libraries created or consolidated by current Crucible builds no longer depend on a separate `Loose` source tree.

M2 discovery is deliberately lazy: normal PNG directory selection performs no model scan. A model-only directory starts discovery automatically; otherwise it begins only after choosing **Live model preview**. Parent fallback is limited to four direct content/provenance scopes and never recursively sweeps a category or the library root. The CLI `asset models` command uses the same bounded rule.

The visual workspace can sort cards by source/name, filename, or file size in either direction. `Scan exact copies` first narrows candidates by byte length, verifies SHA-256, and finally performs a streaming byte-for-byte comparison. It only labels exact groups and enables a collapsed comparison view; it never deletes source or processed assets. The preview-pane selector can switch from synchronized left/right images to compatible WotLK M2 + SKIN sources found by the automatic model browser. The embedded model view is live and rotatable. With no manual candidate selected it parses SKIN material units, resolves the M2 texture lookup for each visible submesh, and decodes every used embedded BLP from the same provenance layer in the background. Playable-race paths load the complete relevant `CharSections.dbc` records, expose skin/face/facial-hair/hair selectors, layer underwear, face, facial hair, and scalp into the standard 256-based character atlas, scale those verified regions for HD atlases, and bind the separate hair material. The selected hair and facial-hair variations are also resolved through `CharHairGeosets.dbc` and `CharacterFacialHairStyles.dbc`. Items & Sets calls that same core appearance service before applying item wear textures and equipment geosets, so its preview no longer depends on a blank manual skin atlas. The same-window geoset inspector names every group and reports its exact IDs, section counts, and triangle counts. Character mode applies the DBC choices, Naked mode suppresses hair/facial features, and Manual mode allows one exact variant or Hidden per group; only **Everything stacked** deliberately combines mutually exclusive styles. A single texture candidate or a set proven byte-for-byte identical is selected automatically; non-identical provenance variants remain unselected until the user chooses one. Missing component files in that chosen provenance are named rather than silently borrowed from a different mod. Crucible currently chooses the first valid texture pass for each submesh; applying a selected PNG deliberately overrides every material for diagnostic comparison. Other replaceable slots and multi-pass shader blending remain explicit fidelity work. `asset preview-info` now lists every named group and submesh in addition to texture slots, material units, and compact render batches. `--naked` suppresses appearance geometry, `--groups=0:3,7:1` applies exact group overrides, and `--all-geosets` is intentionally stacked. `asset appearance-info` reports the race/sex inference and available base-skin records; `asset appearance-render` resolves explicit CharSections choices and provenance from a processed library and writes the composed body plus optional hair texture; `asset appearance-compose` remains the low-level manual atlas command.

The automatic model browser searches recursively below the selected content path. When a texture-only descendant contains no M2, it walks to the nearest parent with models, so selecting `Character\BloodElf\Female\Hair` can expose models stored at `Character\BloodElf\Female`. Models are labeled Ready, Missing Skin, Requires Conversion, or Invalid before loading. Ready models can be searched, switched with previous/next controls, and previewed with the currently selected PNG as a live UV-mapped material candidate.

Keep/Alternative/Reject/Review decisions are saved under `Projects\Definitive-Set.crucible-assets.json` inside the selected library. PNGs are previews only: when the matching BLP exists, the project records and hashes that deployable BLP. Model keeper groups include the M2 plus matching SKIN and external ANIM companions. `definitive-stage` or the **Stage keepers** button re-verifies every hash, preserves logical client paths, and emits `definitive-set.crucible-patch.json`; it never modifies or deletes the processed source library.

For rapid triage, **Undecided only** hides recorded images, **Auto-advance** selects the next candidate, and the keyboard shortcuts are `K` keeper, `A` alternative, `R` review later, and `X` reject. Model keepers additionally resolve embedded BLP paths from the same provenance. A missing dependency or a texture found only in another patch source blocks Keeper rather than silently creating a mixed model. Replaceable body, hair, cape, fur, and creature-skin slots remain visible as required appearance/DBC bindings.

## DBC information, validation, comparison, and editing

```text
wowcrucible dbc info <file.dbc>
wowcrucible dbc dbd-info <file.dbd> <build> [--format=text|json]
wowcrucible dbc schema-audit <definitions-root> <dbc-folder> <build> [--xml=schema.xml] [--only-problems] [--format=text|json]
wowcrucible dbc item-display <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> [--class=N] [--subclass=N] [--inventory=N] [--assets=processed-library] [--format=text|json]
wowcrucible dbc spell-tooltip <Spell.dbc> <spell-id>... [--format=text|json]
wowcrucible dbc item-equipped <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> <base-skin.blp|image> <output.png> --inventory=N --assets=processed-library [--source=name] [--overwrite]
wowcrucible dbc rows <file.dbc> <schema.xml> <id>...
wowcrucible dbc find <file.dbc> <schema.xml> <field> <value>... [--count|--limit=100]
wowcrucible dbc validate <schema.xml> <folder> [--strict] [--recursive]
wowcrucible dbc compare <base.dbc> <override.dbc> <schema.xml> [--summary]
wowcrucible dbc copy-row <base.dbc> <source.dbc> <schema.xml> <source-id> <new-id> <output.dbc> [--set=Field=Value]
wowcrucible dbc set-row <input.dbc> <schema.xml> <id> <output.dbc> --set=Field=Value
wowcrucible dbc promote apply <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>
wowcrucible dbc promote additions <base.dbc> <override.dbc> <schema.xml> <manifest.json> <output.dbc>
wowcrucible dbc clone-remap where <base.dbc> <source.dbc> <schema.xml> <field> <value>... --manifest=<file> --output=<file>
wowcrucible dbc itemset inspect <ItemSet.dbc> <schema.xml> <set-id> [--spell=Spell.dbc]
wowcrucible dbc itemset clone <ItemSet.dbc> <schema.xml> <output.dbc> <source-set> <new-set> --map=old:new,... [--suffix=" Variant"]
wowcrucible dbc itemset effects <ItemSet.dbc> <schema.xml> <output.dbc> <set-id> --effect=required-items:spell-id [...]
```

`item-display` decodes the exact WotLK build-12340 `ItemDisplayInfo` record behind an SQL `item_template.displayid`. It reports both model names, model textures, inventory icons, all eight wearable texture components, geoset groups, helmet visibility masks, flags, spell/item visuals, particle color, and sound group. Class/subclass/inventory values order canonical object-component paths correctly; `--assets` checks only those bounded logical directories in a processed content-first library and reports every matching provenance source. Use `-` for the schema argument to use Crucible's validated built-in 25-field layout.

`dbd-info` resolves one WoWDBDefs table for the requested full client expansion/build and prints its physical WDBC fields after array, localized-string, width, signedness, ID, and non-inline rules are applied. `schema-audit` compares every top-level DBC with the matching DBD build layout and optionally the existing WDBX XML corpus. An intentional zero-byte server placeholder is informational; missing build definitions and physical field-count disagreements produce review exit code `3`.

`spell-tooltip` reads the exact build-12340 English spell name, subtext, description, and aura description for each requested ID. The Items & Sets tooltip keeps one cached catalog for the configured `Spell.dbc`, refreshes it only when the file changes, and uses these decoded fields for all five item spell slots instead of displaying bare numeric IDs. Runtime `$` tokens are preserved rather than guessed.

`item-equipped` composes a resolved armor display onto a chosen character base-skin atlas and writes a PNG suitable for inspection or the native character renderer. Wear textures are grouped by provenance; Crucible selects the most complete single source unless `--source` names one explicitly, and never silently mixes patch variants. The result also reports the inventory-aware character geoset selection needed for the equipped mesh.

`validate` is non-recursive by default and fails when the selected folder has no top-level DBCs. Use `--recursive` intentionally for a directory tree. `--strict` treats raw fallback schemas as failures.

Promotion and clone/remap commands save semantic, reviewable operations. Strings are re-interned in the destination rather than copying invalid raw offsets. Additive promotion and clone/remap preserve existing IDs.

`itemset inspect` resolves the set's localized name, member item IDs, bonus thresholds, and—when `Spell.dbc` is supplied—spell names. `itemset clone` refuses to overwrite an existing set and requires an explicit old-to-new item ID map. `itemset effects` writes up to eight piece-count/spell pairs to a separate output DBC.

## MPQ listing, extraction, creation, and updates

```text
wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json] [--listfile=paths.txt]
wowcrucible mpq tree <archive.mpq> [internal-folder] [--format=text|json] [--listfile=paths.txt]
wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible mpq extract-folder <archive.mpq> <internal-folder> <destination> [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible mpq create <archive.mpq> <files/folders...>
wowcrucible mpq update <small-patch.mpq> <files/folders...>
wowcrucible mpq merge <output.mpq> <source-a.mpq> <source-b.mpq> [...] [--conflicts=block|earlier|later] [--listfile=paths.txt]
```

Filters accept text or `*`, `?`, and `**` globs. `--content-only` excludes `(listfile)`, `(attributes)`, and `(signature)` metadata. JSON output separates entry properties for scripts.

If an archive opens but only exposes StormLib `File000...` placeholders, Crucible reports those entries as unresolved names rather than calling the MPQ incompatible. Supply a compatible external path corpus with `--listfile=paths.txt` to recover known client paths before filtering or extraction.

`tree` lists only the direct files and subfolders at the requested internal folder while reporting recursive counts and bytes. `extract-folder` resolves that exact folder to its recursive files before extraction. The desktop provides the same lazy breadcrumb browser beside the global flat search and displays non-default locale variants explicitly.

Standalone list/tree/extract commands and the desktop archive browser share compressed indexes under `Cache\MPQ` beside the executable when portable. A cache is reused only when the archive and optional external listfile path, size, and write timestamp still match. Writes are atomic, corrupt entries are rebuilt, and pruning retains at most roughly 64 recent indexes within a 512 MiB budget.

`update` is transaction-safe but copies the archive first and refuses archives larger than 2 GiB. Treat large client/mod layers as immutable inputs and build a small manifest-driven patch instead.

`merge` treats every source MPQ as immutable. Repeated internal paths are SHA-256 checked and then compared byte-for-byte; exact copies are stored once. Different bytes at the same internal path block output by default. `--conflicts=earlier` or `--conflicts=later` is an explicit global precedence choice. Hash-only `File000...` names and duplicate locale variants are blocked unless their real paths can be resolved safely.

## Manifest-first patches

```text
wowcrucible manifest create <manifest.json> <output.mpq> <files/folders...> [--allow=glob] [--deny=glob] [--count=N] [--client-exe=Wow.exe]
wowcrucible manifest list <manifest.json>
wowcrucible manifest validate <manifest.json> [existing-patch.mpq]
wowcrucible manifest build <manifest.json> <output-folder>
```

Manifests preserve source-to-archive mappings and can enforce allow/deny globs, exact entry counts, required paths, and a compatible executable hash. Protected `Interface\GlueXML` paths trigger a build-12340 compatibility warning.

## Client indexing, extraction, fusion, and deployment

```text
wowcrucible client install-patch <patch.mpq> <client-root> [--name=patch-X.MPQ]
wowcrucible client clear-cache <client-root>
wowcrucible client index <client-root> <index-directory> [--no-hash] [--listfile=paths.txt] [--client-exe=Wow.exe]
wowcrucible client corpus <output-listfile> <index-directory>...
wowcrucible client show <index-directory>
wowcrucible client extract <index-directory> <archive-relative-path> <folder> [filter] [--resolved-only|--anonymous-only] [--overwrite] [--quiet]
wowcrucible client fusion <base-root> <override-root>... [--output=plan.json] [--stage=review-folder] [--all]
```

Client indexes are resumable and distinguish active, backup, inactive-locale, and custom-subdirectory archives. Anonymous hash-only MPQ entries remain quarantined unless explicitly requested. `install-patch` atomically installs the patch and deletes that exact client’s `Cache` folder only after success.

## Installed servers and SQL overlays

```text
wowcrucible server detect <installed-server-folder>
wowcrucible server inspect <installed-server-folder>
wowcrucible server bindings <installed-server-folder> [--source=core-source]
wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all] [--summary] [--migration=sync.sql]
wowcrucible server client-plan <installed-server-folder> <effective-dbc-folder> [--source=core-source] [--output=plan.json] [--stage=review-folder]
wowcrucible db inspect <host> <port> <user> <database> --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db schemas <host> <port> <user> <database> --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db rows <host> <port> <user> <database> <table> [--search=text] [--filter=column=value] [--sort=column] [--descending] [--offset=N] [--limit=N] [--format=text|json]
wowcrucible db table-admin <host> <port> <user> <database> <table> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db process-list <host> <port> <user> <database> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db user-list <host> <port> <user> <database> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db account <host> <port> <login> <database> grants <account-user> <account-host> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db account <host> <port> <login> <database> <create|password|lock|unlock|drop> <account-user> <account-host> [--locked] [--apply] [--new-password-env=NAME] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db account <host> <port> <login> <database> <grant|revoke> <account-user> <account-host> <privilege[,privilege]> [--global|--table=NAME] [--grant-option] [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db join <host> <port> <user> <database> <relationship-name> [--type=INNER|LEFT|RIGHT] [--limit=N] [--run] [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db index <host> <port> <user> <database> <table> create <name> <column[,column]> [--unique] [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db index <host> <port> <user> <database> <table> drop <name> [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db query <host> <port> <user> <database> <statement.sql> [--write] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db export <host> <port> <user> <database> <table> <output> [--format=csv|jsonl] [--overwrite] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db import <host> <port> <user> <database> <table> <input.csv> [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db dependency-snapshot <host> <port> <user> <database> <table> <output.json> --key=column=value [--key=column=value]... [--limit=N] [--overwrite]
wowcrucible db draft-template <domain> <output.json> [--overwrite]
wowcrucible db content-plan <host> <port> <user> <database> <domain> <draft.json> [--output=plan.sql] [--overwrite] [--apply] [--update] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db snapshot <host> <port> <user> <database> <output.crucible-db-snapshot> [--password-env=ENV_NAME] [--ssl=Preferred] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]
wowcrucible db snapshot-inspect <snapshot-file> [--quick]
wowcrucible db recovery-audit <legacy-snapshot> <output.crucible-db-audit> [--baseline=stock-snapshot] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]
wowcrucible db recovery-inspect <audit-file> [--quick]
wowcrucible db item-audit <host> <port> <user> <database> [--password-env=ENV_NAME] [--dbc=server-dbc-folder] [--output=report.json]
wowcrucible db item-inspect <host> <port> <user> <database> <item-id> [--password-env=ENV_NAME] [--dbc=server-dbc-folder]
wowcrucible db item-clone <host> <port> <user> <database> <source-id> <new-id> [--suffix=" Variant"] [--itemset=ID]
wowcrucible db spell-inspect <host> <port> <user> <database> <spell-id> [--password-env=ENV_NAME] [--dbc=Spell.dbc|folder] [--format=text|json]
```

Server detection reads the live `worldserver.conf`; it does not accept `.dist` templates. Database passwords should be passed through an environment variable so they do not enter command history. DBC audits report the effective runtime value when the core applies an SQL overlay and can export an idempotent migration without applying it.

`db schemas` lists every database visible to the login. The desktop SQL Studio uses the same discovery to switch locally among world, characters, auth, or other accessible schemas; it does not rewrite the shared server workspace's saved world-database target. A favorite records its database and complete primary key and switches back to that schema when reopened.

`db rows` is the read-only CLI half of the complete live table browser. It always returns every live-schema column, supports broad search plus an exact `column=value` filter (`column=<NULL>` for SQL null), validates requested sort/filter columns against the inspected schema, adds primary-key tie breakers for stable paging, and bounds one page to 1–500 rows. The desktop exposes the same operations and can clone any complete primary-keyed row into a new identity; every insert column independently selects VALUE, NULL, or OMIT, so defaults and explicit nulls are not conflated.

`db table-admin` reports the complete live column shape, `SHOW CREATE TABLE` DDL, and every index. `db process-list` reports every connection visible to the supplied login. `db user-list` deliberately reads account metadata without password hashes and reports the server permission error when the login cannot inspect `mysql.user`; it never converts that denial into an empty result.

`db account ... grants` reports the exact `SHOW GRANTS` output for one user/host identity and can inspect the connected login even when it cannot enumerate `mysql.user`. The other account actions are dry-run plans unless `--apply` is explicit. Privilege names are checked against the connected server's `SHOW PRIVILEGES` output, scope defaults to the active database, `--table` narrows it, and `--global` deliberately selects `*.*`. New passwords are read only from `WOW_CRUCIBLE_NEW_DB_PASSWORD` or `--new-password-env`; they are parameterized for execution, shown as `<password supplied in memory>`, and never accepted in command arguments. Account changes can affect server access immediately, so verify them with `account ... grants` after apply.

`db join` accepts a relationship name printed by `db inspect`, generates exact uniquely aliased `source__...` and `target__...` columns, and prints SQL without executing it by default. `--run` executes only that generated SELECT. `db index` likewise prints reviewed DDL by default; `--apply` is required to create or drop an index, ordinary primary-key removal is blocked, and MySQL may implicitly commit DDL.

`db query` reads a UTF-8 SQL file so the statement does not need to be pasted into shell history. Without `--write`, the core service accepts only `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, or `EXPLAIN`, including after leading SQL comments. Every other statement is rejected before it reaches MySQL. `--write` is the explicit automation path for a previously reviewed statement; remember that MySQL DDL can implicitly commit.

`db export` discovers the live table shape and streams the complete table to an atomically published UTF-8 CSV or newline-delimited JSON file. CSV uses `\\N` for SQL `NULL`, quotes commas/newlines correctly, and represents binary values as hexadecimal. Existing outputs require `--overwrite`.

`db import` is a structural dry-run by default: it validates the header against the live schema, rejects unknown/generated/duplicate columns, checks required fields and every row width, and reports the planned row count. `--apply` is required to write. Apply is INSERT-only in one transaction; an existing key or any row error rolls back the complete import instead of replacing data.

`db dependency-snapshot` reads one row by its complete primary key, discovers current-schema declared relationships plus validated AzerothCore item/quest/loot/creature/gameobject/spell/trainer/appearance mappings, and captures complete matching rows for each exact edge. Repeat `--key` for a composite identity. The per-edge limit defaults to 200 and is capped at 500; every truncation is explicit. An empty SQL `_dbc` mirror is recorded as a file-DBC edge instead of falsely reporting a broken reference. The artifact is review data and never executable SQL.

```powershell
wowcrucible db dependency-snapshot 127.0.0.1 3306 acore acore_world item_template item-17802.crucible-dependencies.json --key=entry=17802
```

`db draft-template` creates a portable, editable JSON starting point without connecting to MySQL. Supported domains are `creature`, `gameobject`, `quest`, `gossip-menu`, `gossip-option`, `npc-text`, `trainer`, `trainer-spell`, `trainer-creature`, `legacy-trainer-spell`, `condition`, and `smartai`. `db content-plan` adapts the draft to the actual live schema and prints exact SQL without writing by default. The quest adapter covers the complete 105-field stock 3.3.5a template; behavior adapters cover all current gossip, trainer, condition, NPC-text, and SmartAI columns while preserving composite keys. `--apply` inserts the primary and child rows in one transaction. `--apply --update` requires the primary identity to match exactly one existing row, updates mapped primary fields while preserving custom columns, and inserts only collision-free newly staged child rows; it never silently replaces existing children.

```powershell
wowcrucible db draft-template smartai smartai.json
wowcrucible db content-plan 127.0.0.1 3306 acore acore_world smartai smartai.json --password-env=WOW_CRUCIBLE_DB_PASSWORD
```

`db snapshot` is phase one of legacy SQL recovery. It issues only fixed metadata queries and `SELECT` statements, requests a consistent read-only transaction where the server supports one, streams base-table rows without loading the database into memory, and publishes the compressed artifact atomically. By default it verifies that the selected schema looks like a world database and excludes known auth/character runtime-state tables while retaining reusable definitions such as `mail_loot_template`, `instance_template`, `guild_rewards`, and `pet_levelstats`. Repeat `--include` or `--exclude` for table-name globs. `--include-sensitive` deliberately removes the runtime-state safety filter; those captured rows may themselves contain account secrets even though the live connection password is never serialized.

`db snapshot-inspect` performs offline schema, entry, byte-count, row-count, and SHA-256 integrity checks. The default also decodes every row structure; `--quick` still reads and hashes every table but skips structural decoding.

`db recovery-audit` is the read-only phase-two boundary. With `--baseline`, it emits an atomic, compressed, source-hash-bound audit containing keyed added, modified, and removed rows plus exact before/after field values. Without a baseline, it labels every emitted row an **unattributed candidate** instead of pretending stock rows are custom work. Tables are grouped into content domains for review. A table without a primary key, a collation-dependent/textual key that snapshot format v1 cannot order portably, another incompatible key, a partial schema, or an excluded counterpart is reported but never compared by capture order. Known runtime-state tables remain excluded unless `--include-sensitive` is explicit. `db recovery-inspect` verifies the audit offline; `--quick` still hashes all change streams. The artifact always records `PromotionReady=false`: dependency closure, target translation, ID remapping, selection, SQL generation, and deployment remain later phases.

`item-audit` discovers the current schema instead of assuming one core revision. It checks vendors, achievement rewards, creature/gameobject/item/mail/pickpocket/skinning/disenchant/fishing/spell/reference/prospecting/milling loot, SQL character starting items, usable quest rewards/start items, and every reward/choice slot that exists. `--dbc` reads both `CharStartOutfit.dbc` and `Spell.dbc`: normal starting equipment is not always represented in `playercreateinfo_item`, while crafted/conjured outputs live in create-item spell effects. Spell outputs count only when the spell is reachable through a trainer, character-start action, usable quest reward, an already acquired item's use/learn spell, or a nested learn/trigger-spell edge; simply existing as an unused Spell row is insufficient. Quest rewards require a live starter and ender and must not be disabled; quest starting items require a live starter and must not be disabled. The report calls an item **no known acquisition path**, not certainly unobtainable, because custom server scripts can grant items without a database relationship.

Search the same readable live references used by the guided editors:

```powershell
wowcrucible db reference-search 127.0.0.1 3306 acore acore_world item Thunderfury
wowcrucible db reference-search 127.0.0.1 3306 acore acore_world spell Frostbolt --dbc="C:\Server\data\dbc" --format=json
```

`reference-search` accepts `spell`, `item`, `creature`, `quest`, or `gameobject`, searches an exact numeric ID or partial name, and reports the source of every match. Supplying `--dbc` for spells merges `Spell.dbc` with `spell_dbc` instead of showing duplicate identities. Passwords use `WOW_CRUCIBLE_DB_PASSWORD` or `--password-env`; they are never command arguments.

`item-inspect` traces one exact ID through the same audit and prints the accepted or rejected evidence behind its classification. For example, it explains that item 17 occurring in a nonzero-reference control row is not a direct drop, and that a reward on an explicitly disabled quest does not make the item obtainable.

`item-clone` transactionally copies every writable column currently present in `item_template`, including custom columns unknown to Crucible, plus matching locale rows. It refuses an already-used destination ID. `--itemset=ID` assigns the copy to a set; omitting it preserves the source membership.

`spell-inspect` explains the effective server record for one spell. AzerothCore loads the file `Spell.dbc` first, then a matching `spell_dbc` row replaces that complete server-side record. When `--dbc` is supplied, Crucible compares the two using AzerothCore's exact 234-character `SpellEntryfmt`: ignored `x` cells do not create false differences, integer cells compare by their loaded 32-bit representation, floats compare as floats, and server-consumed strings compare as decoded text. The audit also searches schema-adaptively across recognized proc, script, rank, prerequisite, trainer, item, quest, creature-spell, spell-click, character-start, faction-change, SmartAI, condition, and disable tables. Text output is concise; `--format=json` includes every value and complete primary key from every matched row.

## Common examples

```powershell
# Inspect a table before editing it.
wowcrucible dbc info "D:\Server\data\dbc\Spell.dbc"

# Validate a complete extracted corpus.
wowcrucible dbc validate "Definitions\WotLK 3.3.5 (12340).xml" "D:\DBC" --recursive --strict

# List only real texture payloads as JSON.
wowcrucible mpq list "patch-Z.MPQ" "**\*.blp" --content-only --format=json

# Build and validate a deliberately small patch.
wowcrucible manifest create "classless.json" "patch-W.MPQ" "changed-files" --deny="**\*.bak"
wowcrucible manifest build "classless.json" "build"
wowcrucible manifest validate "classless.json" "build\patch-W.MPQ"
```

The CLI never needs copyrighted game data in the repository. Point commands at your own extracted client/server data and keep generated corpora outside the Git working tree.
