# WoW Crucible CLI reference

The CLI executable is `wowcrucible.exe`. Run `wowcrucible --help` for the short command map or `wowcrucible <group> --help` for group-specific syntax. Paths containing spaces must be quoted.

Add `--devbug` anywhere in a command to keep a detailed portable diagnostic log while preserving normal terminal output. Example: `wowcrucible --devbug mpq list patch-H.MPQ`. Logs are written under `Logs\Debug` beside the executable when that location is writable, and only the newest three CLI Devbug sessions are retained. Normal CLI use creates no routine log.

Exit codes are consistent across workflows:

- `0`: completed successfully.
- `1`: invalid input, I/O failure, or another hard error.
- `2`: missing/invalid command syntax.
- `3`: work completed but unresolved conflicts, blocked assets, warnings that require review, or partial failures remain.

## Searchable desktop/CLI command catalog

```text
wowcrucible tools commands [search words...] [--format=text|json]
```

The command catalog is the same source used by the desktop's same-window `Ctrl+K` palette. Search accepts multiple intent terms rather than requiring an exact feature name, so queries such as `cut items`, `Heidi favorites`, `MPQ merge`, `model viewer animation`, and `server restart` resolve to their native workspaces. Empty search lists all commands in navigation order; no matches return review exit code `3`. Launch `WoWCrucible.Desktop-latest.exe --command-palette="search words"` to open the desktop directly into the filtered palette.

## Tool consolidation inventory

```text
wowcrucible tools inventory [workspace-root] [--format=text|json] [--unassigned-only] [--no-missing]
```

Without a path, the CLI searches upward from its executable for the shared `wow edits` workspace. It inventories every top-level workspace root, every direct `Tools` package, and the explicitly audited AmarothTools/Models/Map children. Tracked, missing, and newly unassigned paths are distinct states. A new unassigned directory is printed first and returns review exit code `3`; it is never silently folded into a generic category. The desktop exposes the same catalog in the single-window **Tool inventory** workspace with search and state filters.

## Offline knowledge and field reference

```text
wowcrucible knowledge search <terms...> [--root=wiki-folder] [--locale=en] [--limit=100] [--format=text|json]
wowcrucible knowledge show <relative-markdown-path> [--root=wiki-folder] [--section=N]
```

The provider indexes local Markdown headings and text only. It does not run Jekyll, shell scripts, JavaScript, HTML, or remote links from the wiki. Search is multi-term, identifier-fragment-aware, language-filterable, and returns the exact source document and matching section. `show` prints one exact indexed article or section as readable text. Without `--root`, Crucible discovers the shared `wiki` folder by walking upward from the executable.

The same-window **Offline knowledge & field reference** route exposes the identical index with responsive result/article splitters and source reveal/copy. Press F1 while a DBC cell is selected to search its table and field automatically; SQL Studio's help action passes the selected table plus whichever complete-row field currently has focus. Use `--knowledge` for direct desktop launch, or find it through `Ctrl+K` with terms such as `wiki field help`.

## Client WDB cache tables

```text
wowcrucible cache info <file.wdb|file.adb> [--definitions=definitions.xml] [--definition=name] [--format=text|json]
wowcrucible cache server-plan <file.wdb> <host> <port> <user> <database> [--definitions=WDB.xml] [--ids=17,17802] [--output=plan.json] [--sql=preview.sql] [--overwrite]
wowcrucible cache server-apply <plan.json> <host> <port> <user> <database> <receipt.json> [--apply] [--overwrite]
wowcrucible cache server-rollback <receipt.json> <host> <port> <user> <database> [--apply]
wowcrucible cache rows <file.wdb|file.adb> [--definitions=definitions.xml] [--definition=name] [--search=text] [--limit=100] [--format=text|json]
wowcrucible cache export <file.wdb|file.adb> <output.csv|jsonl> [--definitions=definitions.xml] [--definition=name] [--format=csv|jsonl] [--overwrite]
```

Cache reads are bounded and source files are always read-only. Crucible validates WDB's version-aware 16/20/24-byte header (24 bytes at build 12340), record ID/length framing, declared payload boundaries, and a two-million-record/64-MiB-per-record safety ceiling. For Cataclysm WCH2 ADB it independently validates the 48-byte header, fixed rows, optional range indexes, string-offset block, copy-table extent, and every checked size multiplication. WCH5/WCH7/WCH8 are rejected rather than guessed because those formats require matching DB2 layout metadata. Crucible natively loads WDBX `Definition/Table/Field` XML plus the older `wdbDef` and `adbDef` dialects, including variable WDB ItemCache stats and ADB string offsets. A build-tagged schema must match the cache header. Without a matching schema it still lists exact record IDs/rows, sizes, offsets, and raw bytes instead of predicting types. CSV and streaming JSON Lines exports are atomic and require `--overwrite` when the destination exists.

The desktop **Client cache tables** workspace is the same provider in the main window: open or drag a `.wdb`/`.adb`, search all decoded values, inspect field/payload offsets and raw remainders, and export without changing the cache. When the shared tool corpus is available, Crucible prefers its exact build-12340 `WDB.xml` for WDB and 4.3.x `adb-definitions.xml` for ADB; a different schema can be selected explicitly.

`cache server-plan` and the desktop **Plan selected → server** action require a verified live SQL profile and a decoded WDB definition. They bind the source SHA-256, exact live preimages, target identity, and table-schema fingerprint into a portable review artifact; map only fields proven for modern `item_template`, `creature_template`, `gameobject_template`, or `quest_template`; and emit preimage-guarded update previews. Unmapped/client-only fields stay explicit, and a missing server identity blocks apply rather than creating an incomplete template. `server-apply` is dry-run unless `--apply` is explicit, locks and rechecks every selected row inside one transaction, and writes a content-hashed rollback receipt before commit. `server-rollback` is also dry-run unless `--apply` is present and refuses the complete rollback if any applied field changed afterward. The desktop exposes the same actions through inline confirmation in the cache workspace, storing plans and receipts beneath the running app folder. None of these paths reproduce obsolete ArcEmu converter targets. Passwords come from `WOW_CRUCIBLE_DB_PASSWORD` unless another environment variable is selected.

## Portable content projects and ID reservations

```text
wowcrucible project create <folder> <name> [--target=wotlk-12340] [--asset-library=folder]
wowcrucible project status <project-folder>
wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]
wowcrucible project occupancy <domain> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--format=text|json]
wowcrucible project reserve-live <project-folder> <domain> <count> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--start=N] [--purpose=text]
```

A content project separates Assets, DBC, SQL, Manifests, Reports, and Staging outputs and keeps `ids.crucible.json` as its durable allocation registry. `occupancy` reports every required SQL/DBC identity source; `reserve-live` writes a reservation only when all mapped sources were read successfully. WotLK race/class allocation stops at ID 31, and mount/spell reservations share the Spell namespace because mount IDs are spell IDs. The password comes from `WOW_CRUCIBLE_DB_PASSWORD` by default. `reserve-ids --occupied` remains the explicit manual route for custom or not-yet-mapped domains; omitting the occupied list returns review exit code `3`.

The desktop exposes the same engine under **Projects & shared IDs** without opening another window. It persists the active project, shows source-by-source readiness and reservation history, and allocates identities for full item copies, staged Spell.dbc/mount-spell clones, and guided creature, gameobject, and quest drafts before their writes are reviewed. Spell cloning includes unsaved identities from the active in-memory table and keeps Mount in the Spell namespace. Reserving from a loaded SQL row explicitly converts the visible decoded fields into a new INSERT variant; it never changes the primary key of an UPDATE silently. Launch directly with `WoWCrucible.Desktop-latest.exe --projects`.

