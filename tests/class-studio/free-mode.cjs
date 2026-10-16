const assert = require('node:assert/strict');
const { pathToFileURL } = require('node:url');
const path = require('node:path');
const fs = require('node:fs/promises');
const { chromium } = require(process.argv[2] || 'playwright');

async function drag(page, source, target) {
  await source.scrollIntoViewIfNeeded();
  const from = await source.boundingBox();
  await page.mouse.move(from.x + 18, from.y + 18);
  await page.mouse.down();
  await page.mouse.move(from.x + 30, from.y + 30, { steps: 3 });
  await target.scrollIntoViewIfNeeded();
  const to = await target.boundingBox();
  await page.mouse.move(to.x + 18, to.y + 18, { steps: 8 });
  await page.mouse.up();
}

(async () => {
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({ viewport: { width: 1700, height: 1100 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const workspace = () => page.evaluate(() => JSON.parse(localStorage.getItem('twc.workspace')));
    const board = i => page.locator('.workspace-boards > .spec-board').nth(i);
    const talent = i => board(i).locator('.talent-icon:not(.empty-slot)').first();
    const empty = i => board(i).locator('.empty-slot').first();
    await page.goto(pathToFileURL(path.resolve(__dirname, '../../src/WoWCrucible.ClassStudio/index.html')).href);
    await page.click('#freeModeBtn');
    await page.selectOption('#workspaceSource', JSON.stringify([2, 0]));
    await page.click('#addWorkspaceSpec');
    await board(0).getByRole('button', { name: 'Duplicate', exact: true }).click();
    let saved = await workspace();
    const ids = new Set(saved.trees[0].t.map(t => t.i));
    assert(saved.trees[1].t.every(t => !ids.has(t.i)), 'copies have separate IDs');
    for (const tree of saved.trees) {
      assert(tree.t.every(t => !t.r || tree.t.some(p => p.i === t.r.reqId)), 'prerequisites remapped');
    }
    await page.selectOption('#workspaceSource', '');
    for (let i = 2; i < 10; i++) await page.click('#addWorkspaceSpec');
    assert.equal(await page.locator('.workspace-boards > .spec-board').count(), 10);
    const before = (await workspace()).trees[0].t.length;
    await page.selectOption('#transferMode', 'move');
    await drag(page, talent(0), empty(1));
    saved = await workspace();
    assert.equal(saved.trees[0].t.length, before - 1, 'move removes source');
    assert.equal(saved.trees[1].t.length, before + 1, 'move adds destination');
    await page.selectOption('#transferMode', 'copy');
    await drag(page, talent(0), empty(1));
    saved = await workspace();
    assert.equal(saved.trees[0].t.length, before - 1, 'copy keeps source');
    assert.equal(saved.trees[1].t.length, before + 2);
    const allIds = saved.trees.flatMap(tree => tree.t.map(t => t.i));
    assert.equal(new Set(allIds).size, allIds.length, 'copies remain uniquely editable across boards');
    page.once('dialog', dialog => dialog.dismiss());
    await drag(page, talent(0), talent(1));
    assert.deepEqual(await workspace(), saved, 'cancelled replacement is atomic');
    await page.selectOption('#transferMode', 'move');
    const side = page.locator('#freeWorkspace .sideboard-block');
    assert.equal(await side.locator('.empty-slot').count(), 480);
    await drag(page, talent(0), side.locator('.empty-slot').first());
    await side.locator('.talent-icon:not(.empty-slot)').click();
    assert(await page.locator('#editorPanel').isVisible(), 'next deliberate click works after dragging');
    await page.fill('#efY', '50');
    await page.click('#efSave');
    saved = await workspace();
    assert.equal(saved.sideboard.t[0].y, 50);
    assert.equal(await side.locator('.empty-slot').count(), 12 * 53 - 1, 'sideboard grows beyond initial size');
    await page.reload();
    assert.deepEqual(await workspace(), saved, 'workspace survives reload');
    assert.equal(await page.locator('.workspace-boards > .spec-board').count(), 10);
    await board(0).locator('input[type=checkbox]').check();
    await board(1).locator('input[type=checkbox]').check();
    await page.selectOption('#workspaceExportClass', '2');
    const downloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Export Selected Specs as Class', exact: true }).click();
    const download = await downloadPromise;
    const exported = JSON.parse(await fs.readFile(await download.path(), 'utf8'));
    assert.equal(exported.classId, 2);
    assert.equal(exported.trees.length, 2);
    const workspaceDownloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Export Workspace', exact: true }).click();
    const workspaceDownload = await workspaceDownloadPromise;
    saved = await workspace();
    page.once('dialog', dialog => dialog.accept());
    await page.locator('#importFile').setInputFiles(await workspaceDownload.path());
    assert.deepEqual(await workspace(), saved, 'workspace export/import roundtrip');
    assert.equal(await page.evaluate(() => localStorage.getItem('twc.customClasses')), null, 'no fake classes created');
    await page.screenshot({ path: path.join(__dirname, '../../.local/class-studio-test/free-mode-verified.png') });

    // Explicit move must also work between different real classes.
    await page.click('#freeModeBtn');
    await page.locator('#pane0 .class-btn').filter({ hasText: /^Paladin$/ }).click();
    await page.click('#splitViewBtn');
    await page.locator('#pane1 .class-btn').filter({ hasText: /^Priest$/ }).click();
    const source = page.locator('#pane0 .talent-icon:not(.empty-slot)').first();
    const id = await source.getAttribute('data-talent-id');
    await drag(page, source, page.locator('#pane1 .empty-slot').first());
    assert.equal(await page.locator('#pane0 .talent-icon[data-talent-id="' + id + '"]').count(), 0);
    const classSource = await page.evaluate(() => JSON.parse(localStorage.getItem('twc.class.2')));
    assert(!classSource.trees.some(t => t.t.some(x => String(x.i) === id)));
    assert.deepEqual(errors, []);
    console.log('PASS: 10 independent boards, prerequisite remapping, move/copy, cancel, expanding sideboard, persistence, exports/import and cross-class move.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
