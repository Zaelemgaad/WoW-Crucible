# WoW Crucible branding

`WoWCrucible-source.png` was supplied by repository owner Zaelemgaad on
2026-07-17 for use as the project's app mark. It is independent project
branding and must not be presented as official Blizzard artwork or endorsement.
Its original creation/source provenance should remain documented if the art is
ever replaced or relicensed separately from the repository.

`WoWCrucible.ico` and `WoWCrucible-128.png` are deterministic derivatives.
Regenerate them from the source artwork with:

```powershell
.\scripts\Build-AppIcon.ps1 `
  -SourcePng .\src\WoWCrucible.Desktop\Assets\Branding\WoWCrucible-source.png `
  -OutputIco .\src\WoWCrucible.Desktop\Assets\Branding\WoWCrucible.ico `
  -OutputPreviewPng .\src\WoWCrucible.Desktop\Assets\Branding\WoWCrucible-128.png
```
