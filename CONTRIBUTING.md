# Contributing

WoW Crucible is early software. Please discuss large architectural changes in an issue before investing substantial work.

## Development setup

- Windows x64
- .NET 10 SDK
- A legally obtained WoW 3.3.5a client or extracted test DBCs for local validation

Build the solution with:

```powershell
dotnet build WoWCrucible.slnx -c Release
```

Do not commit Blizzard game data, client executables, MPQ archives, server databases, credentials, or locally generated patches.

Keep changes focused, explain format assumptions, and add validation for binary-format changes. UI contributions should preserve the generic table editor while favoring understandable specialized workflows for normal content creation.
