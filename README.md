# WoW Crucible

The complete local-tool replacement contract is tracked in [`docs/TOOL-CONSOLIDATION-MATRIX.md`](docs/TOOL-CONSOLIDATION-MATRIX.md). Legacy tools are behavioral and format references during development; no worthwhile workflow is intentionally left as a permanent external dependency.

> [!WARNING]
> WoW Crucible is in very early development. Back up your files and validate generated client/server changes before using them on a live project.

WoW Crucible is an open-source World of Warcraft content-authoring toolkit. Its primary verified target is 3.3.5a (build 12340), with an extensible target-profile system for Classic 1.12.1, TBC 2.4.3, experimental Cata 4.3.4, and future community targets. The long-term goal is one coherent workflow for DBC/DB2 editing, patch creation, spells, items, creatures, races, classes, and current server-core integration.

Client formats and server integration are separate: a client-build profile declares file/archive capabilities, while a live schema adapter targets what the selected server actually exposes. The verified server contract targets current AzerothCore `master` and current TrinityCore branch `3.3.5`. See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## What works today

- Displays the executable's exact source revision in the main header and window title, with the full product version and running folder in its tooltip, so a deliberately frozen test copy cannot be mistaken for the current development build.
- Opens on a workflow-oriented Start Center with plain-language guided and advanced actions plus workspace-readiness checks.
- Includes a same-window, searchable Tool Inventory backed by the executable consolidation catalog. It currently assigns all 94 discovered workspace/Tools roots, reports missing expected roots, sorts new unassigned additions first, and exposes the same scan through text/JSON CLI output so a newly dropped legacy tool cannot be silently overlooked.
- Includes a same-window **Offline knowledge & field reference** that discovers and indexes the local Markdown wiki without executing its site generator, scripts, HTML, or remote links. The current corpus produces 670 documents and more than 5,000 heading-level search sections with language filtering, identifier-fragment-aware multi-term ranking, readable source excerpts, and exact source-path reveal/copy. F1 opens it using the selected DBC table and field as context, while SQL Studio can pass its selected table and focused field; the navigation, command palette, direct `--knowledge` launch, and `wowcrucible knowledge search/show` CLI share the same provider.
- Selects built-in target profiles for Classic 5875, TBC 8606, WotLK 12340, and experimental Cata 15595; accepts external JSON profiles without recompilation.
- Connects to a live MySQL/MariaDB world database, keeps the password in memory only, and inspects actual content-table capabilities before enabling server writes.
- Includes a same-window SQL Studio that discovers and locally switches among every database schema accessible to the configured login without overwriting the saved world-server profile. Its connection state and route to Server & SQL are always visible. It provides broad search, exact-column filters, server-side column sorting, selectable 50–500-row pages, switchable compact/complete-row-card views, every live row column, primary-key-safe create/edit/delete, and complete-row cloning where every insert field explicitly chooses VALUE, NULL, or OMIT. Double-clicking a supported world row opens its decoded guided editor; unsupported/custom tables remain complete live-schema row edits instead of hiding fields. Declared and recognized AzerothCore relationship navigation, read queries with virtualized compact or responsive complete-field result cards, selected-row clipboard copy, atomic current-result CSV/JSONL export, separately confirmed write statements, streaming complete-table CSV/JSONL export, dry-run-first transactional CSV import, and persistent favorites work for arbitrary item/creature/pet/quest/auth/character/other rows. Favorites reopen their recorded database automatically by the complete primary key—including binary keys—rather than a broad text search, can retain notes plus optional related DBC/DB2 and MPQ paths, and route known world tables into decoded guided editors without hiding custom columns. Its responsive dependency graph counts exact incoming/outgoing item, quest, loot, creature, gameobject, spell, trainer, appearance, and DBC-mirror edges; exact-column navigation prevents an unrelated matching number from impersonating a dependency. A complete-root-row JSON snapshot captures reviewable related rows, explicit truncation, and unresolved file-DBC edges without producing executable SQL. The schema/server page exposes exact table DDL and live indexes, guarded create/drop-index plans, and a native table designer that preserves complete server-normalized column clauses while planning add/modify/rename/drop-column, structure-clone, and table-rename DDL. Plans bind to the reviewed `SHOW CREATE TABLE` hash, report affected indexes/declared relationships, and refuse stale targets. Apply must atomically save the exact preimage before sending DDL to MySQL, then upgrades that same artifact with the verified after-state beneath the running app's `Backups\SqlSchema` folder; an interrupted operation therefore cannot erase its reviewed recovery evidence. The same page also covers views/triggers/procedures/functions/events with exact `SHOW CREATE` definitions and atomic export, guided single-SELECT view creation/replacement, exact-object DROP review, scheduled-event state review, visible database processes with confirmed connection termination, permission-aware account metadata and exact grants, server-discovered privilege selection, reviewed create/password/lock/unlock/grant/revoke/drop account actions with parameterized redacted new passwords, and read-only INNER/LEFT/RIGHT joins over recognized relationships with unambiguous source/target column names.
- SQL Studio runs up to 32 independently validated read statements as one sequential batch and keeps every differently shaped result selectable, copyable, and individually exportable. Its quote/comment-aware splitter rejects mixed read/write batches and `SELECT` file output. Successful reads feed a portable, bounded local history with retained bookmarks, labels, database identity, result counts, and same-window reload; the CLI exposes the same batch engine in text or JSON.
- SQL favorites are a searchable review workspace rather than a flat bookmark dump. Multi-term search covers database, table, complete key, label, notes, DBC/DB2, and MPQ context; labels/notes/paths remain editable after saving; and visible or selected favorites can be rechecked against the live server. Verification groups work by schema, reuse one connection per schema, require the recorded key to still equal the table's complete primary key, and distinguish live rows, missing rows, schema changes, and connection failures. `wowcrucible db favorites ... --verify` exposes the same non-mutating check for automation.
- Starts, stops, and restarts auth/world servers natively from the shared Server & SQL workspace. WSL shutdown sends `SIGINT` and waits for graceful completion; a locally launched worldserver receives `saveall` before shutdown. Crucible does not execute the workspace's PowerShell wrapper scripts or force-kill an unowned server process.
- Keeps DBC editing, Items & Sets, MPQ work, Assets & Compare, Server & SQL, dialogs, and the CLI guide inside one splitter-driven Avalonia window; feature navigation no longer creates a pile of child windows or depends on fixed panel widths.
- Adds a responsive same-window command palette on `Ctrl+K` with one shared searchable vocabulary for every native workspace and common file/editor action. Multi-term aliases intentionally match user intent such as `cut items`, `Heidi favorites`, `MPQ merge`, `model viewer`, and `server restart`; arrow keys and Enter route directly without opening another window. `wowcrucible tools commands` exposes the identical ranked catalog in text or JSON, and `WoWCrucible.Desktop-latest.exe --command-palette="search words"` opens directly into a filtered palette.
- Reads the selected installed server's `worldserver.conf` (including the split Windows/WSL test layout), verifies the detected database, and shares that in-memory session with item and recovery workflows.
- Captures a legacy world database through a SELECT-only streaming snapshot, then performs an entirely offline baseline-to-edited audit with field-level additions, edits, removals, domain grouping, source hashes, and explicit unattributed mode when no stock baseline is available. A matching-core audit can be compared with the connected target in the same responsive workspace: target-bound plans classify every selected row as ready, already applied, conflicting, or blocked; removals stay excluded unless explicitly selected; transactional apply rechecks typed preimages under row locks and emits the exact receipt required for stale-safe rollback. Opt-in collision remapping allocates above the live target maximum and visibly rewrites recognized selected references; cross-core field translation and automatic dependency-closure selection remain pending.
- Searches the live world schema plus the configured server `CharStartOutfit.dbc`, `AreaTable.dbc`, and `Spell.dbc` for items with no known vendor, achievement reward, source-reachable loot, usable or system-granted quest reward/start item, SQL/DBC starting item, prospecting, milling, disenchanting, fishing, spell-loot, or causally reachable create-item spell path. Creature and gameobject loot IDs must be owned by a valid template; mail loot must be owned by a level, achievement, arena-season, or usable-quest mail source; container/prospecting/milling/disenchant pools require a reachable input item; spell loot requires a reachable spell; and reference pools count only when reached from one of those owners. Script-created boss caches remain valid because template ownership does not require a static spawn. Trainer, character-start, usable quest-reward, acquired-item use/learn, and nested learn/trigger-spell edges seed the spell graph; merely existing unused spells do not automatically make their output obtainable. Nonzero loot-reference control rows, orphaned loot owners/pools, disabled quests, and orphaned quest-template rewards are not confused with playable acquisition. The visual catalog loads automatically when its tab has a verified SQL profile, explicitly switches among no-known-path, known-path, and all rows, shows the accepted or rejected evidence directly on every row, and reports known/no-path/total counts. Its deliberately broad no-known-path pool remains complete by default but is broken into honest review buckets for other/manual review, deprecated/test/developer records, and NPC/Monster equipment so thousands of internal rows do not impersonate thousands of polished cut player items. Review triage uses both readable names and schema-adaptive evidence: for example, stock WotLK Artifact-quality rows such as item 17 receive an explicit internal/test/cut signal, while a reward reachable only through a deprecated disabled quest pushes item 17802 into the same review bucket. Those signals never fabricate or erase acquisition evidence. Exact-ID search uses an indexed complete catalog, accepts plain, grouped, or `#`-prefixed forms, bypasses classification/review filters, always pins an existing row while stating its classification, and can pin a review batch such as `#17 #17802` together without confusing it with the grouped single ID `17,802`. A selected result opens immediately in decoded item editing, complete-SQL editing, or persistent favoriting with browsable optional DBC/MPQ context. SQL favorites can reopen directly in a supported decoded editor without hiding custom columns. Reports remain exportable from the CLI; repeat `--id` or use `--ids=17,17802` for bounded exact-row proof instead of printing the entire no-path catalog.
- Clones a complete item to a new ID transactionally, preserving every current/custom `item_template` column and locale row, with optional item-set reassignment and strict no-overwrite behavior.
- Inspects and clones `ItemSet.dbc` rows with explicit member-ID remapping, resolves set-bonus spell names, and edits all eight bonus slots into a separate output DBC.
- Detects an installed AzerothCore/TrinityCore workspace from its live `worldserver.conf`, automatically imports server DBC and world-database settings, and supports split Windows-folder/WSL-server launchers such as the bundled test workspace.
- Models core-specific DBC consumers, including AzerothCore SQL overlays and unused tables; an optional core-source path derives current mappings directly from `DBCStores.cpp` instead of relying on the built-in profile.
- Audits an edited server DBC against its live SQL overlay, decodes known GT class/level rows, and identifies the effective server value. The same-window **DBC + SQL deployment** path freezes the exact source DBC, schema, server-file hash, database identity, and SQL row pre-image into a portable bundle with read-only audit SQL, idempotent migration, exact rollback SQL, a one-file client MPQ manifest, and optional collision-safe AzerothCore module export. Apply refuses changed inputs/targets, backs up and atomically replaces the server DBC while the SQL transaction is open, verifies both destinations before commit, and emits a rollback receipt that also refuses to overwrite later work.
- Provides an offline-capable guided item/weapon/armor creator with all ten stat slots and all five item-spell slots, named class/subclass/quality/slot/binding choices, a live WotLK-style tooltip, SQL preview/export, and schema-aware transactional insertion when a server is connected. Its five item-spell lines use a one-time cached `Spell.dbc` catalog for real names, subtexts, descriptions, and aura descriptions while preserving runtime `$` tokens rather than inventing values. Its same-window display resolver follows the live SQL `displayid` through the real 25-field build-12340 `ItemDisplayInfo.dbc`, exposes every model/model-texture/icon/wear-texture/geoset/helmet/visual/particle/sound field, maps canonical and gender-suffixed client paths, finds every matching processed provenance source without a library-wide recursive scan, and loads a selected WotLK M2/SKIN directly. Standalone weapons render as their own model; armor can be equipped on a selected playable character with decoded skin, face, facial-feature, underwear, hair texture, and hair geosets from the same shared CharSections pipeline as Asset Compare, followed by exact eight-region wear mapping, inventory-aware equipment geosets, and explicit character/item provenance choices. Items & Sets links straight to complete-row SQL Studio/Favorites and the byte-safe MPQ merge workspace.
- Provides a native guided creature/NPC creator with four display choices, named faction/type/rank/class/service controls, combat and movement fields, embedded WotLK M2/SKIN preview, vendor inventory and creature-loot child rows, exact SQL preview/export, current normalized or legacy embedded model-column adaptation, and strict transactional no-overwrite insertion. It reserves collision-checked creature IDs from the active project; reserving from a loaded row deliberately turns its decoded fields into a new variant INSERT draft.
- Provides a first-class same-window **Pets & companions** route for complete `pet_levelstats`, `pet_name_generation`, `pet_name_generation_locale`, and `spell_pet_auras` authoring. The schema-adaptive editor preserves every current/custom column and exact composite identity, decodes first/second generated-name halves, validates WotLK's three spell effect slots and pet damage ranges, and resolves creature/spell IDs through the shared readable reference picker. Its bulk curve workspace clones a real complete source curve instead of inventing missing levels, independently scales health/mana/armor/all attributes/damage, retains custom columns, previews missing-only or explicitly acknowledged exact-range updates, binds every target pre-image/schema fingerprint, and applies transactionally without deleting rows. The adjacent family comparison reads every numeric live/custom stat, plots exact two-curve level series without animation, reports missing coverage, and calculates endpoint growth plus normalized per-level deltas. A responsive companion tab resolves `creature_template`/`creature_template_model` through `CreatureDisplayInfo.dbc` and `CreatureModelData.dbc`, exposes each extracted provenance explicitly, and renders source and target WotLK M2/SKIN models with their same-provenance creature textures and base-appearance geosets. The talent/ability workspace follows live template spells, client `CreatureSpellData`, family skill lines, rank/supersession chains, hunter-pet talent tabs/prerequisites, and global or creature-specific pet auras; its complete searchable nodes and evidence edges are never reduced to what fits in the responsive neighborhood drawing. SQL Studio rows and favorites route into the decoded editor; `--pets` opens row editing, `--pet-curve` opens curves/comparison, `--pet-graph` opens the graph, while `db pet-preview`, `db pet-curve`, `db pet-compare`, and `db pet-graph` provide CLI parity.
- Provides a native guided gameobject creator covering all 36 WotLK types. Data0–Data23 are relabeled from the current AzerothCore type union without hiding any raw field; template identity, optional world spawn, chest/fishing-hole loot, quest starter/ender links, portable JSON drafts, SQL export, decoded SQL Studio handoff, and M2/SKIN preview are kept in one transactional workspace. Project allocation checks every live `gameobject_template.entry` and switches a loaded template into a new variant instead of accidentally updating the source.
- Provides a native guided quest workspace that represents all 105 portable WotLK `quest_template` fields and every column discovered in the connected schema. Quest type and flags are decoded while raw values remain editable; objective, requirement, reward, text, POI, creature-giver, and gameobject-giver data can be planned, exported, inserted, or updated transactionally without hiding custom columns. Shared project reservation checks every live quest ID, rewrites the visible primary key, and preserves the complete loaded quest as a new INSERT draft.
- Provides a native complete-field Behaviors & Dialogue workspace for gossip menus/options, all 90 NPC-dialogue fields, normalized and legacy trainers, conditions, and all 31 SmartAI fields. Current AzerothCore types/events/actions/targets are decoded beside editable raw values, composite identities are protected, portable drafts and exact SQL are exportable, and live rows route directly from SQL Studio without opening another window.
- Opens and saves 3.3.5a `WDBC`/`.dbc` files directly, and opens/edits/saves Cataclysm fixed-layout `WDB2`/`.db2` tables through the same staged editor, history, structured import/export, and CLI workflows. WDB2 header metadata, extended ID/string-length maps, and copy tables are preserved; structural edits are conservatively locked when dependent side tables are present.
- Parses WoWDBDefs `.dbd` build ranges using full expansion versions, expands logical arrays/localized strings to exact physical client-table columns, and audits an entire DBC/DB2 directory in a responsive same-window schema workspace with optional WDBX XML cross-checking. Intentional zero-byte placeholders are reported separately from corrupt files, while a folder selected one level too high fails with a likely nested-folder hint instead of claiming a zero-result success.
- Uses a virtual, double-buffered grid suitable for large files such as `Spell.dbc`.
- Includes its own complete 234-column `Spell.dbc` schema and accepts external build-12340 definitions for generic tables.
- Exports a loaded DBC from a modular same-window workspace to atomic CSV, JSONL, or JSON with selectable physical columns, exact/ranged record-key filters, invariant numeric values, decoded strings by default, optional raw string offsets, and explicit `$recordKey`/`$rowIndex` identity metadata. The matching same-window import workspace builds a complete non-mutating preview on an isolated copy, updates by stable key, blocks physical-key rewrites and duplicate targets, makes appending explicit, preserves virtual GT identities, refuses stale plans, and stages the exact batch into the open document for normal review/save. The CLI exposes both workflows; import is dry-run unless an output is explicitly supplied.
- Stages multiple open DBC files at once and switches between them without reloading.
- Decodes known 3.3.5a enum and bit-flag fields into readable names, with raw mode and lossless enum/flag editors.
- Resolves and safely extends DBC string tables.
- Searches all fields in parallel.
- Edits strings, bytes, integers, unsigned values, raw 32-bit values, and floats.
- Creates blank records, clones records with a new ID, and deletes selected records.
- Uses geometric record capacity and single-allocation bulk cloning for large creation batches.
- Supports cell-level undo/redo with `Ctrl+Z` and `Ctrl+Y` (structural operations begin a new history).
- Provides a grouped Spell Workspace for general properties, costs, three effects, localized text, visuals, and links.
- Provides that Spell Workspace natively inside the Avalonia DBC editor, with decoded field meanings and the same staged undo/atomic-save path as direct cell edits. Its live SQL-precedence tab reports whether AzerothCore uses the file record or a complete `spell_dbc` replacement, compares exactly the fields consumed by `SpellEntryfmt`, discovers related spell/proc/script/trainer/item/quest/creature/SmartAI/condition rows across the connected schema, and opens complete primary-keyed rows in same-window SQL Studio. Project-aware cloning reconciles every live `spell_dbc.ID`, the active staged `Spell.dbc` including unsaved clones, and prior Spell/Mount reservations before cloning the selected row under the exact allocated ID. A distinct mount-spell draft action records intent while sharing the Spell namespace; it does not pretend the mount aura, visuals, trainer/item acquisition, and client patch are already complete. Shared same-window reference pickers search readable IDs/names from live SQL and the configured DBC corpus for spells, items, creatures, quests, gameobjects, cast times, durations, ranges, rune costs, visuals, icons, and spell difficulty records; returning from a picker restores the exact guided editor instead of discarding it.
- Saves atomically and creates `.bak` files before overwriting data.
- Accepts DBC and patch-builder drag-and-drop.
- Builds WotLK patch MPQs from edited DBCs or existing folder trees.
- Displays editable internal MPQ paths and preserves folder hierarchy.
- Opens existing MPQ patches and safely adds or replaces files while keeping a `.bak` copy.
- Merges multiple small patch MPQs from the picker, drag-and-drop, or CLI without mutating sources: duplicate paths are verified by SHA-256 and byte-for-byte comparison, exact copies are stored once, different-byte path conflicts block output unless earlier/later archive precedence is explicitly selected, and short flat temporary payload names prevent deeply nested projects from tripping StormLib's Windows path limit.
- Calculates recursive client-asset closure directly inside the MPQ workspace: WotLK M2 view SKINs/embedded BLPs, WMO groups/textures/doodad M2s, and ADT/WDT terrain textures/models/WMOs are followed transitively. Missing, invalid, and cross-provenance edges block patch staging until the user explicitly selects a physical candidate; replaceable M2 slots remain visible as DBC/SQL bindings rather than invented files. An optional complete target-client index resolves effective root/locale MPQ precedence, probes anonymous entries by exact requested path, SHA-256 compares extracted effective bytes, omits byte-identical base assets, retains different bytes as intentional overrides, and refuses to inherit an ambiguously ordered path. Format-4 manifests bind every inherited path/archive/hash to a deterministic client-index fingerprint, and direct builds emit that target-bound companion manifest automatically. A clean minimal closure stages directly into the manifest-driven patch builder.
- Installs verified patch MPQs into a selected client and deletes that client's exact `Cache` folder only after a successful update; GUI builds written directly into the configured client `Data` folder do the same automatically.
- Remembers server `data\\dbc` and WoW client `Data` paths for future open, sync, and patch dialogs.
- Allows explicit selection of the WotLK build-12340 schema XML and remembers separate base/override DBC layers.
- Keeps normal use silent: no startup, success, or activity logs, with only unhandled fatal diagnostics retained when possible. Opt-in **Devbug Mode** adds a live structured terminal and detailed asynchronous session log, retains only the newest three sessions, and never records database passwords. See [docs/DEVBUG-MODE.md](docs/DEVBUG-MODE.md).
- Houses Crucible-owned settings, profiles, and logs in organized folders beside the executable. Read-only installations fall back to `%LOCALAPPDATA%\\WoWCrucible`, and every Devbug session identifies the effective data root.
- Browses large MPQs without loading file contents through a lazy folder/breadcrumb view plus a separate global flat-path search, preserves visible locale variants, extracts selected files/folders or the current folder recursively in the background, and reports unresolved hash-only names honestly. Compressed app-local indexes beside the executable are identity-bound to archive/listfile path, size, and timestamp, atomically rebuilt after changes/corruption, and capped so giant archives reopen without repeated file-table scans or unbounded cache growth.
- Opens later-client CASC storage read-only beside MPQ in the same archive workspace through a reproducibly built, commit-pinned MIT CascLib provider. Folder and flat search retain FileDataId/key-only rows instead of hiding unknown names, external listfiles remain optional path hints, and selected files/folders extract in the background. Crucible never mutates CASC storage and never downloads missing CDN payloads implicitly.
- Builds content-first asset libraries where provenance is inserted immediately before each file, keeping every archive and imported-folder version of the same Character/UI/World directory adjacent instead of splitting sources into separate trees. New loose-file scans and extracted-folder imports write directly into that layout.
- Consolidates older `Loose\Content` libraries into the same content-first tree with a read-only dry run, strict all-or-nothing blocking for non-identical destination conflicts, byte-for-byte duplicate verification, and a durable apply journal.
- Visually compares PNG assets by content directory rather than filename: a versioned compact sidecar opened the current 162.5 MB test catalog in roughly 0.15 seconds, includes model-only M2/SKIN paths, falls back safely when stale/corrupt/unwritable, and never replaces the CSV as the durable source. Path/source/name filters, selectable filename/source/file-size sorting, cancellable SHA-256 plus byte-for-byte exact-copy grouping, 96-image lazy pages, two arbitrary comparison slots, synchronized pixel zoom/pan, dimensions, provenance, and direct Explorer reveal remain non-destructive.
- Opens **Assets & compare** inside the existing Crucible window, with an explicit return-to-editor action, resizable split panes, wrapped tool groups, and scrollable configuration headers so controls remain reachable when the window is resized. M2 discovery stays idle during ordinary PNG browsing, starts automatically for model-only paths or explicitly for Live model preview, and searches only bounded direct provenance scopes instead of recursively probing the whole asset library.
- Opens a native **Texture Lab** in that same window. It validates and live-previews BLP1/BLP2 mips, decodes palette/JPEG/raw-BGRA/DXT1/DXT3/DXT5 to PNG, encodes Wrath-compatible BLP2 from common images, chooses alpha compression automatically, identifies salvageable corrupt tail mips as warnings, and validates whole folders without any BLPConverter executable. A narrowly detected legacy-exporter defect with `0xFFFFFFFF` offsets and cumulative mip ends is recovered only when it yields a bounded valid chain, with an explicit warning.
- Opens a native **Maps & World** workspace in the same window for WotLK ADT, WDT, and WDL files. It accepts file-picker, direct-open, CLI, and drag/drop input; validates reversed chunk headers and bounds; decodes WDT `MAIN` and WDL `MAOF` 64×64 presence grids; decodes every ADT `MCNK` 16×16 coordinate, area, holes, base height, bounded `MCVT` height grid, ordered MTEX catalog, complete MCLY texture layers, and packed-4-bit/big-8-bit/RLE MCAL alpha maps; inventories referenced terrain textures/models/WMOs; and provides a responsive clickable height-colored grid without launching Noggit. Single/Ctrl-multi/all-present terrain selection can preview a finite whole-cell height offset. True vertex brushes edit exact MCVT floats with click-positioned tile-local center/radius, raise/lower, absolute-height flatten, neighbor-aware smooth, repeatable seeded noise, and independent linear/smooth/constant falloff. Selected cells expose decoded layer slots/flags/alpha/effects and can reassign a fixed-width layer to any existing MTEX texture. They can also append a new client-relative BLP to MTEX and add a matching MCLY/MCAL layer while enforcing Wrath's four-layer limit, preserving packed-versus-8-bit storage, rebuilding MHDR/MCIN and nested MCNK offsets, and re-parsing the structurally rewritten tile. The same click-positioned radial brush paints an existing additional layer toward alpha 0–255 with opacity/falloff and optional selected-cell restriction. Alpha writes preserve the existing encoding and slice length; an RLE result that cannot fit is refused rather than shifting MCNK offsets. Every edit path writes a separate hash/preimage-bound ADT atomically; the original is never an output target, affected cells/layers/maps are re-parsed, and portable receipts record exact pre/post identity.
- Decodes ADT/WDT object placement tables instead of collapsing them to filenames: MMID→MMDX/MDDF doodad M2s and MWID→MWMO/MODF world models preserve each unique ID, finite position/orientation, flags, and raw scale; WMO extents and doodad/name sets are retained as well. Maps & World lists every placed M2 and WMO instance separately, resolves its exact client path through the provenance selector, and opens the chosen native M2/SKIN or WMO geometry in the same preview tab. The selected instance's stored orientation and fixed-point scale are applied through an isolated scene transform that resets for every model, so map placement cannot leak into character, geoset, animation, or equipped-item previews. Multi-instance world-scene composition remains a separate rendering stage.
- Builds resumable client indexes with per-archive SHA-256 identities and reusable MPQ content catalogs, detects active/inactive locales, backup/custom subdirectory scopes and renamed build-12340 executables, marks anonymous hash-only entries explicitly, recovers names from local/cross-client path corpora, and resumes indexed extraction without rescanning giant archives or rewriting already-complete files.
- Includes a visual Client Inspector for indexing/resuming a whole installation, color-coded archive scopes, loose runtime/config/AddOn inventory, plain-language compatibility guidance, content-category summaries, direct archive browsing, and provenance-preserving extraction.
- Turns an extracted/effective client DBC directory into a reviewed client-to-server deployment plan: byte identity, row/field counts, current-core consumer, SQL-overlay warning, restart requirement, unresolved layer conflicts, portable JSON, and separate non-live staging trees for the patch and server DBC candidates.
- Plans client fusion against an explicit stock/effective base, omits base-identical files, deduplicates identical candidates, exports a reviewable plan, blocks path conflicts, recommends additive path/ID remapping instead of silent replacement, and stages only resolved changes into a small patch manifest.
- Ships a scriptable `wowcrucible.exe` CLI across DBC, MPQ, manifests, clients, installed servers, live database items, item sets, and resumable bulk asset-library workflows, plus built-in group help and a complete shipped reference.
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
- Begins a native modern-to-3.3.5 asset conversion pipeline in the same Avalonia window: recursively drag/drop M2/WMO files and folders, safely identify MD20/MD21 M2 and chunked WMO structures, validate chunk bounds, inventory companion skins/animations/WMO groups and modern FileDataID chunks, and preview already-compatible Wrath M2/SKIN and grouped WMO geometry. Dedicated conversion workspaces are published atomically with separately namespaced SHA-256-verified source/dependency snapshots; their machine-readable reports can be moved, reopened, and fully reverified. Modern output writing remains blocked until each translated structure can be validated instead of silently discarded.
- Embeds native 3D preview directly in Crucible: validated WotLK MD20 vertices, both native UV streams, companion SKIN topology, material units, complete texture/coordinate/weight/transform lookup ranges, and compact per-submesh render batches drive an interactive mesh with drag rotation and wheel zoom. Asset Compare automatically searches the selected path and its nearest useful parent, labels every M2 as ready/missing-skin/requires-conversion/invalid, switches quickly among sources, and resolves every texture definition used by visible material stages from the selected provenance layer. Common legacy two-stage WotLK Opaque/Mod/Mod2x/Add combiners render in one isolated material layer so the model blend is applied once; environment-mapped, explicit, or three-plus-stage shaders are visibly labeled as first-stage fallbacks instead of being presented as exact. For playable race models it reads the configured server's real `CharSections.dbc`, exposes skin, face, facial-hair, and hair choices, composes the selected appearance into the native body atlas, and binds separate hair material. The same-window geoset inspector names every group and offers DBC-driven, naked, exact manual, and deliberately stacked diagnostic modes. The renderer validates bones and attachment lookups and can overlay native helmet/shoulder/weapon/sheath/effect/vehicle bind points. Item Creator mounts resolved equipment on those animated bind points, including paired shoulders, without mixing provenance. The model browser now exports the exact visible/current scrubbed pose to OBJ/MTL plus resolved PNG textures; a source-hashed JSON receipt retains geoset, animation, and WoW pass metadata that OBJ cannot express. CLI `asset model-export` provides the repeatable route. Remaining explicit/environment shader variants and particles remain fidelity stages.
- Renders native WotLK WMO roots and visual groups directly inside Modern asset conversion, GameObjects, and Maps & World without opening another window. The bounded parser validates root/group chunk structure, material and triangle ranges, flattens render batches, resolves BLP materials from the same processed provenance when possible, exposes per-group selection and wireframe diagnostics, and reports missing/collision-only groups instead of hiding incomplete input. Maps turns each ADT/WDT WMO reference into a provenance-aware selector backed by the configured processed library: a unique or same-map-provenance root is selected automatically, while ambiguous layers require an explicit choice. It never silently mixes conflicting archive layers.
- Adds native Wrath character animation sampling to the same embedded preview: version-264 sequences, alias chains, main-file/global tracks, external `.anim` payloads, packed quaternions, parented bone matrices, four-weight vertex skinning, animated attachment points, and animated bounds. The responsive in-window strip selects a sequence, plays/pauses, and scrubs exact milliseconds. Head, weapon, shield, back, quiver, and paired shoulder models inherit the selected native attachment bone's live world transform, so equipment follows the animated character instead of freezing playback or detaching. Every visible material-unit pass is preserved in priority order, with native opaque, alpha, additive, additive-alpha, modulate, and modulate-x2 blend handling plus unlit flags. Common legacy two-texture combiners route each stage through its declared primary or secondary UV stream; unsupported shader families remain explicit fidelity findings rather than silently discarded layers.
- Maintains a persistent `Projects\Definitive-Set.crucible-assets.json` beside an asset library. Texture or model decisions can be marked Keeper, Alternative, Rejected, or Review with category and notes. Records preserve provenance, hashes, logical client destinations, deployable BLP sources, and M2 SKIN/animation companions. Keeper staging re-verifies hashes and produces a tiny manifest-driven patch tree without changing the processed library.
- Resolves model dependency closure before accepting a deployable keeper. Same-source SKIN, ANIM, and embedded BLP paths are included automatically; missing paths and cross-source-only matches block staging, while replaceable character/creature texture slots are reported as explicit future DBC/appearance bindings.
- Creates portable content-project folders with separate Assets, DBC, SQL, Manifests, Reports, and Staging outputs plus a persistent ID registry. The same-window **Projects & shared IDs** workspace creates/opens projects, selects target profiles and asset libraries, shows every authoritative occupancy source and reservation, and scans every mapped live SQL identity plus matching DBC stable key before allocation. It refuses incomplete coverage, enforces WotLK's 31-ID race/class ceiling, shares one collision namespace between mount and spell IDs, and accepts explicit occupied lists for custom domains. Items, spells/mount drafts, creatures, gameobjects, and quests reserve and fill their identities from this shared registry before any SQL or saved-DBC write.

