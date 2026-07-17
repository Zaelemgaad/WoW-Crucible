# WoW Crucible

> [!WARNING]
> WoW Crucible is in very early development. Back up your files and validate generated client/server changes before using them on a live project.

WoW Crucible is an open-source World of Warcraft content-authoring toolkit. Its primary verified target is 3.3.5a (build 12340), with an extensible target-profile system for Classic 1.12.1, TBC 2.4.3, experimental Cata 4.3.4, and future community targets. The long-term goal is one coherent workflow for DBC/DB2 editing, patch creation, spells, items, creatures, races, classes, and current server-core integration.

Client formats and server integration are separate: a client-build profile declares file/archive capabilities, while a live schema adapter targets what the selected server actually exposes. The verified server contract targets current AzerothCore `master` and current TrinityCore branch `3.3.5`. See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## What works today

- Opens on a workflow-oriented Start Center with plain-language guided and advanced actions plus workspace-readiness checks.
- Selects built-in target profiles for Classic 5875, TBC 8606, WotLK 12340, and experimental Cata 15595; accepts external JSON profiles without recompilation.
- Connects to a live MySQL/MariaDB world database, keeps the password in memory only, and inspects actual content-table capabilities before enabling server writes.
- Detects an installed AzerothCore/TrinityCore workspace from its live `worldserver.conf`, automatically imports server DBC and world-database settings, and supports split Windows-folder/WSL-server launchers such as the bundled test workspace.
- Models core-specific DBC consumers, including AzerothCore SQL overlays and unused tables; an optional core-source path derives current mappings directly from `DBCStores.cpp` instead of relying on the built-in profile.
- Audits a server DBC against its live SQL overlay, decodes known GT class/level rows, identifies the effective server value, and exports an idempotent DBC-to-SQL migration preview without modifying the database.
- Provides an offline-capable guided item/weapon/armor creator with all ten stat slots and all five item-spell slots, named class/subclass/quality/slot/binding choices, a live WotLK-style tooltip, SQL preview/export, and schema-aware transactional insertion when a server is connected.
- Opens and saves 3.3.5a `WDBC`/`.dbc` files directly.
- Uses a virtual, double-buffered grid suitable for large files such as `Spell.dbc`.
- Includes its own complete 234-column `Spell.dbc` schema and accepts external build-12340 definitions for generic tables.
- Stages multiple open DBC files at once and switches between them without reloading.
- Decodes known 3.3.5a enum and bit-flag fields into readable names, with raw mode and lossless enum/flag editors.
- Resolves and safely extends DBC string tables.
- Searches all fields in parallel.
- Edits strings, bytes, integers, unsigned values, raw 32-bit values, and floats.
- Creates blank records, clones records with a new ID, and deletes selected records.
- Uses geometric record capacity and single-allocation bulk cloning for large creation batches.
- Supports cell-level undo/redo with `Ctrl+Z` and `Ctrl+Y` (structural operations begin a new history).
- Provides a grouped Spell Workspace for general properties, costs, three effects, localized text, visuals, and links.
- Saves atomically and creates `.bak` files before overwriting data.
- Accepts DBC and patch-builder drag-and-drop.
- Builds WotLK patch MPQs from edited DBCs or existing folder trees.
- Displays editable internal MPQ paths and preserves folder hierarchy.
- Opens existing MPQ patches and safely adds or replaces files while keeping a `.bak` copy.
- Remembers server `data\\dbc` and WoW client `Data` paths for future open, sync, and patch dialogs.
- Allows explicit selection of the WotLK build-12340 schema XML and remembers separate base/override DBC layers.
- Writes handled and fatal crash details to a portable `Logs` folder beside the application (also available through **Open Logs**), with `%LOCALAPPDATA%\\WoWCrucible\\Logs` as a fallback for read-only install locations.
- Browses large MPQs without loading file contents, filters paths instantly, and extracts selected files or whole archives in the background.
- Builds resumable client indexes with per-archive SHA-256 identities and reusable MPQ content catalogs, detects active/inactive locales, backup/custom subdirectory scopes and renamed build-12340 executables, marks anonymous hash-only entries explicitly, recovers names from local/cross-client path corpora, and resumes indexed extraction without rescanning giant archives or rewriting already-complete files.
- Includes a visual Client Inspector for indexing/resuming a whole installation, color-coded archive scopes, loose runtime/config/AddOn inventory, plain-language compatibility guidance, content-category summaries, direct archive browsing, and provenance-preserving extraction.
- Turns an extracted/effective client DBC directory into a reviewed client-to-server deployment plan: byte identity, row/field counts, current-core consumer, SQL-overlay warning, restart requirement, unresolved layer conflicts, portable JSON, and separate non-live staging trees for the patch and server DBC candidates.
- Plans client fusion against an explicit stock/effective base, omits base-identical files, deduplicates identical candidates, exports a reviewable plan, blocks path conflicts, recommends additive path/ID remapping instead of silent replacement, and stages only resolved changes into a small patch manifest.
- Ships a scriptable `wowcrucible.exe` CLI for DBC information and MPQ list/extract/create/update operations.
- Compares layered DBC directories as base-only, override-only, identical, or genuinely overridden, with cancellable semantic row/field comparison (decoded strings do not differ merely because their offsets moved).
- Promotes selected fields or complete rows from an override DBC into an output DBC by record ID, safely re-interns strings, and saves/reapplies semantic promotion manifests.
- Generates and applies additive-only DBC promotion manifests containing IDs absent from the baseline, preserving every existing record by construction.
- Clones selected source records into newly allocated IDs with a hash-bound old-to-new mapping, enabling additive ports of conflicting records without overwriting baseline identities.
- Follows a cloned record's foreign-key dependency, clones the referenced child records, and rewrites only the new parent records to the new child IDs.
- Saves portable patch manifests and builds fresh, tiny MPQs containing only listed DBC/UI changes.
- Treats an imported folder as the MPQ staging root and previews every editable source-to-archive mapping with suspicious-root warnings before building.
- Detects protected `Interface\\GlueXML` changes, warns that stock clients may reject them through `GLUEXML.TOC.SIG`, and can bind a patch manifest to the SHA-256 of a known-compatible `Wow.exe`.
- Enforces manifest allow/deny/required globs and exact entry counts, provides a dry-run source-to-archive listing, and verifies an existing MPQ for missing, unexpected, or size-mismatched content without rebuilding it.
- Refuses copy-update operations on archives larger than 2 GB; giant mod/client layers are immutable inputs, never working patch targets.

