# Reference tool audit

WoW Crucible uses the local legacy tools as workflow research, not as compatibility authorities. Their strongest ideas are reimplemented against Crucible's build-12340 client model and capability-detected current server adapters.

No Trinity Creator source or visual assets are copied into Crucible. The local Trinity Creator checkout does not include a clear source license, so it is treated strictly as behavioral reference material.

## Trinity Creator

Useful patterns:

- Starts with named authoring jobs: item, quest, creature, loot, vendor, and model viewing.
- Groups fields by the concept a user is editing instead of exposing database columns first.
- Uses readable enumerations, bitmask checklists, contextual labels, and nearby lookup buttons.
- Provides live item-tooltip and quest-dialog previews in the vocabulary users see in game.
- Supplies intent-based creature templates such as boss, questgiver, vendor, repair vendor, and escort NPC.
- Calculates or suggests dependent values such as prices, IDs, and item-derived properties.
- Keeps lookup tools available beside the active editor.
- Separates SQL-file export from direct database deployment.
- Attempts profile-driven table/column mapping.

Ideas to retain but redesign:

- Templates should be portable content intents with a visible change preview, not UI code that directly sets old database fields.
- Database writes must use transactions, parameterized commands, schema introspection, and a generated SQL preview. Trinity Creator's string-built queries and delete-then-insert overwrite behavior are not acceptable deployment foundations.
- Profiles should describe detected capabilities, not claim compatibility based only on an emulator/date label.
- Suggested defaults must explain what they changed and remain reversible.

## Keira3

Useful patterns:

- Searchable selectors for related records rather than bare numeric IDs.
- Reusable multi-row editors for loot, conditions, and other child collections.
- Immediate full-query and changed-fields-only SQL previews.
- Editors organized around current AzerothCore domain models.
- Shared option catalogs, validation, and consistent selector buttons.

Crucible must keep Keira's AzerothCore assumptions behind an AzerothCore adapter and separately support current TrinityCore 3.3.5 capabilities.

## WoW Database Editor

Useful patterns:

- Definition-driven editors and parameters.
- Solution/project organization for related changes.
- Visual quest chains, world-map context, smart-script tooling, SQL interpretation, and source integration.
- Modular providers that separate core-specific behavior from shared editing infrastructure.

Its breadth is a useful architectural reference; Crucible should avoid reproducing its complexity in beginner workflows.

## WDBX Editor and MyDbcEditor

Useful patterns:

- Broad format/schema coverage and external definition corpora.
- Staging multiple tables and generic raw editing.
- Fixed-value catalogs that turn numeric cells into named choices.

Crucible retains the raw table as a power-user escape hatch while domain workspaces become the normal authoring surface.

## MPQ Editor and StormLib tools

Useful patterns:

- Familiar archive tree/list browsing, filters, extraction, and explicit internal paths.

Crucible adds background operations, small manifest-driven patch construction, content policies, archive validation, and refusal to copy-update giant source layers.

## Product model

Every creator should have three synchronized layers:

1. **Guided intent** — templates, readable choices, safe defaults, live preview, and explanations.
2. **Change plan** — all affected DBC rows, server records, files, IDs, warnings, and validation results before mutation.
3. **Expert controls** — raw values, advanced fields, SQL/manifest output, CLI automation, and target-specific overrides.

A beginner can stay in the first layer. A power user can inspect and alter every generated detail without leaving the project or losing the portable intent.

## Implementation order

1. Start Center and workflow-oriented navigation.
2. Shared searchable reference selector and side lookup panel.
3. Portable project/change-plan model with validation and preview.
4. Capability-detected database connection profiles and read-only schema inspection.
5. Guided item creator with live tooltip preview and SQL/DBC change plan.
6. Creature templates and model/faction lookups.
7. Quest, loot, vendor, conditions, and related multi-row editors.
8. Race/class workflows spanning every required client and server table.