## Command line

See the complete copy-paste-oriented [CLI reference](docs/CLI-REFERENCE.md) for command groups, options, exit codes, safety behavior, and resumable bulk asset-library processing.

```text
wowcrucible asset inspect modern-model.m2 [building.wmo ...]
wowcrucible asset preview-info extracted-wrath-model.m2
wowcrucible asset model-export extracted-wrath-model.m2 visible-model.obj --animation=0 --time=500
wowcrucible asset wmo-preview-info extracted-wrath-root.wmo --groups
wowcrucible asset workspace native-conversion-project modern-assets-folder [more-files ...]
wowcrucible dbc info Spell.dbc
wowcrucible dbc rows CreatureModelData.dbc schema.xml 1332 1333 1334
wowcrucible dbc export Spell.dbc schema.xml spell-audit.jsonl --columns=ID,Name_Lang[enUS],Effect[0] --ids=133,116
wowcrucible dbc import Spell.dbc schema.xml spell-edits.jsonl --output=Spell-edited.dbc
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
wowcrucible server dbc-audit "C:\path\to\installed-server" edited\gtRegenMPPerSpt.dbc schema.xml [--source="C:\path\to\core-source"] [--all] [--migration=sync.sql] [--bundle=review-bundle]
wowcrucible server dbc-apply "C:\path\to\installed-server" review-bundle
wowcrucible server dbc-rollback "C:\path\to\installed-server" review-bundle\applications\...\deployment-receipt.json
wowcrucible server dbc-module-export review-bundle "C:\path\to\module-root"
wowcrucible db inspect 127.0.0.1 3306 admin acore_world --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db rows 127.0.0.1 3306 admin acore_world item_template --filter=entry=17802 --sort=entry --format=json
wowcrucible db table-admin 127.0.0.1 3306 admin acore_world item_template --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db process-list 127.0.0.1 3306 admin acore_world --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db account 127.0.0.1 3306 admin acore_world grants acore localhost --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db account 127.0.0.1 3306 admin acore_world grant editor localhost "SELECT,INSERT,UPDATE,DELETE" --table=item_template --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db join 127.0.0.1 3306 admin acore_world crucible_item_vendor --type=LEFT --limit=100 --run --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db query 127.0.0.1 3306 admin acore_world reviewed-query.sql --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db export 127.0.0.1 3306 admin acore_world item_template item_template.csv --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db import 127.0.0.1 3306 admin acore_world item_template reviewed-items.csv --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db draft-template gameobject new-gameobject.json
wowcrucible db content-plan 127.0.0.1 3306 admin acore_world gameobject new-gameobject.json --output=review.sql --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db snapshot 127.0.0.1 3306 admin old_world old-world.crucible-db-snapshot --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db snapshot-inspect old-world.crucible-db-snapshot
wowcrucible db recovery-audit old-world.crucible-db-snapshot old-world.crucible-db-audit --baseline=matching-stock.crucible-db-snapshot
wowcrucible db recovery-inspect old-world.crucible-db-audit
wowcrucible db sync-plan 127.0.0.1 3306 admin acore_world old-world.crucible-db-audit target-sync.json --include=item_template
wowcrucible db sync-inspect target-sync.json --sql=target-sync-preview.sql
wowcrucible db sync-apply 127.0.0.1 3306 admin acore_world target-sync.json target-sync-receipt.json --apply
wowcrucible db sync-rollback 127.0.0.1 3306 admin acore_world target-sync-receipt.json --apply
wowcrucible client install-patch patch-Z.MPQ "C:\Games\WoW" [--name=patch-Z.MPQ]
wowcrucible client clear-cache "C:\Games\WoW"
wowcrucible client index "C:\Games\WoW" client-index [--no-hash] [--listfile=known-paths.txt] [--client-exe=Wow.exe]
wowcrucible client extract client-index "Data\patch-W.MPQ" extracted "DBFilesClient\*.dbc" --resolved-only
wowcrucible client corpus combined-paths.txt first-client-index second-client-index [...]
wowcrucible client show client-index
wowcrucible client extract client-index "Data\patch-Z.mpq" extracted-layer [filter] [--resolved-only|--anonymous-only] [--overwrite] [--quiet]
wowcrucible client fusion extracted-stock extracted-mod-a extracted-mod-b [--output=fusion-plan.json] [--stage=fusion-review] [--all]
wowcrucible server client-plan "C:\path\to\installed-server" extracted-effective-dbc [--source=core-source] [--output=plan.json] [--stage=server-review]
wowcrucible asset texture-decode texture.blp texture.png [--mip=0]
wowcrucible asset texture-encode texture.png texture.blp [--format=auto] [--quality=best]
wowcrucible asset texture-validate texture-library --recursive
wowcrucible asset library-import extracted-archive asset-library provenance-name [--workers=6]
wowcrucible asset library-consolidate asset-library [--apply]
wowcrucible asset library-catalog asset-library
wowcrucible mpq list patch.MPQ [filter] [--content-only] [--format=json] [--listfile=paths.txt]
wowcrucible mpq tree patch.MPQ [internal-folder] [--format=text|json] [--listfile=paths.txt]
wowcrucible mpq extract patch.MPQ output-folder [path-glob-or-text] [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible mpq extract-folder patch.MPQ internal-folder output-folder [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible mpq create patch-W.MPQ file-or-folder [...]
wowcrucible mpq update patch-W.MPQ file-or-folder [...]
wowcrucible mpq merge patch-merged.MPQ patch-A.MPQ patch-B.MPQ [...] --conflicts=block
wowcrucible casc list client-root [filter] [--local-only] [--format=json] [--listfile=paths.txt]
wowcrucible casc tree client-root [internal-folder] [--local-only] [--format=json] [--listfile=paths.txt]
wowcrucible casc extract client-root output-folder [filter] [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible casc extract-folder client-root internal-folder output-folder [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible manifest create classless.json patch-W.mpq changed-files-folder [--allow=glob] [--deny=glob] [--count=N] [--client-exe=Wow.exe]
wowcrucible manifest list classless.json
wowcrucible manifest validate classless.json [existing-patch.mpq]
wowcrucible manifest build classless.json output-folder
```

