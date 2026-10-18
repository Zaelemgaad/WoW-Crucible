# UI activation coverage

Observed: 2026-10-18T07:01:02.8920955+00:00

This is an activation inventory, not a claim that every workflow succeeds.
`activated` means the recorded handler/selection was exercised. Picker cancellation,
empty-data validation, and successful loaded-data actions are distinguished in Evidence.
`untested` cases remain explicit; disabled controls are never force-enabled.

| Screen | Activated | Failed | Untested |
| --- | ---: | ---: | ---: |
| AddonAuditView | 11 | 0 | 7 |
| AssetComparisonView | 92 | 0 | 42 |
| BehaviorWorkspaceView | 42 | 0 | 22 |
| CacheTableWorkspaceView | 5 | 0 | 11 |
| ClientWorkspaceView | 48 | 0 | 43 |
| CompatibilityLabView | 2 | 0 | 7 |
| CreatureWorkspaceView | 120 | 0 | 46 |
| DbcExportWorkspaceView | 24 | 0 | 3 |
| DbcImportWorkspaceView | 12 | 0 | 2 |
| DbcStagingWorkspaceView | 21 | 0 | 5 |
| DbdSchemaAuditView | 19 | 0 | 4 |
| GameObjectWorkspaceView | 125 | 0 | 34 |
| ItemWorkbenchView | 730 | 0 | 54 |
| KnowledgeWorkspaceView | 12 | 0 | 8 |
| LayeredDbcWorkspaceView | 12 | 0 | 15 |
| MapWorkspaceView | 175 | 0 | 59 |
| ModelBrowserView | 43 | 0 | 14 |
| MpqWorkspaceView | 81 | 0 | 17 |
| NativeConversionWorkspaceView | 18 | 0 | 18 |
| PetAbilityGraphView | 20 | 0 | 5 |
| PetLevelCurveView | 40 | 0 | 23 |
| ProjectWorkspaceView | 94 | 0 | 26 |
| QuestWorkspaceView | 157 | 0 | 146 |
| ServerSqlWorkspaceView | 67 | 0 | 51 |
| SqlWorkspaceView | 242 | 0 | 114 |
| TextureWorkspaceView | 148 | 0 | 29 |
| TextureWorkspaceView-loaded | 153 | 0 | 28 |
| ToolInventoryView | 8 | 0 | 4 |
| UserControl | 0 | 0 | 2 |
| WorkspaceSetupView | 28 | 0 | 7 |
| embedded | 559 | 0 | 72 |
| navigation | 35 | 0 | 0 |
| shell | 134 | 0 | 201 |
| shell-loaded | 210 | 0 | 157 |

Total: 4763 observed cases; 3487 activated, 0 failed, 1276 untested.
Unhandled/runner errors: 0.

- [Every observed control/option and its evidence](runtime-controls.csv)
- [Declared source callbacks](source-callbacks.csv)

Source catalog: 1153 detected callback declarations. It includes factories
and handlers for contexts not instantiated by the runtime walk. It is a textual
catalog, not a compiler proof: indirect bindings, templates, and data-generated
controls can require additional scenarios. Source rows are never counted as activated
merely because a runtime control has a similar label.

Reproduce from the checkout root:

```powershell
dotnet run --project tests/WoWCrucible.Desktop.Tests -- --ui-coverage --activate
python scripts/Write-UiCoverageReport.py
```

## Remaining coverage

The counts include individual dropdown choices, repeated controls in different
screens, and read-only outputs; they are not a count of unique app features.
Native controls are driven through handlers/properties and browser controls through
DOM events. This does not test OS mouse hit-testing, native file-dialog interaction,
or every data-dependent state. Live SQL, deployment, Windows integration, and
large asset workflows need separate isolated fixtures. No live client/server data
was written by this run.

| Untested reason | Cases |
| --- | ---: |
| Requires a reviewed data/output, system integration, navigation, or deployment scenario; not blindly clicked. | 539 |
| Dynamic control inventoried outside the activation snapshot; needs a separate state scenario. | 216 |
| Disabled in this fixture state; needs enabling data/context. | 150 |
| No rows populated; needs a loaded-data scenario. | 70 |
| Inventoried; scenario not reached. | 67 |
| Pointer-driven resize requires hit-testing; layout changes are covered separately. | 60 |
| No choices populated; needs loaded data. | 60 |
| Read-only output; no input activation applicable. | 32 |
| Context action requires target selection, system integration, or a reviewed scenario. | 31 |
| Menu container; individual child commands are inventoried separately. | 20 |
| Option inventoried but not selected in this run. | 11 |
| Disabled in this fixture state. | 9 |
| Changes Windows integration or opens a diagnostic console; requires an exclusive desktop scenario. | 6 |
| Needs a dedicated import or commit scenario; retained as untested. | 2 |
| File-input selection needs an import fixture; not opened on the shared desktop. | 2 |
| Read-only or disabled. | 1 |