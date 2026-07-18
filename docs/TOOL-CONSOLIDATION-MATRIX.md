# Tool consolidation matrix

This is the tracking contract for making the local legacy-tool collection obsolete. A capability is never dropped because its existing implementation is old, broken, slow, unlicensed, or awkward. Those conditions only change how Crucible rebuilds it.

The final implementation must be native and maintainable in Crucible. Legacy executables may be used temporarily as behavioral or format oracles during development, but they are not acceptable permanent runtime dependencies. Compatible source can be incorporated under its license; everything else is independently implemented from observable behavior, file formats, server/client sources, and original tests.

## Definition of obsolete

A local tool is considered replaced only when Crucible can complete its worthwhile workflow with:

1. A beginner-facing guided path with readable names and safe defaults.
2. An expert path exposing raw fields, IDs, schemas, and generated artifacts.
3. Previewable changes before filesystem, MPQ, DBC, client, or SQL mutation.
4. Transactional or atomic writes, backups, verification, and explicit rollback information.
5. CLI parity for repeatable/bulk work.
6. No required feature window outside the main Crucible process.
7. No permanent dependency on an abandoned local executable.

## Workspace roots

| Local root | Capability to absorb | Crucible destination |
|---|---|---|
| `acore-cms` | Account/realm/character administration, server status, modules, web-facing management concepts | Server Administration and deployment API |
| `azerothcore-wotlk` | Current AzerothCore schemas, DBC/SQL bindings, commands, reload/restart rules, migrations | Versioned AzerothCore adapter generated from source evidence |
| `TrinityCore-3.3.5` | Current TrinityCore schemas and behavioral differences | Separate TrinityCore adapter; never inferred from AzerothCore column similarity |
| `CascLib`, `Tools\CASC`, `Tools\cascExplorer` | CASC indexing, listfile/path lookup, extraction | Native CASC source provider and unified archive browser |
| `client and old core` | Baseline/client comparison, fusion evidence, old SQL customization recovery | Client/server diff, extraction, and portable change-plan import |
| `Coffee` | ADT/WDT/WDL/WMO/M2/BLP/DBC edge cases and map repair workflows | Native validators, map project fixtures, guarded transformations |
| `Current-AzerothCore-Server` | Installed-server detection and live deployment target | Shared Server & SQL session; already active |
| `HeidiSQL` | Table/query browsing and direct SQL maintenance | Schema-aware SQL explorer, query preview, transactions, snapshots, diff/import |
| `Keira3` | Guided AzerothCore item/creature/quest/loot/vendor/conditions editors | Domain creators backed by target adapters and shared selectors |
| `listfiles` | MPQ/CASC path discovery | Unified path catalog and dependency resolver |
| `mpqeditor_en`, `StormLib`, `stormlib_dll`, `Tools\MPQ*`, `Tools\MyWarCraftStudio*` | Fast MPQ tree/list browsing, extraction, creation, update, listfiles | Single-window MPQ builder/browser/deployment; core operations already native |
| `MultiConverter`, `Tools\Models`, `Tools\wmo editor*` | Modern-to-WotLK M2/WMO/animation conversion and validation | Native staged converter with explicit loss report and dependency closure |
| `MyDbcEditor`, `Tools-MyDbcEditor*`, `WDBX.Editor`, `WDBXEditor`, `Tools\DBC*`, `Tools\WDBXEditor`, `Tools\WoWDBDefs` | Generic DBC/DB2 editing, definitions, CSV/SQL conversion, fixed-value names | Multi-table virtual editor, DBD/XML providers, structured import/export, profile adapters |
| `Noggit 3.2614`, `Tools\Map`, `Tools\LightMapper`, `Tools\skyboxeditor` | Terrain, water, objects, textures, zones, lighting, skyboxes, WDT/WDL/ADT work | Native map project, 2D/3D previews, guarded transforms, dependency-aware patch staging |
| `TrinityCreator` | Beginner item/quest/creature/loot/vendor/model workflows and intent templates | Guided creator layer with live previews and generated change plans |
| `wiki` | Field meanings, flags, enums, command/database documentation | Searchable offline knowledge/reference provider beside every editor |
| `WoW Spell Editor`, `WoW-Spell-Editor`, `Tools\spell editor 2`, `Tools\DBC\WoWSpellEditor*` | Spell search, effects, named fields, bulk staging, DBC export | Spell workspace spanning Spell DBCs and SQL overlays |
| `WoWDatabaseEditor` | Project organization, quest graphs, SmartAI, map context, source/database providers | Portable content projects, graph editors, shared provider architecture |
| `wow-model-viewer`, `wowmodelviewer`, `Tools\WoW Model Viewer*`, `Tools\Other\WMV` | Character/item/NPC/object rendering, geosets, equipment, animation, export | Native embedded renderer shared by all creators |
| `Zips&rars` | Preserved inputs/backups | Read-only archive inventory/import planning; never destructive cleanup without proof |
| `WowForge` | Empty local root at audit time | Retain as a watched root; rescan if content appears |
| `WoW-Crucible-User-Copy` | Frozen user build | Deliberately excluded from development scans and never silently updated |