An MPQ browser can only display names known to an internal or external `(listfile)`. Client indexing preserves every hash-only entry under its synthetic StormLib name, reports the unresolved count, and can retry name recovery from a corpus built across multiple clients. `--resolved-only` prevents synthetic names from being mistaken for reusable folder paths; `--anonymous-only` quarantines those unresolved payloads for signature/content inspection.

Standalone MPQ listing and extraction also accept `--listfile=paths.txt`; unresolved `File000...` placeholders are reported as a naming limitation, not archive incompatibility.
- Maps standalone DBCs to `DBFilesClient\\<name>.dbc`.
- Synchronizes a saved DBC into a selected server `data\\dbc` directory with backup.

Specialized editors will follow the principles in [docs/UX-PRINCIPLES.md](docs/UX-PRINCIPLES.md): grouped fields, named flags, human-readable references, change previews, validation, and duplicate-first creation.

## Run

Requirements: Windows x64 and the .NET 10 SDK.

The high-performance Avalonia desktop is the active application. It has a static themed workspace, multi-file DBC staging, per-document undo/redo, guided item and spell editing, shared Server & SQL detection, MPQ building/browsing/deployment, legacy database recovery, background loading/search, a direct-rendered virtual WDBC viewport, responsive single-window feature routing, and an interactive native M2/SKIN mesh preview. The older WinForms source remains only as a behavior reference while each remaining capability is rebuilt natively; it is not the intended long-term shell or a runtime dependency.

