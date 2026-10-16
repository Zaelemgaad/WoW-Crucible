# Class Studio

Open **Create game content → Class Studio**, search for it in **Commands**, or run
`WoWCrucible.Desktop.exe --class-studio`. Crucible serves the editor on loopback and
opens it in the default browser. Keep Crucible open while using folder selection or
source loading. There is one frontend, in `src/WoWCrucible.ClassStudio`, packaged by
the desktop project; there is no separate browser implementation to maintain.

The existing local `E:\wowps\3.3.5 WOTLK\site\app` directory is a junction to this
source directory. Its original file URL remains usable for offline talent planning.
To transfer existing browser designs to the Crucible session, use **Export entire
workspace** in the old page, then **Restore workspace** in Class Studio. Individual
talent JSON files still use **Import JSON**. Restore replaces the current editor
workspace; export it first if both designs matter.

## Canvas and windows

- Each spec and the sideboard has a grip at the upper left and a resize handle at
  the lower right. **Fit** sizes the window to its content and available height.
  Focus either handle and use arrow keys; Shift uses one-pixel steps.
- **Grid** toggles the canvas background. **Grid spacing** accepts 1–64 pixels.
  **Snap** aligns board centers to grid lines and aligns edges/centers to nearby
  boards and viewport boundaries. Zoom changes the canvas scale.
- New boards find unoccupied space. Moving an existing board never rearranges the
  others. Positions, sizes, stacking, inner scrolling and canvas settings persist.
- The page fits the viewport; only a canvas extending beyond it and individual
  window contents scroll. Resizing deliberately below content width permits an
  inner horizontal scrollbar. Default board sizes fit their columns.
- The help strip has a corner X. Dismissal persists; **?** brings it back.
  Talent dialogs and the spell window can be moved by their titles and resized.
- Free Mode supports independent spec copies without creating fake classes.
  **Drag → Move everywhere** removes the source after a successful transfer;
  **Copy everywhere** retains it. Replacement cancellation leaves both unchanged.

## CoA sources and spellbooks

Open **Spells & sources** and choose the server data/DBC directory, client root/Data
directory, and CoA core source checkout. These initially use Crucible's configured
workspace paths. Select **CoA / Ascension** as the client loader for the CoA client.
The standard Wrath resolver remains available and retains its ambiguity checks.

CoA's ascending, case-insensitive `patch-*.MPQ` filename precedence follows the
installed checkout's `tools/New-CoAArchiveList.ps1` and
`tools/Build-CoAPantyhoseItems.py`. Indexing caches archive identities; it does not
copy the client or hash every large archive. Only required tables are extracted.
Every loaded table identifies its archive/file and SHA-256. Server and client
layers remain independently selectable. `customs/` is exposed separately rather
than assuming a particular core's overlay semantics.

The editor reads Spell, ChrClasses, Talent, TalentTab, SkillLineAbility,
SkillRaceClassInfo, SkillLine and SpellIcon with exact schemas. For CoA it also
reads the verified fields of CharacterAdvancement, CharacterAdvancementClassTypes,
ChrSpecs, SpellRank and NPCTrainer. The packed advancement layout is checked as
179 fields / 692 bytes; byte flags, unaligned fields and IDs above 65535 retain
their actual values. Unknown packed fields are not guessed.

From the selected CoA core it reads the current `ClassSpells`, progression `Ranks`
and `TaughtAbilities` arrays. These are source definitions, not proof of which
binary is running. Additional compiled hooks may grant spells beyond these lists.

Connect the world database in Crucible Workspace setup, then enable **Include
connected world SQL** and refresh to include inspected starting, trainer, quest,
rank, prerequisite, learned-spell and `spell_dbc` rows. Credentials never enter the
browser. This uses read-only SELECTs. No server restart or game-file write occurs.
Missing tables and missing connections are listed under source coverage; the editor
does not claim a complete runtime learn list when a source is unavailable.

The class view follows class masks, exclusions, skills, talent ranks, CoA class
groups, known core lists and transitive learn/rank relationships. Profession recipes
remain in **All spells** instead of appearing as class abilities. Disabled CoA
advancement entries remain visible and labelled, so designers can edit them.
SQL-only spells appear in the catalog; matching SQL overrides are identified and
have an **Open server SQL override** action. Large catalogs are paged.

Select a row to edit its complete schema fields. **Clone as new** requires a new
key; **New row** creates a blank record. **Learning rules / tables** exposes the
actual relationship rows for additions, changes and removals. Numeric validation
rejects invalid 32-bit values; SQL bigint/decimal text retains exact precision.
Refresh keeps staged edits and reports changed source fingerprints.

**Use class talents** imports a selected class into the working editor. Stock
Talent/TalentTab coordinates are retained. CoA uses ChrSpecs root relationships to
form planning boards and preserves original advancement records and requirements.
The generated planning coordinates are explicitly not claimed to be the native
CoA UI layout. CoA board titles identify the spec ID and root spell; they can be
renamed. Native CoA spec presentation still needs its own implementation when
deploying a finished design.

## Exports and persistence

- **Export entire workspace**: all editor state, boards, layouts, settings and
  staged spell/table changes as a restorable JSON file.
- **Export changes JSON**: versioned implementation plan with source paths,
  fingerprints, table/format, exact before/after values, and verified byte offsets
  where applicable. A null `after` means deletion; a null `before` means insertion.
- **Export table CSV**: added/edited rows from the selected table. Schema-resolved
  DBC exports use the same names and `$recordKey` convention as Crucible's existing
  structured DBC importer. Use **Import structured DBC rows** to preview those
  changes against the corresponding table; append must be enabled for new IDs.
- SQL CSV contains actual inspected column names. CoA packed-table and compiled
  core-list CSVs are labelled implementation plans, not stock DBC import files.
  They require the corresponding core/client change. Deletions remain in the JSON
  plan; CSV never silently pretends to perform them.
- Talent JSON and the existing talent-reference CSV remain available per class.
  They describe a design; they do not automatically implement new spell behavior.

Hosted editor state is saved atomically beside Crucible under
`Config/class-studio.json`, with browser storage as the working copy. A loopback
token and same-origin checks protect native endpoints. No new production package
or externally installed service is required.

## Validation

Core reader/import checks:

```powershell
dotnet run --project tests/WoWCrucible.Core.Tests -- --class-studio
dotnet run --project tests/WoWCrucible.Desktop.Tests
node tests/class-studio/data.cjs
```

The optional CoA corpus check takes server DBC, test-output, client and core paths:

```text
dotnet run --project tests/WoWCrucible.Core.Tests -- --class-studio <server-dbc> <output> <client-root-or-Data> <core-checkout>
```

It reads live data and writes generated test fixtures only to the output directory.
Browser fixtures use `.local/class-studio-test` and an already installed Playwright:

```text
node tests/class-studio/canvas.cjs <path-to-playwright>
node tests/class-studio/free-mode.cjs <path-to-playwright>
node tests/class-studio/spellbook.cjs <path-to-playwright>
```

The spellbook test produces a CSV; rerunning the corpus check verifies that CSV
through Crucible's real DBC import preview, without modifying the source.
Checks cover canvas sizing/snapping, ten-board copy/move and cancellation,
workspace persistence, native HTTP boundaries, source schemas, packed CoA fields,
class filters, full spell editing, cloning, source talent import, and exports.
They do not deploy changes or substitute for gameplay validation of a finished spec.
