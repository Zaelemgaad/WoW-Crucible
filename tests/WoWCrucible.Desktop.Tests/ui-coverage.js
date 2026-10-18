(async function () {
  const cases = new Map(), errors = [];
  window.coverageResult = null;
  const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
  const original = { alert: window.alert, confirm: window.confirm, prompt: window.prompt };
  let notices = [];
  window.alert = message => notices.push(String(message));
  window.confirm = message => { notices.push('Confirmation cancelled: ' + message); return false; };
  window.prompt = message => { notices.push('Prompt cancelled: ' + message); return null; };
  const onError = e => errors.push(e.message || String(e.reason));
  addEventListener('error', onError); addEventListener('unhandledrejection', onError);
  const selector = 'button,input,select,textarea,a[data-action],summary,[role=button],.icon-picker-trigger';
  function label(e) { return e.id || e.getAttribute('aria-label') || e.getAttribute('data-action') || e.title || e.textContent.trim().slice(0, 100) || e.placeholder || e.type || e.tagName; }
  const nodeKeys = new WeakMap();
  function indexNodes() {
    const ordinals = new Map();
    for (const e of document.querySelectorAll(selector)) {
      const scope = e.closest('#spellbook,#classModal,#editorPanel,#freeWorkspace,.pane')?.id || 'toolbar';
      const base = `${scope}/${e.tagName}/${label(e)}`;
      const ordinal = ordinals.get(base) || 0; ordinals.set(base, ordinal + 1);
      nodeKeys.set(e, base + '/' + ordinal);
    }
  }
  function key(e) {
    if (!nodeKeys.has(e)) indexNodes();
    return nodeKeys.get(e) || `${e.closest('.pane')?.id || 'canvas'}/${e.tagName}/${label(e)}`;
  }
  function entry(e, option = 'activate') {
    const id = key(e) + '/' + option;
    if (!cases.has(id)) cases.set(id, { Id: 'embedded/' + id, Scope: 'embedded', Control: label(e), Kind: e.tagName, Option: option, Status: 'untested', Evidence: 'Inventoried; scenario not reached.', Activations: 0 });
    return cases.get(id);
  }
  function inventory() {
    indexNodes();
    for (const e of document.querySelectorAll(selector)) {
      entry(e);
      if (e.tagName === 'SELECT') for (const o of e.options) entry(e, 'select ' + o.value + ': ' + o.textContent);
    }
  }
  async function act(e, action = () => e.click(), option = 'activate') {
    const row = entry(e, option);
    if (e.disabled) { row.Evidence = 'Disabled in this fixture state.'; return; }
    notices = [];
    const before = errors.length;
    try {
      await action(); await sleep(20);
      row.Status = errors.length > before ? 'failed' : 'activated'; row.Activations++;
      row.Evidence = 'DOM control event dispatched; native picker/confirm requests cancel in this test. ' + notices.join(' | ');
    } catch (error) { row.Status = 'failed'; row.Evidence = String(error); errors.push(String(error)); }
    inventory();
  }
  async function inputs(root = document) {
    for (const e of [...root.querySelectorAll('select,input,textarea')]) {
      if (!e.isConnected) continue;
      if (e.disabled || e.readOnly || e.type === 'file') { entry(e).Evidence = e.type === 'file' ? 'File-input selection needs an import fixture; not opened on the shared desktop.' : 'Read-only or disabled.'; continue; }
      if (e.tagName === 'SELECT') {
        const original = e.value;
        for (const o of [...e.options]) {
          if (!e.isConnected) break;
          await act(e, () => { e.value = o.value; e.dispatchEvent(new Event('change', { bubbles: true })); }, 'select ' + o.value + ': ' + o.textContent);
        }
        if (e.isConnected) { e.value = original; e.dispatchEvent(new Event('change', { bubbles: true })); }
        const count = [...cases.values()].filter(row => row.Id.startsWith('embedded/' + key(e) + '/select ') && row.Activations > 0).length;
        entry(e).Status = count ? 'activated' : 'untested'; entry(e).Activations = count; entry(e).Evidence = 'See individual option selections.';
      } else if (e.type === 'checkbox') {
        await act(e, () => { e.click(); e.click(); });
      } else {
        const original = e.value;
        await act(e, () => { e.value = e.type === 'number' || e.type === 'range' ? String(Math.min(Number(e.max || 255), Number(original || 0) + 1)) : original + ' '; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); });
        e.value = original; e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true }));
      }
    }
  }
  async function clickId(id) { const e = document.getElementById(id); if (e) await act(e); }
  try {
    await StudioBridge.ready;
    inventory();
    for (const name of [...document.querySelectorAll('#pane0 .class-btn')].map(label)) {
      const button = [...document.querySelectorAll('#pane0 .class-btn')].find(e => label(e) === name);
      if (button) {
        await act(button);
        for (const handle of document.querySelectorAll('#pane0 .board-move,#pane0 .board-resize'))
          await act(handle, () => { handle.dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowRight',bubbles:true})); handle.dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowLeft',bubbles:true})); });
      }
    }
    await clickId('splitViewBtn');
    for (const name of [...document.querySelectorAll('#pane1 .class-btn')].map(label)) {
      const button = [...document.querySelectorAll('#pane1 .class-btn')].find(e => label(e) === name);
      if (button) {
        await act(button);
        for (const handle of document.querySelectorAll('#pane1 .board-move,#pane1 .board-resize'))
          await act(handle, () => { handle.dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowRight',bubbles:true})); handle.dispatchEvent(new KeyboardEvent('keydown', {key:'ArrowLeft',bubbles:true})); });
      }
    }
    await inputs(document.getElementById('classModal')); await clickId('cmCancel');
    await clickId('editModeBtn');
    // Fractional zoom formerly rounded the stage past its viewport, causing
    // scrollbars to repeatedly appear/disappear inside ResizeObserver.
    const zoomInput = [...document.querySelectorAll('#pane0 .canvas-tools label')].find(e => e.textContent.includes('Zoom'))?.querySelector('input');
    if (zoomInput) for (const zoom of [25, 50, 100, 101, 125, 200, 100]) {
      await act(zoomInput, async () => {
        zoomInput.value = zoom; zoomInput.dispatchEvent(new Event('change', { bubbles: true }));
        await sleep(150);
        const viewport = document.querySelector('#pane0 .board-canvas');
        const first = [viewport.clientWidth, viewport.clientHeight]; await sleep(150);
        if (viewport.clientWidth !== first[0] || viewport.clientHeight !== first[1]) throw new Error('Canvas dimensions did not settle at zoom ' + zoom);
        const stage = viewport.querySelector('.board-stage').getBoundingClientRect();
        if (stage.width + 1 < viewport.clientWidth || stage.height + 1 < viewport.clientHeight) throw new Error('Canvas background does not cover viewport at zoom ' + zoom);
        for (const board of viewport.querySelectorAll('.canvas-board')) {
          const bounds = board.getBoundingClientRect();
          if (bounds.right > stage.right + 1 || bounds.bottom > stage.bottom + 1) throw new Error('Canvas does not contain its boards at zoom ' + zoom);
        }
      }, 'zoom ' + zoom);
    }
    await inputs();
    const empty = document.querySelector('#pane0 .talent-icon.empty-slot');
    if (empty) { await act(empty); await inputs(document.getElementById('editorPanel')); inventory(); await clickId('editorClose'); }
    await clickId('freeModeBtn');
    await clickId('addWorkspaceSpec'); await inputs(document.getElementById('freeWorkspace'));
    for (const id of ['showHelp', 'dismissHint', 'spellbookBtn']) await clickId(id);
    const server = document.querySelector('[aria-label="server folder"]');
    server.value = window.coverageServer; server.dispatchEvent(new Event('change', { bubbles: true }));
    for (const name of ['client', 'core']) {
      const field = document.querySelector(`[aria-label="${name} folder"]`); field.value = ''; field.dispatchEvent(new Event('change', { bubbles: true }));
    }
    const load = [...document.querySelectorAll('#spellbook button')].find(b => b.textContent === 'Load / refresh sources');
    await act(load);
    for (let i = 0; i < 200 && document.querySelector('.studio-status').textContent.startsWith('Reading'); i++) await sleep(25);
    if (!document.querySelector('.studio-status').textContent.startsWith('Loaded 1 tables.')) throw new Error('Spell fixture source did not load: ' + document.querySelector('.studio-status').textContent);
    await inputs(document.getElementById('spellbook'));
    const newRow = [...document.querySelectorAll('#spellbook button')].find(b => b.textContent === 'New row');
    await act(newRow); await inputs(document.querySelector('.studio-editor'));
    // Snapshot buttons by stable keys; re-find after re-render so detached nodes are never counted.
    inventory();
    const buttons = [...document.querySelectorAll('button,a[data-action],summary,.icon-picker-trigger')].map(e => ({ key: key(e), name: label(e) }));
    const blocked = new Set(['importBtn', 'restoreStudio', 'cmSave', 'cmDelete', 'Save talent', 'Apply exact preview']);
    for (const item of buttons) {
      const e = [...document.querySelectorAll('button,a[data-action],summary,.icon-picker-trigger')].find(e => key(e) === item.key);
      if (!e) continue;
      const region = e.closest('#spellbook,#freeWorkspace,#pane0,#pane1');
      if (region?.id === 'spellbook') await clickId('spellbookBtn');
      if (region?.id === 'freeWorkspace' && region.classList.contains('hidden')) await clickId('freeModeBtn');
      if (region?.id.startsWith('pane') && document.querySelector('#panesContainer').classList.contains('hidden')) await clickId('freeModeBtn');
      if (!e.isConnected || e.closest('.hidden')) continue;
      if (blocked.has(item.name)) { entry(e).Evidence = 'Needs a dedicated import or commit scenario; retained as untested.'; continue; }
      if (entry(e).Status === 'activated' || e.classList.contains('class-btn')) continue;
      await act(e);
    }
    inventory();
  } catch (error) { errors.push(String(error.stack || error)); }
  finally {
    Object.assign(window, original);
    removeEventListener('error', onError); removeEventListener('unhandledrejection', onError);
    window.coverageResult = { Cases: [...cases.values()], Errors: errors };
  }
})();