## Command line

```text
wowcrucible dbc info Spell.dbc
wowcrucible dbc rows CreatureModelData.dbc schema.xml 1332 1333 1334
wowcrucible dbc find CreatureDisplayInfo.dbc schema.xml ModelID 6000 6001 6002 [--count|--limit=100]
wowcrucible dbc validate "WotLK 3.3.5 (12340).xml" dbc-folder [--strict] [--recursive]
wowcrucible dbc compare base\Spell.dbc override\Spell.dbc "WotLK 3.3.5 (12340).xml"
wowcrucible dbc compare base\Spell.dbc override\Spell.dbc "WotLK 3.3.5 (12340).xml" --summary
wowcrucible dbc promote apply base\Spell.dbc override\Spell.dbc schema.xml selection.dbc-promotion.json output\Spell.dbc
wowcrucible dbc promote additions base\CreatureModelData.dbc mod\CreatureModelData.dbc schema.xml additions.json output\CreatureModelData.dbc
wowcrucible dbc clone-remap where base\CreatureDisplayInfo.dbc mod\CreatureDisplayInfo.dbc schema.xml ModelID 6000 6001 --manifest=display-map.json --output=merged\CreatureDisplayInfo.dbc
wowcrucible dbc copy-row base\CharSections.dbc source\CharSections.dbc schema.xml 4946 9000000 output\CharSections.dbc --set=VariationIndex=16
wowcrucible dbc set-row merged\CreatureDisplayInfo.dbc schema.xml 38924 output\CreatureDisplayInfo.dbc --set=ExtendedDisplayInfoID=9000000
wowcrucible server detect "C:\path\to\installed-server"
wowcrucible server inspect "C:\path\to\installed-server"
wowcrucible server bindings "C:\path\to\installed-server" [--source="C:\path\to\core-source"]
wowcrucible server dbc-audit "C:\path\to\installed-server" gtRegenMPPerSpt.dbc schema.xml [--source="C:\path\to\core-source"] [--all] [--migration=sync.sql]
wowcrucible db inspect 127.0.0.1 3306 admin acore_world --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible client index "C:\Games\WoW" client-index [--no-hash] [--listfile=known-paths.txt] [--client-exe=Wow.exe]
wowcrucible client extract client-index "Data\patch-W.MPQ" extracted "DBFilesClient\*.dbc" --resolved-only
wowcrucible client corpus combined-paths.txt first-client-index second-client-index [...]
wowcrucible client show client-index
wowcrucible client extract client-index "Data\patch-Z.mpq" extracted-layer [filter] [--resolved-only|--anonymous-only] [--overwrite] [--quiet]
wowcrucible client fusion extracted-stock extracted-mod-a extracted-mod-b [--output=fusion-plan.json] [--stage=fusion-review] [--all]
wowcrucible server client-plan "C:\path\to\installed-server" extracted-effective-dbc [--source=core-source] [--output=plan.json] [--stage=server-review]
wowcrucible mpq list patch.MPQ [filter] [--content-only] [--format=json]
wowcrucible mpq extract patch.MPQ output-folder [path-glob-or-text] [--quiet|--progress=N]
wowcrucible mpq create patch-W.MPQ file-or-folder [...]
wowcrucible mpq update patch-W.MPQ file-or-folder [...]
wowcrucible manifest create classless.json patch-W.mpq changed-files-folder [--allow=glob] [--deny=glob] [--count=N] [--client-exe=Wow.exe]
wowcrucible manifest list classless.json
wowcrucible manifest validate classless.json [existing-patch.mpq]
wowcrucible manifest build classless.json output-folder
```

