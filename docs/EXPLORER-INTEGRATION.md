# Windows Explorer Menu

Crucible can add a cascading **Crucible** menu for `.dbc` and `.db2` files.
Enable it under **Workspace setup > Windows Explorer**. The checkbox removes
the menu again. Registration is per Windows user and does not require an
administrator or change default file associations.

Select one or many files in Explorer, then right-click:

- **Crucible > Open** opens the selection as editor tabs.
- **Crucible > Convert to CSV** writes `Table.csv` beside each `Table.dbc` or `Table.db2`.
- **Crucible > Convert to JSON** writes `Table.json` in the same way.

This is a classic cascading context menu. On Windows 11 it may be under
**Show more options**. Crucible does **not** have to be running. Conversion
starts an on-demand worker, never the editor. A native progress window offers
cancellation, then a summary reports exported, skipped, failed, and cancelled
files. The worker exits after 15 idle seconds; no service or startup task is
installed. Only **Open** starts the editor.

## Export Behavior

- Original DBC/DB2 files are read-only during export.
- Existing CSV/JSON outputs are skipped, never silently overwritten.
- Selecting a same-named DBC and DB2 in the same folder rejects both conflicting
  outputs. Neither silently wins.
- One invalid file does not stop the rest of the batch.
- Cancellation keeps completed exports and removes the current partial output.
- CSV uses UTF-8, decoded strings, invariant numbers, and proper quoting. JSON
  uses an array of objects. Both share the editor's row exporter, including its
  `$recordKey` and `$rowIndex` fields.
- Schema discovery is shared with the editor. Configured XML definitions apply
  to WDBC; WDB2 and WDC1 use WoWDBDefs with their build/layout identity. An
  unknown or mismatched layout fails explicitly instead of exporting guessed
  field meanings. Configure definitions in Workspace setup or DBD schemas & audit.
- Full per-file results replace **one** report beside the executable:
  `Logs/Explorer/last-export.json`. **Open last export results** in the Explorer
  settings opens it. No repeated backups or report history are generated.

The menu targets the executable used to install it. After moving a portable
installation, disable and re-enable the checkbox from the new location.

## Command Line Registration

Run the desktop executable, not `wowcrucible.exe` or `dotnet`:

```powershell
& .\WoWCrucible.Desktop.exe --install-explorer-menu
& .\WoWCrucible.Desktop.exe --remove-explorer-menu
```

`--quiet` suppresses error dialogs for scripted registration; failures still
return exit code 1 and write the latest report. Registration owns only the
Crucible menu and its three COM class keys beneath `HKCU/Software/Classes`.
Conflicting keys not marked as Crucible-owned are rejected.

## Architecture And Verification

Explorer invokes a per-user **out-of-process** COM local server through
`IExecuteCommand` / `IObjectWithSelection`. The selected `IShellItemArray` is
passed intact rather than expanded into a shell command. This avoids the
legacy per-file process and selection-size limits described in
[Microsoft's verb selection documentation](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-employ-the-verb-selection-model).
No managed extension is loaded into Explorer. Opening the editor transfers the
selection over redirected stdin, also without command-line length limits.

```powershell
dotnet run --project tests/WoWCrucible.Core.Tests -- --batch-export
.\scripts\Test-ExplorerIntegration.ps1 -ExecutablePath <desktop.exe>
```

The Windows regression queries and invokes the real cascading shell menu
without clicking or taking desktop focus. It covers cold activation with no
editor running, a 512-file selection longer than a Windows command line,
CSV/JSON values, same-name collisions, untouched inputs, existing outputs,
worker shutdown, and install/remove. `-DbcDirectory <folder>` additionally
exports disposable copies of the two scaling-stat DBCs. `-KeepInstalled` leaves
the tested executable registered; otherwise the previous registration is
restored. Fixtures are removed, not retained as another client copy.