## Expanded `Tools` collection

Every top-level Tools directory is assigned below, including redundant editors. Redundancy strengthens test coverage; it does not create multiple permanent backends.

| Tools directories | Native replacement target |
|---|---|
| `Adb_Wdb_Parser*`, `WDB Converter*`, `WoWParser*` | ADB/WDB cache parsing, build-aware structures, SQL/CSV/JSON export |
| `ADB-DB2-DBC - CSV convert`, `DB2 Editor*`, `DBC_DB2_Extractor`, `SQLtoDB2_Fix*` | DB2/DBC/CSV/SQL round-trip provider with schema validation |
| `AmarothTools\ClientItem` | Item client-record planning and display dependency resolution |
| `AmarothTools\NPCGenerator` | Character/NPC appearance import into additive display/extra DBC plus SQL plans |
| `AmarothTools\GobGenerator` | Bulk model-to-GameObjectDisplayInfo/gameobject generation |
| `AmarothTools\ListfileCreation`, `AmarothTools\WMOListFile` | Path catalogs and recursive dependency graphs |
| `CASC`, `cascExplorer` | CASC list/browse/extract/diff source provider |
| `CSVed*` | Built-in schema-aware table view, sort/filter, bulk transforms, CSV import/export |
| `DBC`, `DBC Editor*`, `DBC Tool*`, `DBC Viewer*`, `DBC_XT`, `DBC2ConverSQL*`, `dbc2sql*`, `DBCtoCSV`, `DBCUtil*`, `MyDbcEditor*`, `WDBXEditor` | One generic DBC/DB2 workbench with raw and decoded modes |
| `LightMapper` | Light table map visualization and time/color-band editor |
| `Map\AdtAdder`, `ADTGrids`, `FuTa`, `GroundEffects`, `GruulMeWDT`, `Rius Zone Masher` | ADT copy/add/grid/offset/alpha/ground-effect/zone and WDT project operations |
| `Map\Noggit*` | Capabilities folded into native map editing rather than permanent launch integration |
| `Models\anim porter` | Build-aware animation transfer with ID remap and companion validation |
| `Models\M2ModRedux*`, `MDLVIS*`, `MDX`, `Scripts` | M2/MDX inspect/edit/import/export operations and model component validation |
| `Models\MultiConverter*` | Native modern-to-3.3.5 conversion with immutable source staging |
| `Models\WoW Blender Studio` | Model import/export concepts reproduced through documented interchange formats |
| `MPQ`, `MPQEditor*`, `MPQWorkshop*`, `MyWarCraftStudio*` | Unified high-performance MPQ workspace |
| `Other\010`, `PuTTy*` | Hex/structure inspection and remote/server-terminal needs replaced by structured inspectors and controlled process/WSL console output |
| `Other\Mordred_LoginScreen` | GlueXML/UI project, protected-path validation, executable hash binding |
| `Other\WMT335a` | Client/model/texture research assigned to corresponding native workspaces |
| `pjdbcEditer*` | SQL-backed DBC editing and schema mapping in the shared SQL/DBC engine |
| `skyboxeditor` | Light/Skybox DBC creator with live world/map preview |
| `spell editor 2` | Full spell authoring and effect workspace |
| `Tallis` | Plugin/provider concepts incorporated into the Crucible extension boundary |
| `Textures\BLPConverterGUI`, `BLPPhotoshopPlugin*` | Native BLP decode/encode, alpha/mipmap validation, PNG round trip, bulk queue |
| `wmo editor*` | Native WMO inspect/edit/validate plus portal/group/material dependency views |
| `WoW Model Viewer*` | Native M2/SKIN/WMO character/creature/item renderer and exporter |
| `WoWDBDefs` | Build-range DBD provider and schema cross-checking |

