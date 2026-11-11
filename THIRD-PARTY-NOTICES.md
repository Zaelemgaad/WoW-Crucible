# Third-party notices

## StormLib

WoW Crucible includes the 64-bit Windows StormLib binary for MPQ archive support.

Copyright (c) 1999-2013 Ladislav Zezula. StormLib is distributed under the MIT License; see [third_party/StormLib/LICENSE](third_party/StormLib/LICENSE).

## TACTSharp

WoW Crucible includes TACTSharp by Martin Benjamins as a local-only fallback for custom CASC installations that omit Blizzard install manifests while retaining usable root, encoding, and index data. TACTSharp is distributed under the MIT License; see [third_party/TACTSharp/LICENSE](third_party/TACTSharp/LICENSE) and exact build provenance in [third_party/TACTSharp/SOURCE.md](third_party/TACTSharp/SOURCE.md).

## BCnEncoder.NET

WoW Crucible uses BCnEncoder.NET by Nominom for managed BC1/BC2/BC3 texture compression and decompression. The NuGet package is offered under `MIT OR Unlicense`; Crucible uses it under the Unlicense option. Project: <https://github.com/Nominom/BCnEncoder.NET>.

## StbImageSharp

WoW Crucible uses StbImageSharp, a fully managed C# port of `stb_image`, to decode common source images and BLP1 JPEG payloads. Its project declares the port public domain. Project: <https://github.com/StbSharp/StbImageSharp>.

## Microsoft.Data.Sqlite

WoW Crucible uses Microsoft.Data.Sqlite as the managed ADO.NET provider for project-local DBC staging databases. Copyright Microsoft Corporation. It is distributed under the MIT License. Project: <https://github.com/dotnet/efcore>.

## SQLitePCLRaw and SQLite

WoW Crucible uses SQLitePCLRaw by Eric Sink / SourceGear to load the bundled native SQLite runtime. SQLitePCLRaw is distributed under the Apache License 2.0; the native SQLite library is in the public domain. The patched native package is pinned explicitly so NuGet cannot resolve the vulnerable SQLite build formerly pulled by the provider bundle. Projects: <https://github.com/ericsink/SQLitePCL.raw> and <https://sqlite.org/>.

## CUE4Parse and CUE4Parse-Conversion

The command line's `models import-mod` uses CUE4Parse and CUE4Parse-Conversion by FabianFG and contributors (the library FModel is built on) to read Unreal Engine paks and IoStore containers and to write ActorX meshes and animations. Both are distributed under the Apache License 2.0. Project: <https://github.com/FabianFG/CUE4Parse>. They bring their own dependencies through NuGet, among them SixLabors.ImageSharp (Six Labors Split License: Apache 2.0 for open source and qualifying use), SkiaSharp (MIT), SharpGLTF (MIT), Newtonsoft.Json (MIT), Serilog (Apache 2.0), BouncyCastle (MIT) and ZstdSharp (MIT). Oodle and zlib-ng are not bundled: the command uses copies already on the PC (beside it, or FModel's).

## Reference projects

WDBX Editor, WoW Spell Editor, Keira3, WoW Database Editor, AzerothCore, and TrinityCore informed format research and workflow design. Their source code and game/server data are not vendored into this repository. Each remains subject to its own license.
