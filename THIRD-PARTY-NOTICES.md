# Third-party notices

## StormLib

WoW Crucible includes the 64-bit Windows StormLib binary for MPQ archive support.

Copyright (c) 1999-2013 Ladislav Zezula. StormLib is distributed under the MIT License; see [third_party/StormLib/LICENSE](third_party/StormLib/LICENSE).

## BCnEncoder.NET

WoW Crucible uses BCnEncoder.NET by Nominom for managed BC1/BC2/BC3 texture compression and decompression. The NuGet package is offered under `MIT OR Unlicense`; Crucible uses it under the Unlicense option. Project: <https://github.com/Nominom/BCnEncoder.NET>.

## StbImageSharp

WoW Crucible uses StbImageSharp, a fully managed C# port of `stb_image`, to decode common source images and BLP1 JPEG payloads. Its project declares the port public domain. Project: <https://github.com/StbSharp/StbImageSharp>.

## Microsoft.Data.Sqlite

WoW Crucible uses Microsoft.Data.Sqlite as the managed ADO.NET provider for project-local DBC staging databases. Copyright Microsoft Corporation. It is distributed under the MIT License. Project: <https://github.com/dotnet/efcore>.

## SQLitePCLRaw and SQLite

WoW Crucible uses SQLitePCLRaw by Eric Sink / SourceGear to load the bundled native SQLite runtime. SQLitePCLRaw is distributed under the Apache License 2.0; the native SQLite library is in the public domain. The patched native package is pinned explicitly so NuGet cannot resolve the vulnerable SQLite build formerly pulled by the provider bundle. Projects: <https://github.com/ericsink/SQLitePCL.raw> and <https://sqlite.org/>.

## Reference projects

WDBX Editor, WoW Spell Editor, Keira3, WoW Database Editor, AzerothCore, and TrinityCore informed format research and workflow design. Their source code and game/server data are not vendored into this repository. Each remains subject to its own license.
