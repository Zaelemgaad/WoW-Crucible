# Reference tool audit

> Consolidation policy update: earlier notes that describe a capability as “keep external” now mean reference-only during implementation, not a permanent product boundary. The finished capability must be native and maintainable in Crucible. See [TOOL-CONSOLIDATION-MATRIX.md](TOOL-CONSOLIDATION-MATRIX.md) for the complete replacement contract.

WoW Crucible uses the local legacy tools as workflow research, not as compatibility authorities. Their strongest ideas are reimplemented against Crucible's build-12340 client model and capability-detected current server adapters.

Age, abandonment, poor performance, broken dependencies, or an unusable current build are never sufficient reasons to dismiss a tool. Every tool is evaluated for the problem that caused it to exist, its intended complete workflow, useful interaction patterns, format knowledge, failure modes, licensing, and whether the capability still belongs in a unified Crucible workflow. A broken implementation can still describe a feature worth rebuilding independently.

No Trinity Creator source or visual assets are copied into Crucible. The local Trinity Creator checkout does not include a clear source license, so it is treated strictly as behavioral reference material.

## Trinity Creator

Useful patterns:

- Starts with named authoring jobs: item, quest, creature, loot, vendor, and model viewing.
- Groups fields by the concept a user is editing instead of exposing database columns first.
- Uses readable enumerations, bitmask checklists, contextual labels, and nearby lookup buttons.
- Provides live item-tooltip and quest-dialog previews in the vocabulary users see in game.
- Supplies intent-based creature templates such as boss, questgiver, vendor, repair vendor, and escort NPC.
- Calculates or suggests dependent values such as prices, IDs, and item-derived properties.
- Keeps lookup tools available beside the active editor.
- Separates SQL-file export from direct database deployment.
- Attempts profile-driven table/column mapping.

Ideas to retain but redesign:

- Templates should be portable content intents with a visible change preview, not UI code that directly sets old database fields.
- Database writes must use transactions, parameterized commands, schema introspection, and a generated SQL preview. Trinity Creator's string-built queries and delete-then-insert overwrite behavior are not acceptable deployment foundations.
- Profiles should describe detected capabilities, not claim compatibility based only on an emulator/date label.
- Suggested defaults must explain what they changed and remain reversible.

## Keira3

Useful patterns:

- Searchable selectors for related records rather than bare numeric IDs.
- Reusable multi-row editors for loot, conditions, and other child collections.
- Immediate full-query and changed-fields-only SQL previews.
- Editors organized around current AzerothCore domain models.
- Shared option catalogs, validation, and consistent selector buttons.

Crucible must keep Keira's AzerothCore assumptions behind an AzerothCore adapter and separately support current TrinityCore 3.3.5 capabilities.

## WoW Database Editor

Useful patterns:

- Definition-driven editors and parameters.
- Solution/project organization for related changes.
- Visual quest chains, world-map context, smart-script tooling, SQL interpretation, and source integration.
- Modular providers that separate core-specific behavior from shared editing infrastructure.

Its breadth is a useful architectural reference; Crucible should avoid reproducing its complexity in beginner workflows.

## WDBX Editor and MyDbcEditor

Useful patterns:

- Broad format/schema coverage and external definition corpora.
- Staging multiple tables and generic raw editing.
- Fixed-value catalogs that turn numeric cells into named choices.

Crucible retains the raw table as a power-user escape hatch while domain workspaces become the normal authoring surface.

## MPQ Editor and StormLib tools

Useful patterns:

- Familiar archive tree/list browsing, filters, extraction, and explicit internal paths.

Crucible adds background operations, small manifest-driven patch construction, content policies, archive validation, and refusal to copy-update giant source layers.

## The two WoW Model Viewer projects

The similarly named projects solve materially different problems:

- `Miorey/wow-model-viewer` is a small ISC JavaScript integration wrapper. Its convenient API can display characters, equipment, items, NPCs, objects, animations, and appearance changes in a page, but the actual renderer and game-data delivery come from minified Wowhead/ZAM assets, remote services, and a CORS proxy/cache. It is useful interaction research but cannot be Crucible's primary renderer: custom local server assets may not exist in that content service, upstream internals change, and the underlying renderer/data do not inherit the wrapper's simple licensing.
- `wowmodelviewer/wowmodelviewer` is the full GPLv3 desktop application. Its local model/character composition, equipment attachment, geosets, textures, animation, particles, creature/item display lookup, CASC browsing, and export behavior are the capability reference. Directly incorporating or deriving Crucible from its renderer is legally possible, but the combined application would need GPLv3-compatible distribution rather than MIT-only distribution. That is a project licensing choice, not a reason to reject its features. The current application is also C++/Qt/wxWidgets/OpenGL and oriented around CASC rather than Crucible's C#/WinForms and build-12340 MPQ stack, so licensing alone does not make it an embeddable preview component.

