# GUI Audit

Record the commit/build, test name, exact action, expected result, observed result,
and pass/fail/pending status. A passing core test is not a passing GUI test.
Retest changed workflows after every small batch of improvements.

The desktop may be in active use during testing. Do not send global keystrokes,
move the pointer, activate/reposition windows, or assume focus without arranging
an exclusive test interval. Use background tests and control-specific inspection;
mark focus-dependent results inconclusive if another application received input.
Leave manual keyboard/drag checks for the user when the desktop is shared.

## Browse Loose Models

### Viewer Usability Work Queue

2026-09-19 follow-up, implemented and locally verified:
- [x] Left-drag orbit, right-drag orbit-target pan, middle-drag model translation;
      face framing, stable zoom target, direct camera controls.
- [x] Named animations, persistent playback when switching clips, duration beside
      the timeline rather than in animation names.
- [x] Face variants grouped by geoset ID. The initial selection includes base
      body (0), first hands/gloves (4), bare feet (20), hand attachment (23), eyes
      (33), eyebrows (34), two available ear variants (7), 3201 and one full face.
      All other groups start off, including boots (5), sleeves, knees and cloaks.
      No geoset checkbox is locked; saved choices override these defaults.
- [x] Saved per-model geoset/texture defaults override initial selections.
- [x] Texture role labels and visible usage/missing-binding state; each material
      has its own dropdown and browse control. Choices are filtered by material
      role and character, retaining matching textures from other packs. Shared
      search narrows those choices without changing assigned textures.
- [x] Non-destructive deletion marks replace Skip; previous Skip records are not
      automatically treated as requests to delete anything.
- [x] Depth-tested mesh rendering, opaque versus alpha-cutout material behavior,
      and no skipped triangles in large models.
- [x] Regression checks and updated build/shortcut. Manual mouse checks remain
      separate from the automated controller/view tests below.

Open **Visuals & World > Model browser**, or **Commands > Preview an M2**.
The browser reads loose M2/SKIN/SKEL/ANIM/BLP files and models inside ZIPs in
place. It does not extract the collection, modify source files, or build MPQs.

1. Open a model folder. Search for a model or relative folder name, and switch
   between **Loose files**, **Inside ZIPs**, and the review filters. The results
   must remain scrollable and the scan cancellable.
2. Select a model with its companions present. Rotate/zoom the preview, choose
   an animation, press **Play**, and scrub the timeline. Opening another model
   must replace both geometry and texture controls, without a stale frame.
3. In **Textures**, use the dropdown or browse button beside a material. Change the
   choice several times quickly. The last choice must win. Unresolved textures
   remain explicitly unassigned; these are not a complete character appearance.
   Body choices must exclude hair, eyes, icons and partial face/underwear overlays.
   The default scope includes matching characters across packs; **Include other
   models** expands that scope without removing role filtering. Local files sort
   first, and dropdown options show their source folder. Browse is an explicit
   override, including for unknown/numerically named textures. Changing search
   or scope must not clear an existing choice, even an override outside the list.
4. Move the orbit target with right-drag, reposition the model with middle-drag,
   orbit with left-drag, and zoom. Toggle **Geosets** and change faces. The camera,
   zoom, selected animation, playback state and timeline must not reset. Only
   opening a different model, **Frame model**, or **Focus face** should reframe.
   Resize the pane dividers and narrow the window; panes must remain resizable.
5. Mark a model **Keep** or **Mark for deletion**, restart Crucible, and filter by that review.
   The classification must persist without moving/deleting the source model.
6. Pick a face, accessory visibility and a texture from another folder. Choose
   **Save defaults**, then open another model and return. Those choices must be
   restored together. **Defaults** restores saved geosets; **Reset defaults**
   clears the saved override and restores initial geosets and texture bindings.
   Turn 3201 off, change faces, save defaults and reopen the model: it must stay
   off. Boots, sleeves and capes must start off on a model without saved defaults;
   deliberately saved armor choices must still be restored.
