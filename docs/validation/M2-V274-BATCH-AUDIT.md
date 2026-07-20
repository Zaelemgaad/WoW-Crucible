# Version-274 static M2 batch audit

Date: 2026-07-19

Source corpus: `G:\extras\classic (1)`

Target: WoW 3.3.5a M2 version 264 and SKIN version 2

This audit validates Crucible's native clean-room batch path against real local assets. No source or converted game asset is committed to the repository.

## Initial strict plan

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
