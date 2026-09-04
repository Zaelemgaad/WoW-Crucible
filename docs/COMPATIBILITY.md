# Compatibility contract

WoW Crucible's primary, verified client target is World of Warcraft 3.3.5a, build 12340. MoP 5.4.8 and Legion 7.3.5 are experimental target profiles with real-corpus table and server-binding proof. Client compatibility is profile-driven so community contributors can add targets without scattering build checks throughout the editor. Server compatibility is deliberately not fixed to a bundled repack, database dump, or historical core revision.

WoWDBDefs `.dbd` build ranges and exact build-specific WDBX XML are independent native schema sources for WDBC and fixed-layout WDB2 tables. WDC1 requires an exact build and layout-hash-resolved DBD layout. Optional audit proof writes unchanged tables to isolated temporary storage and requires byte-identical SHA-256 output. Mutation audits additionally reload every changed logical value, verify metadata/side-table behavior, and require a stable second save. Raw `PTCH` update layers, WDB5/WDB6, and WDC2+ remain separate formats and are never treated as interchangeable.

## Client target profiles

Built-in profiles currently describe:

| Client | Build | Tier | Current boundary |
|---|---:|---|---|
| Classic 1.12.1 | 5875 | Schema ready | WDBC definition is available; full corpus round-trip validation is still required. |
| The Burning Crusade 2.4.3 | 8606 | Schema ready | WDBC definition is available; full corpus round-trip validation is still required. |
| Wrath of the Lich King 3.3.5a | 12340 | Verified | Primary tested WDBC and MPQ target. |
| Cataclysm 4.3.4 | 15595 | Experimental | The real locale-cache slice—20 WDBC and 5 fixed-layout WDB2 tables—is exact-schema and byte-round-trip verified. Raw update-layer `PTCH` files are identified, but complete base-plus-delta reconstruction, full base-corpus verification, and later DB2 families remain pending. |
| Mists of Pandaria 5.4.8 | 18414 | Experimental | A real SkyFire server corpus passed 417 nonempty WDBC/WDB2 schema, byte-round-trip, and isolated mutation checks; 34 zero-byte placeholders remain explicitly unverifiable. The 58 WDB2 files carry the compatible producer build 18273. MPQ deployment is supported. |
| Legion 7.3.5 | 26972 | Experimental | All 611 real LegionCore WDC1 tables passed exact layout resolution and byte round trips. Isolated mutations cover every supported storage mode plus external IDs, copies, relationships, and offset maps. CASC is read/extract and payload staging only; no CASC publication claim is made. |

Each profile declares a stable ID, client build, compatible embedded table builds, schema filename, table formats, archive format, support tier, and notes. Additional JSON profiles can be placed in `%LOCALAPPDATA%\WoWCrucible\Profiles` or the application's `profiles` directory. A profile enables only capabilities implemented by the engine; WDB2 and WDC1 files retain their format identity and are never mislabeled as WDBC.

Definition XML files provide names and types, but are not proof of safe round trips. A target moves to **Verified** only after its complete legal test corpus passes unmodified byte-for-byte round trips and representative edits.

The exact 2026-08-26 MoP/Legion corpus evidence and nonclaims are recorded in [the compatibility-lab report](validation/MOP-LEGION-COMPATIBILITY-LAB.md). Outstanding field-note contracts are tracked separately in [the acceptance ledger](FIELD-NOTES-STATUS.md).

`tools compatibility-clone` and the desktop **Compatibility lab** prepare isolated client/server worktrees from the same explicit request used by the audit. Copying is streamed and cancellation-resumable only through a matching Crucible ownership marker; completed clones are immutable inputs and are accepted only after full relative-path, length, and SHA-256 identity proof. Exclusions are either declared directory leaf names or exact normalized source-relative file paths, and the same contract controls preparation, marker ownership, and later audits. A lane can also disable recursive table discovery when its selected effective table directory contains inactive locale or legacy subtrees; that scope is shared by schema, mutation, deployment, and collision checks. The service refuses wildcards, path traversal, source/destination overlap, destination collisions, and reparse-point traversal before treating a tree as an isolated test surface.