7. Switch animations while playing: playback must continue. Duration belongs
   beside the seek bar. Pause, seek and switch geosets: the model stays paused.
8. Open a folder containing a loose model and an identical ZIP/extracted copy.
   There must be one result, with a copy count. Search by either source folder;
   both searches must find that result. In **Details**, choose either original
   location and verify its textures and saved defaults are still available.
   Different SKIN or animation data must remain separate results. Marking the
   group for deletion must persist for all its copies without deleting files.

Automated checks:

2026-09-20 archive/catalog follow-up: focused core tests cover loose/ZIP bundle
grouping, distinct SKIN/ANIM variants (including external AFID/SFID references),
missing-versus-resolved dependencies, JSON round trips, archive traversal/link
rejection, read-only 7z directories, byte-verified hardlinks, existing-file
collisions, resume, deferred space-budget retries, and opt-in removal of verified
archives. Folder, filename, FileDataID and skeleton-ownership indexes are shared
across models. A real 116,403-file collection scanned in 9.16 seconds: 832 source
models, 739 grouped results, 93 duplicate copies, no scan errors. These counts
do not assert that every model renders or works in a client.

Native headless preview regressions passed, including grouped-source search,
null/recycled templates, clearing the old source selector, saved defaults,
camera/playback retention and rendered depth/alpha checks. Manual source-selector
and whole-collection visual checks remain pending.

Latest follow-up: per-material texture layout restored; blind first-variant
selection for every geoset group removed; 3201 no longer forced on by rebuilding
controls or changing faces. Optimized Debug build passed with zero warnings or
errors. Core default-selection tests and the native-frame regression passed,
including saved neck-off/armor-on overrides, per-row cross-folder assignment,
search without assignment changes, unused/unresolved states and stale controls.
Human and Blood Elf previews passed animated-pixel/nonblank checks; the restored
Human texture layout was inspected in a headless screenshot. Ten-frame local
render samples measured 15.1 ms/Human and 14.0 ms/Blood Elf. No global input was
sent; interactive mouse/dropdown use remains a manual check.

Texture-role follow-up: optimized Debug build, focused core suite, native-frame
regressions and Human animated/nonblank preview all passed. The full read-only
`final_bench` catalog contains 44,051 BLPs (the earlier `rg` listing omitted
ignored folders). The Human model now offers 315 body, 561 hair, 322 eye and 20
item/cape choices by default, not 44,051 in every row. The model's own folder
sorts first. Role indexing of that catalog took 467 ms in a local sample.
The UI regression covers same-character scope, cross-model opt-in, saved manual
overrides, source-folder labels and null/recycled dropdown templates. Classification
uses declared M2 paths/FileDataIDs plus recognizable filename/path roles; it is
candidate filtering, not proof of matching UV layouts. Unclassified files are
not inserted into every slot, but remain manually selectable with Browse.

```powershell
dotnet run --project tests/WoWCrucible.Core.Tests -- --model-browser
pwsh -NoProfile -File scripts/Test-ModelBrowserPreview.ps1 -DesktopDirectory src/WoWCrucible.Desktop/bin/Debug/net10.0 -ModelPath <model.m2> -ScreenshotPath <preview.png>
pwsh -NoProfile -File scripts/Test-PreviewFrameLifetime.ps1 -DesktopDirectory src/WoWCrucible.Desktop/bin/Debug/net10.0
```

Viewer follow-up verification: grouped Human face sections, neck covers, absent
variant-1 ears, accessory exclusion, fixed orbit targets and animation names pass
the focused core suite. The native-frame regression also checks camera/model
placement, animation switching, timeline retention, paused seeks, saved defaults
and deletion-mark JSON round trips. Pixel assertions cover intersecting opaque
triangles across materials, ignored opaque texture alpha and cutout holes.
All 500 texture lifetime cycles still pass, as does the 300-cycle creature-template
regression. No global mouse/keyboard automation or user settings writes were used.

