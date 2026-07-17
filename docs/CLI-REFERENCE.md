# WoW Crucible CLI reference

The CLI executable is `wowcrucible.exe`. Run `wowcrucible --help` for the short command map or `wowcrucible <group> --help` for group-specific syntax. Paths containing spaces must be quoted.

Exit codes are consistent across workflows:

- `0`: completed successfully.
- `1`: invalid input, I/O failure, or another hard error.
- `2`: missing/invalid command syntax.
- `3`: work completed but unresolved conflicts, blocked assets, warnings that require review, or partial failures remain.

## Asset inspection and libraries

```text
wowcrucible asset inspect <model.m2|building.wmo>...
wowcrucible asset preview-info <wrath-model.m2>
wowcrucible asset workspace <new-output-folder> <files/folders...>
wowcrucible asset library-plan <source-folder> <library-folder> [--max-gb=2]
wowcrucible asset library-run <library-folder> <blpconverter.exe> [--workers=6]
wowcrucible asset library-status <library-folder>
```

`library-plan` recursively inventories loose BLP files and MPQs, but only reads archive file tables for MPQs below `--max-gb`. The library must be outside the source tree. The plan records source paths, archive identities, logical extraction sizes, entry counts, BLP counts, and skipped/failed archives.

`library-run` is resumable. It preserves each archive in a distinct provenance folder, preserves duplicate locale variants with suffixes, never overwrites an existing extracted file or PNG, copies loose BLPs into the library without modifying the source, converts in bounded parallel batches, verifies PNG output, writes a checkpoint after every archive, and generates `asset-catalog.csv` with Maps/UI/Characters/Creatures/Items/Textures/ModelsAndWorld/Audio/Other categories. A corrupt or unsupported individual archive entry is recorded in the checkpoint while the remaining entries continue. If the external converter rejects a BLP, Crucible retries its batch neighbors individually and writes the genuinely unsupported paths to `.blp-conversion-failures.txt` inside that provenance root.

Example:

```powershell
wowcrucible asset library-plan "G:\extras" "G:\Crucible-Extras-Processed" --max-gb=2
wowcrucible asset library-run "G:\Crucible-Extras-Processed" "C:\Tools\BLPConverter.exe" --workers=6
wowcrucible asset library-status "G:\Crucible-Extras-Processed"
```

Stopping `library-run` does not discard completed work. Run the same command again to resume past existing extraction/conversion outputs.

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
wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json]
wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N]
wowcrucible mpq create <archive.mpq> <files/folders...>
wowcrucible mpq update <small-patch.mpq> <files/folders...>
```

Filters accept text or `*`, `?`, and `**` globs. `--content-only` excludes `(listfile)`, `(attributes)`, and `(signature)` metadata. JSON output separates entry properties for scripts.

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