## Asset inspection and libraries

```text
wowcrucible asset texture-info <file.blp>
wowcrucible asset map-info <file.adt|wdt|wdl> [--cells] [--placements] [--format=text|json]
wowcrucible asset adt-height-plan <input.adt> <delta> <x:y,x:y|all> <plan.json> [--overwrite]
wowcrucible asset adt-height-apply <plan.json> <output.adt> [--overwrite]
wowcrucible asset adt-brush-plan <input.adt> <center-x:center-y> <radius> <strength> <plan.json> [--mode=raise-lower|flatten|smooth|noise] [--target-height=N] [--seed=N] [--falloff=linear|smooth|constant] [--overwrite]
wowcrucible asset adt-brush-apply <plan.json> <output.adt> [--overwrite]
wowcrucible asset adt-texture-info <input.adt> [--cells] [--format=text|json]
wowcrucible asset adt-texture-plan <input.adt> <layer-slot> <texture-id> <x:y,x:y|all> <plan.json> [--overwrite]
wowcrucible asset adt-texture-apply <plan.json> <output.adt> [--overwrite]
wowcrucible asset adt-texture-add-plan <input.adt> <client-texture.blp> <x:y,x:y> <plan.json> [--encoding=auto|packed-4-bit|big-8-bit|rle-8-bit] [--initial-alpha=0] [--overwrite]
wowcrucible asset adt-texture-add-apply <plan.json> <output.adt> [--overwrite]
wowcrucible asset adt-alpha-info <input.adt> [--cells] [--format=text|json]
wowcrucible asset adt-alpha-plan <input.adt> <layer-slot> <center-x:center-y> <radius> <target-alpha> <opacity> <x:y,x:y|all> <plan.json> [--falloff=linear|smooth|constant] [--overwrite]
wowcrucible asset adt-alpha-apply <plan.json> <output.adt> [--overwrite]
wowcrucible asset texture-decode <file.blp> <output.png> [--mip=N] [--overwrite]
wowcrucible asset texture-encode <image.png|jpg|bmp|tga> <output.blp> [--format=auto|dxt1|dxt1a|dxt3|dxt5] [--quality=fast|balanced|best] [--no-mips] [--overwrite]
wowcrucible asset texture-validate <file-or-folder> [--recursive]
wowcrucible asset inspect <model.m2|building.wmo>...
wowcrucible asset m2-downport-plan <modern.m2> [--skin=file.skin] [--listfile=id-path.csv] [--format=text|json]
wowcrucible asset m2-downport-scan <file-or-folder>... [--listfile=id-path.csv] [--format=text|json]
wowcrucible asset m2-downport <modern.m2> <new-output-folder> [--skin=file.skin] [--listfile=id-path.csv]
wowcrucible asset dependency-graph <processed-library> <root.m2|wmo|adt|wdt> [--target-index=client-index] ["--target-choice=client-path|archive"]... [--only-problems] [--manifest=patch.json] [--output-mpq=name.MPQ] [--format=text|json]
wowcrucible asset creature-appearances <model-client-path> [--dbc=target-dbc-folder] [--schema=file] [--library=processed-library --provenance=source-name] [--format=text|json]
wowcrucible asset preview-info <wrath-model.m2> [--skin=file.skin] [--dbc=folder] [--hair=N] [--facial-hair=N] [--animation=sequence-index] [--time=milliseconds] [--naked|--groups=group:variant,...|--all-geosets]
wowcrucible asset model-export <wrath-model.m2> <output.obj> [--skin=file.skin] [--animation=sequence-index --time=milliseconds] [--texture=slot:file.blp]... [--naked|--groups=group:variant,...|--all-geosets] [--overwrite]
wowcrucible asset wmo-preview-info <root-or-group.wmo> [--groups] [--content-root=folder] [--format=text|json]
wowcrucible asset path-candidates <processed-library> <client-path> [--preferred=provenance] [--format=text|json]
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

The same native inspection/workspace flow is available under **Modern asset conversion** in the desktop navigation. It accepts recursive drag/drop, keeps source and dependency snapshots immutable and hash-verified, reopens moved `conversion-report.json` workspaces, and previews M2 files that are already compatible with WotLK 3.3.5a. `m2-downport-plan` is read-only and returns review exit code `3` with every blocker when the model is outside the verified profile; `m2-downport-scan` performs that planning once across any number of files/folders and retains every per-file blocker or read failure in text/JSON. Scan results distinguish conversion-ready modern models, already-ready MD20/version-264 files, blocked models, and actual read failures. The optional `--listfile` accepts streamed `FileDataID;client/path` (also comma/tab) data without loading a multi-million-line corpus into memory. Batch scan resolves the complete requested ID set in one pass. Missing IDs and one ID mapped to multiple distinct paths remain blockers; the exact listfile hash and resolved paths are bound into the plan and rechecked before conversion. The profile covers real-corpus MD21/version-274 static armor and head models with one companion SKIN, embedded or listfile-resolved hardcoded texture paths, rigorously validated timestamp-zero single-key RGB/opacity color tracks, one-stage packed shader 0 (`Opaque`) or 16 (`Mod`) materials, two-stage native explicit shaders `0x8000` (`Opaque_Mod2xNA_Alpha`) and `0x8001` (`Opaque_AddAlpha`), and two-stage packed shader 14 (`Opaque_Mod2xNA`). Native explicit IDs are byte-preserved with synthesized primary/environment routes. Shader 14 is translated into WotLK's global blend-override table with `[Opaque, Mod2xNA]` stages; the writer relocates model-name bytes only after proving no other live structure occupies the extended-header fields and canonicalizes references to absent transparency/texture-animation definitions as explicit none values. A model mixing shader 14 with explicit IDs is blocked because enabling the WotLK global-blend flag changes the meaning of every material shader field. The optional `0x200000` newer-exporter record-order flag and empty TXAC are supported, while other modern semantic chunks, attachments, emitters, ribbons, lights, cameras, or unverified materials remain blocked. `m2-downport` requires a new/empty destination, rehashes all immutable inputs, unwraps MD21, writes version 264, embeds resolved texture names, preserves verified constant color series, repacks and rewrites the common SKIN arrays, independently reloads the output through Crucible's Wrath parser, verifies embedded paths, constant tracks, material combiners, and geometry counts, then atomically publishes a JSON receipt. Modern shadow-batch metadata is explicitly reported as omitted because SKIN v2 has no corresponding array. Files outside this profile remain untouched and blocked rather than being partially stripped.

`preview-info` reports every Wrath animation sequence plus validated particle-emitter and ribbon-emitter bone, texture, material/blend, sprite-sheet/lifetime/resolution, flags, and position. `--skin=file.skin` deliberately inspects an alternate view or LOD SKIN instead of forcing the default `00.skin`; structure and index bounds are validated against the selected M2. Build-264 multi-texture particle emitters list all three decoded five-bit texture-definition indices in their actual layer order. Add `--animation=N --time=MS` to resolve aliases and external `.anim` files, sample the weighted bone/camera/light pose at an exact time, report a capped particle pose plus its first 16 deterministic sprites, and report capped ribbon trails with section counts and endpoints. This is the command-line diagnostic for the same animation/effect path used by the in-window model preview.

Explicit build-264 shaders 0/1/2/5/6/21/23 and three-stage shader 3 now have bounded render plans. `preview-info` shows shader 2 as primary/environment `Opaque_AddAlpha_Alpha`, shader 5 as primary/primary additive-alpha, shader 21 as primary/secondary `Mod_Mod (edge fade)`, and shader 23 as primary/primary `Opaque_Alpha`; all remain conservatively `exact=False`. Shader 21 uses animated view-space normals and a linear face-on-to-silhouette opacity curve. Structure-invalid SKIN/M2 pairs are rejected before shader routing.

`model-export` writes the exact currently requested visible mesh as Wavefront OBJ/MTL. `--animation=N --time=MS` exports the sampled weighted pose; without it the bind mesh is exported. `--texture=slot:file.blp` decodes a real M2 texture definition to a neighboring PNG and binds it in the MTL. A `.crucible-model.json` receipt retains source M2/SKIN hashes, geoset selection, animation time, and the original WoW render flags/blend modes that OBJ cannot represent exactly. Existing outputs are refused unless `--overwrite` is explicit. The same export is available above the live model in Asset Compare and snapshots the current scrubbed pose before background writing.

`creature-appearances` maps an exact client M2 path through build-12340 `CreatureModelData.dbc` and every matching `CreatureDisplayInfo.dbc` row, including its three creature texture variations and same-provenance extracted M2/SKIN/BLP source. Target/server DBCs take precedence. `--library` plus `--provenance` allows an imported model absent from the target DBCs to use only that selected extracted layer's own compatible creature DBC pair; incompatible layouts and incomplete pairs remain explicit findings instead of guessed texture assignments.

`wmo-preview-info` loads a WotLK WMO root (or infers the root from a selected `_###.wmo` group), validates root/group chunk bounds and render indices, and reports groups, vertices, triangles, material bindings, bounds, missing/collision-only groups, and resolved BLP paths. `--groups` includes per-group geometry details. `--content-root` supplies an extracted client root for texture resolution; processed libraries otherwise prefer the WMO's exact provenance and use another provenance only when exactly one candidate exists. The same bounded loader drives the embedded WMO views in Modern asset conversion, GameObjects, and Maps & World.