Crucible therefore implements an independent embedded preview pipeline. It parses validated Wrath `MD20` vertex data including both UV streams, bones, animation tracks, attachment and attachment-lookup data, native camera/light/particle/ribbon records, texture definitions, every referenced texture/coordinate/weight/transform lookup, material units, and companion `SKIN` topology. Every valid matching SKIN/LOD view remains selectable rather than being collapsed into one guessed companion. The renderer resolves decoded character appearances and geosets, highlights named native bind points, mounts animated equipment and paired shoulders, offers native perspective cameras without displacing the normal orbit view, applies animated embedded lights, samples bounded plane/sphere particle motion and native three-stage sprite life ramps, decodes and composes all three packed five-bit texture indices for build-264 multi-texture particles, reconstructs bounded textured ribbon trails from past bone poses, and keeps each model/texture inside its selected provenance. Common legacy two-stage materials use independently implemented isolated Skia passes and their declared UV routes; explicit shader 0/1/6 two-stage families use separately testable alpha-weighted Mod2xNA and additive-alpha pass plans, while shader 3 adds its third primary-UV additive-alpha stage. Fixed-function environment stages use view-space sphere-map coordinates derived from Crucible's animated normals. Remaining limitations in per-combiner alpha, other explicit/three-plus-stage shaders, spline particles, and exact effect ordering are surfaced as approximation or fallback evidence. No renderer source from the reference applications is incorporated. The same control is shared by item, creature/NPC, gameobject, race/class appearance, spell-visual, and conversion workflows rather than becoming another standalone viewer.

The independently implemented explicit-material coverage now also handles shader 2's base-alpha-masked environment contribution, shader 5's primary/primary additive-alpha route, shader 21's primary/secondary modulation with animated-normal linear view-angle opacity, and shader 23's primary/primary alpha interpolation. Real processed version-264 model pairs validate shaders 2, 21, and 23; the encountered shader-5 SKIN is orphaned, so its end-to-end claim is deliberately withheld and its route is protected by a synthetic build-264 structure test. Baby Fire Elemental and Druid Flight Form validate shader 21 end to end, while Tol'vir Dome and Corrupted Goblin converted LOD pairs are correctly rejected because their SKIN lookups exceed their paired M2 vertex arrays. These routes remain approximate until exact client fade and alpha-output rules are reproduced.

The preview consumes the editor's in-memory draft/change plan first. Saving DBC/SQL, building an MPQ, clearing the game cache, and restarting a server/client are deployment verification steps—not prerequisites for seeing the intended result. Where exact client behavior cannot yet be simulated, Crucible must label the approximation and show which dependencies or runtime behaviors still require an in-client test.

## Expanded local Tools collection

The July 2026 workspace review covered the added Amaroth Toolpack directories plus the newer standalone DBC/DB2, CASC, model, map, texture, and spell utilities under `Tools`. The pack README dates the Amaroth bundle to December 2019 and says several included utilities were last updated in 2017 or earlier. Most binaries have no local source license. Crucible must not copy or redistribute their binaries or source unless the license is explicit and compatible; the workflows and file formats can be independently implemented.

### High-value workflows to implement natively