## Constructive MoP and Legion hybrid

The native profile boundary and the constructive mashup are separate contracts. Directly opening a Legion WDC1 table as a MoP WDBC/WDB2 table remains invalid. `tools cross-build-mashup` instead reads both exact schemas, projects donor semantics into newly written host-layout rows, collision-remaps identities, rewrites declared references, and verifies the resulting MoP files before publication. The original host and donor inputs remain immutable.

The currently proved direction is `mop-18414 <- legion-26972`. Optional FileData bridging resolves Legion CASC assets by exact listfile path and either reuses the MoP client's effective asset or publishes a verified host-compatible payload. Legion `ItemVisuals` model IDs receive an explicit `ItemVisualEffects` projection instead of being copied into a field with different meaning. Supported version-272/274 M2 models are projected to native unchunked MoP `MD20` v272 with their exact SKIN v3 and BLP closure; unsupported ribbons/shaders, multi-SKIN models, missing local CASC files, and every other unresolved dependency skip their owning donor rows and remain visible in the result.

The full `Mists of the Pandaren Legion v5` evidence is recorded in [the compatibility-lab report](validation/MOP-LEGION-COMPATIBILITY-LAB.md). This proves an installed hybrid MPQ plus matching server-table payload in an isolated MoP clone. It does not prove that SkyFire implements donor-only Legion systems, and it does not promote either experimental profile to Verified.

## Supported server families

### AzerothCore

- Target: current `azerothcore/azerothcore-wotlk` `master`.
- Compatibility is determined from the core revision, the world database `version` row, and live `information_schema` metadata.
- SQL is generated through an AzerothCore adapter. AzerothCore-only tables or columns must not leak into other adapters.

### TrinityCore

- Target: current `TrinityCore/TrinityCore` branch `3.3.5`.
- TrinityCore `master` targets the modern retail client and is **not** a valid server profile for WoW Crucible's 3.3.5a client project.
- Compatibility is determined from the branch/revision, TDB version, and live `information_schema` metadata.
- SQL is generated through a TrinityCore 3.3.5 adapter.

### SkyFire 5.4.8

- Target profile: `mop-18414` with MPQ plus WDBC/WDB2 tables.
- Installed workspace detection recognizes ProjectSkyfire/SkyFire evidence and separate `dbc`/`db2` roots.
- Source-backed bindings parse the selected checkout instead of reusing AzerothCore or TrinityCore assumptions. The audited checkout resolves 172 loaded DBC stores and 16 explicitly unused stores.
- Live SQL authoring remains capability-driven; a SkyFire source binding is not a claim that every guided AzerothCore SQL workflow is portable.

### LegionCore 7.3.5

- Target profile: `legion-26972` with CASC plus WDC1 tables.
- Installed workspace detection recognizes LegionCore and rebinds absolute configured client-table paths to an explicitly selected isolated clone during compatibility testing.
- Source-backed bindings resolve 297 loaded stores and 307 explicitly unused stores in the audited checkout.
- Server table deployment can be planned and staged. Client WDC1 publication requires a separately proved target-build CASC publisher and is therefore reported as a boundary, never silently routed through MPQ.

## Detection and safety rules

Before enabling server writes, a profile must collect:

1. Core family (`AzerothCore`, `TrinityCore`, `SkyFire`, or `LegionCore`).
2. Git branch and revision when a source checkout is available.
3. Server-reported core revision when a running worldserver is available.
4. World database version/cache ID.
5. Actual table, column, type, nullability, default, key, and constraint metadata from `information_schema`.

Generated operations target a capability model, not a guessed version number. For example, a spell workflow asks whether the selected profile supports `spell_proc`, which columns exist, and how that core represents ranks. The adapter then emits the appropriate SQL.