An MPQ browser can only display names known to an internal or external `(listfile)`. Client indexing preserves every hash-only entry under its synthetic StormLib name, reports the unresolved count, and can retry name recovery from a corpus built across multiple clients. `--resolved-only` prevents synthetic names from being mistaken for reusable folder paths; `--anonymous-only` quarantines those unresolved payloads for signature/content inspection.
- Maps standalone DBCs to `DBFilesClient\\<name>.dbc`.
- Synchronizes a saved DBC into a selected server `data\\dbc` directory with backup.

Specialized editors will follow the principles in [docs/UX-PRINCIPLES.md](docs/UX-PRINCIPLES.md): grouped fields, named flags, human-readable references, change previews, validation, and duplicate-first creation.

## Run

Requirements: Windows x64 and the .NET 10 SDK.

```powershell
dotnet run --project src/WoWCrucible.App/WoWCrucible.App.csproj
```

Open a DBC directly:

```powershell
dotnet run --project src/WoWCrucible.App/WoWCrucible.App.csproj -- "C:\path\to\Spell.dbc"
```

## Build

```powershell
dotnet build WoWCrucible.slnx -c Release
```

The corpus test runner accepts a WDBX 12340 definition XML and a directory containing extracted 3.3.5a DBC files. Copyrighted game data is intentionally not included.

## Roadmap

1. Project-wide ID allocation, validation, and portable content projects.
2. Expand the Spell Workspace with named flags, searchable references, and related-table navigation.
3. Guided creature/NPC, vendor, loot, quest, race, and class creators on the live capability model.
4. DB2 support and complete corpus verification for additional client profiles.
5. Expand the revision-aware AzerothCore/TrinityCore DBC binding and audit engine into transactional multi-destination deployment plans.

## Contributing

The project is intentionally public at an early stage. Bug reports, workflow suggestions, format research, tests, and focused pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Legal

WoW Crucible is an independent community project and is not affiliated with or endorsed by Blizzard Entertainment. World of Warcraft and related names are trademarks of their respective owners. This project does not distribute Blizzard game data.

Source code is released under the [MIT License](LICENSE). StormLib retains its license under [third_party/StormLib/LICENSE](third_party/StormLib/LICENSE).
