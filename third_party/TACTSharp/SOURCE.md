# TACTSharp source and build provenance

- Upstream source: <https://github.com/Marlamin/TACTSharp>
- Pinned commit: `7a3ba22a960b0f056b13ebd10843c769085bca04`
- License: MIT; see `LICENSE` in this folder.
- Included source snapshot: upstream `*.cs` files under `src`, with line endings normalized to LF and trailing whitespace removed
- Local project wrapper: `TACTSharp.csproj`, target framework `net10.0`, version `0.2.0-alpha`

The vendored source is built with Crucible through its project reference. A standalone equivalent is:

```powershell
dotnet build third_party/TACTSharp/TACTSharp.csproj -c Release
```

Crucible uses TACTSharp only as a local, read-only fallback when CascLib cannot
open an incomplete custom installation. `TryCDN` and listfile downloading are
disabled before any configuration is loaded.
