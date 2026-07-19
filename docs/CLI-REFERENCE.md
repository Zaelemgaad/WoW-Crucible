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

## Portable content projects and ID reservations

```text
wowcrucible project create <folder> <name> [--target=wotlk-12340] [--asset-library=folder]
wowcrucible project status <project-folder>
wowcrucible project reserve-ids <project-folder> <domain> <count> [--start=N] [--occupied=ids.txt] [--purpose=text]
wowcrucible project occupancy <domain> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--format=text|json]
wowcrucible project reserve-live <project-folder> <domain> <count> <host> <port> <user> <database> --dbc=folder --schema=schema.xml [--start=N] [--purpose=text]
```

A content project separates Assets, DBC, SQL, Manifests, Reports, and Staging outputs and keeps `ids.crucible.json` as its durable allocation registry. `occupancy` reports every required SQL/DBC identity source; `reserve-live` writes a reservation only when all mapped sources were read successfully. WotLK race/class allocation stops at ID 31, and mount/spell reservations share the Spell namespace because mount IDs are spell IDs. The password comes from `WOW_CRUCIBLE_DB_PASSWORD` by default. `reserve-ids --occupied` remains the explicit manual route for custom or not-yet-mapped domains; omitting the occupied list returns review exit code `3`.

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
wowcrucible asset dependency-graph <processed-library> <root.m2|wmo|adt|wdt> [--target-index=client-index] ["--target-choice=client-path|archive"]... [--only-problems] [--manifest=patch.json] [--output-mpq=name.MPQ] [--format=text|json]
wowcrucible asset preview-info <wrath-model.m2> [--dbc=folder] [--hair=N] [--facial-hair=N] [--animation=sequence-index] [--time=milliseconds] [--naked|--groups=group:variant,...|--all-geosets]
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

The same native inspection/workspace flow is available under **Modern asset conversion** in the desktop navigation. It accepts recursive drag/drop, keeps source and dependency snapshots immutable and hash-verified, reopens moved `conversion-report.json` workspaces, and previews M2 files that are already compatible with WotLK 3.3.5a. A successful inspection does not claim that a modern MD21 model has been converted; output writing remains intentionally blocked until its translated structures can be validated.

`preview-info` reports every Wrath animation sequence. Add `--animation=N --time=MS` to resolve aliases and external `.anim` files, sample the weighted bone pose at an exact time, and report its resulting bounds. This is the command-line diagnostic for the same animation path used by the in-window model preview.

`model-export` writes the exact currently requested visible mesh as Wavefront OBJ/MTL. `--animation=N --time=MS` exports the sampled weighted pose; without it the bind mesh is exported. `--texture=slot:file.blp` decodes a real M2 texture definition to a neighboring PNG and binds it in the MTL. A `.crucible-model.json` receipt retains source M2/SKIN hashes, geoset selection, animation time, and the original WoW render flags/blend modes that OBJ cannot represent exactly. Existing outputs are refused unless `--overwrite` is explicit. The same export is available above the live model in Asset Compare and snapshots the current scrubbed pose before background writing.

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

The visual workspace can sort cards by source/name, filename, or file size in either direction. `Scan exact copies` first narrows candidates by byte length, verifies SHA-256, and finally performs a streaming byte-for-byte comparison. It only labels exact groups and enables a collapsed comparison view; it never deletes source or processed assets. The preview-pane selector can switch from synchronized left/right images to compatible WotLK M2 + SKIN sources found by the automatic model browser. The embedded model view is live and rotatable. With no manual candidate selected it parses SKIN material units, resolves the M2 texture lookup for each visible submesh, and decodes every used embedded BLP from the same provenance layer in the background. Playable-race paths load the complete relevant `CharSections.dbc` records, expose skin/face/facial-hair/hair selectors, layer underwear, face, facial hair, and scalp into the standard 256-based character atlas, scale those verified regions for HD atlases, and bind the separate hair material. The selected hair and facial-hair variations are also resolved through `CharHairGeosets.dbc` and `CharacterFacialHairStyles.dbc`. Items & Sets calls that same core appearance service before applying item wear textures and equipment geosets, so its preview no longer depends on a blank manual skin atlas. The same-window geoset inspector names every group and reports its exact IDs, section counts, and triangle counts. Character mode applies the DBC choices, Naked mode suppresses hair/facial features, and Manual mode allows one exact variant or Hidden per group; only **Everything stacked** deliberately combines mutually exclusive styles. The attachment inspector separately validates the M2 bone table, attachment records, and lookup table, names the native helmet/shoulder/weapon/sheath/effect/vehicle points, and highlights the selected bind position on the live model without guessing. A single texture candidate or a set proven byte-for-byte identical is selected automatically; non-identical provenance variants remain unselected until the user chooses one. Missing component files in that chosen provenance are named rather than silently borrowed from a different mod. Crucible currently chooses the first valid texture pass for each submesh; applying a selected PNG deliberately overrides every material for diagnostic comparison. Child-item mounting and multi-pass shader blending remain explicit fidelity work. `asset preview-info` now lists every named group and submesh in addition to texture slots, material units, compact render batches, validated bone counts, and each attachment's name, bone, bind position, and lookup slots. `--naked` suppresses appearance geometry, `--groups=0:3,7:1` applies exact group overrides, and `--all-geosets` is intentionally stacked. `asset appearance-info` reports the race/sex inference and available base-skin records; `asset appearance-render` resolves explicit CharSections choices and provenance from a processed library and writes the composed body plus optional hair texture; `asset appearance-compose` remains the low-level manual atlas command.

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