- **WDBX/WoWDBDefs schema providers and cross-checker — native foundation landed.** Crucible parses exact build-specific WDBX XML plus full expansion/build DBD ranges, logical columns, arrays, localized strings, widths, signedness, ID annotations, and non-inline fields, with same-window and CLI corpus audits. Either exact provider can authorize a table while the other stays visible as coverage evidence. The real build-12340 server corpus now resolves all 245 non-empty WDBC files through at least one exact provider plus one explicit zero-byte `CharVariations` placeholder; the known DBD-only gaps and disagreement remain visible without vetoing exact XML. The fixed-layout WDB2 provider resolves build 15595 from each file and preserves header/index/string-length/copy metadata. A real 15595 locale-cache corpus of 20 WDBC and 5 WDB2 tables passes 25/25 exact schema checks and byte-identical unchanged writes; all 25 corresponding raw update-layer `PTCH` entries are classified as deltas instead of complete tables. Complete base-plus-delta reconstruction, the full base corpus, and later DB2 container families remain pending.
- **Spell/table editing database workspace — native foundation landed.** Every exactly resolved DBC can now create a project-local SQLite workspace with decoded named columns, immutable baseline/identity triggers, touched-row diff tracking, bounded named-binding reads, and separately reviewed bulk `UPDATE`/`INSERT`. Source and schema fingerprints are rechecked before reviewed changes pass through the normal stale-safe DBC importer into an explicit output or the open desktop document. The portable DBC remains authoritative and the staging database is never confused with AzerothCore's `spell_dbc` or other SQL overlays. Richer visual query construction and project rebase/merge remain follow-up work.
- **Client item synchronization — implemented natively and losslessly.** The old ClientItem rebuilt `Item.dbc` solely from `item_template`, while its companion inserted partial synthetic server rows to avoid deleting client-only/NPC equipment IDs. Crucible instead compares all eight WotLK client fields against a live schema-adapted snapshot, validates every display dependency, preserves every existing client-only row, and plans only missing or actually different SQL-backed records. Apply binds source/schema/display/snapshot hashes, writes a new binary DBC plus ASCII WMV catalog, strict manifest/receipt, and ready tiny MPQ; it never writes fake templates or mutates SQL/selected DBCs. The desktop additionally rechecks the live snapshot immediately before output.
- **Listfile/WMO closure and Amaroth Toolkit — implemented natively.** WMOListFile recursively scraped strings from already extracted WMO/M2/MDX/ADT files and required repeated destructive archive preprocessing plus manual merged-mode extraction. Crucible's processed-library and indexed-client dependency graph instead resolves WMO groups, textures, doodad models, each doodad's M2/SKIN/BLP closure, and ADT/WDT references transitively with exact provenance, hashes, missing/ambiguous blockers, direct extraction, and strict manifests. Toolkit's other substantive listfile-to-gameobject and item SQL/DBC actions are covered by the native collision-safe bulk GameObject generator and lossless Item client synchronizer rather than CSV append scripts.
- **Player launcher/distribution — native offline/local foundation landed; authenticated transport remains.** AmarothLauncher used web filelists, optional linked groups, broad outdated-MPQ pruning, ZIP deployment, self-update, changelog/news, developer/player channels, and FTP editing. Crucible now independently implements the maintainable local core: immutable copied release bundles; content identity and SHA-256 for every required/optional payload; changelog/channel metadata; complete target-preimage plans; explicit unowned replacements; exact prior-release ownership; removal only when an owned path still has its installed hash; verified pre-mutation backups/staging; pending/committed recovery receipts; target-client Wow closure; mandatory Cache invalidation; and postimage/state-bound rollback. Desktop and dry-run-first CLI share the provider. It does not claim to be a public network updater yet: authenticated channel manifests/signing-key trust, resumable HTTP transport, hardened publication, news delivery, and launcher self-update remain assigned, and the insecure FTP/unsigned-filelist design is not reproduced.
- **NPC appearance generator — implemented natively.** Crucible strictly parses the useful 18-line WMV `.chr` contract without consuming Amaroth code or runtime binaries, resolves the normalized model path against the actual target `CreatureModelData`, maps all eleven armor entries through the verified live `item_template`, and exposes unresolved items plus every preserved weapon/quiver/unknown/trailing value. Its deterministic plan reuses semantic DBC duplicates, allocates only genuinely missing extra/display IDs, binds all inputs by SHA-256, and writes a new-only bundle containing changed target-based DBCs, a content-derived baked BLP path, strict manifest/receipt, and a ready tiny MPQ. The resulting display is handed to Crucible's normal live-schema creature template planner; incompatible equipment semantics are reported instead of guessed. Same-window and CLI routes share the provider.
- **Gameobject generator — implemented natively for extracted and indexed MPQ sources.** Crucible's same-window/CLI bulk workflow consumes selected M2/root-WMO files or folders, maps content-first processed provenance or an explicit extracted-client root, or resolves virtual paths directly through a complete client MPQ index. Indexed mode records the effective archive layer and physical entry, recursively snapshots dependencies, blocks ambiguous nonstandard precedence unless explicitly resolved, and then feeds the identical reviewed generator. It validates real geometry, reuses semantic model-path displays, allocates collision-safe display/template identities, adapts complete INSERT-only templates to the live schema, and atomically emits DBC/SQL/manifest/receipt plus a tiny MPQ. Hash-bound apply refuses stale inputs and ambiguous same-path/cross-provenance content; it never reproduces the legacy generator's `REPLACE` behavior. Direct CASC-index roots remain pending real-corpus validation.
- **Recursive asset dependency graph — native MPQ foundation landed.** Crucible follows WotLK M2 view SKINs and embedded/replaceable textures, WMO groups/textures/doodad models, and ADT/WDT terrain textures/models/WMOs transitively from immutable processed sources. Same-provenance resolution is automatic; missing, invalid, and cross-source edges block staging; explicit candidate selection recomputes the chosen source's downstream graph. A complete effective target-client index now proves byte-identical inherited paths, omits them from minimal patches, binds their archive hashes/fingerprint in the manifest, and keeps different bytes as intentional overrides. Direct indexed GameObject sources use the same precedence model while materializing a durable review snapshot. Equivalent direct CASC-layer closure still awaits a real later-client corpus.
- **BLP preview, conversion, exact channel painting, material composition, mask transforms, compression proof, and complete consumer lookup — native foundation landed.** The included BLPConverter 8.1 and Photoshop-plugin documentation established palette/DXT/alpha/mipmap and direct-authoring requirements but remain reference material rather than copied implementation. Crucible now owns a clean-room managed BLP1/BLP2 parser, palette/JPEG/raw-BGRA/DXT decode, mip validation/salvage reporting, BLP2 encode, path-preserving bulk conversion, immutable-source RGBA/RGB/alpha brush/fill/invert editing with channel isolation and bounded undo, ordered bottom-to-top RGBA material composition with eight blend modes/opacity/offsets/clipping evidence, exact Alpha/luminance/R/G/B mask selection with inversion/dimension mapping and independent RGBA scale/offset transforms, editor/proof handoff, plus an actual encode/decode loss proof with responsive decoded/difference views, exact per-channel/combined MAE/RMSE/maximum/PSNR, alpha-boundary findings, scriptable loss gates, desktop/CLI parity, and atomic PNG/BLP export. Its catalog-driven reverse index maps exact M2 embedded, WMO MOTX, and ADT/WDT MTEX BLP references back to every physical provenance source, preserves its last committed state on cancellation, and exposes incomplete parse coverage. A companion exact appearance resolver maps `CharSections`, `CreatureDisplayInfo`/`CreatureModelData`, replaceable slots 1/6/11–13, physical model provenance, and optional live creature SQL uses. Selected binary, model, DBC, and SQL consumers hand off to their complete native same-window editors. Remaining work is broader texture effects and UV-aware transforms—not an external converter, explorer, image-editor, or database runtime dependency.
- **CASC source provider.** CascView/CASCExplorer and the large listfiles are useful research inputs for post-WotLK assets. Crucible now indexes and extracts CASC read-only through a reproducibly built, exact-commit CascLib provider, using listfiles only as path hints. Old bundled explorer binaries are not runtime dependencies; real later-client corpus verification remains pending because this workspace currently contains only MPQ-era client layouts.
- **Light/map visualization and band authoring — native foundation landed.** Crucible validates the complete build-12340 `Light`/`LightParams`/`LightSkybox`/`LightIntBand`/`LightFloatBand` graph, converts stored coordinates, plots sources/radii, and samples 18 named color plus six float bands across the wrapped client day. A responsive environment view composes the exact sampled sky/fog/cloud/ambient/water colors; illustrative celestial placement is labeled separately. LightSkybox `.mdx` paths resolve actual `.m2` sources with hash-aware provenance selection and native same-provenance texture preview. Empty bands are visible fallbacks and can be initialized safely. The editor owns complete 1–16-key curves, readable controls, interpolated insertion, persistent drafts, staged outputs, guarded replacement, complete-row preimages, `.bak`, reparse verification, and receipts. CLI inspection/composition/authoring share the providers. Exact client atmosphere and draw ordering remain a runtime fidelity stage.
- **Model conversion recipes.** MultiConverter, M2ModRedux, old Blender scripts, and WoW Blender Studio establish the need for model version inspection, rename/repoint assistance, skin/animation dependency validation, conversion logs, and documented interchange. Crucible must implement the worthwhile conversion/validation workflow natively; a legacy or maintained external editor may be used only as a temporary behavioral oracle or optional interchange endpoint while that native coverage is unfinished.
- **Protected UI/login-screen project.** The Mordred login-screen sample is useful for dependency-aware GlueXML authoring and resolution previews. Crucible already warns about protected GlueXML and executable hashes; a UI project should add changed-file previews and explicitly bind the compatible executable without distributing it.

