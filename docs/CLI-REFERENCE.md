# WoW Crucible CLI reference

The CLI executable is `wowcrucible.exe`. Run `wowcrucible --help` for the short command map or `wowcrucible <group> --help` for group-specific syntax. Paths containing spaces must be quoted.

Add `--devbug` anywhere in a command to keep a detailed portable diagnostic log while preserving normal terminal output. Example: `wowcrucible --devbug mpq list patch-H.MPQ`. Logs are written under `Logs\Debug` beside the executable when that location is writable, and only the newest three CLI Devbug sessions are retained. Normal CLI use creates no routine log.

Exit codes are consistent across workflows:

- `0`: completed successfully.
- `1`: invalid input, I/O failure, or another hard error.
- `2`: missing/invalid command syntax.
- `3`: work completed but unresolved conflicts, blocked assets, warnings that require review, or partial failures remain.

## Portable content projects and ID reservations

```text
wowcrucible project create <folder> <name> [--target=wotlk-335a-12340] [--asset-library=folder]
wowcrucible project status <project-folder>
wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]
```

A content project separates Assets, DBC, SQL, Manifests, Reports, and Staging outputs and keeps `ids.crucible.json` as its durable allocation registry. Domains such as `CreatureTemplate`, `CreatureModelData`, `CreatureDisplayInfo`, `CreatureDisplayInfoExtra`, `Spell`, `Item`, `Race`, and `Class` have independent ID spaces. Supply `--occupied` from a live DBC/SQL audit whenever IDs will be deployed; omitting it still reserves against the project registry but returns warning exit code `3` because absence from the project does not prove absence from the target server.

## Asset inspection and libraries

```text
wowcrucible asset inspect <model.m2|building.wmo>...
wowcrucible asset preview-info <wrath-model.m2>
wowcrucible asset workspace <new-output-folder> <files/folders...>
wowcrucible asset library-plan <source-folder> <library-folder> [--max-gb=2]
wowcrucible asset library-run <library-folder> <blpconverter.exe> [--workers=6]
wowcrucible asset library-import <extracted-folder> <library-folder> <provenance> <blpconverter.exe> [--workers=6]
wowcrucible asset library-repair <library-folder> <blpconverter.exe> [--workers=6]
wowcrucible asset library-layout <library-folder> [--apply]
wowcrucible asset library-status <library-folder>
wowcrucible asset compare-folders <library-folder> [path-filter]
wowcrucible asset compare-files <library-folder> <logical-directory>
wowcrucible asset models <library-folder> <logical-directory>
wowcrucible asset definitive-status <library-folder>
wowcrucible asset definitive-stage <library-folder> <output-folder>
```

`library-plan` recursively inventories loose BLP files and MPQs, but only reads archive file tables for MPQs below `--max-gb`. The library must be outside the source tree. The plan records source paths, archive identities, logical extraction sizes, entry counts, BLP counts, and skipped/failed archives.

`library-run` is resumable. It preserves each archive in a distinct provenance folder, preserves duplicate locale variants with suffixes, never overwrites an existing extracted file or PNG, copies loose BLPs into the library without modifying the source, converts in bounded parallel batches, verifies PNG output, writes a checkpoint after every archive, and generates `asset-catalog.csv` with Maps/UI/Characters/Creatures/Items/Textures/ModelsAndWorld/Audio/Other categories. A corrupt or unsupported individual archive entry is recorded in the checkpoint while the remaining entries continue. If the external converter rejects a BLP, Crucible retries its batch neighbors individually and writes the genuinely unsupported paths to `.blp-conversion-failures.txt` inside that provenance root. Executables that reject the required `/M` batch syntax now fail immediately instead of spawning a retry for every texture.

`library-import` safely ingests a folder produced by another MPQ extractor while retaining a single explicit provenance label. It preserves every file type, converts staged BLPs, uses exact byte comparisons when resuming, refuses differing destination files, and never modifies or deletes the extracted source folder.

Example:

```powershell
wowcrucible asset library-plan "G:\extras" "G:\Crucible-Extras-Processed" --max-gb=2
wowcrucible asset library-run "G:\Crucible-Extras-Processed" "C:\Tools\BLPConverter.exe" --workers=6
wowcrucible asset library-status "G:\Crucible-Extras-Processed"
```

Stopping `library-run` does not discard completed work. Run the same command again to resume past existing extraction/conversion outputs.

Use `library-repair` after upgrading the converter or after opening a library created by an older Crucible build. It never re-extracts MPQs; it retries only BLPs whose matching PNG is absent, refreshes the per-provenance failure lists, and rebuilds the catalog.

Archive libraries use a content-first comparison layout. For example, `Archives\patch-Y-id\Content\Character\BloodElf\Female\hair.png` becomes `Archives\Content\Character\BloodElf\Female\patch-Y-id\hair.png`, placing every patch's version of the same content directory beside the others. `library-layout` is a non-mutating conflict/count dry run by default; add `--apply` to perform the resumable same-volume migration and rebuild the catalog. Existing destinations are never overwritten.

