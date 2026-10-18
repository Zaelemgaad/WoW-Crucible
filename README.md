# WoW Crucible

**A desktop editor for World of Warcraft private-server projects.**

Build talent trees, edit game data, work with models and textures, and prepare
client patches in one application. Crucible runs locally on your PC.

## What can I edit?

| I want to… | Open… |
| --- | --- |
| Design classes, talent trees and spell lists | Class Studio |
| Change items, NPCs, quests or other server records | The matching content editor, or SQL Studio |
| Edit spells and other client data records | Data tables — the game's DBC / DB2 files |
| Work with appearances | Models or Textures |
| Edit terrain and world placements | Maps & World |
| Inspect client files or build a patch | Client Inspector or Archives & Patches |

**Start with Client & server setup**, then choose an editor from **Editors** or
the searchable tool browser. **File → Open game file** picks the appropriate
editor for a supported file. **Layout** controls which panels and bars you see;
**F6** switches to an editor-only view and back.

## Run it

On Windows, install the **.NET 10 SDK**, clone this repository, then run this
command from its folder:

```powershell
dotnet run --project src/WoWCrucible.Desktop/WoWCrucible.Desktop.csproj
```

Class Studio uses the Microsoft Edge WebView2 Runtime. It is embedded in Crucible;
it does not need a web server. Bring your own client files and server data.

## What should I know first?

Crucible is under active development. **WotLK 3.3.5a is the main tested target**;
other versions and custom cores have different levels of support. Check the
[compatibility guide](docs/COMPATIBILITY.md) for your project. Keep backups and
review changes before applying them to a live server or client.

## More detail

- [Extended README](README-EXTENDED.md) — full feature list, commands and build details
- [Class Studio guide](docs/CLASS-STUDIO.md)
- [Opening files and editing items / quests](docs/EDITOR-FIELDS.md)
- [UI test results and known coverage gaps](docs/GUI-AUDIT.md)
- [Report a bug or request a feature](https://github.com/Zaelemgaad/WoW-Crucible/issues)
- [Contributing](CONTRIBUTING.md)

Independent community project; not affiliated with Blizzard Entertainment.
No Blizzard game data is distributed. [MIT license](LICENSE); bundled libraries
retain their own licenses.
