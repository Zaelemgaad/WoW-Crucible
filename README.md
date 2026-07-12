# WoW Crucible

> [!WARNING]
> WoW Crucible is in very early development. Back up your files and validate generated client/server changes before using them on a live project.

WoW Crucible is an open-source content-authoring toolkit for World of Warcraft 3.3.5a (build 12340). The long-term goal is one coherent workflow for DBC editing, patch MPQ creation, spells, items, creatures, races, classes, and current AzerothCore/TrinityCore 3.3.5 integration.

The client build is fixed, but server integration will not be tied to an old repack. The compatibility contract targets current AzerothCore `master` and current TrinityCore branch `3.3.5` through separate capability-driven adapters. See [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## What works today

- Opens and saves 3.3.5a `WDBC`/`.dbc` files directly.
- Uses a virtual, double-buffered grid suitable for large files such as `Spell.dbc`.
- Includes its own complete 234-column `Spell.dbc` schema and accepts external build-12340 definitions for generic tables.
- Resolves and safely extends DBC string tables.
- Searches all fields in parallel.
- Edits strings, bytes, integers, unsigned values, raw 32-bit values, and floats.
- Creates blank records, clones records with a new ID, and deletes selected records.
- Saves atomically and creates `.bak` files before overwriting data.
- Accepts DBC and patch-builder drag-and-drop.
- Builds WotLK patch MPQs from edited DBCs or existing folder trees.
- Displays editable internal MPQ paths and preserves folder hierarchy.
- Maps standalone DBCs to `DBFilesClient\\<name>.dbc`.
- Synchronizes a saved DBC into a selected server `data\\dbc` directory with backup.

Specialized editors will follow the principles in [docs/UX-PRINCIPLES.md](docs/UX-PRINCIPLES.md): grouped fields, named flags, human-readable references, change previews, validation, and duplicate-first creation.

## Run

Requirements: Windows x64 and the .NET 10 SDK.

```powershell
dotnet run --project src/WoWCrucible.App/WoWCrucible.App.csproj
```

Open a DBC directly:

```powershell
dotnet run --project src/WoWCrucible.App/WoWCrucible.App.csproj -- "C:\path\to\Spell.dbc"
```

## Build

```powershell
dotnet build WoWCrucible.slnx -c Release
```

The corpus test runner accepts a WDBX 12340 definition XML and a directory containing extracted 3.3.5a DBC files. Copyrighted game data is intentionally not included.

## Roadmap

1. Undo/redo, bulk operations, validation, and project-wide ID allocation.
2. Spell workspace with grouped effects, flags, localized text, and related-table navigation.
3. CSV import/export and target-aware SQL generation.
4. MPQ browsing and extraction.
5. Current AzerothCore and TrinityCore 3.3.5 server profiles.
6. Guided item, race, and class creation spanning required client and server records.

## Contributing

The project is intentionally public at an early stage. Bug reports, workflow suggestions, format research, tests, and focused pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## Legal

WoW Crucible is an independent community project and is not affiliated with or endorsed by Blizzard Entertainment. World of Warcraft and related names are trademarks of their respective owners. This project does not distribute Blizzard game data.

Source code is released under the [MIT License](LICENSE). StormLib retains its license under [third_party/StormLib/LICENSE](third_party/StormLib/LICENSE).