`merge` treats every source MPQ as immutable. Repeated internal paths are SHA-256 checked and then compared byte-for-byte; exact copies are stored once. Different bytes at the same internal path block output by default. `--conflicts=earlier` or `--conflicts=later` is an explicit global precedence choice. Hash-only `File000...` names and duplicate locale variants are blocked unless their real paths can be resolved safely. Merge payloads use short flat temporary names while logical archive paths remain separate, so deeply nested project/output locations do not exceed StormLib's Windows destination-path limit.

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
wowcrucible server dbc-audit <installed-server-folder> <dbc-file-or-name> <schema.xml> [--source=core-source] [--all] [--summary] [--migration=sync.sql] [--bundle=folder]
wowcrucible server dbc-apply <installed-server-folder> <bundle-folder>
wowcrucible server dbc-rollback <installed-server-folder> <deployment-receipt.json>
wowcrucible server dbc-module-export <bundle-folder> <module-root>
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
wowcrucible db query <host> <port> <user> <database> <statement.sql> [--batch] [--batch-format=text|json] [--output=result.csv|jsonl] [--format=csv|jsonl] [--overwrite] [--write] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db export <host> <port> <user> <database> <table> <output> [--format=csv|jsonl] [--overwrite] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db import <host> <port> <user> <database> <table> <input.csv> [--apply] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db dependency-snapshot <host> <port> <user> <database> <table> <output.json> --key=column=value [--key=column=value]... [--limit=N] [--overwrite]
wowcrucible db draft-template <domain> <output.json> [--overwrite]
wowcrucible db content-plan <host> <port> <user> <database> <domain> <draft.json> [--output=plan.sql] [--overwrite] [--apply] [--update] --password-env=ENV_NAME [--ssl=Preferred]
wowcrucible db snapshot <host> <port> <user> <database> <output.crucible-db-snapshot> [--password-env=ENV_NAME] [--ssl=Preferred] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]
wowcrucible db snapshot-inspect <snapshot-file> [--quick]
wowcrucible db recovery-audit <legacy-snapshot> <output.crucible-db-audit> [--baseline=stock-snapshot] [--include=glob]... [--exclude=glob]... [--include-sensitive] [--overwrite]
wowcrucible db recovery-inspect <audit-file> [--quick]
wowcrucible db favorites <host> <port> <user> <database> [--search=text] [--verify] [--format=text|json]
wowcrucible db sync-plan <host> <port> <user> <database> <verified-audit> <plan.json> [--include=glob]... [--include-removals] [--auto-remap] [--remap-start=ID] [--maximum=N] [--overwrite]
wowcrucible db sync-inspect <plan.json> [--sql=preview.sql] [--overwrite]
wowcrucible db sync-apply <host> <port> <user> <database> <plan.json> <receipt.json> [--apply] [--overwrite]
wowcrucible db sync-rollback <host> <port> <user> <database> <receipt.json> [--apply]
wowcrucible db item-audit <host> <port> <user> <database> [--password-env=ENV_NAME] [--dbc=server-dbc-folder] [--output=report.json]
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

`db draft-template` creates a portable, editable JSON starting point without connecting to MySQL. Supported domains are `creature`, `gameobject`, `quest`, `gossip-menu`, `gossip-option`, `npc-text`, `trainer`, `trainer-spell`, `trainer-creature`, `legacy-trainer-spell`, `condition`, and `smartai`. `db content-plan` adapts the draft to the actual live schema and prints exact SQL without writing by default. The quest adapter covers the complete 105-field stock 3.3.5a template; behavior adapters cover all current gossip, trainer, condition, NPC-text, and SmartAI columns while preserving composite keys. `--apply` inserts the primary and child rows in one transaction. `--apply --update` requires the primary identity to match exactly one existing row, updates mapped primary fields while preserving custom columns, and inserts only collision-free newly staged child rows; it never silently replaces existing children.

