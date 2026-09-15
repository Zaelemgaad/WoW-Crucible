# GUI Audit

Record the commit/build, test name, exact action, expected result, observed result,
and pass/fail/pending status. A passing core test is not a passing GUI test.
Retest changed workflows after every small batch of improvements.

## Storage Rules

- Keep authoritative clients, servers, and baseline backups as inputs.
- Reuse existing build outputs and worktrees. Do not clone clients per build.
- Before a storage-limited run, reclaim verified disposable generated files and
  budget all new outputs against those reclaimed bytes. Logical hard-link sizes
  are not physical disk usage.
- Cleanup only deletes exact manifest-owned disposable files. It does not adopt
  old, untracked folders or delete baseline backups because of their names.
- Keep one audit record; remove transient test scratch when the run finishes.

## Cleanup

Build the test runner once, then create a small fixture from the repository root:

```powershell
dotnet build tests/WoWCrucible.Core.Tests -c Release
dotnet tests/WoWCrucible.Core.Tests/bin/Release/net10.0/WoWCrucible.Core.Tests.dll --gui-cleanup-fixture .local/gui-audit/cleanup
```

The fixture command refuses to overwrite an existing directory. It writes six
three-byte markers plus project metadata, never a client copy.

1. Open **Projects & shared IDs**, then **Owned files & cleanup**. Choose the
   fixture folder in that tab's path field. **Apply exact preview** is disabled.
2. Click **Preview cleanup**. Expect three entries (scratch, cache, expired
   diagnostics), nine eligible file bytes, and the chosen project path.
3. Change the path. Expect the entries to clear and Apply to become disabled.
   Restore the fixture path, preview again, then click **Inspect ownership**.
   Expect six owned files and no active cleanup preview.
4. Preview again and click **Apply exact preview** once. Expect three files
   removed, nine file bytes, and no enabled Apply button. Protected deliverable,
   backup, and receipt markers must remain. Preview again: zero eligible files.
5. Choose a nonexistent project folder and preview. Expect an explicit error,
   no stale entries, and Apply disabled. No previous project may be cleaned.

The automated equivalent, including replaced folder links, changed content,
extended expiry, foreign manifests, and duplicate entries, is:

```powershell
dotnet tests/WoWCrucible.Core.Tests/bin/Release/net10.0/WoWCrucible.Core.Tests.dll --artifact-ownership
```

Tests clean up their own fixtures in `finally`/`Dispose`. The manual fixture is
retained for the GUI audit; it is not a versioned client backup.

## Client Storage

Open **CLIENT TABLES & PATCHES > Client workshop > Client storage**. Use **Plan...**
beside **Reviewed plan** to load an existing `client-hardlink-plan.json`.

Check that coverage, group counts, and file rows match that report, and that the
view remains responsive while scrolling. This is a read-only check: do not choose
**Apply reviewed links...** or **Restore independent files...**. An old report is
evidence of its scan, not proof that today's files still match it.
