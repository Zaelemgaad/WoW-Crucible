# Addons

Open **Addons** from the command palette. Choose an addon collection or client
folder, set its interface number (30300 for original Wrath), and scan. An optional
excluded folder is skipped together with its entire subtree. The result list and
inspection pane can be resized; search covers names, versions, paths and findings.

The audit compares complete package path/content hashes, follows TOC and XML
script/include references, checks installation-local required dependencies, and
keeps account and character SavedVariables declarations separate. Embedded library
TOCs are not counted as separate installed addons. Blizzard dependencies are supplied
by the client. Junctions and symbolic links are not traversed.
Packages with excluded content or links have no complete fingerprint and are not
listed as exact duplicates. Exclusions apply inside packages as well as during
folder discovery.

```powershell
wowcrucible client addon-audit "E:\Addon Collection" --interface=30300 --exclude="E:\Addon Collection\Reference" --output="E:\addon-audit.json"
```

Exit 3 means the report contains load-reference or discovery errors; exit 1 is an
execution failure. No files are modified by scanning. Matching version labels do
not prove matching content. Interface differences require API review, not an
automatic TOC-number edit. Static checks do not prove live-client behavior.

Consolidating a multi-addon suite also requires reviewing runtime loaders, addon
events, asset paths and existing SavedVariables. This audit does not pretend that
moving child directories alone performs that conversion, and does not deploy
addons or alter client settings.

The focused core regression is `WoWCrucible.Core.Tests --addon-audit`. On Windows,
`scripts/Test-AddonAuditView.ps1 -DesktopDirectory <build> -AddonDirectory <collection>`
checks list recycling, selection, filtering and inspector state without opening a
window or sending keyboard/mouse input.