Both Human Female (44,027 vertices) and Blood Elf Female (33,588 vertices) render
and animate with their available body textures. The build used
`dotnet build src/WoWCrucible.Desktop -c Debug -p:Optimize=true --no-restore`
in the existing output directory: no extra client or versioned output copy.
Ten-frame 1440x900 headless samples took approximately 29 ms/Human and 15 ms/Blood
Elf per complete browser render. These small local measurements are not a general
performance guarantee. Meshes have per-pixel depth; particles/ribbons still use
the existing composited effect path, and unsupported modern shader approximations
remain documented limitations rather than claims of full client-render parity.

2026-09-19: synthetic folder/ZIP discovery, separate model/skeleton global and
track address spaces, animated vertices, 32-bit triangle offsets, source hashes,
geoset selection, malformed input, and cancellation passed. The real Blood Elf
model rendered with its available body texture; pixel checks distinguished the
idle frames at 0 and 500 ms. Recycled empty/populated list templates passed.
These do not claim manual pointer testing or WoW Model Viewer visual parity.
The final folder pass discovered 506 models: 468 loaded geometry, 465 also
sampled an animation, and 41 reported a load/animation failure. The scan found
96 unopened archives and no scan errors. Parent-skeleton globals and reordered
child camera sequences also have a synthetic regression fixture.

Known gaps remain visible: missing/ambiguous companions, incomplete character
texture composition, unsupported modern material combiners and particle layouts,
and RAR/7z/MPQ containers (counted but not opened). Header versions are format
information, not a verdict about a patched game's ability to render a model.
The broad corpus suite passed its M2 and desktop-layout checks, then stopped at
the race-12-to-22 customization-promotion assertion using the local Tempest DBC
corpus. That suite is not reported as a full pass.

### Texture Change Crash

2026-09-19, reported against `abf0cc7`: changing a texture in Model Browser
terminated the process. The Devbug trace showed rapid texture selections;
Windows Application event 1026 recorded `SKBitmap.ToShader` /
`sk_bitmap_make_shader` in `M2DrawOperation.DrawStage`. No managed crash file
was produced. This was a native render-resource lifetime error, not an M2
format rejection or a recycled list row.

Root cause: deferred render operations borrowed the UI's mutable texture maps
and native bitmaps. Replacing textures disposed bitmaps while older frames
could still use them. Mounted models and the WMO viewer shared that pattern.
Render operations now retain immutable bitmap leases and their own collection
snapshots until Avalonia retires them. Animation poses are copied at frame
capture instead of sharing the UI's next animation sample. Native textures
are released after the final owner finishes; pixel data is not copied per frame.

Verification: the lifetime regression failed on the old build before entering
the unsafe native call. The fixed build passes 100 replacement/clear/disposal
cycles each for M2 material, manual, mounted and particle-composite textures,
plus WMO textures. Each cycle holds two old frames, checks changed pixels for
the new texture, renders old frames on a worker thread, and verifies final
native-handle release. Sampled-pose isolation also passes. The real-model
pixel/animation check, focused model-browser core suite, and creature-template
regression pass. Manual rapid texture selection in the visible app remains a
user check; these tests do not claim complete renderer fidelity.

## Edit a DBC Record

Use disposable copies of `ScalingStatDistribution.dbc` and `ScalingStatValues.dbc`
from the same build. No server or client setup is required to open a WDBC file.

1. Choose **File > Open DBC / DB2...** and open both files. The picker must say
   DBC / DB2, and the files must have separate, clearly named tabs. The large
   tools pane is closed by default. Toggle **Row editor** to show the right pane.
2. Select `ScalingStatDistribution.dbc`, enter an existing ID in **Record ID**,
   and click **Go to ID**. The selected record appears under **Row** on the right.
   Each stat shows its numeric ID and a named choice, directly above its bonus.
   Unused slots show `-1`; unknown IDs stay editable and explicitly unknown.