Turn on **DEVBUG ON** in the header for persistent diagnostic mode, or launch once with `--devbug`. Normal mode creates no routine logs. Devbug logs and its live terminal use structured action/result entries and retain only the newest three sessions.

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj
```

Open a DBC or WotLK M2 directly in the Avalonia preview:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- "C:\path\to\Spell.dbc"
```

Open the visual asset comparison workspace directly inside the main Crucible window:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- "--asset-compare=G:\Crucible-Extras-Processed"
```

Open SQL Studio directly:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --sql-studio
```

Open the shared project/ID allocator or decoded creature workspace directly:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --projects
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --creatures
```

Open the decoded gameobject workspace directly:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --gameobjects
```

Open the complete decoded quest workspace directly:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --quests
```

Open pet stats, generated names, localization, and spell-aura mappings directly:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --pets
```

Open gossip, trainers, conditions, and SmartAI directly:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj -- --behaviors
```

Run the legacy WinForms reference shell (development comparison only):

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

1. Connect the landed same-window project allocator beyond items, spells/mount drafts, creatures, gameobjects, and quests into race/class, recovery/fusion, and unified validation/change plans.
2. Extend **Legacy SQL Recovery & Promotion** beyond the implemented target conflict analysis, coordinated selected-row ID remapping, SQL preview, transactional apply, and receipt rollback: add graph-driven dependency-closure selection, cross-core field adapters, and persistent project-registry allocation. The default path remains read-only and never carries deletions into the target implicitly.
3. Expand the Spell Workspace with named flags, searchable references, related-table navigation, and optional project-local SQLite bulk editing.
4. Guided creature/NPC appearance import, gameobject generation, vendor, loot, quest, race, and class creators on the live capability model.
5. Extend the landed Cataclysm WDB2 provider to later DB2 families, verify the landed CASC provider against real later-client corpora, and complete full-corpus verification for additional client profiles.
6. Expand the revision-aware AzerothCore/TrinityCore DBC binding and audit engine into transactional multi-destination deployment plans.

The detailed decisions from legacy and newly added local tools are recorded in [the reference-tool audit](docs/REFERENCE-TOOL-AUDIT.md).

## Contributing

The project is intentionally public at an early stage. Bug reports, workflow suggestions, format research, tests, and focused pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Legal

WoW Crucible is an independent community project and is not affiliated with or endorsed by Blizzard Entertainment. World of Warcraft and related names are trademarks of their respective owners. This project does not distribute Blizzard game data.

Source code is released under the [MIT License](LICENSE). StormLib retains its license under [third_party/StormLib/LICENSE](third_party/StormLib/LICENSE), and the pinned CascLib provider retains its MIT license and exact source/build provenance under [third_party/CascLib](third_party/CascLib/SOURCE.md).
