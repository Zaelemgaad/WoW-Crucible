# Shared editor workspace

Crucible keeps its tool browser and open-workspace tabs visible while editing.
Opening Items, Maps, Textures, SQL, or another native editor no longer replaces
the application navigation. Home opens the content editors directly; Data tables
retains the existing multi-document DBC/DB2 editor, including its save prompts,
history and row inspector.

This is the shared-shell implementation from the
[editor research](research/editor-ux-2026-10-16/index.html). It is not a claim that
every existing authoring form has received its final redesign. Individual object
workflows, universal draft/save semantics and freely floating native tool panels
remain separate work. Class Studio retains its shared frontend and existing
movable/snapping canvas; no second browser implementation was introduced.

## Navigation

- Search the tool browser using object names or tasks, including spell, item,
  terrain, texture, patch, and database. Its groups and the Editors menu use the
  existing shared command catalog; no capabilities were removed.
- Open editors appear in tabs. Select a tab to return to the same editor instance.
  Closing its tab preserves that editor's state **for the current app session**;
  reopen it from the tool browser. Closing a workspace tab does not save, apply,
  delete, or discard its content. App shutdown still follows the existing editor
  persistence rules; this is not automatic draft recovery across restarts.
- Home and Data tables stay pinned. The arrow menu lists every open workspace
  when the tab row overflows. Right-click an editor tab to move it left or right.
- `Ctrl+Tab` / `Ctrl+Shift+Tab` cycle workspaces. `Alt+Left` visits the previous
  open workspace. `Ctrl+W` closes the active workspace tab or, inside Data tables,
  the active data document with its existing modified-file checks.
- `Ctrl+K` finds commands. Data-table save/undo/redo shortcuts operate only while
  that editor is visible; they cannot silently modify a hidden DBC from another
  editor. File-menu save actions explicitly name data tables.

## Layout

The **Layout** menu is always available. The tool browser can sit on either edge,
resize with its divider, close with its corner X, and reopen with **Tools**.
**F6** temporarily focuses the editor and restores the surrounding panes when
pressed again. **Restore default layout** changes layout, not content.

The shared split-pane control supports:

- draggable dividers with separate remembered proportions for wide and tall
  arrangements;
- explicit side-by-side, top-to-bottom, or automatic arrangement;
- swapping pane positions and focusing either pane;
- restoring both panes and resetting the original split;
- persistent layout in the existing portable `Config/desktop.json` settings.

These actions are in **Layout > Editor panes** and the divider's context menu.
Double-clicking a divider restores its original split. The implementation covers
editors using `ResponsiveSplitGrid`; it does not pretend that every custom viewport
or legacy fixed grid has become a floating dock.

Editor action bars appear below the active tab. Server status stays in the global
header. Server-operation feedback has a dismiss button; it retains failure details
and existing lifecycle handling. The underlying CoA source adapters, archive order,
record schemas and deployment validation are unchanged.

## Implementation and checks

- `WorkspaceNavigation` groups existing commands once for the browser, Home and
  Editors menu. Tests require every workspace command to occur exactly once.
- `WorkspaceTabState` owns tab order, active selection and bounded Back history.
  `MainWindow.WorkspaceShell` retains editor instances and owns navigation/layout.
- `ResponsiveSplitGrid` owns pane arrangement and proportion changes. It exposes
  the same actions to its divider menu and the shell's Layout menu.
- `EditorLayoutSettings` is serialized by the existing atomic settings writer.
  Invalid stored split fractions fall back to the editor's initial proportions.
- Asset comparison resumes when its retained tab is selected. An interrupted
  initial index is retried rather than treated as an already loaded index.

Run:

```powershell
dotnet build src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj --no-restore -v minimal
dotnet run --project tests/WoWCrucible.Desktop.Tests -- --workspace-shell
dotnet run --project tests/WoWCrucible.Desktop.Tests
dotnet run --project tests/WoWCrucible.Core.Tests -- --class-studio
```

The shell suite uses actual Avalonia controls with isolated empty settings. It
checks tab closure/reopening, retained edit content, history/order, every workspace
route's navigation coverage, layout serialization, orientation changes, left/right
browser placement, and asset-editor suspension/resumption. It renders Home at
1440×900, 1024×768 and 800×600, and Items/Textures/Maps at the first two sizes into
`.local/editor-shell-tests`. It does not focus or send global input to the desktop,
connect a real database, alter game files, or claim a human usability test.

Manual checks still required: real pointer dragging, keyboard traversal with the
window focused, and user evaluation of the new workflow. The research dossier's
broader novice-authoring trials remain pending.

Validation on 17 October 2026: Class Studio now uses a retained embedded view and
direct native messages. The former HTTP host and browser-launch path have been
removed. Real WebView2 integration, the full CoA catalog, the desktop shell, core
readers, canvas, independent spec boards and spellbook UI fixtures pass. See
[Class Studio](CLASS-STUDIO.md) for commands and remaining manual checks. Localhost
connectivity is no longer part of the editor's operation or validation.