3. Choose **Save As...** to put the test copy in a separate folder. Change a
   stat ID to `3` and its bonus to `6000` in the right pane. Press **Ctrl+S**
   without leaving the bonus field. Expect Agility and its updated bonus in both
   views. Close the file, reopen the saved copy, and verify both values.
4. Click **Undo**, then **Redo**. Expect the row pane and grid to agree after each
   action. Repeat a change directly in the grid; it must update the row pane too.
5. Enter `not-a-number` in a numeric field and try Save or another file tab.
   Expect a visible validation error, no save or document switch, and the invalid
   input retained for correction. Press Escape in that field to cancel the edit.
6. Switch to `ScalingStatValues.dbc` and select a record. Expect its own character
   level, armor, DPS, and budget fields, not fields left over from the other DBC.
   Filter records, switch tabs, and return: each file retains its own filter.
7. Drag the right-pane divider and narrow the window. Values, labels, and controls
   must remain usable without text painting over adjacent cells. Technical
   container/schema information belongs under **Details**, not in the row form.

The focused non-GUI checks exercise the actual desktop edit history and shared
stat semantics, including 64-bit values and unknown IDs:

```powershell
dotnet tests/WoWCrucible.Core.Tests/bin/Release/net10.0/WoWCrucible.Core.Tests.dll --dbc-editor <stock-dbc-folder>
```

## Resize DBC / DB2 Columns

1. Open a DBC or DB2 using **File > Open DBC / DB2...**. Drag the right edge of
   any column header, including **Row** and **Record ID**. Expect a horizontal
   resize cursor, continuously updated rows, and no dirty-file marker.
2. Make adjacent columns noticeably different widths, then scroll horizontally.
   Click and edit a cell near a boundary: selection and the text box must match
   that cell. Tab between cells and check that navigation reveals the right field.
3. Double-click a header boundary. Expect that column to fit its header and the
   displayed values in all matching rows, not only the rows currently on screen.
   Apply a filter and repeat; auto-fit must use those matching rows. Large tables
   must stay responsive; Escape cancels an in-progress fit or restores a drag.
4. Right-click a header and choose **Reset column width**, then **Reset all column
   widths**. Expect only the chosen scope to reset and the scrollbar to adjust.
5. Enable **Split** on the same document. Resize in either pane; both panes
   must share widths while retaining their own scroll positions. Switch file tabs,
   close/reopen the file, and restart Crucible: adjusted widths must be restored
   for the same table/schema without leaking into an unrelated table layout.
6. Narrow the window or right pane while editing. The text box must stay aligned,
   clipped out of the header/pinned columns, and retain any uncommitted value.

The `--dbc-editor` checks above also cover the actual shared column geometry,
boundary hit tests, variable-width scrolling/navigation, virtual/physical record
IDs, JSON width round-trips, schema isolation, and unchanged DBC data/history.
They do not replace the operating-system pointer and visual checks in this section.

## Creature Appearance Catalog

2026-09-17 regression: build `3162c82` dereferenced an empty template item while
Avalonia recycled a list container. The test below reproduced that exception
before the fix and passed 300 clear/rebind cycles afterward. Interactive scrolling
and filtering still require the manual check below.

1. Open **Creatures & NPCs > DBC appearance catalog** with a configured target
   DBC folder. Click **Target DBC catalog** if the catalog has not loaded yet.
2. Scroll far down and back up repeatedly. Resize the catalog pane while partway
   down the list, switch to **Identity & appearance**, then return to the catalog.
   Expect no crash and the correct display ID, model, scale, and texture details.
3. Search for a known display ID, then text with no matches, then clear the search.
   Reload the catalog. Expect the matching rows to replace the previous results,
   with no stale cards, missing entries, or fatal errors in the Devbug log.
