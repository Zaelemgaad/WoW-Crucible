# CUE4Parse-Natives

- Upstream: `https://github.com/FabianFG/CUE4Parse` (folder `CUE4Parse-Natives`), the native part of the CUE4Parse
  library the command line already uses through NuGet (1.2.2.202610).
- What it does here: decompresses ACL-compressed animations (`nReadACLData`, `nReadCurveACLData`) for
  `models import-mod(s)`. Without it those animations cannot be exported.
- Origin of this binary: the copy FModel ships inside its single-file `FModel.exe` (extracted by .NET to
  `%TEMP%\.net\FModel\<id>\` when FModel runs), taken from the user's FModel install on 2026-11-11. It is the static build
  (imports only `KERNEL32.dll`), so it needs no Visual C++ runtime.
- Exports: `IsFeatureAvailable`, `nAllocate`, `nDeallocate`, `nCompressedTracks_IsValid`, `nReadACLData`,
  `nReadCurveACLData`, `nTracksHeader_SetDefaultScale`: every entry point CUE4Parse 1.2.2.202610 imports from it.
- It contains no Oodle code (Oodle is not bundled; the command uses a copy already on the PC).
- License: CUE4Parse is Apache-2.0 (`LICENSE`); it is built with the Animation Compression Library (`LICENSE-ACL`, MIT)
  and Realtime Math (`LICENSE-RTM`, MIT) by Nicholas Frechette.
- `win-x64/CUE4Parse-Natives.dll` SHA-256: `176F5C81A42DF41CE8E5D5DD6F981939446F828E5265D5F4D263FCA086E8EAEF`
