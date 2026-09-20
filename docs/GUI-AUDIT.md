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
      has its own dropdown and browse control. Shared search filters available
      BLPs throughout the opened folder without changing assigned textures.
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

Automated checks:

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