## Capability status

| Capability family | Current Crucible state | Replacement acceptance target |
|---|---|---|
| DBC WDBC editing | Native multi-document virtual editor, decoded fields, undo/redo, bulk clone, layer/promotion services | DBD/DB2 formats, structured import/export, domain workspaces, effective DBC+SQL view |
| M2/SKIN preview | Native mesh/UV, bounded discovery, SKIN material-unit and M2 texture-lookup parsing, per-submesh embedded BLP assignment, DBC-driven skin/face/facial-hair/hair selection, exact `CharHairGeosets` and `CharacterFacialHairStyles` geometry selection, native body-atlas composition, explicit provenance | Remaining replaceable slots, multi-pass shaders, equipment, animation, particles, export |
| MPQ | Native create/update/list/extract/manifest/validation/deployment; transaction-safe small-patch merge with byte-verified deduplication and explicit conflict policy; single-window workspace; files/folder-tree drag-and-drop | Tree mode, locale/listfile recovery, streaming performance, complete MPQEditor parity |
| Server & SQL | Shared automatic WSL/local detection, complete-schema verification, native lifecycle controls, paged full-row SQL Studio, primary-key-safe insert/update/delete, declared/inferred relation navigation, streaming CSV/JSONL export, dry-run transactional CSV import, confirmed query/write path, and persistent cross-table favorites | Visual join designer, indexes/users/process administration, broader domain adapters, deployment rollback, and remaining advanced HeidiSQL parity |
| Items & sets | Schema-adaptive SQL/DBC acquisition audit with exact-ID evidence and reachable spell-creation graph, decoded/full-SQL handoff, full schema copy, ItemSet clone/effects | Finish tooltip/model fidelity, broaden script/static-analysis evidence, and complete remaining target adapters |
| Spells | Core DBC editor and legacy WinForms spell form exist | Native Avalonia effect-centric spell workspace with SQL overlay diagnosis |
| Creatures/NPCs/gameobjects | Native creature/NPC creator plus all-36-type gameobject creator; current/legacy model adaptation, type-aware Data0–23, creature/gameobject spawns, vendor/loot/quest-link children, portable drafts, embedded M2/SKIN preview, SQL plan/export, strict transactional insert/update; normalized and legacy trainer authoring | WMO rendering and map placement canvas |
| Quests/loot/vendors/conditions | Native complete-schema quest editor with decoded types/flags, all objective/reward/text/POI fields and giver relationships; complete-field gossip/menu/NPC-text/condition/SmartAI authoring with current-core enum decoding, SQL Studio handoff, portable drafts, and transactional insert/update; multi-row vendor and creature-loot plans | Visual quest/SmartAI graph canvases, reusable loot/vendor editors, shared ID selectors, and broader dependency-aware portable change plans |
| BLP/textures | Native BLP1/BLP2 validation and live mip preview; palette/JPEG/raw-BGRA/DXT1/DXT3/DXT5 decode; corrupt-tail salvage with explicit warnings; PNG/BMP/JPEG/TGA → mipmapped BLP2 encode; native parallel asset-library conversion; CLI and single-window Texture Lab | Material-layer composition, edit brushes/channels, loss metrics, and texture dependency closure |
| WMO/ADT/WDT/WDL/maps | Inspection/conversion beginnings only | Native map/world project with 2D/3D previews and validated patch closure |
| CASC | Local libraries/listfiles inventoried | Native provider integrated beside MPQ in the same archive UI |
| Client/server fusion | Diff/planning services exist | Additive ID allocation, cross-layer dependency/change plan, verified deploy/rollback |
| CLI | Broad command groups and shipped guide | Command parity for every UI operation, structured JSON output, generated searchable command palette |

This matrix is updated whenever a capability lands, a new local tool appears, or a previously unknown workflow is discovered.