Comparison is directory-first because expansions frequently rename equivalent assets. `compare-folders` searches logical content paths and reports PNG/source counts; `compare-files` lists every direct PNG from every provenance folder in the selected path without requiring filenames to match. The Avalonia **Assets & compare** workspace exposes the same model visually with paged, lazy thumbnails and two comparison slots.

Launch that visual workspace directly with `WoWCrucible.Desktop-latest.exe "--asset-compare=G:\Crucible-Extras-Processed"`. Selecting `Character\BloodElf\Female`, for example, shows all direct PNGs under every archive patch folder plus direct PNGs from the matching `Loose\Content` path; it does not guess that differently named files are equivalent. Loose-only paths also appear in the same navigator with `Loose` as their source.

The visual workspace can sort cards by source/name, filename, or file size in either direction. `Scan exact copies` first narrows candidates by byte length, verifies SHA-256, and finally performs a streaming byte-for-byte comparison. It only labels exact groups and enables a collapsed comparison view; it never deletes source or processed assets. The preview-pane selector can switch from synchronized left/right images to compatible WotLK M2 + SKIN sources found by the automatic model browser. The embedded model view is live and rotatable; applying a selected PNG uses the model's real first UV map but remains an explicitly labeled candidate preview until replaceable slots and full `CharSections` composition are resolved.

The automatic model browser searches recursively below the selected content path. When a texture-only descendant contains no M2, it walks to the nearest parent with models, so selecting `Character\BloodElf\Female\Hair` can expose models stored at `Character\BloodElf\Female`. Models are labeled Ready, Missing Skin, Requires Conversion, or Invalid before loading. Ready models can be searched, switched with previous/next controls, and previewed with the currently selected PNG as a live UV-mapped material candidate.

Keep/Alternative/Reject/Review decisions are saved under `Projects\Definitive-Set.crucible-assets.json` inside the selected library. PNGs are previews only: when the matching BLP exists, the project records and hashes that deployable BLP. Model keeper groups include the M2 plus matching SKIN and external ANIM companions. `definitive-stage` or the **Stage keepers** button re-verifies every hash, preserves logical client paths, and emits `definitive-set.crucible-patch.json`; it never modifies or deletes the processed source library.

For rapid triage, **Undecided only** hides recorded images, **Auto-advance** selects the next candidate, and the keyboard shortcuts are `K` keeper, `A` alternative, `R` review later, and `X` reject. Model keepers additionally resolve embedded BLP paths from the same provenance. A missing dependency or a texture found only in another patch source blocks Keeper rather than silently creating a mixed model. Replaceable body, hair, cape, fur, and creature-skin slots remain visible as required appearance/DBC bindings.

## DBC information, validation, comparison, and editing

```text
wowcrucible dbc info <file.dbc>
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

`validate` is non-recursive by default and fails when the selected folder has no top-level DBCs. Use `--recursive` intentionally for a directory tree. `--strict` treats raw fallback schemas as failures.

Promotion and clone/remap commands save semantic, reviewable operations. Strings are re-interned in the destination rather than copying invalid raw offsets. Additive promotion and clone/remap preserve existing IDs.

`itemset inspect` resolves the set's localized name, member item IDs, bonus thresholds, and—when `Spell.dbc` is supplied—spell names. `itemset clone` refuses to overwrite an existing set and requires an explicit old-to-new item ID map. `itemset effects` writes up to eight piece-count/spell pairs to a separate output DBC.

## MPQ listing, extraction, creation, and updates

```text
wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json] [--listfile=paths.txt]
wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible mpq create <archive.mpq> <files/folders...>
wowcrucible mpq update <small-patch.mpq> <files/folders...>
```

Filters accept text or `*`, `?`, and `**` globs. `--content-only` excludes `(listfile)`, `(attributes)`, and `(signature)` metadata. JSON output separates entry properties for scripts.

If an archive opens but only exposes StormLib `File000...` placeholders, Crucible reports those entries as unresolved names rather than calling the MPQ incompatible. Supply a compatible external path corpus with `--listfile=paths.txt` to recover known client paths before filtering or extraction.

`update` is transaction-safe but copies the archive first and refuses archives larger than 2 GiB. Treat large client/mod layers as immutable inputs and build a small manifest-driven patch instead.

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
wowcrucible db item-audit <host> <port> <user> <database> [--password-env=ENV_NAME] [--output=report.json]
wowcrucible db item-clone <host> <port> <user> <database> <source-id> <new-id> [--suffix=" Variant"] [--itemset=ID]
```

Server detection reads the live `worldserver.conf`; it does not accept `.dist` templates. Database passwords should be passed through an environment variable so they do not enter command history. DBC audits report the effective runtime value when the core applies an SQL overlay and can export an idempotent migration without applying it.

`item-audit` discovers the current schema instead of assuming one core revision. It checks vendor, creature/gameobject/item/mail/pickpocket/skinning/disenchant/fishing/spell/reference/prospecting/milling loot, character starting items, and every quest reward/choice slot that exists. The report calls an item **no known acquisition path**, not certainly unobtainable, because custom server scripts can grant items without a database relationship.

`item-clone` transactionally copies every writable column currently present in `item_template`, including custom columns unknown to Crucible, plus matching locale rows. It refuses an already-used destination ID. `--itemset=ID` assigns the copy to a set; omitting it preserves the source membership.

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
