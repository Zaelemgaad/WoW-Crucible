# MoP and Legion compatibility lab

Date: 2026-08-26

## Scope

This audit used disposable worktrees under the repository's ignored `.local\compat-lab` directory. Source clients, runnable servers, and source-core checkouts were read-only inputs. Dynamic `Cache`, log, crash, error, and Legion `OmegaTransmog` directories were explicitly excluded from clone identity; every other source/clone file was matched by relative path, length, and SHA-256.

| Lane | Client source | Server source | Core source |
|---|---|---|---|
| MoP 5.4.8 | `E:\wowps\5.4.8 MOP\World of Warcraft 5.4.8` | `E:\wowps\5.4.8 MOP\MOPTempest\Server` | `E:\wowps\Src Repos\SkyFire_548` |
| Legion 7.3.5 | `E:\wowps\7.3.5 LEGION\Client` | `E:\wowps\7.3.5 LEGION\LegionCore` | `E:\wowps\Src Repos\LegionCore-7.3.5` |

The passing run is `compat-20260826-074800-6d390deeb00a4f3c9fe093dd29eede31`. Its ignored machine-local JSON and Markdown reports remain under `.local\compat-lab\runs`.

## Current source routing

The table above is frozen evidence for the 2026-08-26 run; it is not an active-source registry. As of
2026-08-30, new Legion work uses only `E:\wowps\Firestorm Launcher\Legion`. The fully hash-audited
2026-08-26 Legion clone remains preserved as an immutable reproducibility input, but no new run may
silently treat its former live-client path as current.

The current stock MoP server baseline is the binary Emucoach repack at
`E:\wowps\EMUCOACH\mop\MOPPREMIUM\Repack`. `MOPTempest` is a frequently changing personal project,
so it is no longer the stock MoP lane. No exact source checkout for the Emucoach binary has been
identified; the active machine-local request records a null core source and Crucible reports the
resulting loss of source-backed server-consumer evidence instead of substituting SkyFire source.

These routes are staged in separate date-named clone roots. They have not replaced the passing corpus
or been claimed as a passing run.

## Results

| Check | MoP 5.4.8 | Legion 7.3.5 |
|---|---:|---:|
| Profile | `mop-18414` | `legion-26972` |
| Archive boundary | MPQ | CASC |
| Discovered table files | 451 | 611 |
| Exact schema matches | 417 | 611 |
| Byte-identical unchanged round trips | 417 | 611 |
| Empty server placeholders | 34 | 0 |
| Schema or persistence failures | 0 | 0 |
| Source-backed loaded stores | 172 | 297 |
| Source-backed unused stores | 16 | 307 |
| Native deployment entries classified identical | 451 | 611 |

MoP's 417 nonempty tables comprise 359 WDBC and 58 WDB2 files. The WDB2 files carry producer build 18273, which is explicitly admitted by profile `mop-18414`; it is not treated as an accidental build mismatch. The fixed-layout mutation audit covered WDBC, WDB2, strings, float32, 8/32/64-bit values, arrays, padding, permitted structural edits, and blocked side-table structural edits.

Legion's mutation audit covered all five WDC1 storage modes (`none`, `immediate`, `common`, `pallet`, and `pallet-array`) plus inline and external IDs, copy tables, relationships, offset maps, and empty tables. Every selected mutation was reloaded, compared logically, saved a second time, and required stable canonical bytes.

As an independent format check, WoWDatabaseEditor's `FastWdc1Reader` at revision `0f96a022de476e020d2b8e58eeed9ad0f66696dc` parsed all 9 canonical mutation outputs. The probe compiled the reference parser sources directly because that checkout's optional `DBCD` project was absent; no Crucible parser code was linked into the independent reader.

The two lanes share 355 logical table names. Zero have the same physical layout. MoP-to-Legion rejected all 451 inputs as `IncompatibleTarget`; Legion-to-MoP rejected all 611. No cross-target table was staged. This is the intended stress result: filename overlap cannot bypass profile/container/layout validation.

## Constructive hybrid run

The profile-defense result above was followed by a separate constructive run which translates semantics instead of passing raw files across profiles. `Mists of the Pandaren Legion v5` used a fresh clone of the proved MoP client/server pair as the runnable host and the proved Legion table/CASC pair as the donor. The ignored machine-local request is `.local\mashup-lab\mists-full-v5.request.json`; the complete run is `.local\mashup-lab\runs\mists-of-the-pandaren-legion-v5-20260826-135747-f3bebc7b551b46e0a4591e46183e78df`.