4. Entries missing a model must remain visible with their diagnostic text. The
   lifecycle fix must not filter out problematic data or disable virtualization.

The no-window regression exercises the real built view/template and Avalonia
ContentPresenter through 300 populated/cleared row cycles, including usable and
missing-model entries. It does not operate the mouse or modify saved settings.
Run in a fresh PowerShell 7.6+ process on Windows after a Desktop build:

```powershell
pwsh -NoProfile -File scripts/Test-CreatureAppearanceTemplate.ps1 -DesktopDirectory src/WoWCrucible.Desktop/bin/Debug/net10.0
```

This is a template lifecycle regression, not a full interactive scrolling test.

## Drop a Model

1. Open **World & Assets > Modern asset conversion** (or use Commands).
2. In Windows Explorer, select an extracted `.m2` or `.wmo` file. Drag it onto
   empty space in the asset list or preview area, then repeat on the drop banner.
3. Expect a copy cursor, the filename in the asset list, and an inspection result.
   Adding an asset must not convert it, copy its entire client, or modify it.
4. Drop a non-model file. It must not be advertised as a supported model drop.
   A conversion report is accepted as a report, not as a model.

Test the actual operating-system drag, not only the file-picker code path.
Run Explorer and Crucible at the same Windows privilege level.

## Storage Rules

- Keep authoritative clients, servers, and baseline backups as inputs.
- Reuse existing build outputs and worktrees. Do not clone clients per build.
- Before a storage-limited run, reclaim verified disposable generated files and
  budget all new outputs against those reclaimed bytes. Logical hard-link sizes
  are not physical disk usage.
- Cleanup only deletes exact manifest-owned disposable files. It does not adopt
  old, untracked folders or delete baseline backups because of their names.
- Keep one audit record; remove transient test scratch when the run finishes.

## Cleanup

Build the test runner once, then create a small fixture from the repository root:

```powershell
dotnet build tests/WoWCrucible.Core.Tests -c Release
dotnet tests/WoWCrucible.Core.Tests/bin/Release/net10.0/WoWCrucible.Core.Tests.dll --gui-cleanup-fixture .local/gui-audit/cleanup
```

The fixture command refuses to overwrite an existing directory. It writes six
three-byte markers plus project metadata, never a client copy.

1. Open **Projects & shared IDs**, then **Owned files & cleanup**. Choose the
   fixture folder in that tab's path field. **Apply exact preview** is disabled.
2. Click **Preview cleanup**. Expect three entries (scratch, cache, expired
   diagnostics), nine eligible file bytes, and the chosen project path.
3. Change the path. Expect the entries to clear and Apply to become disabled.
   Restore the fixture path, preview again, then click **Inspect ownership**.
   Expect six owned files and no active cleanup preview.
4. Preview again and click **Apply exact preview** once. Expect three files
   removed, nine file bytes, and no enabled Apply button. Protected deliverable,
   backup, and receipt markers must remain. Preview again: zero eligible files.
5. Choose a nonexistent project folder and preview. Expect an explicit error,
   no stale entries, and Apply disabled. No previous project may be cleaned.

The automated equivalent, including replaced folder links, changed content,
extended expiry, foreign manifests, and duplicate entries, is:

```powershell
dotnet tests/WoWCrucible.Core.Tests/bin/Release/net10.0/WoWCrucible.Core.Tests.dll --artifact-ownership
```

Tests clean up their own fixtures in `finally`/`Dispose`. The manual fixture is
retained for the GUI audit; it is not a versioned client backup.

## Client Storage

Open **CLIENT TABLES & PATCHES > Client workshop > Client storage**. Use **Plan...**
beside **Reviewed plan** to load an existing `client-hardlink-plan.json`.

Check that coverage, group counts, and file rows match that report, and that the
view remains responsive while scrolling. This is a read-only check: do not choose
**Apply reviewed links...** or **Restore independent files...**. An old report is
evidence of its scan, not proof that today's files still match it.


