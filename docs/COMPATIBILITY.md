# Compatibility contract

WoW Crucible's primary, verified client target is World of Warcraft 3.3.5a, build 12340. Client compatibility is profile-driven so community contributors can add targets without scattering build checks throughout the editor. Server compatibility is deliberately not fixed to a bundled repack, database dump, or historical core revision.

## Client target profiles

Built-in profiles currently describe:

| Client | Build | Tier | Current boundary |
|---|---:|---|---|
| Classic 1.12.1 | 5875 | Schema ready | WDBC definition is available; full corpus round-trip validation is still required. |
| The Burning Crusade 2.4.3 | 8606 | Schema ready | WDBC definition is available; full corpus round-trip validation is still required. |
| Wrath of the Lich King 3.3.5a | 12340 | Verified | Primary tested WDBC and MPQ target. |
| Cataclysm 4.3.4 | 15595 | Experimental | MPQ and WDBC-era files are understood, but DB2 editing is not implemented. |

Each profile declares a stable ID, client build, schema filename, table formats, archive format, support tier, and notes. Additional JSON profiles can be placed in `%LOCALAPPDATA%\WoWCrucible\Profiles` or the application's `profiles` directory. A profile enables only capabilities implemented by the engine; selecting Cata does not mislabel a DB2 file as editable WDBC.

Definition XML files provide names and types, but are not proof of safe round trips. A target moves to **Verified** only after its complete legal test corpus passes unmodified byte-for-byte round trips and representative edits.

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

## Detection and safety rules

Before enabling server writes, a profile must collect:

1. Core family (`AzerothCore` or `TrinityCore`).
2. Git branch and revision when a source checkout is available.
3. Server-reported core revision when a running worldserver is available.
4. World database version/cache ID.
5. Actual table, column, type, nullability, default, key, and constraint metadata from `information_schema`.

Generated operations target a capability model, not a guessed version number. For example, a spell workflow asks whether the selected profile supports `spell_proc`, which columns exist, and how that core represents ranks. The adapter then emits the appropriate SQL.

The application can now connect to a live MySQL/MariaDB world database and inspect selected content tables through `information_schema`. Connection passwords are session-only. The guided item creator is the first capability-aware writer: it previews/exports SQL, maps only columns proved to exist, refuses duplicate entry IDs, parameterizes values, and inserts within a transaction. Creature/NPC, vendor, loot, quest, race, and class creators will reuse the same inspection boundary.

DBC deployment uses a separate server-table binding model. A binding records the core family/profile, supported revision description, DBC filename, server consumption state, SQL overlay table, record-key strategy, known row dimensions, deployment destinations, and restart requirement. Crucible ships a versioned 3.3.5 GT profile and can parse the selected checkout's `DBCStores.cpp` for source-backed mappings. Live `*_dbc` schemas are inspected rather than hidden by the older content-table allowlist. The DBC/SQL audit is read-only; its optional migration output is an idempotent preview and is never applied implicitly.

The **Detect Server Workspace** flow reads a live `worldserver.conf` to import `DataDir` and `WorldDatabaseInfo`. It ignores `.conf.dist` templates, never persists the detected password, and supports a Windows control/DBC folder whose launcher points to a WSL-hosted configuration. Selecting a source checkout is intentionally insufficient because its template credentials do not prove an installed server layout.

If a required capability cannot be proven, WoW Crucible must stop that part of the operation and explain what is missing. It must not silently generate old-repack SQL.

## Content projects

A WoW Crucible project will keep portable content intent separate from deployment output:

```text
Portable content model
    ├── Selected client profile → supported DBC/DB2 changes and patch archive
    ├── Current AzerothCore adapter → revision-aware SQL/module changes
    └── Current TrinityCore 3.3.5 adapter → revision-aware SQL/core changes
```

This permits one custom spell/item/race/class project to be validated and deployed to either supported server family where the requested feature is implementable.

## Legacy SQL recovery: capture and offline baseline audit implemented

An old customized database is evidence, not a deployable patch by itself. Phase one is now implemented as `wowcrucible db snapshot`: a SELECT-only streaming capture of world base tables into an atomic compressed `.crucible-db-snapshot` artifact. It records server/core identity where discoverable, table engines and collations, ordered primary keys (including composite keys), columns/types/nullability/defaults, exact row counts, canonical row values, and per-table plus aggregate SHA-256 hashes. `db snapshot-inspect` validates the artifact completely offline.

The live connection password is never serialized. Known auth and character runtime-state tables are excluded by default without excluding reusable world definitions such as mail loot, instance templates, guild rewards, or pet level stats. `--include-sensitive` is an explicit override and can place account-derived secrets from table rows into the artifact, so such captures must be protected accordingly. A consistent read-only transaction is requested and its actual support state is recorded; the service still contains no database-writing command when an older server can provide only a best-effort consistent read.

The **Legacy SQL Recovery & Promotion** workflow uses a three-way model:

```text
verified baseline → legacy edited server → current target server
                  └─ captured intent ──────┘
```

The baseline-to-legacy boundary is now implemented as `db recovery-audit`. It verifies both input snapshots first, hash-binds the resulting compressed artifact to them, and records keyed row additions, edits, removals, and exact before/after field values. Tables are grouped into understandable domains such as items and sets, classes/races and starting data, pets, spells, creatures, vendors and loot, quests, gameobjects, and DBC overlays. Without a baseline it deliberately produces unattributed candidates rather than claiming every stock row is custom. No-primary-key tables, collation-dependent textual keys that snapshot format v1 cannot order portably, and incompatible or partially captured schemas are visible but blocked from row inference.

The pending target comparison will prove which explicitly approved changes can be applied unchanged, which require column translation or ID remapping, and which depend on related rows that must move together. Domain labels are not yet dependency closure: core-specific relationship adapters, selection, three-way conflict states, target capability translation, ID-registry proposals, rollback generation, and transactional deployment remain unimplemented.

Neither a snapshot nor a baseline delta claims to infer intent, relationships, or safe deployment. Target-specific SQL will be generated only after live capability inspection, dependency validation, collision checks, explicit user selection, and a reviewable three-way plan.

Safety defaults are deliberately conservative: a detected difference is not automatically treated as intentional, nothing is written during capture or comparison, credentials are excluded from every artifact, and legacy deletions are not exported or applied by default. Any future deployment step must retain SQL preview, parameterized operations, transactions, and an explicit rollback path.

## What legacy material is used for

The `Ac-Web Repack v1.0` directory is useful as an extracted corpus of real 3.3.5a DBC files. It is not an authoritative database schema, API, or deployment target.

Keira3 and WoW Database Editor are reference implementations for workflows and current schema knowledge. WoW Crucible does not inherit their database assumptions without checking them against the selected live profile.
