# Realm talents and tooltip audit

Class Studio reads the selected realm, effective client archives, and core source. It does not change the game files or SQL while loading.

## Source contracts

- `ChrSpecs`: verified 65 fields / 260 bytes. Class token at 4, spec token at 8, displayed English name at 116, root advancement ID at 112. Root spell names are not spec names.
- `CharacterAdvancementTabTypes`: 19 fields / 76 bytes, token at 4. This client-owned table joins spec tokens to advancement tab IDs even when absent from server DBCs. Its provenance remains `client`.
- `CharacterAdvancement`: packed 179 fields / 692 bytes. Type at 4, column/row at 120/124, graph X/Y floats at 398/402, class group/tab at 128/132, choice group at 116. Source rows remain attached to every editable node.
- Bronzebeard/Remix use one-based Column and zero-based Row, as `CATalentBrowser:SetEntry` does. CoA uses PositionX/PositionY, as `CharacterAdvancementUtil.GetEntryPosition` and `TalentTreeBase:BuildTree` do. The selected client's native entry exporter reads these positions from runtime offsets 0xf8/0xfc; native row construction copies them from its decoded DBC record at 0xd0/0xd4.
- Classless trees join both class group and tab; Holy, Protection, Frost, and Restoration are not merged across classes.
- Disabled/deprecated/server-excluded and client-hidden entries remain in the table editor but are excluded from presented talent trees. CoA/seasonal availability follows the corresponding entry flag. Remix admission constants come from `AscensionRemixPolicy.h`.
- Remix class/spec presentation comes from its `client/wxl/RemixRealm.cpp` hooks, fingerprinted alongside the constants. No hardcoded Templar rename in the frontend.
- Talent textures use the effective client archive catalog and batched BLP decoding. Bundled stock icons are used for bundled stock data. Missing source icons are explicitly marked rather than silently replaced with a different client's texture.
- Real CoA choice nodes share a position. The editor retains all alternatives and exposes a selector. Existing board geometry remains independently saved.

The current local server and effective client hashes agree for CharacterAdvancement, ChrSpecs, Spell, and SpellIcon. Some classes actually have four ChrSpecs records in this client; the reader does not arbitrarily discard the fourth.

## Every tooltip-producing path reviewed

Source-wide searches covered `ToolTip`, `SetToolTip`, `ToolTipText`, `MouseHover`, `title`, `showTooltip`, and generated HTML, excluding build output and bundled data/vendor code.

| Surface | Data path and handling |
| --- | --- |
| Class Studio talents, sideboard, free workspace, current rank, next rank | All call `showTooltip`; names, metadata, descriptions, prerequisites and tree names pass through the shared native renderer before HTML escaping. Stale asynchronous hover results cannot reopen a hidden/replaced tooltip. |
| Native item preview | `SpellTooltipService` resolves spell names, rank labels, description and aura description using the selected Spell/Duration/Radius tables. `ItemCreatorView.AddTooltip` and `AddPair` render name/flavor/other preview strings. |
| Older WinForms item preview | `ItemTooltipControl.Line` and `PairedLine` use the same presentation renderer. |
| Icon pickers | Titles are bundled icon filenames, not spell descriptions. Missing native icons use fixed explanatory titles. |
| Class Studio board handles, empty cells, copy/restore/export controls | Fixed action help; no spell-template inputs. |
| Item/quest/game-object/SQL field help | `ItemCreatorView`, `QuestWorkspaceView`, `GameObjectWorkspaceView`, `SqlWorkspaceView`, `ItemCreatorForm`: schema/catalog descriptions, flag meanings, numeric contracts and static help; no spell templates. |
| App navigation, tabs and status | `MainWindow`, `WorkspaceTabStrip`, `WorkspaceNavigator`, `WorkspaceStartPage`, `WorkspaceRuntimeStrip`, `AssetComparisonWindow`, `ItemWorkbenchWindow`, `AddonAuditView`: actions, paths, realm names, diagnostic details. Paths and diagnostic messages remain literal; they are not spell templates. |
| DBC and model/map controls | `DbcRowEditorView`, `VirtualDbcView.Columns`, `ResponsiveSplitGrid`, `ModelBrowserView`, `ModelBrowserTextures`, `M2PreviewView`, `MapSceneView`, `MapWorkspaceView`: field types, paths, dimensions, animation IDs and fixed help. |
| XAML tooltips and legacy folder picker help | Fixed action text; no template interpolation. |

Raw description templates remain visible in editable source fields and exports. Rendering never edits those strings.

## Preview limits

Numeric spell fields, related-spell references, common WoW markup and CoA embedded spell references are resolved. Character-dependent attack/spell power remains a readable symbolic expression. Unsupported client annotations, absent source rows and unsupported formulas produce readable unavailable-value notices; the renderer does not invent values. This is not an emulator of every Extensions.dll instruction.

The corpus test reports both zero raw-instruction leakage and the number of incomplete previews. These are separate assertions: a readable preview is not necessarily a complete in-game calculation.

## Repeatable validation

- `dotnet run --project tests/WoWCrucible.Core.Tests -- --tooltip-audit <dbc-folder>`: representative formula/reference/cycle cases and every nonempty name, rank, description and aura field in the selected Spell.dbc.
- `dotnet run --project tests/WoWCrucible.Core.Tests -- --class-studio <server-dbc> .local/class-studio-test <client-root> <core-root>`: schemas, packed offsets, explicit source layers, immutable sources, structured import, effective archive reads.
- `node tests/WoWCrucible.Desktop.Tests/realm-talents-regression.js`: all five realm catalogs, actual names/positions, hidden-entry exclusion, separate Hero trees and Remix membership.
- `dotnet run --project tests/WoWCrucible.Desktop.Tests -- --embedded-studio --coa`: native WebView bridge, every configured realm SQL source, icon decoding, hovered Poison Rain source description, last-realm restoration and retained layout.

## Validation results (2026-10-18)

- Desktop and legacy WinForms builds: zero warnings/errors.
- Spell corpus: 210,799 records; 533,281 nonempty text fields; zero raw instruction leaks. 13,125 fields reported incomplete context or unsupported instructions (retained in `.local/tooltip-audit/corpus.json`).
- Five-realm placement regression: 6,190 source placements checked.
- All 3,000 unique visible CoA talent/ability icon names resolved and decoded without missing assets.
- Embedded checks passed for all five world SQL sources, the Poison Rain hover renderer, retained layouts, wide graph edits and choice-node access. An earlier attempt timed out reading Bronzebeard SQL; the subsequent complete run passed without changing its checks or database configuration.

These tests read real local sources and render the editor. They do not claim an in-game gameplay test or complete emulation of CoA's learning rules. Source requirements remain attached for export; the editor's point-allocation preview is not a replacement for the realm's learning engine.