```powershell
wowcrucible db draft-template smartai smartai.json
wowcrucible db content-plan 127.0.0.1 3306 acore acore_world smartai smartai.json --password-env=WOW_CRUCIBLE_DB_PASSWORD
```

`db snapshot` is phase one of legacy SQL recovery. It issues only fixed metadata queries and `SELECT` statements, requests a consistent read-only transaction where the server supports one, streams base-table rows without loading the database into memory, and publishes the compressed artifact atomically. By default it verifies that the selected schema looks like a world database and excludes known auth/character runtime-state tables while retaining reusable definitions such as `mail_loot_template`, `instance_template`, `guild_rewards`, and `pet_levelstats`. Repeat `--include` or `--exclude` for table-name globs. `--include-sensitive` deliberately removes the runtime-state safety filter; those captured rows may themselves contain account secrets even though the live connection password is never serialized.

`db snapshot-inspect` performs offline schema, entry, byte-count, row-count, and SHA-256 integrity checks. The default also decodes every row structure; `--quick` still reads and hashes every table but skips structural decoding.

`db recovery-audit` is the read-only phase-two boundary. With `--baseline`, it emits an atomic, compressed, source-hash-bound audit containing keyed added, modified, and removed rows plus exact before/after field values. Without a baseline, it labels every emitted row an **unattributed candidate** instead of pretending stock rows are custom work. Tables are grouped into content domains for review. A table without a primary key, a collation-dependent/textual key that snapshot format v1 cannot order portably, another incompatible key, a partial schema, or an excluded counterpart is reported but never compared by capture order. Known runtime-state tables remain excluded unless `--include-sensitive` is explicit. `db recovery-inspect` verifies the audit offline; `--quick` still hashes all change streams. The audit itself always records `PromotionReady=false` because it remains immutable evidence; the separate target-bound `sync-plan` step may advance compatible selected rows, while cross-core translation and automatic dependency-closure selection remain explicit later work.

`db sync-plan` advances a verified matching-core baseline audit into a target-bound synchronization review. It compares each selected primary-keyed operation with the live target using the audit's typed before/after values and labels it ready, already applied, conflicting, or blocked. Source removals are excluded unless `--include-removals` is explicit; unknown/unattributed rows are never promotable. Missing tables/columns, required target-only INSERT fields, missing modified rows, and target values that differ from both baseline and edited content remain visible blockers. An occupied numeric single-column key stays a conflict by default. `--auto-remap` instead allocates an ID above the target table's live maximum (and optional `--remap-start` floor), then rewrites matching selected parent keys plus declared/recognized selected references. Every mapping and rewrite count is printed and hash-bound into the review plan; Crucible never silently changes unrelated target rows. The plan contains no password and is bound to the exact host, port, user, and database.

`db sync-inspect` verifies the audit identity, target binding, selection policy, and complete operation stream through one content hash and can export a deliberately non-committing SQL preview. `db sync-apply` is dry-run unless `--apply` is explicit and refuses the entire plan while any conflict or blocked operation remains. Apply sorts declared dependencies, opens one transaction, locks and rechecks every row, requires exactly one affected row per operation, and publishes an immutable target-bound receipt only after commit. `db sync-rollback --apply` verifies that receipt and reverses it in dependency-safe order only when every current postimage still matches; a stale row aborts the complete rollback. The same controls live in Server & SQL → Recover legacy SQL edits without opening another window.

`item-audit` discovers the current schema instead of assuming one core revision. It checks vendors, achievement rewards, creature/gameobject/item/mail/pickpocket/skinning/disenchant/fishing/spell/reference/prospecting/milling loot, SQL character starting items, usable and system-granted quest rewards/start items, and every reward/choice slot that exists. Loot rows are causal: creature/gameobject IDs must be template-owned, mail templates must have a recognized sender system, item-processing pools require an acquired input, spell-loot requires a reachable spell, and reference pools require a reachable parent. `--dbc` reads `CharStartOutfit.dbc`, `AreaTable.dbc`, and `Spell.dbc`: normal starting equipment is not always represented in `playercreateinfo_item`, valid fishing owners come from client areas (with the SQL fishing-skill table as fallback), and crafted/conjured outputs live in create-item spell effects. Spell outputs count only when the spell is reachable through a trainer, character-start action, usable quest reward, an already acquired item's use/learn spell, or a nested learn/trigger-spell edge; simply existing as an unused Spell row is insufficient. Quest rewards normally require a live starter and ender and must not be disabled; LFG reward quests are accepted through their explicit core-owned table, and event quest-starter relations are recognized. The report calls an item **no known acquisition path**, not certainly unobtainable, because custom server scripts and core code can grant items without a discoverable SQL/DBC relationship.

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
