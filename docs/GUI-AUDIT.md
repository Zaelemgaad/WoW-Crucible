# GUI Audit

Record the commit/build, test name, exact action, expected result, observed result,
and pass/fail/pending status. A passing core test is not a passing GUI test.
Retest changed workflows after every small batch of improvements.

The desktop may be in active use during testing. Do not send global keystrokes,
move the pointer, activate/reposition windows, or assume focus without arranging
an exclusive test interval. Use background tests and control-specific inspection;
mark focus-dependent results inconclusive if another application received input.
Leave manual keyboard/drag checks for the user when the desktop is shared.

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