## Repeatable UI stress run — 2026-10-18

Baseline: `9ac1480af5911a388d84c6c6c9fba63357188ace`, tested in the canonical
checkout with the working-tree fixes accompanying this audit. Windows 11,
.NET 10, Avalonia 12.1.0; the native embedded editor uses the installed WebView2.
No production dependency was added. Existing unrelated model work was preserved.

Run from the checkout root, one stress process at a time:

```powershell
dotnet run --project tests/WoWCrucible.Desktop.Tests -- --ui-stress 180034 3000
```

The arguments are a reproducible random seed and action count (1–100,000).
The runner writes an action journal and JSON result/resource samples under
`.local/ui-stress`. Its settings/browser profile belong to the test executable,
not the installed desktop executable. It creates two small DBC fixtures and an
export in that directory, verifies the source files remain unchanged, and never
connects to live SQL or writes game files. It does not move the mouse, send global
keystrokes, or show/activate the test window.

Coverage:

- Rapid open/select/close/reopen across 11 entry points: items, creatures,
  gameobjects, quests, behaviors, pets, pet curves, pet abilities, textures,
  maps, and the embedded editor. Behaviors and pets intentionally share a view.
- Native tab button events, nested tabs, command-palette searches, pane
  visibility/docking/reset, split-layout options, and four window dimensions.
- Embedded spellbook/help/free-mode/split/edit controls, checking JavaScript
  errors/rejections and browser identity across navigation.
- Repeated DBC import/export opening, retained choices, separate editors for
  separate documents, applying an older document's no-op import while another
  table is selected, parent-document closure, unique tab IDs after removal,
  30 overlapping previews per tool, changing options during preview, and
  closing a table during preview. Closing the app must dispose its browser.
- Dispatcher/unobserved-task failures fail the run; sampled process memory,
  handles, and GDI/USER resources are reported rather than hidden behind an
  arbitrary pass threshold. The journal records actions before execution.

Reproduced defects and corrections:

1. Seed **180028**, 50 actions: reopening export created extra tabs containing
   already-disposed editors. Import used the same ownership pattern. Editors
   now belong to their open document and are reused across navigation. Closing
   the document removes its tool pages and releases them. Tab identity allocation
   no longer depends on the current page count. Applying a retained import only
   refreshes panes showing that document.
2. Seed **180030**, 50 actions: overlapping export previews left the final status
   at "Export preview cancelled" after a newer preview succeeded. Superseded
   operations no longer overwrite current progress/status. Cancellation sources
   remain owned by their running task until its final cleanup, including import
   replacement and document closure.
3. Seed **180032**, 50 actions: changing import options during preview allowed
   the obsolete result to repopulate the apply plan. Option/path changes now
   cancel the pending preview; starting another preview clears the previous
   apply plan and confirmation.

After fixes:

| Run | Random actions | Embedded clicks within those actions | Result |
| --- | ---: | ---: | --- |
| Seed 180033 | 2,000 | 2,040 | PASS, including document/preview regressions |
| Seed 180034 | 3,000 | 3,040 | PASS, also including retained-import targeting and closure during preview |
| Workspace shell suite | deterministic navigation/layout checks | — | PASS |
| `scripts/Test-PreviewFrameLifetime.ps1` | 500 replacement/queued-frame/disposal cycles | — | PASS |

The preview-lifetime script also passed its existing pose, texture selection,
camera/geoset/playback continuity, and depth checks. Build: zero warnings/errors.
For seed 180034, measured handler-plus-layout P95 was **31.46 ms**, maximum
**109.86 ms**; these are not end-to-end human input latency measurements. From
steps 2,250–3,000, GDI objects stayed at 25, USER objects at 32–33, and native
handles at 759–760. After the document phase and final collection, managed memory
was 131.8 MiB and process private memory 393.7 MiB. This short run is not proof
against long-session or dataset-dependent leaks.

