# Field-notes acceptance status

Date reviewed: 2026-08-26

Canonical source: `F:\from_U235T\WOW_CRUCIBLE_FIELD_NOTES.md`

This is an acceptance ledger, not a replacement for the field notes. The field-note backlog is not entirely fixed. Items below are classified by current source and test evidence; a similarly named UI or helper does not count as completion of a broader contract.

## Implemented

| Field-note contract | Evidence |
|---|---|
| MPQ native `MAX_PATH` preflight | `PatchArchiveService` calculates every absolute StormLib source path, exposes the longest path, and refuses paths beyond its 259-character native boundary before archive mutation. Regression coverage exercises a boundary-length source. |
| Noncanonical empty DBC string offsets | `WdbcFile` distinguishes offset zero from semantic empty text, preserves donor empty offsets, and interns a real NUL for new empty values. The regression fixture begins its string block with nonempty data. |
| Whole extracted patch-stack overlap index | `ExtractedArchiveOverlapIndexService` builds a resumable SQLite index across ordered named stacks, records supplier/effective/conflict state, treats structured tables separately, and has CLI plus same-window query surfaces. |
| Artifact ownership and safe cleanup | `ArtifactOwnershipService` records exact generated paths, hashes, operation/category/expiry, previews reclaimable bytes, and revalidates project identity, resolved path, ownership, size, and hash before deleting only eligible owned files. |
| MPQ reserved metadata round trip | `(listfile)`, `(attributes)`, and `(signature)` are metadata, omitted from ordinary content extraction, rejected as source payload collisions, and covered by extract/recreate/byte-compare tests. |
| Empty DBC stage-query semantics | A valid zero-row query exits successfully. `--require-rows` and `--expect-count=N` make cardinality explicit; conflicting expectations are rejected. |
| Unsigned DBC JSON import | Structured import accepts decimal `4294967295`, `0xFFFFFFFF`, and bare `FFFFFFFF` for `uint32`; regression coverage requires identical output bytes. |

## Partial

| Field-note contract | Current boundary |
|---|---|
| Component-level playable-race customization | Native race/customization promotion, exact provenance closure, paired ordinary/HD table handling, runtime path limits, and specialized DK palette exposure exist. A general interactive per-component material planner for every race and physical texture family is not complete. |
| Batch character-material authoring | Texture composition, character appearance previews, strict asset provenance, and runtime-path policy exist. The requested all-race batch planner with native-template discovery, primary/companion occupancy, full material-family classification, and one review queue is not complete. |
| Reversible client/server deployment as one owned unit | Target-bound client releases and synchronized DBC/SQL bundles have receipts and rollback. A universal deployment unit spanning every generated MPQ/CASC payload, arbitrary server table set, and every runtime destination is not complete. |

## Pending

### Effective-client full-table row-loss guard

`DbcLayerComparer` can compare two explicitly selected full tables and report added, modified, and removed IDs. That is useful evidence, but it does not satisfy the field-note publication contract.

The missing Core feature must resolve the effective pre-output client load chain for every full DBC replacement, prove the winning provider, compare identity sets, reject duplicate IDs and unexplained removals, reject unrelated server DBC inputs, and require an explicit removed-ID manifest for deliberate deletions. Patch creation must call that guard automatically, and the GUI must display provider/source/output counts plus added/changed/removed IDs before publication.

Until that exists, a manually assembled full-table patch can still replace a richer late client table with an undersized server copy. This remains a release blocker for claiming that all current field notes are fixed.