### Reference-only inputs while native coverage is incomplete

- Noggit, WoW Blender Studio, 010 Editor, WMV, Photoshop, PuTTY, HxD, and specialized WMO/model editors are workflow/format references during migration. Optional import/export or launch bridges do not satisfy replacement: each worthwhile editing, inspection, validation, preview, and deployment capability remains assigned to a maintainable Crucible-native destination.
- MPQEditor, MyWarCraftStudio, and the many generic DBC/CSV editors are behavior references; Crucible already owns the safer archive and table workflows.
- The toolpack `Wow.exe` is build 12340 and claims removed UI checks. Its SHA-256 is `E463C25FA6D49D2D2057FB0252BC8B8E0BDD9DDD30CAD940F91CC7AFA9847855`. Treat it as a user-supplied compatibility candidate: back up the selected executable, record its hash in the patch manifest, and never commit or redistribute it.

### Additional standalone roots reviewed July 17, 2026

#### Coffee

`Coffee` is a 2016 preservation repository containing 4,156 files (about 459 MiB) across ADT, BLP, DBC, M2, MPQ, WDB, WDL, WDT, and WMO categories. Its own README says that most material was authored by other people, complete credits are unavailable, and the tools are probably obsolete. Some individual subprojects have licenses, but there is no umbrella license that makes the entire collection safe to copy or redistribute.

