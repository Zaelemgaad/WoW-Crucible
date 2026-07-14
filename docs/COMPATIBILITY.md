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

## What legacy material is used for

The `Ac-Web Repack v1.0` directory is useful as an extracted corpus of real 3.3.5a DBC files. It is not an authoritative database schema, API, or deployment target.

Keira3 and WoW Database Editor are reference implementations for workflows and current schema knowledge. WoW Crucible does not inherit their database assumptions without checking them against the selected live profile.