`path-candidates` resolves one exact client-relative path against the content-first processed library without scanning or loading its full visual catalog. A single physical candidate is selected automatically. Multiple provenance candidates remain unresolved and return exit code `3` until `--preferred` names one exact provenance; their bytes are not assumed equivalent. Maps & World uses the same operation for the WMO paths extracted from an ADT/WDT.

`map-info` validates a WotLK ADT, WDT, or WDL without loading the complete file into memory. The normal report summarizes chunks, terrain/world-grid occupancy, height range, coordinates, referenced assets, and placed-object counts; `--cells` emits every present terrain cell and `--placements` emits every decoded MMID→MMDX/MDDF M2 doodad plus MWID→MWMO/MODF WMO instance with UID, position, orientation, flags, and scale. WMO records also include extents and doodad/name sets. `--format=json` returns the complete inspection model with numeric vector fields used by the same-window Maps & World grid and placed-instance selector.

ADT height editing is intentionally two-stage. `adt-height-plan` binds a finite signed delta and explicit `x:y` cells (or all present cells) to the exact source SHA-256 and original MCNK base heights. `adt-height-apply` refuses a changed source or in-place source overwrite, writes a separate ADT atomically, re-parses every edited height range, and emits a companion `.crucible-map-edit.json` receipt. The Maps & World workspace provides the same selection, preview, and write path visually.

`adt-brush-plan` edits the real 145-float MCVT vertex grids with a tile-local radial brush rather than shifting whole cells. The center uses the ADT's `0..16` terrain grid and radius uses that same coordinate space. Raise/lower applies signed strength; flatten moves toward the required absolute `--target-height` by at most the strength magnitude; smooth moves toward the immutable neighboring source-height average by at most that magnitude; noise uses the magnitude as amplitude and a deterministic signed `--seed`. Linear/smooth/constant radial falloff remains independent of the operation. The plan records the mode plus every exact float offset, preimage, weight, and postimage. `adt-brush-apply` recomputes those values from the bound source, retains the source, writes atomically, re-parses every affected cell, and emits `.crucible-map-brush.json`. The visual grid places the center by click and draws the exact radius before preview/write.

`adt-texture-info` decodes the ordered MTEX catalog and every MCNK→MCLY layer record, including slot, flags, alpha offset, ground-effect ID, resolved path, and exact texture-ID byte offset. `adt-texture-plan` reassigns an existing layer slot across explicit cells (or every cell that has the slot) to an existing MTEX ID. It deliberately does not resize MTEX or add layers. Apply remains source-hash/preimage bound, writes a separate ADT atomically, re-parses every changed layer, and emits `.crucible-map-texture.json`. The same-window map workspace shows these decoded layers on cell selection and provides the identical preview/write workflow.

`adt-texture-add-plan` is the separate structural path for a texture that is not yet present. It appends one normalized client-relative `.blp` path to MTEX and adds one MCLY plus matching MCAL map to every explicit selected cell. Cells with four layers are refused. Auto encoding follows the tile's existing packed-versus-8-bit family and refuses mixed or evidence-free inference; an explicit packed, big, or RLE encoding is always reviewable. Apply validates the source and every selected MCNK preimage, shifts known nested offsets, rewrites MHDR and all live MCIN absolute offsets/sizes, writes a separate ADT atomically, and re-parses the catalog, layers, alpha maps, and map grid before emitting `.crucible-map-structure.json`.

`adt-alpha-info` validates every paintable MCLY→MCAL slice and reports its texture, encoding, fixed capacity, bytes used, and decoded range/average. Packed 4-bit maps retain Wrath's duplicated final edge, big maps preserve all 4,096 bytes, and compressed maps use bounded RLE decoding. `adt-alpha-plan` paints an existing non-base layer toward alpha 0–255 using tile-local center/radius, opacity, falloff, and either explicit cells or every intersected compatible cell. Plans bind the source hash, exact MCAL offsets/capacities, encoded preimages, and decoded postimages. `adt-alpha-apply` never resizes a chunk: RLE is recompressed only when it still fits the existing slice, the source cannot be the output, every written map is re-decoded, and `.crucible-map-alpha.json` records the verified result. Maps & World exposes the same click-positioned preview/write path without another window.

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

The visual workspace can sort cards by source/name, filename, or file size in either direction. `Scan exact copies` first narrows candidates by byte length, verifies SHA-256, and finally performs a streaming byte-for-byte comparison. It only labels exact groups and enables a collapsed comparison view; it never deletes source or processed assets. The preview-pane selector can switch from synchronized left/right images to compatible WotLK M2 + SKIN sources found by the automatic model browser. Every valid matching SKIN/LOD is listed in the same-window selector; changing it reloads geometry, dependencies, geosets, and export state without changing the M2 or opening another viewer. The embedded model view is live and rotatable. With no manual candidate selected it parses SKIN material units, both build-264 UV streams, and the complete referenced M2 texture/coordinate/weight/transform lookup ranges, then decodes every used embedded BLP from the same provenance layer in the background. Common legacy two-stage Opaque/Mod/Mod2x/Add materials are combined inside one isolated layer using their declared primary or secondary UV stream. Explicit shader 0/1/6 two-stage units use validated primary/environment routing and deterministic alpha-weighted Mod2xNA or additive-alpha pass plans; explicit shader 3 adds its third primary-UV additive-alpha layer. Other explicit or three-plus-stage families remain counted and labeled as first-stage fallbacks. Fixed-function environment stages use sphere-map coordinates derived from the current animated view-space normals. Native M2 portrait/character-info cameras can be selected explicitly and use their animated position, target, roll, perspective FOV, and clipping planes; the default remains the draggable orbit camera. Embedded directional and point lights follow their declared bone plus animated ambient/diffuse color, intensity, and attenuation. Playable-race paths load the complete relevant `CharSections.dbc` records, expose skin/face/facial-hair/hair selectors, layer underwear, face, facial hair, and scalp into the standard 256-based character atlas, scale those verified regions for HD atlases, and bind the separate hair material. The selected hair and facial-hair variations are also resolved through `CharHairGeosets.dbc` and `CharacterFacialHairStyles.dbc`. Items & Sets calls that same core appearance service before applying item wear textures and equipment geosets, so its preview no longer depends on a blank manual skin atlas. The same-window geoset inspector names every group and reports its exact IDs, section counts, and triangle counts. Character mode applies the DBC choices, Naked mode suppresses hair/facial features, and Manual mode allows one exact variant or Hidden per group; only **Everything stacked** deliberately combines mutually exclusive styles. The attachment inspector separately validates the M2 bone table, attachment records, and lookup table, names the native helmet/shoulder/weapon/sheath/effect/vehicle points, and highlights the selected bind position on the live model without guessing. A single texture candidate or a set proven byte-for-byte identical is selected automatically; non-identical provenance variants remain unselected until the user chooses one. Missing component files in that chosen provenance are named rather than silently borrowed from a different mod. Applying a selected PNG deliberately overrides every material for diagnostic comparison. `asset preview-info` lists every named group and submesh plus texture slots, material units, each resolved texture stage, its UV source, combiner support/exactness, compact render batches, validated bone counts, attachments, cameras, lights, and sampled camera/light poses when `--animation` is used. `--naked` suppresses appearance geometry, `--groups=0:3,7:1` applies exact group overrides, and `--all-geosets` is intentionally stacked. `asset appearance-info` reports the race/sex inference and available base-skin records; `asset appearance-render` resolves explicit CharSections choices and provenance from a processed library and writes the composed body plus optional hair texture; `asset appearance-compose` remains the low-level manual atlas command.