Use Coffee as format and workflow archaeology only. Source from a particular subproject may be studied or reused only after its individual origin and license are verified. Its strongest contribution is the breadth of map edge cases: holes, water, terrain offsets, WDT creation, reference repair, ground doodads, zone data, model collision, and import/export scripts. Those cases should become fixtures for Crucible's future asset validators rather than bundled executables.

#### Noggit Studio 3.2614

The standalone `Noggit 3.2614` directory is an unsigned, binary-only-in-this-workspace RelWithDebInfo build from July 2018 using Qt 5.9. The executable identifies itself as `Noggit Studio - 3.2614`, has SHA-256 `DE5274C20747059AC95D99BEEB65EA9013B1CD6A9273F648F570399D27DEE618`, and exposes project settings for separate game/project paths, imports, a WMV log, and optional MySQL fields; this particular binary says MySQL support was not compiled in.

Noggit is strong behavioral evidence for interactive terrain, texture, water, object, zone, minimap, horizon, and UID work. Crucible's native map project must absorb those worthwhile workflows incrementally. During that migration an optional isolated launch bridge may prepare a copied project tree, inventory affected ADT/WDT/WDL and referenced assets, then hash/diff and validate outputs before patch staging; that bridge is temporary compatibility, not the completed destination. Never point an untested old Noggit build directly at the only copy of a client or project.

#### MultiConverter 3.6.2

The local MultiConverter source targets .NET Core 3.1 and explicitly converts modern assets down toward WoW 3.3.5a. Its documented support is M2 from BFA/Shadowlands and WMO from BFA; ADT and WDT are not claimed as supported even though unfinished converter code exists. The UI recursively accepts drag-and-drop input and overwrites files in place. The M2 path strips modern chunks, remaps some animation IDs, adjusts cameras/skins/render flags, and currently zeros particle count/offset. Its listfile/bootstrap logic also depends on old `wow.tools` and Battle.net endpoints and may no longer initialize. The repository contains no top-level license, so its code cannot be copied into Crucible without permission or later license clarification.

MultiConverter remains valuable as behavioral evidence for the Legion-to-WotLK workflow, but the abandoned, unlicensed implementation will not be a Crucible backend. Crucible's replacement is a native clean implementation based on documented file structures and independently authored validation fixtures:

1. Copy selected source assets and their `.skin`/animation/texture dependencies into a conversion staging directory; never give it originals.
2. Record input hashes and converter executable/source identity, inject a user-selected current listfile, and disable its updater/network bootstrap where possible.
3. Translate one asset family at a time while recording every mapped, baked, removed, or unsupported source feature.
4. Parse and validate the resulting M2/WMO structures, list removed or downgraded features (especially particles), and compare dependency closure before accepting output.
5. Preserve source-relative paths and feed only approved results into a tiny manifest-driven MPQ.

