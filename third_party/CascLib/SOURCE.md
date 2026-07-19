# CascLib native provider

- Upstream: `https://github.com/ladislav-zezula/CascLib`
- Audited source commit: `9fb2d3894198d44dee2ec56c0d48b49727a124d8`
- License: MIT; see `LICENSE` in this directory.
- Build: x64 Release with `.github/workflows/build-casclib.yml`.
- Reproducibility patch: `.github/patches/CascLib-no-mfc-resource.patch` replaces the optional MFC resource header with the standard Windows resource header. It does not change CascLib code or exports.
- Build run: `https://github.com/Zaelemgaad/WoW-Crucible/actions/runs/29677052971`
- `win-x64/CascLib.dll` SHA-256: `4D8A43172D08A4B5D47B854F1C59EDCA10B482C076E330CC94FBC20F516DF39C`

The DLL is a native read-only storage provider. Crucible does not use the opaque `CascLibCore.dll` distributed with older CASC Explorer bundles.