The automatic model browser searches recursively below the selected content path. When a texture-only descendant contains no M2, it walks to the nearest parent with models, so selecting `Character\BloodElf\Female\Hair` can expose models stored at `Character\BloodElf\Female`. Models are labeled Ready, Missing Skin, Requires Conversion, or Invalid before loading. Ready models can be searched, switched with previous/next controls, and previewed with the currently selected PNG as a live UV-mapped material candidate.

Keep/Alternative/Reject/Review decisions are saved under `Projects\Definitive-Set.crucible-assets.json` inside the selected library. PNGs are previews only: when the matching BLP exists, the project records and hashes that deployable BLP. Model keeper groups include the M2 plus matching SKIN and external ANIM companions. `definitive-stage` or the **Stage keepers** button re-verifies every hash, preserves logical client paths, and emits `definitive-set.crucible-patch.json`; it never modifies or deletes the processed source library.

For rapid triage, **Undecided only** hides recorded images, **Auto-advance** selects the next candidate, and the keyboard shortcuts are `K` keeper, `A` alternative, `R` review later, and `X` reject. Model keepers additionally resolve embedded BLP paths from the same provenance. A missing dependency or a texture found only in another patch source blocks Keeper rather than silently creating a mixed model. Replaceable body, hair, cape, fur, and creature-skin slots remain visible as required appearance/DBC bindings.

## DBC information, validation, comparison, and editing

```text
wowcrucible dbc info <file.dbc|file.db2>
wowcrucible dbc dbd-info <file.dbd> <build> [--format=text|json]
wowcrucible dbc schema-audit <definitions-root> <table-folder> <build> [--xml=schema.xml] [--only-problems] [--format=text|json]
wowcrucible dbc item-display <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> [--class=N] [--subclass=N] [--inventory=N] [--assets=processed-library] [--format=text|json]
wowcrucible dbc spell-tooltip <Spell.dbc> <spell-id>... [--format=text|json]
wowcrucible dbc item-equipped <ItemDisplayInfo.dbc> <schema.xml|-> <display-id> <base-skin.blp|image> <output.png> --inventory=N --assets=processed-library [--source=name] [--overwrite]
wowcrucible dbc rows <file.dbc|file.db2> <schema.xml|file.dbd|definitions-folder> <id>...
wowcrucible dbc export <file.dbc|file.db2> <schema> <output.csv|json|jsonl> [--format=csv|json|jsonl] [--columns=A,B|--column=Name] [--ids=1,2|--id=N] [--raw-string-offsets] [--overwrite]
wowcrucible dbc find <file.dbc|file.db2> <schema> <field> <value>... [--count|--limit=100]
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
wowcrucible dbc import <file.dbc|file.db2> <schema> <input.csv|json|jsonl> [--format=csv|json|jsonl] [--append] [--raw-string-offsets] [--output=changed.dbc|db2] [--overwrite] [--report=text|json]
```

`item-display` decodes the exact WotLK build-12340 `ItemDisplayInfo` record behind an SQL `item_template.displayid`. It reports both model names, model textures, inventory icons, all eight wearable texture components, geoset groups, helmet visibility masks, flags, spell/item visuals, particle color, and sound group. Class/subclass/inventory values order canonical object-component paths correctly; `--assets` checks only those bounded logical directories in a processed content-first library and reports every matching provenance source. Use `-` for the schema argument to use Crucible's validated built-in 25-field layout.

`dbd-info` resolves one WoWDBDefs table for the requested full client expansion/build and prints its physical fields after array, localized-string, width, signedness, ID, and non-inline rules are applied. `schema-audit` compares every top-level DBC or DB2 with the matching DBD build layout and optionally the existing WDBX XML corpus. Selecting a folder one level too high fails with a likely nested-folder hint instead of reporting zero successful results. An intentional zero-byte server placeholder is informational; missing build definitions and physical field-count disagreements produce review exit code `3`.

Generic row, find, export, import, compare, clone, promotion, and edit commands accept fixed-layout WDB2 input when `<schema>` is the matching `.dbd` file or the WoWDBDefs definitions folder. The WDB2 build and table hash come from the file header. Renamed outputs receive a small `.crucible-table.json` identity sidecar so they can reopen with the correct DBD. Extended ID/string-length maps and copy tables round-trip unchanged; structural row/ID edits are blocked when those dependent side tables are present. WDB5/WDB6/WDC are not yet supported.

`spell-tooltip` reads the exact build-12340 English spell name, subtext, description, and aura description for each requested ID. The Items & Sets tooltip keeps one cached catalog for the configured `Spell.dbc`, refreshes it only when the file changes, and uses these decoded fields for all five item spell slots instead of displaying bare numeric IDs. Runtime `$` tokens are preserved rather than guessed.

`item-equipped` composes a resolved armor display onto a chosen character base-skin atlas and writes a PNG suitable for inspection or the native character renderer. Wear textures are grouped by provenance; Crucible selects the most complete single source unless `--source` names one explicitly, and never silently mixes patch variants. The result also reports the inventory-aware character geoset selection needed for the equipped mesh.

`export` streams schema-aware rows without modifying the DBC. CSV, JSON Lines, and JSON array output always include `$recordKey` and `$rowIndex`; virtual `AutoGenerate` GT identities therefore remain separate from their physical float data. Strings are decoded by default, while `--raw-string-offsets` is an explicit binary-diagnostic mode. Column and record-key filters are exact, missing requested names/keys fail, existing outputs are refused unless `--overwrite` is explicit, and a cancelled export never publishes its temporary file. The same choices are available from **Export rows** in the loaded DBC toolbar, including inclusive key ranges in the visual workspace.

`import` consumes those CSV, JSON Lines, and JSON-array rows and is a dry-run by default. It validates every value against the resolved physical schema on an isolated in-memory copy, reports exact cell changes, and writes nothing unless `--output` is supplied. `$recordKey` selects stable physical/virtual records; physical keys cannot be changed, missing keys require `--append`, virtual appends must be contiguous, and tables without a proven key can update existing `$rowIndex` values but cannot append. Unknown columns and duplicate targets fail instead of being discarded. Decoded strings are re-interned; `--raw-string-offsets` is deliberately separate and bounds-checks offsets. `--overwrite` is required to replace an existing output and retains its previous bytes as `.bak`. In the desktop, **Import rows** previews the same plan, refuses it if the staged DBC changes afterward, then applies it as one dirty structural batch for normal review and Save/Save As.