The native replacement now completes this staged contract for the verified version-274 armor/head profile. It fingerprints a whole source root, deterministically discovers and resolves a nearby optional listfile once, converts eligible M2/SKIN pairs in isolated bounded-parallel workspaces, independently reloads every result, and atomically publishes exact source-relative paths under one MPQ-ready `Payload` directory. Automatic listfile selection requires every requested ID and agreement among all complete candidates; conflicting mappings remain blocked. The writer also loss-accounts the uniform 16-byte single-SKIN LDV1 distance metadata instead of treating it as unknown or silently dropping arbitrary chunks. It preserves the compatible WotLK `0x10` flag, validated global-sequence clocks, and validated embedded multi-sequence/lookup tables, then force-samples every emitted sequence before publication; external `.anim` dependencies and unknown flags remain blockers. Its particle path independently validates every modern 492-byte emitter and referenced track/curve, preserves packed gravity data supported by the legacy structure, copies the first 476 bytes into a new contiguous Wrath emitter table, and only omits EXP2 when every value is demonstrably neutral. Partial publication is refused by default; explicit ready-only publication keeps every unsupported sibling and reason in the receipt. A real `G:\extras\classic (1)` run converted 106 eligible pairs, retained 6 blockers and zero failures. All 188 files from the prior 94-model payload remained SHA-256 identical; the 24 new outputs are the M2/SKIN pairs for 12 newly supported particle helmets. The remaining native work is non-neutral EXP2 translation, camera/attachment/full-character and mixed-material M2 coverage plus WMO conversion, not wrapping the abandoned converter.

The conversion project format remains backend-neutral so a user can compare another maintained converter's output without making it a trusted or required dependency.

### Explicitly reject

- Do not integrate listfile removal/obfuscation (`FuckItUp.exe`). It defeats provenance, inspection, interoperability, and recovery.
- Do not integrate camera/coordinate hacks or any workflow intended to bypass game/server rules.
- Do not include trial-bypass registry scripts from the 010 Editor folder.
- Do not reuse old launchers that transfer patches over FTP or accept raw database credentials without current transport and secret-handling controls.
- Do not reproduce `INSERT`/`REPLACE` string concatenation from the legacy generators. All generated database work requires parameterized inspection, SQL preview, transactions, backups, and explicit rollback.

### Revised implementation sequence from this audit

1. Reconstruct Cataclysm base-plus-`PTCH` effective tables and verify the complete base corpus, then add separately verified WDB5/WDB6/WDC providers without weakening fixed-layout safety.
2. Bind the landed dependency graph to an effective target-client index so proven inherited assets are omitted from tiny patches.
3. Broaden the landed native staged modern-to-3.3.5 M2 writer beyond the verified static profile, followed by WMO support.
4. Guided NPC appearance import and additive display/extra/SQL planning.
5. Extend the landed direct MPQ-index GameObject generation contract to CASC virtual paths after real later-client corpus verification.
6. Extend the landed project-local SQLite spell/table staging foundation with visual query construction and explicit baseline rebase/merge.
7. Real-corpus verification and effective-build/profile comparison for the landed read-only CASC provider.
8. Native light/map project visualization and editing, with guarded external launch profiles only as temporary migration aids.

## Product model

Every creator should have three synchronized layers:

1. **Guided intent** — templates, readable choices, safe defaults, live preview, and explanations.
2. **Change plan** — all affected DBC rows, server records, files, IDs, warnings, and validation results before mutation.
3. **Expert controls** — raw values, advanced fields, SQL/manifest output, CLI automation, and target-specific overrides.

A beginner can stay in the first layer. A power user can inspect and alter every generated detail without leaving the project or losing the portable intent.

## Implementation order

1. Start Center and workflow-oriented navigation.
2. Shared searchable reference selector and side lookup panel.
3. Portable project/change-plan model with validation and preview.
4. Capability-detected database connection profiles and read-only schema inspection.
5. Guided item creator with live tooltip preview and SQL/DBC change plan.
6. Creature templates and model/faction lookups.
7. Quest, loot, vendor, conditions, and related multi-row editors.
8. Race/class workflows spanning every required client and server table.
