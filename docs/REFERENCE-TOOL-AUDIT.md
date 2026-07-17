# Reference tool audit

WoW Crucible uses the local legacy tools as workflow research, not as compatibility authorities. Their strongest ideas are reimplemented against Crucible's build-12340 client model and capability-detected current server adapters.

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

## Expanded local Tools collection

The July 2026 workspace review covered the added Amaroth Toolpack directories plus the newer standalone DBC/DB2, CASC, model, map, texture, and spell utilities under `Tools`. The pack README dates the Amaroth bundle to December 2019 and says several included utilities were last updated in 2017 or earlier. Most binaries have no local source license. Crucible must not copy or redistribute their binaries or source unless the license is explicit and compatible; the workflows and file formats can be independently implemented.

### High-value workflows to implement natively

- **WoWDBDefs schema provider and cross-checker.** The local corpus contains 823 DBD files, including 238 definitions that mention build 12340. Against the current 245 non-empty server DBCs, only `ChrRaces`, `Holidays`, `LockType`, `Material`, `NamesReserved`, `SkillLine`, `SoundSamplePreferences`, and `WorldChunkSounds` lack a local build-12340 DBD. This is an excellent independent schema source beside WDBX XML, especially for future DB2 profiles. `CharVariations` is the inverse edge case: its DBD exists while the server file is an intentional empty placeholder.
- **Spell editing database workspace.** WoW Spell Editor demonstrates useful MySQL/SQLite staging, named bindings, bulk queries, and DBC export. Crucible should keep the portable DBC as the source artifact, use a project-local SQLite workspace optionally, and never confuse a staging table with AzerothCore's SQL overlays.
- **NPC appearance generator.** Amaroth NPCGenerator's WMV `.chr` to `CreatureDisplayInfo`/`CreatureDisplayInfoExtra` plus SQL workflow is directly useful. Crucible's version should allocate/deduplicate IDs, parse the appearance source, resolve item display references, generate a client asset manifest, preview every DBC/SQL change, and deploy transactionally.
- **Gameobject generator.** GobGenerator's model-list to `GameObjectDisplayInfo` and `gameobject_template` pipeline is valuable for bulk imported M2/WMO assets. Crucible should consume indexed MPQ/CASC paths directly, preserve provenance, allocate IDs centrally, use capability-detected current schemas, and output rollback-safe plans instead of `REPLACE` queries.
- **Recursive asset dependency graph.** WMOListFile's central idea—discover BLP/M2/WMO dependencies recursively—is essential for reliable tiny patches. Crucible should parse ADT, WMO, M2, skin, and animation dependencies from immutable source layers and fail a manifest when required assets are absent.
- **BLP preview and conversion queue.** The included BLPConverter 8.1 source states GPL licensing and supports palette, DXT1/3/5, alpha modes, mipmaps, character, and clothing presets. Crucible can detect and invoke a user-installed converter through a bounded external adapter, or use a separately licensed library, but must not copy GPL implementation into the MIT codebase. Preview, alpha/mipmap validation, bulk conversion, and path-preserving output belong in Crucible.
- **CASC source provider.** CascView/CASCExplorer and the large listfiles are useful research inputs for post-WotLK assets. Crucible should eventually index CASC through a maintained library/provider abstraction, using listfiles only as path hints. Old bundled explorer binaries should not become runtime dependencies.
- **Light/map visualization.** LightMapper's map overlay for `Light`, `LightIntBand`, and related tables is a good future project visualization. ADT copy, offset, ground-effect, alpha-map, WDT, and Noggit workflows belong behind explicit map projects with coordinate previews, backups, and dependency checks.
- **Model conversion recipes.** MultiConverter, M2ModRedux, old Blender scripts, and WoW Blender Studio establish the need for model version inspection, external converter profiles, rename/repoint assistance, skin/animation dependency validation, and conversion logs. Crucible should orchestrate maintained external authoring tools rather than attempt to absorb obsolete model editors.
- **Protected UI/login-screen project.** The Mordred login-screen sample is useful for dependency-aware GlueXML authoring and resolution previews. Crucible already warns about protected GlueXML and executable hashes; a UI project should add changed-file previews and explicitly bind the compatible executable without distributing it.

### Keep external or treat only as reference

- Noggit, WoW Blender Studio, 010 Editor, WMV, Photoshop, PuTTY, HxD, and specialized WMO/model editors remain external authoring applications. Crucible can generate inputs, validate outputs, and offer configured launch shortcuts.
- MPQEditor, MyWarCraftStudio, and the many generic DBC/CSV editors are behavior references; Crucible already owns the safer archive and table workflows.
- The toolpack `Wow.exe` is build 12340 and claims removed UI checks. Its SHA-256 is `E463C25FA6D49D2D2057FB0252BC8B8E0BDD9DDD30CAD940F91CC7AFA9847855`. Treat it as a user-supplied compatibility candidate: back up the selected executable, record its hash in the patch manifest, and never commit or redistribute it.

### Explicitly reject

- Do not integrate listfile removal/obfuscation (`FuckItUp.exe`). It defeats provenance, inspection, interoperability, and recovery.
- Do not integrate camera/coordinate hacks or any workflow intended to bypass game/server rules.
- Do not include trial-bypass registry scripts from the 010 Editor folder.
- Do not reuse old launchers that transfer patches over FTP or accept raw database credentials without current transport and secret-handling controls.
- Do not reproduce `INSERT`/`REPLACE` string concatenation from the legacy generators. All generated database work requires parameterized inspection, SQL preview, transactions, backups, and explicit rollback.

### Revised implementation sequence from this audit

1. DBD build/profile parser plus WDBX-vs-DBD schema cross-checking.
2. BLP preview/conversion adapter and asset validation in the client inspector.
3. Recursive M2/WMO/ADT dependency graph feeding required manifest paths.
4. Guided NPC appearance import and additive display/extra/SQL planning.
5. Bulk gameobject generation from indexed model paths.
6. Project-local SQLite staging for spell/table bulk editing.
7. CASC indexing provider for supported post-WotLK source profiles.
8. Light/map project visualization and guarded external authoring-tool launch profiles.

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