The application can connect to a live MySQL/MariaDB world database and discover its complete schema through `information_schema`. Connection passwords are session-only. SQL Studio exposes every discovered table and column with primary-key-safe writes, while the guided item, creature/NPC, gameobject, quest, gossip, dialogue, normalized/legacy trainer, condition, and SmartAI editors map only proved columns, preview/export SQL, parameterize values, and write transactionally. The behavior layer uses current normalized tables when present and retains a portable `npc_trainer` adapter for older cores. Views, triggers, procedures, functions, and events are discovered from server metadata and read back through `SHOW CREATE`; visibility and mutation remain subject to the connected account's native MySQL/MariaDB privileges rather than being treated as proof that an object category is empty.

Database synchronization requires a baseline-compared audit whose stock and edited snapshots expose matching core identity. The live target may have additional columns, but every selected source key and changed field must exist; a target-only required INSERT column, missing table/field, or divergent value blocks that operation instead of guessing a translation. This supports exact same-schema and compatible additive-schema promotion today. Occupied numeric single-column keys can be remapped only through the explicit opt-in plan setting: Crucible allocates above the live maximum and rewrites selected relationships recognized from target metadata/adapters. Renamed or semantically changed fields across different core generations still require future adapters.

DBC deployment uses a separate server-table binding model. A binding records the core family/profile, supported revision description, DBC filename, server consumption state, SQL overlay table, record-key strategy, known row dimensions, deployment destinations, and restart requirement. Crucible ships a versioned 3.3.5 GT profile and can parse the selected checkout's `DBCStores.cpp` for source-backed mappings. Live `*_dbc` schemas are inspected rather than hidden by the older content-table allowlist. The DBC/SQL audit is read-only; its optional migration output is an idempotent preview and is never applied implicitly. A synchronized deployment bundle binds that reviewed audit to SHA-256 identities for the edited DBC, schema, and live server DBC plus an exact database endpoint/user/schema and SQL row pre-image. Apply and receipt-driven rollback both abort on stale inputs, changed rows, changed files, or a different target profile.

The **Detect Server Workspace** flow reads a live `worldserver.conf` to import `DataDir` and `WorldDatabaseInfo`. It ignores `.conf.dist` templates, never persists the detected password, and supports a Windows control/DBC folder whose launcher points to a WSL-hosted configuration. Selecting a source checkout is intentionally insufficient because its template credentials do not prove an installed server layout. Normal WSL localhost forwarding is attempted first. When MySQL is proven unreachable from Windows and the installed WSL configuration declares a loopback host, Crucible may create a process-owned ephemeral TCP relay from that distribution's current host-facing IPv4 address to its `127.0.0.1` MySQL listener. The relay binds an operating-system-assigned port, retains the declared database identity in artifacts/settings, applies to every permitted schema/login on that configured endpoint, and is destroyed on retarget/exit. It never edits `bind-address`, firewall rules, `netsh portproxy`, or the server configuration.

If a required capability cannot be proven, WoW Crucible must stop that part of the operation and explain what is missing. It must not silently generate old-repack SQL.

## Content projects

A WoW Crucible project will keep portable content intent separate from deployment output:

```text
Portable content model
    ├── Selected client profile → supported DBC/DB2 changes and patch archive
    ├── Current AzerothCore adapter → revision-aware SQL/module changes
    ├── Current TrinityCore 3.3.5 adapter → revision-aware SQL/core changes
    ├── SkyFire source bindings → MoP server table deployment plan
    └── LegionCore source bindings → Legion server table deployment plan
```

This permits one content project to be checked against an explicit client/server target rather than copied across expansions by filename. Cross-target profile tests deliberately reject raw MoP tables under the Legion profile and raw Legion tables under the MoP profile, even when 355 logical table names overlap. A deliberate hybrid must use the constructive mashup pipeline above so every row is rewritten into the host layout and its losses remain auditable.

## Legacy SQL recovery: capture and offline baseline audit implemented

