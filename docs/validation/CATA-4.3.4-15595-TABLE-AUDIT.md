# Cataclysm 4.3.4.15595 table audit

This evidence records Crucible's July 19, 2026 validation against the locally supplied Cataclysm 4.3.4 client. No Blizzard table payload is committed to the repository.

## Inputs

- Client root: `G:\clients\cata 4.3.4`
- Client build: `4.3.4.15595`
- Indexed executable SHA-256: `4C1766C40CBF7E2F26B7A0A75FFD95488DA56F1EE421C41D9B8C98CA6303978D`
- Exact WDBX schema: `Cata 4.3.4 (15595).xml`
- Optional cross-check corpus: local WoWDBDefs `definitions` directory
- Complete-table source for this slice: `Data\Cache\enGB\patch-enGB-15595.MPQ`
- Raw delta source: `Data\enGB\wow-update-enGB-15595.MPQ`

## Complete-table results

The locale cache yielded 25 top-level client tables: 20 `WDBC` and 5 fixed-layout `WDB2`. All 25 matched the exact build XML field count and record byte width. With proof mode enabled, all 25 unchanged saves reproduced the source file byte-for-byte and therefore had identical SHA-256 values.

The five WDB2 results were:

| Table | Fields | Record bytes | SHA-256 |
|---|---:|---:|---|
| `Item-sparse` | 133 | 532 | `BD21D2A012C4A983DC3179DE84D63ABD1CFCD1F7063D0785426D75636DD24C06` |
| `Item` | 8 | 32 | `3335BAD4BC3B1CC76B761ACECF6FA8AAD37999C319B42527806A16F4C9D4B063` |
| `ItemCurrencyCost` | 2 | 8 | `B87CE2656E6F6F7388A17E7C0E54207A60097AF4847C3A8D5C274744B2D51EE9` |
| `ItemExtendedCost` | 31 | 124 | `825D8AB23BBF4C50F39389F54520C35E5BD27DBA5AB241D2B18ACB77E65D8FDD` |
| `KeyChain` | 33 | 36 | `415EE6F6ABE27463111093D10817FD8DA474824CE4B5CBD83F75A9049D9C4EE7` |

`KeyChain` is an important width fixture: its 33 logical physical fields occupy 36 bytes because its ID is four bytes and the key material is 32 one-byte values. Counting fields alone would not have proved the layout.

The same 25/25 result was reproduced with the exact XML as the only selected schema provider, proving that a missing or differently located WoWDBDefs checkout no longer blocks an otherwise exact build layout.

## Delta-layer results

The corresponding update archive exposed 25 `PTCH` payloads. Crucible classified all 25 as update deltas and rejected them as standalone editable tables. None were sent through WDBC/WDB2 parsing or counted as a successful schema audit.

## Support boundary

This proves the real build-15595 locale-cache slice and the fixed-layout containers represented there. It does **not** prove every base client table, reconstruct the effective base-plus-`PTCH` chain, or authorize later WDB5/WDB6/WDC families. The Cataclysm target therefore remains **Experimental** until the complete effective base corpus is reconstructed and verified.

To repeat the exact-schema and unchanged-write proof after extracting the tables:

```powershell
wowcrucible dbc schema-audit - <table-folder> 15595 --xml="Cata 4.3.4 (15595).xml" --roundtrip --format=json
```