`validate` is non-recursive by default and fails when the selected folder has no top-level DBCs. Use `--recursive` intentionally for a directory tree. `--strict` treats raw fallback schemas as failures.

Promotion and clone/remap commands save semantic, reviewable operations. Strings are re-interned in the destination rather than copying invalid raw offsets. Additive promotion and clone/remap preserve existing IDs.

`itemset inspect` resolves the set's localized name, member item IDs, bonus thresholds, and—when `Spell.dbc` is supplied—spell names. `itemset clone` refuses to overwrite an existing set and requires an explicit old-to-new item ID map. `itemset effects` writes up to eight piece-count/spell pairs to a separate output DBC.

## MPQ listing, extraction, creation, and updates

```text
wowcrucible mpq list <archive.mpq> [filter] [--content-only] [--format=json] [--listfile=paths.txt]
wowcrucible mpq tree <archive.mpq> [internal-folder] [--format=text|json] [--listfile=paths.txt]
wowcrucible mpq extract <archive.mpq> <folder> [filter] [--quiet|--progress=N] [--workers=N] [--listfile=paths.txt]
wowcrucible mpq extract-folder <archive.mpq> <internal-folder> <destination> [--quiet|--progress=N] [--workers=N] [--listfile=paths.txt]
wowcrucible mpq create <archive.mpq> <files/folders...>
wowcrucible mpq update <small-patch.mpq> <files/folders...>
wowcrucible mpq merge <output.mpq> <source-a.mpq> <source-b.mpq> [...] [--conflicts=block|earlier|later] [--listfile=paths.txt]
```

Filters accept text or `*`, `?`, and `**` globs. `--content-only` excludes `(listfile)`, `(attributes)`, and `(signature)` metadata. JSON output separates entry properties for scripts.

If StormLib's first enumeration exposes `File000...` placeholders while the archive contains `(listfile)`, Crucible automatically extracts that metadata to a unique temporary file and retries it as an explicit listfile. Recovered names are accepted only when entry count and the complete multiset of block index, locale, size, compressed size, and flags still match while the anonymous count decreases. The temporary file is removed and never enters the portable cache. Entries still anonymous after that proof are reported as unresolved names rather than calling the MPQ incompatible; supply a compatible external path corpus with `--listfile=paths.txt` to attempt further recovery before filtering or extraction.

`tree` lists only the direct files and subfolders at the requested internal folder while reporting recursive counts and bytes. `extract-folder` resolves that exact folder to its recursive files before extraction. The desktop provides the same lazy breadcrumb browser beside the global flat search and displays non-default locale variants explicitly.

Extraction opens source archives explicitly read-only and uses a separate StormLib archive handle per worker. Every enumerated member retains its exact block index, so parallel extraction selects the intended locale variant without racing StormLib's process-global locale setting; legacy caller-constructed entries without an index use a serialized compatibility fallback. Writes remain per-file atomic, same-destination variants stay ordered, cancellation leaves no `.extracting` file behind, and a later run can resume around existing indexed outputs. Auto uses up to four workers; `--workers=1..16` and the desktop selector allow storage-specific tuning. On the workspace's USB SATA SSD, a byte-verified Patch-H sample of 10,000 files (781,411,574 output bytes) measured 49.82–49.88 seconds with one worker and 43.39–43.52 seconds with two/four workers, with zero SHA-256 mismatches.

Standalone list/tree/extract commands and the desktop archive browser share compressed indexes under `Cache\MPQ` beside the executable when portable. A cache is reused only when the archive and optional external listfile path, size, and write timestamp still match. Writes are atomic, corrupt entries are rebuilt, and pruning retains at most roughly 64 recent indexes within a 512 MiB budget.

`update` is transaction-safe but copies the archive first and refuses archives larger than 2 GiB. Treat large client/mod layers as immutable inputs and build a small manifest-driven patch instead.

`merge` treats every source MPQ as immutable. Repeated internal paths are SHA-256 checked and then compared byte-for-byte; exact copies are stored once. Different bytes at the same internal path block output by default. `--conflicts=earlier` or `--conflicts=later` is an explicit global precedence choice. Hash-only `File000...` names and duplicate locale variants are blocked unless their real paths can be resolved safely. Merge payloads use short flat temporary names while logical archive paths remain separate, so deeply nested project/output locations do not exceed StormLib's Windows destination-path limit.

## Read-only CASC browsing and extraction

```text
wowcrucible casc list <storage-folder> [filter] [--local-only] [--format=text|json] [--listfile=paths.txt]
wowcrucible casc tree <storage-folder> [internal-folder] [--local-only] [--format=text|json] [--listfile=paths.txt]
wowcrucible casc extract <storage-folder> <destination> [filter] [--quiet|--progress=N] [--listfile=paths.txt]
wowcrucible casc extract-folder <storage-folder> <internal-folder> <destination> [--quiet|--progress=N] [--listfile=paths.txt]
```