An old customized database is evidence, not a deployable patch by itself. Phase one is now implemented as `wowcrucible db snapshot`: a SELECT-only streaming capture of world base tables into an atomic compressed `.crucible-db-snapshot` artifact. It records server/core identity where discoverable, table engines and collations, ordered primary keys (including composite keys), columns/types/nullability/defaults, exact row counts, canonical row values, and per-table plus aggregate SHA-256 hashes. `db snapshot-inspect` validates the artifact completely offline.

The live connection password is never serialized. Known auth and character runtime-state tables are excluded by default without excluding reusable world definitions such as mail loot, instance templates, guild rewards, or pet level stats. `--include-sensitive` is an explicit override and can place account-derived secrets from table rows into the artifact, so such captures must be protected accordingly. A consistent read-only transaction is requested and its actual support state is recorded; the service still contains no database-writing command when an older server can provide only a best-effort consistent read.

The **Legacy SQL Recovery & Promotion** workflow uses a three-way model:

```text
verified baseline → legacy edited server → current target server
                  └─ captured intent ──────┘
```

The baseline-to-legacy boundary is now implemented as `db recovery-audit`. It verifies both input snapshots first, hash-binds the resulting compressed artifact to them, and records keyed row additions, edits, removals, and exact before/after field values. Tables are grouped into understandable domains such as items and sets, classes/races and starting data, pets, spells, creatures, vendors and loot, quests, gameobjects, and DBC overlays. Without a baseline it deliberately produces unattributed candidates rather than claiming every stock row is custom. No-primary-key tables, collation-dependent textual keys that snapshot format v1 cannot order portably, and incompatible or partially captured schemas are visible but blocked from row inference.

Target comparison proves which selected changes can be applied unchanged, which collide, and which are already present. Target-bound plans, three-way row states, exact changed-row dependency closure, SQL preview, transactional apply, exact rollback receipts, and opt-in coordinated ID remapping for selected recognized relationships are implemented. For differently shaped targets, `db sync-bridge` generates an editable profile bound to both the verified audit and canonical live target schema. Same-name fields pass through; primary-row table/key/column renames, reviewed non-key drops, and typed required-target defaults are explicit and hash-bound. Reviewed structural outputs can additionally emit one or more normalized target rows from each audited source operation. Each output selects admitted source change kinds and a target INSERT/UPDATE/DELETE kind, maps the complete live target primary key, and binds every required before/after value to an exact audited source value, typed constant, or reviewed named lookup. Lookups use explicit typed equality predicates against another changed audit row or the live target; zero/multiple matches block, and live evidence is rechecked under locks before apply. Deterministic numeric add/multiply, string prefix/suffix, typed exact-map, and null-fallback transforms are available without arbitrary SQL or script execution. Structural DELETE requires every writable target column so rollback can recreate the exact row. Blank bindings, ambiguous lookups, key/column/output collapse, missing required fields, and unsafe preimages block rather than guessing; dependency closure retains all outputs sharing one source lineage. Applying a current v5 plan after a target schema or target-lookup result change is refused. Deeper integration with the persistent project ID registry remains future work.

Neither a snapshot nor a baseline delta claims to infer intent. Target-specific SQL is generated only after live capability inspection, collision checks, explicit selection, and a reviewable three-way plan; apply repeats those checks under row locks.

Safety defaults are deliberately conservative: a detected difference is not automatically treated as intentional, nothing is written during capture or comparison, credentials are excluded from every artifact, and legacy deletions are not exported or applied by default. Any future deployment step must retain SQL preview, parameterized operations, transactions, and an explicit rollback path.

## What legacy material is used for

The `Ac-Web Repack v1.0` directory is useful as an extracted corpus of real 3.3.5a DBC files. It is not an authoritative database schema, API, or deployment target.

Keira3 and WoW Database Editor are reference implementations for workflows and current schema knowledge. WoW Crucible does not inherit their database assumptions without checking them against the selected live profile.