Harness correction: never-shown Avalonia windows normally leave composition
inactive. The first exploratory run therefore retained dirty visuals and was
not a valid leak measurement. The runner explicitly starts the real compositor;
reported passing runs above use that corrected setup.

Limits: this drives native controls/handlers and WebView DOM events with real
layout/rendering, not OS mouse hit-testing, focused keyboard input, or native
file-picker dialogs. It does not cover every editor, large live datasets, SQL
operations, or every possible timing. Source-file integrity checks and the
separate preview lifetime suite supplement the randomized navigation checks.

## Systematic control activation audit — 2026-10-18

The reproducible inventory and per-control evidence are in
[UI activation coverage](validation/ui-coverage/README.md). Unlike the randomized
stress test, this walk enumerates the workspace command routes, native tabs,
realized controls and dropdown options, plus the embedded editor's DOM controls.
A separate source callback catalog exposes definitions outside the realized
screens without claiming they were activated.

Run from the checkout root:

```powershell
dotnet run --project tests/WoWCrucible.Desktop.Tests -- --ui-coverage --activate
python scripts/Write-UiCoverageReport.py
```

For the embedded-editor regression alone, add `--embedded-only`; it writes a
separate local report and does not overwrite the full inventory. Test settings,
generated fixtures and reports stay in the test executable or `.local/ui-coverage`.
Windows remain unshown and their real compositor runs. A test-only storage
provider cancels native pickers; picker cancellation is explicitly identified in
the evidence, not counted as successful file import/export. Confirmations in the
embedded editor also cancel. Existing app settings and live game data are untouched.

The loaded-data scenarios use a generated BLP texture and Spell.dbc, plus a
temporary Crucible project. They exercise texture editing controls and staging
database creation, query, mutation preview/application, diff and publication to
an in-memory DBC. Assertions check that the source files remain unchanged.

The audit reproduced a persistent `ResizeObserver loop completed with
undelivered notifications` error in the embedded canvas. Rounding the stage size
up at fractional zoom, combined with a percentage minimum size, caused scrollbars
to change the observed viewport repeatedly. Removing the rounding alone did not
resolve the extended Free Mode scenario. Canvas fit now uses CSS percentage sizing
against the viewport and explicit board extents, eliminating the observer that
measured and rewrote its own layout. The regression exercises
25%, 50%, 100%, 101%, 125% and 200% zoom and checks that viewport dimensions settle;
browser errors still fail the audit rather than being suppressed.

The loaded staging scenario also reproduced Avalonia's cross-thread-access
exception: creation read a checkbox inside `Task.Run`; query and mutation read
text fields there too. These operations now capture their input on the UI thread
before scheduling the service call. The fixture checks success statuses, applies
a mutation through the reviewed workflow, and verifies the open document becomes
dirty while its on-disk source remains unchanged.

This is not a claim of complete interaction coverage. Disabled/data-dependent
controls, native pointer hit-testing, live SQL/deployment, Windows integration,
and contexts not instantiated by the walk remain explicit gaps. Read-only
outputs and menu containers are catalogued but have no input action. See the
report for the exact tested/failed/untested breakdown and reasons.

Final run: **4,763** inventoried runtime cases, **3,487** activated, **0** failed,
**1,276** untested; **1,153** textual source callback declarations catalogued
separately. The final full run includes the canvas geometry assertions and loaded
staging success assertions. It exited successfully with no captured JavaScript,
dispatcher or runner errors. WebView2 emitted a shutdown diagnostic about
unregistering `Chrome_WidgetWin_0` (Windows error 1412) after the passing summary;
this is recorded separately and is not a claim of a clean native stderr log.

Post-fix randomized regression: seed **180035**, **2,000/2,000** actions and
**1,940** embedded clicks passed, including the existing document/preview checks.
