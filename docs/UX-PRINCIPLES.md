# Editing UX principles

The [16 October 2026 editor UX research dossier](research/editor-ux-2026-10-16/index.html)
compares 17 editor families and 40 sources, audits the CoA-targeted application,
and specifies complete task flows and usability trials. The
[shared editor shell](EDITOR-WORKSPACE.md) implements the navigation and pane-layout
foundation. The remaining task-flow requirements are proposals, not completed or
user-tested behavior. Use their task-level gates alongside these principles when
replacing an editor workflow.

Keira3 demonstrates several workflow ideas that are worth retaining independently of its Angular/Electron implementation and AzerothCore-specific data model.

Specialized WoW Crucible editors should provide:

- A workflow-oriented start surface that names user goals before file formats or database tables.
- A beginner-safe guided intent layer, a complete change-plan preview, and expert controls over the same underlying model.
- Intent templates with documented defaults rather than unexplained bundles of numeric values.
- Live previews in recognizable in-game terms when practical, such as item tooltips and quest dialogs.

- Searchable human-readable selectors for referenced IDs, while keeping the numeric value visible.
- Duplicate/clone-first creation for complex records such as spells, creatures, and items.
- Fields grouped by purpose rather than one enormous raw-record form.
- Flags presented as named checkboxes with the raw hexadecimal mask available.
- Immediate validation beside the affected field.
- Related-record previews and navigation without leaving the editor.
- Major workspaces should replace the main content area and provide a clear way back instead of multiplying top-level windows without need.
- Every workflow must remain operable at its supported minimum window size: controls wrap or scroll, dense panes use splitters, and resizing must not leave actions permanently off-screen.
- Form fields use content-aware, minimum, and stretch sizing instead of hard-coded widths. Fixed dimensions are reserved for intrinsically fixed visuals such as icons or preview tiles, not ordinary inputs.
- A change preview showing both the portable content change and generated target-specific output.
- A clear distinction between a complete deployment and only the fields changed by the current action.
- Multi-row sub-editors for effects, loot, conditions, and similar repeated structures.
- Reversible operations and an explicit dirty/changed state.

The generic virtual DBC table remains available as an escape hatch. It is not intended to be the primary interface for authoring a spell, item, race, or class.

See [REFERENCE-TOOL-AUDIT.md](REFERENCE-TOOL-AUDIT.md) for the local tool-corpus findings and implementation order.

## Reusable panels and layout presets

User direction accepted on 17 October 2026: the unit of composition is an editing
panel, not an isolated class-editor application. Class authoring is a named preset
assembled from the same tools available elsewhere. Embedding the existing editor
removes its transport dependency; it does not by itself satisfy this requirement.

The reviewed references give specific interaction precedents:

| Reference | Observed behavior | Requirement for Crucible |
| --- | --- | --- |
| [Unity Inspector](https://docs.unity3d.com/6000.0/Documentation/Manual/InspectorOptions.html) | Multiple inspectors can be moved, docked, resized and focused on a particular object. | Properties can follow selection or stay pinned. Editing a spell must not require opening an unrelated class workspace. |
| [Godot docks and layouts](https://docs.godotengine.org/en/stable/tutorials/editor/customizing_editor.html) | Docks can move, resize or float; named layouts retain their arrangements. | A layout stores panel placement and visibility, independently of game-data edits. Closing/resetting a panel never deletes its draft. |
| [Unreal editor interface](https://dev.epicgames.com/documentation/en-us/unreal-engine/unreal-editor-interface) | The viewport, object list, Details and content tools provide distinct roles in one editor. | Search/browse, selection, editing and preview must have identifiable roles and share the same selected object. |

These are design precedents, not evidence that copying their appearance will make
Crucible usable. The existing dossier covers 17 editor families and 40 sources;
no claim of testing thousands of apps or proving novice usability is made.

The class-authoring preset should open a spell catalog, learning rules, talent
canvas/spec boards, sideboard, properties, and changes/export panels. Each panel
must also be available independently. Other presets should reuse the same panels
where their responsibilities match, and users must be able to save their own
arrangements. Tool defaults should provide a useful starting layout so a new user
does not have to build one before editing.

Implementation boundaries for the next UI pass:

- Shared project/source/draft services own records and edits. Panels render and
  manipulate those services; they must not each load a conflicting copy of the data.
- Layout state owns positions, dimensions, visibility, docking and optional grid
  snapping. Moving/resetting layouts never changes game content or selected ranks.
- A single panel catalog drives Add panel, reopen, and presets. A preset is data
  describing an arrangement, not a duplicated class-specific interface.
- Each panel has an obvious title, close control, resize affordance and recoverable
  placement. Grid visibility and spacing apply to the workspace; edge, neighbor,
  and grid snapping remain separate choices.
- User-facing controls are named for the job: find spell, change cost, choose
  learning level, review changes. IDs/raw tables remain available as expert detail.
- Validate a complete class workflow plus a mixed item/spell workflow, close/reopen,
  restart, narrow windows and a fresh-user trial before calling the redesign done.

The modular-panel framework and presets are still pending implementation. Current
retained editor tabs and embedded Class Studio are integration infrastructure,
not a substitute for this work.