| Hybrid result | Count |
|---|---:|
| Shared domains considered | 357 |
| Host-format domains converted | 311 |
| Donor rows added | 1,363,472 |
| Semantically equal donor rows reused | 1,349,041 |
| Fixed-identity host rows retained | 39 |
| Colliding identities remapped | 294,931 |
| DBD-declared references rewritten | 521,743 |
| Unresolved references retained in ledger | 112,437 |
| Donor-only tables | 253 |
| Shared tables with no convertible rows | 46 |
| Explicit projections into a host table | 2 |

The asset bridge resolved 335 of 335 requested FileDataID paths, reused 289 paths already available from the MoP client, and produced 110 patch assets: 25 native MoP `MD20` v272 files, 25 matching SKIN v3 files, and 60 BLP files. The projection preserves the MoP-era 492-byte particle record, native material/lookups, and SKIN shadow batches while resolving Legion FileData references to embedded client paths. `ItemVisuals` converted 180 of 200 donor rows, reused 18 equivalent rows, skipped 2 structurally blocked rows, and synthesized 124 verified `ItemVisualEffects` rows. The two blocked models were `spells\wind_chakram_missile_reverse.m2` (unsupported two-ribbon/two-stage shader structure) and `spells\felmag_empowered_aurafel.m2` (two SFID SKIN references where the proved profile supports exactly one). Sixteen requested Legion loading-screen assets were absent from the local CASC; their owning rows were skipped rather than published with missing files.

The installed MPQ contains 421 manifest entries: 311 `DBFilesClient` tables plus the 110 assets above. An independent extraction recovered all 422 physical members, including the MPQ listfile. SHA-256 comparison matched all 421 manifest sources to their extracted members, all 311 server payload tables to their installed copies, and all 311 recorded install preimages to the pristine MoP source tables. A separate binary audit verified all 25 M2 members as unchunked `MD20` v272 with the required MoP flag and no FileData/exporter flags, and validated every array range in all 25 SKIN v3 members including the shadow array. The installed and run MPQs both hash to `ED2719580B465C02D113BDE2C5DE201165FA1C5FE05C1A8E2899B47A1F8C3A68`.

Two earlier candidates exposed real converter bugs and were not accepted as the result. v3 produced Wrath v264 models instead of native MoP v272 models. After that target was corrected, v4's verifier mistook bounding-box floats for collision-array headers and rejected every otherwise supported model. The v5 regression suite locks both corrected contracts before the complete corpus build.

This is a literal data-and-asset hybrid installed into an isolated MoP worktree. It is not yet a gameplay pass: the existing Cata realm occupying the default auth/world ports was deliberately left untouched, so this run did not start the hybrid server or client. SkyFire code, SQL, scripts, maps, donor-only table semantics, and the 112,437 retained unresolved references remain the next runtime/content integration surface.

## CASC publication boundary

CASC list and local extraction are supported. Legion WDC1 edits and client/server payloads can be validated and staged, but publishing those payloads into a runnable CASC client is not yet supported.

WhiteoutLib was evaluated from `E:\wowps\Tools\WhiteoutLib` at commit `23114819e29d55090d86a6240f8590fed73b7e27`. A synthetic 20-file writer test passed. A real Legion byte-identical replacement also reopened correctly through WhiteoutLib, but the operation peaked near 43.5 GiB private memory and the resulting storage did not resolve the known external listfile through Crucible's independent CascLib reader. It therefore failed the interoperability and resource contract and was rejected rather than added as a production dependency.

## What this proves

- Exact table parsing, schema resolution, unchanged persistence, representative mutation persistence, source binding, and deployment classification for the audited MoP and Legion corpora.
- Isolated clones remained identical to their sources throughout the passing run.
- Raw cross-expansion tables are blocked at the target-profile boundary even when names collide; explicit semantic translation can publish a separately verified host-format hybrid.

It does not prove gameplay, client startup, server startup, map behavior, every future corpus, or CASC publication. The constructive hybrid proves its translated MPQ/server payload and installation bytes, not runtime behavior. Those require separate acceptance tests and must not be inferred from this report.
