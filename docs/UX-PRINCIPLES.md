# Editing UX principles

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
- A change preview showing both the portable content change and generated target-specific output.
- A clear distinction between a complete deployment and only the fields changed by the current action.
- Multi-row sub-editors for effects, loot, conditions, and similar repeated structures.
- Reversible operations and an explicit dirty/changed state.

The generic virtual DBC table remains available as an escape hatch. It is not intended to be the primary interface for authoring a spell, item, race, or class.

See [REFERENCE-TOOL-AUDIT.md](REFERENCE-TOOL-AUDIT.md) for the local tool-corpus findings and implementation order.