The desktop exposes the same provider beside MPQ in **MPQ & CASC archives**. CASC storage is always opened read-only. Enumeration preserves synthetic FileDataId, content-key, and encoded-key names when a real path is unavailable; an external listfile is only a path hint. Extraction selects one locally available locale row for each path, writes through a sibling temporary file, and never downloads absent CDN content implicitly.

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
wowcrucible client extract <index-directory> <archive-relative-path> <folder> [filter] [--resolved-only|--anonymous-only] [--overwrite] [--quiet] [--workers=N]
wowcrucible client fusion <base-root> <override-root>... [--output=plan.json] [--stage=review-folder] [--all]
```

Client indexes are resumable and distinguish active, backup, inactive-locale, and custom-subdirectory archives. Anonymous hash-only MPQ entries remain quarantined unless explicitly requested. `install-patch` atomically installs the patch and deletes that exact client’s `Cache` folder only after success.

## Installed servers and SQL overlays

```text
wowcrucible server detect <installed-server-folder>
wowcrucible server inspect <installed-server-folder>
wowcrucible server bindings <installed-server-folder> [--source=core-source]
wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all] [--summary] [--migration=sync.sql] [--bundle=folder]
wowcrucible server dbc-apply <installed-server-folder> <bundle-folder>
wowcrucible server dbc-rollback <installed-server-folder> <deployment-receipt.json>
wowcrucible server dbc-module-export <bundle-folder> <module-root>
wowcrucible server client-plan <installed-server-folder> <effective-dbc-folder> [--source=core-source] [--output=plan.json] [--stage=review-folder]
wowcrucible db inspect <host> <port> <user> <database> --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db schemas <host> <port> <user> <database> --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db rows <host> <port> <user> <database> <table> [--search=text] [--filter=column=value] [--sort=column] [--descending] [--offset=N] [--limit=N] [--format=text|json]
wowcrucible db table-admin <host> <port> <user> <database> <table> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db table-design <host> <port> <user> <database> <table> <add|modify|rename|drop|clone|rename-table|add-fk|drop-fk|add-check|drop-check> [column-or-constraint] [--name=value] [--definition="complete clause"] [--first|--after=column] [--columns=a,b --references=table --reference-columns=x,y --delete=RESTRICT --update=CASCADE] [--expression="boolean expression"] [--apply] [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db process-list <host> <port> <user> <database> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db user-list <host> <port> <user> <database> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db account <host> <port> <login> <database> grants <account-user> <account-host> [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db account <host> <port> <login> <database> <create|password|lock|unlock|drop> <account-user> <account-host> [--locked] [--apply] [--new-password-env=NAME] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db account <host> <port> <login> <database> <grant|revoke> <account-user> <account-host> <privilege[,privilege]> [--global|--table=NAME] [--grant-option] [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db join <host> <port> <user> <database> <relationship-name> [--type=INNER|LEFT|RIGHT] [--limit=N] [--run] [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db index <host> <port> <user> <database> <table> create <name> <column[,column]> [--unique] [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db index <host> <port> <user> <database> <table> drop <name> [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db query <host> <port> <user> <database> <statement.sql> [--batch] [--batch-format=text|json] [--output=result.csv|jsonl] [--format=csv|jsonl] [--overwrite] [--write] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db export <host> <port> <user> <database> <table> <output> [--format=csv|jsonl] [--overwrite] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db import <host> <port> <user> <database> <table> <input.csv> [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db dependency-snapshot <host> <port> <user> <database> <table> <output.json> --key=column=value [--key=column=value]... [--limit=N] [--overwrite]
wowcrucible db draft-template <domain> <output.json> [--overwrite]
wowcrucible db content-plan <host> <port> <user> <database> <domain> <draft.json> [--output=plan.sql] [--overwrite] [--apply] [--update] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db pet-curve <host> <port> <user> <database> <source-creature> <target-creature> [--levels=1-80] [--health=1] [--mana=1] [--armor=1] [--attributes=1] [--damage=1] [--output=curve.sql] [--overwrite] [--format=text|json] [--apply] [--update-existing] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db pet-compare <host> <port> <user> <database> <left-creature> <right-creature> [--levels=1-80] [--metric=hp] [--output=report] [--overwrite] [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db pet-preview <host> <port> <user> <database> <creature-entry> --dbc=folder [--schema=definitions.xml] [--library=processed-assets] [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db pet-graph <host> <port> <user> <database> <creature-entry> --dbc=folder --schema=definitions.xml [--format=text|json] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db snapshot <host> <port> <user> <database> <output.crucible-db-snapshot> [--password-env=ENV_NAME] [--ssl=Preferred] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]
wowcrucible db snapshot-inspect <snapshot-file> [--quick]
wowcrucible db recovery-audit <legacy-snapshot> <output.crucible-db-audit> [--baseline=stock-snapshot] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]
wowcrucible db recovery-inspect <audit-file> [--quick]
wowcrucible db favorites <host> <port> <user> <database> [--search=text] [--verify] [--format=text|json]
wowcrucible db sync-plan <host> <port> <user> <database> <verified-audit> <plan.json> [--include=glob]... [--include-removals] [--auto-remap] [--remap-start=ID] [--maximum=N] [--overwrite]
wowcrucible db sync-inspect <plan.json> [--sql=preview.sql] [--overwrite]
wowcrucible db sync-apply <host> <port> <user> <database> <plan.json> <receipt.json> [--apply] [--overwrite]
wowcrucible db sync-rollback <host> <port> <user> <database> <receipt.json> [--apply]
wowcrucible db item-audit <host> <port> <user> <database> [--id=N]... [--ids=N,N] [--format=text|json] [--password-env=ENV_NAME] [--dbc=server-dbc-folder] [--output=report.json]
wowcrucible db item-inspect <host> <port> <user> <database> <item-id> [--password-env=ENV_NAME] [--dbc=server-dbc-folder]
wowcrucible db item-clone <host> <port> <user> <database> <source-id> <new-id> [--suffix=" Variant"] [--itemset=ID]
wowcrucible db spell-inspect <host> <port> <user> <database> <spell-id> [--password-env=ENV_NAME] [--dbc=Spell.dbc|folder] [--format=text|json]
wowcrucible db objects <host> <port> <user> <database> [--type=view|trigger|procedure|function|event] [--format=text|json]
wowcrucible db object-show <host> <port> <user> <database> <type> <name> [--format=text|json]
wowcrucible db object-export <host> <port> <user> <database> <output.sql> [--overwrite]
wowcrucible db object-drop <host> <port> <user> <database> <type> <name> [--apply]
wowcrucible db view-set <host> <port> <user> <database> <name> <select.sql> [--apply]
wowcrucible db event-state <host> <port> <user> <database> <name> <enable|disable> [--apply]
```

Server detection reads the live `worldserver.conf`; it does not accept `.dist` templates. Database passwords should be passed through an environment variable so they do not enter command history. DBC audits report the effective runtime value when the core applies an SQL overlay and can export an idempotent migration without applying it.

`--bundle` creates a new portable, secret-free deployment directory only after an exact named schema and a proven SQL-overlay binding succeed. It copies and hashes the edited DBC and schema, records the live server DBC hash and exact SQL pre-image, writes read-only audit/idempotent migration/exact rollback SQL, and emits a one-entry `DBFilesClient` patch manifest. `dbc-apply` revalidates every bundle artifact and target, locks and compares the reviewed SQL rows, performs the SQL migration and atomic server-DBC replacement with a verified backup, verifies parity before committing, and writes a receipt. `dbc-rollback` requires that receipt and refuses to overwrite either destination if it changed after apply. `dbc-module-export` copies the already reviewed migration into a collision-safe `data/sql/db-world` filename without connecting to MySQL.

`db schemas` lists every database visible to the login. The desktop SQL Studio uses the same discovery to switch locally among world, characters, auth, or other accessible schemas; it does not rewrite the shared server workspace's saved world-database target. A favorite records its database and complete primary key and switches back to that schema when reopened.

`db favorites` searches the same portable cross-table favorites shown in SQL Studio. Terms are matched across database, table, complete key, label, notes, and optional DBC/DB2 or MPQ paths. Add `--verify` to group the filtered favorites by recorded schema, reuse one connection per schema, and require each recorded key to still equal that table's complete current primary key before checking for exactly one row. Text/JSON output distinguishes live, missing, schema-changed, and failed checks; any non-live result returns review exit code `3`. The command never changes SQL data or favorite metadata.

```powershell
wowcrucible db favorites 127.0.0.1 3306 acore acore_world --search="17802 thunderfury" --verify --format=json
```

`db rows` is the read-only CLI half of the complete live table browser. It always returns every live-schema column, supports broad search plus an exact `column=value` filter (`column=<NULL>` for SQL null), validates requested sort/filter columns against the inspected schema, adds primary-key tie breakers for stable paging, and bounds one page to 1–500 rows. The desktop exposes the same operations and can clone any complete primary-keyed row into a new identity; every insert column independently selects VALUE, NULL, or OMIT, so defaults and explicit nulls are not conflated.

`db table-admin` reports the complete live column shape, `SHOW CREATE TABLE` DDL, and every index. `db process-list` reports every connection visible to the supplied login. `db user-list` deliberately reads account metadata without password hashes and reports the server permission error when the login cannot inspect `mysql.user`; it never converts that denial into an empty result.

`db table-design` is the CLI equivalent of **SQL Studio → Schema & server → Columns & table changes / Foreign keys & checks**. It parses exact server-normalized column clauses, composite foreign keys, ON DELETE/UPDATE rules, and nested CHECK expressions from `SHOW CREATE TABLE` instead of rebuilding a lossy subset, then plans add/modify/rename/drop-column, `CREATE TABLE ... LIKE` structure cloning, table rename, or reviewed constraint add/drop operations. Definitions may retain enum/set members, defaults, comments, generated expressions, character attributes, and other server-supported clauses; guided validation blocks statement delimiters, comments, unbalanced syntax, injected top-level ALTER clauses, unknown constraint columns, invalid SET NULL targets, and duplicate identities. Foreign-key column lists are ordered and must match the referenced list length. `drop-check` selects MySQL `DROP CHECK` or MariaDB `DROP CONSTRAINT` from the inspected server identity, while `drop-fk` warns that MySQL may retain its supporting index. The default only prints the exact DDL and warnings. `--apply` rechecks the reviewed `SHOW CREATE TABLE` SHA-256 and refuses a stale or newly occupied target. It must atomically persist a pending artifact containing the exact preimage and reviewed SQL before sending one DDL statement to MySQL, then re-inspects the result and upgrades the same artifact with the verified after-state under the running executable's `Backups\SqlSchema` directory. A pending artifact explicitly says the DDL was not verified; it is never presented as a completed receipt. Schema evidence does not preserve data removed by `drop`, so destructive/enforcement-removing operations remain explicit.

```powershell
wowcrucible db table-design 127.0.0.1 3306 admin acore_world custom_table add CrucibleNote --definition="varchar(255) DEFAULT NULL COMMENT 'Crucible note'" --after=entry --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db table-design 127.0.0.1 3306 admin acore_world custom_table clone --name=custom_table_copy --apply --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db table-design 127.0.0.1 3306 admin acore_world child_table add-fk fk_child_parent --columns=parent_id,parent_kind --references=parent_table --reference-columns=id,kind --delete=CASCADE --update=RESTRICT --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db table-design 127.0.0.1 3306 admin acore_world child_table add-check chk_level_range --expression="`minlevel` <= `maxlevel`" --password-env=WOW_CRUCIBLE_DB_PASSWORD
```

`db account ... grants` reports the exact `SHOW GRANTS` output for one user/host identity and can inspect the connected login even when it cannot enumerate `mysql.user`. The other account actions are dry-run plans unless `--apply` is explicit. Privilege names are checked against the connected server's `SHOW PRIVILEGES` output, scope defaults to the active database, `--table` narrows it, and `--global` deliberately selects `*.*`. New passwords are read only from `WOW_CRUCIBLE_NEW_DB_PASSWORD` or `--new-password-env`; they are parameterized for execution, shown as `<password supplied in memory>`, and never accepted in command arguments. Account changes can affect server access immediately, so verify them with `account ... grants` after apply.

`db join` accepts a relationship name printed by `db inspect`, generates exact uniquely aliased `source__...` and `target__...` columns, and prints SQL without executing it by default. `--run` executes only that generated SELECT. `db index` likewise prints reviewed DDL by default; `--apply` is required to create or drop an index, ordinary primary-key removal is blocked, and MySQL may implicitly commit DDL.

`db objects` discovers views, triggers, procedures, functions, and scheduled events from the active schema; `object-show` prints the server's exact `SHOW CREATE` definition. `object-export` publishes all exact definitions atomically with delimiter wrappers and preserves `DEFINER` clauses while warning that they must be reviewed before cross-server import. `object-drop`, `view-set`, and `event-state` print reviewed DDL unless `--apply` is explicit. Guided views accept exactly one independently validated `SELECT`; batches, writes, `SHOW`-family statements, and `SELECT` file output are blocked. DROP binds to an object discovered by both type and name, and event state changes accept only a discovered scheduled event. The same responsive controls live under SQL Studio → Schema & server → Database objects.

`db query` reads a UTF-8 SQL file so the statement does not need to be pasted into shell history. Without `--write`, the core service accepts only `SELECT`, `SHOW`, `DESCRIBE`, `DESC`, or `EXPLAIN`, including after leading SQL comments. Every other statement is rejected before it reaches MySQL, and `SELECT ... INTO OUTFILE/DUMPFILE` is also blocked. `--batch` enables up to 32 semicolon-separated read statements; the splitter understands quoted values, quoted identifiers, and SQL comments, validates each statement independently, and prints every differently shaped result in text or JSON. A single read result can be atomically exported with `--output`; CSV preserves `NULL` as `\N`, while JSONL uses JSON null and stable unique names when a query returns duplicate column labels. Binary cells are hexadecimal. Existing files require `--overwrite`, and cancellation never publishes a partial result. `--write` is the explicit automation path for a previously reviewed statement and cannot be combined with batch or result-export switches; remember that MySQL DDL can implicitly commit.

`db export` discovers the live table shape and streams the complete table to an atomically published UTF-8 CSV or newline-delimited JSON file. CSV uses `\\N` for SQL `NULL`, quotes commas/newlines correctly, and represents binary values as hexadecimal. Existing outputs require `--overwrite`.

`db import` is a structural dry-run by default: it validates the header against the live schema, rejects unknown/generated/duplicate columns, checks required fields and every row width, and reports the planned row count. `--apply` is required to write. Apply is INSERT-only in one transaction; an existing key or any row error rolls back the complete import instead of replacing data.

`db dependency-snapshot` reads one row by its complete primary key, discovers current-schema declared relationships plus validated AzerothCore item/quest/loot/creature/gameobject/spell/trainer/appearance mappings, and captures complete matching rows for each exact edge. Repeat `--key` for a composite identity. The per-edge limit defaults to 200 and is capped at 500; every truncation is explicit. An empty SQL `_dbc` mirror is recorded as a file-DBC edge instead of falsely reporting a broken reference. The artifact is review data and never executable SQL.

```powershell
wowcrucible db dependency-snapshot 127.0.0.1 3306 acore acore_world item_template item-17802.crucible-dependencies.json --key=entry=17802
```

`db draft-template` creates a portable, editable JSON starting point without connecting to MySQL. Supported domains are `creature`, `gameobject`, `quest`, `gossip-menu`, `gossip-option`, `npc-text`, `trainer`, `trainer-spell`, `trainer-creature`, `legacy-trainer-spell`, `pet-level-stats`, `pet-name-part`, `pet-name-locale`, `spell-pet-aura`, `condition`, and `smartai`. `db content-plan` adapts the draft to the actual live schema and prints exact SQL without writing by default. The quest adapter covers the complete 105-field stock 3.3.5a template; schema-adaptive behavior and pet adapters cover every current/custom column while preserving exact composite keys. `--apply` inserts the primary and child rows in one transaction. `--apply --update` requires the primary identity to match exactly one existing row, updates mapped primary fields while preserving custom columns, and inserts only collision-free newly staged child rows; it never silently replaces existing children.

```powershell
wowcrucible db draft-template smartai smartai.json
wowcrucible db content-plan 127.0.0.1 3306 acore acore_world smartai smartai.json --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db draft-template pet-level-stats pet-level-80.json
wowcrucible db content-plan 127.0.0.1 3306 acore acore_world pet-level-stats pet-level-80.json --password-env=WOW_CRUCIBLE_DB_PASSWORD
```

`db pet-curve` reads every requested `pet_levelstats` row from an existing source creature and creates a target curve with the same per-level shape. Health, mana, armor, all five attributes, and minimum/maximum damage have independent invariant-decimal scale factors; unknown live columns are retained unchanged. A source gap, composite-key mismatch, numeric overflow, invalid damage range, or changed target/schema blocks the operation. The default is a dry-run SQL preview. `--apply` inserts only missing target levels and preserves existing rows; replacing exact existing levels additionally requires `--update-existing`. Both policies run in one transaction and never delete levels.

The desktop exposes this under **Pets & companions → Bulk level curve…** without opening another application window. `WoWCrucible.Desktop-latest.exe --pet-curve` launches directly into it.

```powershell
wowcrucible db pet-curve 127.0.0.1 3306 admin acore_world 416 900416 --levels=1-80 --health=1.25 --damage=1.1 --output=review-pet-curve.sql --password-env=WOW_CRUCIBLE_DB_PASSWORD
```

`db pet-compare` reads two curves without changing them. Every numeric live/custom column becomes a metric with exact per-level left/right values, start-to-end growth, end-level delta, average normalized delta, and paired-level count. Missing levels remain explicit and produce review exit code `3`; they are never interpolated. Omit `--metric` for all metric summaries, or select one raw column name for its complete level table. JSON preserves the same structured points and coverage lists. The desktop **Family comparison** tab renders the selected metric as a responsive static two-line plot beside the exact table.

`db pet-preview` follows one creature's live `creature_template` plus normalized `creature_template_model` mapping (or legacy `modelid1`–`modelid4`) through build-12340 `CreatureDisplayInfo.dbc` and `CreatureModelData.dbc`. With `--library`, it reports every exact content-first provenance containing the logical M2 and whether that same provenance also contains its view-0 SKIN and creature texture variations. It never substitutes a texture or SKIN from a different patch source. The desktop **Companion preview** tab renders the same selectable sources side-by-side with base-appearance geosets and no fixed panel dimensions.

`db pet-graph` combines the connected world's current creature row and normalized or legacy template spell slots with exact build-12340 `CreatureSpellData`, `CreatureFamily`, `SkillLine`, `SkillLineAbility`, `TalentTab`, and `Talent` records. It also includes every matching global or creature-specific `spell_pet_auras` row. Text and JSON retain every unique node, every rank/prerequisite/supersession relationship, and the exact SQL or DBC evidence for every edge; filtered desktop lists do not delete graph data. SQL reads page until the exact match count is exhausted rather than silently stopping at 500 custom aura rows. The same-window **Talent & ability graph…** route provides a responsive selected-neighborhood drawing beside exhaustive searchable node and evidence tabs. `WoWCrucible.Desktop-latest.exe --pet-graph` launches directly into it.

```powershell
wowcrucible db pet-compare 127.0.0.1 3306 admin acore_world 416 17252 --levels=1-80 --metric=hp --format=json --output=imp-vs-felhunter.json --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db pet-preview 127.0.0.1 3306 admin acore_world 416 --dbc="C:\server\data\dbc" --schema="C:\definitions\WotLK 3.3.5 (12340).xml" --library="G:\Crucible-Extras-Processed" --password-env=WOW_CRUCIBLE_DB_PASSWORD
wowcrucible db pet-graph 127.0.0.1 3306 admin acore_world 69 --dbc="C:\server\data\dbc" --schema="C:\definitions\WotLK 3.3.5 (12340).xml" --format=json --password-env=WOW_CRUCIBLE_DB_PASSWORD
```

`db snapshot` is phase one of legacy SQL recovery. It issues only fixed metadata queries and `SELECT` statements, requests a consistent read-only transaction where the server supports one, streams base-table rows without loading the database into memory, and publishes the compressed artifact atomically. By default it verifies that the selected schema looks like a world database and excludes known auth/character runtime-state tables while retaining reusable definitions such as `mail_loot_template`, `instance_template`, `guild_rewards`, and `pet_levelstats`. Repeat `--include` or `--exclude` for table-name globs. `--include-sensitive` deliberately removes the runtime-state safety filter; those captured rows may themselves contain account secrets even though the live connection password is never serialized.

`db snapshot-inspect` performs offline schema, entry, byte-count, row-count, and SHA-256 integrity checks. The default also decodes every row structure; `--quick` still reads and hashes every table but skips structural decoding.

`db recovery-audit` is the read-only phase-two boundary. With `--baseline`, it emits an atomic, compressed, source-hash-bound audit containing keyed added, modified, and removed rows plus exact before/after field values. Without a baseline, it labels every emitted row an **unattributed candidate** instead of pretending stock rows are custom work. Tables are grouped into content domains for review. A table without a primary key, a collation-dependent/textual key that snapshot format v1 cannot order portably, another incompatible key, a partial schema, or an excluded counterpart is reported but never compared by capture order. Known runtime-state tables remain excluded unless `--include-sensitive` is explicit. `db recovery-inspect` verifies the audit offline; `--quick` still hashes all change streams. The audit itself always records `PromotionReady=false` because it remains immutable evidence; the separate target-bound `sync-plan` step may advance compatible selected rows, while cross-core translation and automatic dependency-closure selection remain explicit later work.

`db sync-plan` advances a verified matching-core baseline audit into a target-bound synchronization review. It compares each selected primary-keyed operation with the live target using the audit's typed before/after values and labels it ready, already applied, conflicting, or blocked. Source removals are excluded unless `--include-removals` is explicit; unknown/unattributed rows are never promotable. Missing tables/columns, required target-only INSERT fields, missing modified rows, and target values that differ from both baseline and edited content remain visible blockers. An occupied numeric single-column key stays a conflict by default. `--auto-remap` instead allocates an ID above the target table's live maximum (and optional `--remap-start` floor), then rewrites matching selected parent keys plus declared/recognized selected references. Every mapping and rewrite count is printed and hash-bound into the review plan; Crucible never silently changes unrelated target rows. The plan contains no password and is bound to the exact host, port, user, and database.

`db sync-inspect` verifies the audit identity, target binding, selection policy, and complete operation stream through one content hash and can export a deliberately non-committing SQL preview. `db sync-apply` is dry-run unless `--apply` is explicit and refuses the entire plan while any conflict or blocked operation remains. Apply sorts declared dependencies, opens one transaction, locks and rechecks every row, requires exactly one affected row per operation, and publishes an immutable target-bound receipt only after commit. `db sync-rollback --apply` verifies that receipt and reverses it in dependency-safe order only when every current postimage still matches; a stale row aborts the complete rollback. The same controls live in Server & SQL → Recover legacy SQL edits without opening another window.

`item-audit` discovers the current schema instead of assuming one core revision. It checks vendors, achievement rewards, creature/gameobject/item/mail/pickpocket/skinning/disenchant/fishing/spell/reference/prospecting/milling loot, SQL character starting items, usable and system-granted quest rewards/start items, and every reward/choice slot that exists. Loot rows are causal: creature/gameobject IDs must be template-owned, mail templates must have a recognized sender system, item-processing pools require an acquired input, spell-loot requires a reachable spell, and reference pools require a reachable parent. `--dbc` reads `CharStartOutfit.dbc`, `AreaTable.dbc`, and `Spell.dbc`: normal starting equipment is not always represented in `playercreateinfo_item`, valid fishing owners come from client areas (with the SQL fishing-skill table as fallback), and crafted/conjured outputs live in create-item spell effects. Spell outputs count only when the spell is reachable through a trainer, character-start action, usable quest reward, an already acquired item's use/learn spell, or a nested learn/trigger-spell edge; simply existing as an unused Spell row is insufficient. Quest rewards normally require a live starter and ender and must not be disabled; LFG reward quests are accepted through their explicit core-owned table, and event quest-starter relations are recognized. The report calls an item **no known acquisition path**, not certainly unobtainable, because custom server scripts and core code can grant items without a discoverable SQL/DBC relationship. Each reported row also carries a review group (`OtherManualReview`, `DeprecatedTestOrDeveloper`, or `NpcOrMonsterEquipment`). Triage uses names plus explicit metadata/source evidence: stock WotLK Artifact quality is flagged for internal/test/cut review and deprecated/disabled-source findings are retained, but neither signal changes the acquisition result. Repeat `--id=N` or use `--ids=N,N` to print only exact requested rows—including known-path rows—plus their evidence; a missing requested ID returns review exit code `3`. `--format=json` emits the same bounded selection. When `--output` is supplied without an ID selection, the complete JSON report is written without redundantly flooding stdout with every no-path row.

```powershell
wowcrucible db item-audit 127.0.0.1 3306 acore acore_world --ids=17,17802 --dbc="C:\Server\data\dbc" --format=json
```

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
