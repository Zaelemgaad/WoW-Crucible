# Version-274 static M2 batch audit

Date: 2026-07-19

Source corpus: `G:\extras\classic (1)`

Target: WoW 3.3.5a M2 version 264 and SKIN version 2

This audit validates Crucible's native clean-room batch path against real local assets. No source or converted game asset is committed to the repository.

## Initial strict plan without a listfile

- M2 files inspected: 112
- Eligible for the currently verified static armor/head profile: 68
- Explicitly blocked: 44
- Read/parse failures: 0
- Optional listfile passes per batch: one

The blocked records remain part of the batch receipt. They were not stripped, guessed, or silently omitted. The normal publication policy refused the incomplete corpus; the audit deliberately selected ready-only publication.

## Published payload

- Converted M2 files: 68
- Converted companion SKIN files: 68
- Output layout: `Payload\<source-relative-client-path>`
- Receipt: `batch-conversion-receipt.json`
- Publication: new/empty destination, sibling staging, atomic final move

Every pair was converted in isolated temporary storage, hashed, independently reloaded through Crucible's Wrath parser, and checked for preserved geometry/material counts before publication.

## Independent follow-up classification

The packaged batch planner was then run against the published `Payload` tree as a new input corpus:

- Files inspected: 68
- Already valid Wrath MD20/version-264: 68
- Still requiring conversion: 0
- Blocked: 0
- Failed: 0

This proves the batch operation produced structurally recognized target files and did not merely rename or copy the modern inputs. Animated, camera, emitter, unsupported material, unresolved FileDataID, and WMO families remain explicit future profiles.

## Expanded auto-listfile and LDV1 audit

The native profile was then extended with two independently guarded behaviors:

- Nearby listfile discovery requires complete resolution of the exact requested FileDataIDs. Two discovered local corpora produced the same requested mappings, so the newer candidate was selected and hash-bound automatically. Different complete mappings would have blocked selection.
- All 13 encountered `LDV1` chunks were exactly 16 bytes and shared one validated single-SKIN structure. Crucible accepts only that structure, records omission of its finite modern distance threshold as a loss, and preserves the sole SKIN geometry. Changed signature/reserved bytes remain blockers.

The expanded strict plan produced:

- M2 files inspected: 112
- Eligible: 88
- Explicitly blocked: 24
- Read/parse failures: 0
- Converted M2/SKIN pairs: 88

An independent plan over the expanded output classified 88/88 files as already valid Wrath MD20/version-264, with zero conversion-ready, blocked, or failed records. Before consolidating the earlier 68-model payload, all 136 overlapping M2/SKIN files were SHA-256 compared and proven byte-identical. Its prior receipt remains under `PreviousReceipts`; only the verified duplicate payload bytes were removed.

## WotLK flags, global clocks, and embedded sequences

The profile was then extended without relaxing its preservation rules:

- The WotLK-compatible model flag `0x10` is retained while only the verified modern flags are removed; unknown flag bits still block conversion.
- Global-sequence arrays are bounds-checked and byte-preserved, including zero-duration clocks.
- Multiple embedded 64-byte animation sequences and their signed lookup table are bounds- and reference-checked, byte-preserved, and accepted only when no conventional external `.anim` companion exists.
- Every sequence in every emitted output is parsed and sampled at time zero and its bounded midpoint before atomic publication. This traverses nested tracks and prevents an unverified embedded/external payload from hiding behind an unused animation.

The same 112-model corpus then produced:

- Eligible and converted M2/SKIN pairs: 94
- Explicitly blocked: 18
- Read/parse/conversion failures: 0
- Newly supported pairs: 6
- Byte comparison against the previous payload: all 176 overlapping M2/SKIN files identical, 0 changed

The stable `G:\Crucible-Converted-Assets\classic-1-static-m2` payload now contains 94 M2 and 94 SKIN files. The prior 68-model, 88-model, and temporary 94-model audit receipts remain under `PreviousReceipts`; temporary duplicate payloads were removed only after all 367 retained counterparts were SHA-256 identical.

## Particle stride, packed gravity, and EXP2 audit

The 15 particle-bearing helmet models contained 37 emitters. Crucible now validates every modern 492-byte record, its ten scalar animation tracks, enabled track, five normalized lifetime/cell curves, optional filenames/spline array, bone/texture references, blend/type fields, and the 16-byte post-Cataclysm tail. Every tail in this corpus is zero. Conversion appends a new contiguous 476-byte Wrath emitter table and proves every output record is byte-identical to the legacy prefix of its source record; referenced payload offsets therefore remain unchanged. The native `0x00800000` packed-gravity flag and four-byte vector keys are preserved rather than passed through MultiConverter's disabled, mathematically incorrect expansion routine.

Six particle models also carry EXP2. Three use identity Z/color/alpha values and empty alpha-cutoff curves, so that metadata is explicitly loss-accounted and omitted. One priest helmet uses two finite nonzero Z-source overrides with identity color/alpha multipliers and empty alpha-cutoff curves. Crucible now authors exact timestamp-zero, one-key native Wrath Z-source tracks for its single embedded animation sequence; the output independently reloads with `-1.1111112` and `-1.388889`. The other two models carry alpha-cutoff behavior and remain blocked. No multiplier or cutoff behavior is guessed or stripped.

The same 112-model corpus then produced:

- Eligible and converted M2/SKIN pairs: 107
- Explicitly blocked: 5
- Read/parse/conversion failures: 0
- Newly supported particle pairs: 12
- Particle emitters validated: 37 across 15 source models; 32 are represented across 13 outputs and the 5 emitters in 2 alpha-cutoff models remain blocked
- Byte comparison against the preceding particle payload: all 212 overlapping M2/SKIN files identical, 0 changed
- New output files: 2 (the newly translated M2 and its SKIN)

The independently published audit tree is `G:\Crucible-Converted-Assets\classic-1-static-m2-exp2-z-audit`. The five blockers are two full character models with multi-SKIN/AFID/BFID/camera/attachment/event requirements, one mixed packed/explicit-shader helmet, and two EXP2 alpha-cutoff helmets.
