(function () {
'use strict';

var BASE = window.TALENT_DATA;               // { classId: { classId, name, file, trees:[{n,t:[...]}] } }
var ICONS = window.ICON_MANIFEST || [];       // list of icon base names available under icons/
var CLASS_FILES = ['warrior','paladin','hunter','rogue','priest','deathknight','shaman','mage','warlock','druid'];

var COLS = 4;

var STATE = {
  level: 80,
  pointsPerTier: 5,
  tiers: 11,
  editMode: false,
  splitView: false,
  freeMode: false,
  transferMode: 'auto',
  workspace: null,
  panes: [null, null],
  data: {},            // classId -> working class data { classId, name, trees }
  customClasses: [],   // [{id, name, icon}]  icon = "class:<file>" or "spell:<iconname>"
};

var suppressNextClick = false;

// ---------------------------------------------------------------- storage --

function lsGet(key, fallback) {
  try {
    var raw = localStorage.getItem(key);
    return raw == null ? fallback : JSON.parse(raw);
  } catch (e) { return fallback; }
}
function lsSet(key, value) {
  try { localStorage.setItem(key, JSON.stringify(value)); window.StudioBridge.changed(); }
  catch (e) { alert('Could not save your changes in this browser: ' + e.message + '\nExport your work as JSON before closing.'); }
}
function lsDel(key) {
  try { localStorage.removeItem(key); window.StudioBridge.changed(); } catch (e) {}
}

function settingsKey() { return 'twc.settings'; }
function classOverrideKey(classId) { return 'twc.class.' + classId; }
function buildKey(classId) { return 'twc.build.' + classId; }
function customClassesKey() { return 'twc.customClasses'; }
function panesKey() { return 'twc.panes'; }

function loadSettings() {
  var s = lsGet(settingsKey(), null);
  if (s) {
    STATE.level = clamp(s.level || 80, 10, 255);
    STATE.pointsPerTier = clamp(s.pointsPerTier || 5, 1, 20);
    STATE.tiers = clamp(s.tiers || 11, 1, 30);
    STATE.editMode = !!s.editMode;
    STATE.freeMode = !!s.freeMode;
    STATE.transferMode = ['auto', 'move', 'copy'].indexOf(s.transferMode) !== -1 ? s.transferMode : 'auto';
  }
  STATE.customClasses = lsGet(customClassesKey(), []);
  var p = lsGet(panesKey(), null);
  if (p) {
    STATE.panes = [p.panes && p.panes[0] != null ? p.panes[0] : null, p.panes && p.panes[1] != null ? p.panes[1] : null];
    STATE.splitView = !!p.splitView;
  }
}
function saveSettings() {
  lsSet(settingsKey(), { level: STATE.level, pointsPerTier: STATE.pointsPerTier, tiers: STATE.tiers, editMode: STATE.editMode, freeMode: STATE.freeMode, transferMode: STATE.transferMode });
}
function savePanes() {
  lsSet(panesKey(), { panes: STATE.panes, splitView: STATE.splitView });
}
function saveCustomClasses() {
  lsSet(customClassesKey(), STATE.customClasses);
}

// ------------------------------------------------------------------ utils --

function clamp(n, lo, hi) { return Math.max(lo, Math.min(hi, n)); }
function ce(tag, cls) { var e = document.createElement(tag); if (cls) e.className = cls; return e; }
function qs(sel, root) { return (root || document).querySelector(sel); }
function escapeHtml(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
    return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
  });
}
function stripHtml(s) {
  if (!s) return '';
  return String(s).replace(/<br\s*\/?>/gi, '\n').replace(/<[^>]+>/g, '').trim();
}
function deepClone(o) { return JSON.parse(JSON.stringify(o)); }
function parseClassId(raw) { return /^-?\d+$/.test(raw) ? Number(raw) : raw; }
function iconRefToSrc(ref) {
  if (!ref) return 'icons/_placeholder.png';
  if (ref.indexOf('class:') === 0) return 'classicons/' + ref.slice(6) + '.jpg';
  if (ref.indexOf('spell:') === 0) return 'icons/' + ref.slice(6) + '.png';
  return 'icons/' + ref + '.png';
}

// ------------------------------------------------------------- class registry --

function classMeta(classId) {
  if (BASE[classId]) {
    var b = BASE[classId];
    return { classId: classId, name: b.name, iconSrc: 'classicons/' + b.file + '.jpg', isCustom: false };
  }
  var c = STATE.customClasses.filter(function (x) { return x.id === classId; })[0];
  if (c) return { classId: classId, name: c.name, iconSrc: iconRefToSrc(c.icon), isCustom: true };
  return null;
}

function allClassIds() {
  var baseIds = Object.keys(BASE).map(Number).sort(function (a, b) {
    return BASE[a].name.localeCompare(BASE[b].name);
  });
  var customIds = STATE.customClasses.map(function (c) { return c.id; });
  return baseIds.concat(customIds);
}

function blankTrees() {
  return normalizeTrees([{ n: 'Tree 1', t: [] }, { n: 'Tree 2', t: [] }, { n: 'Tree 3', t: [] }]);
}
function blankSideboard() {
  return normalizeTrees([{ n: 'Side Board', t: [] }])[0];
}

// ------------------------------------------------------------- data model --

function normalizeTrees(trees) {
  trees.forEach(function (tree) {
    tree.t.forEach(function (t) {
      if (Array.isArray(t.r)) {
        var reqTalent = tree.t[t.r[0]];
        t.r = reqTalent ? { reqId: reqTalent.i, reqRank: t.r[1] } : null;
      }
      if (t.k == null) t.k = 0;
    });
  });
  return trees;
}

function nextCustomId(classData) {
  var max = 0;
  classData.trees.forEach(function (tree) {
    tree.t.forEach(function (t) { if (t.i > max) max = t.i; });
  });
  classData.sideboard.t.forEach(function (t) { if (t.i > max) max = t.i; });
  return Math.max(max + 1, 900000);
}

function getBucket(cd, treeIdx) {
  return treeIdx === 'sideboard' ? cd.sideboard : cd.trees[treeIdx];
}

function getClassData(classId) {
  if (classId === 'workspace') return getWorkspace();
  if (STATE.data[classId]) return STATE.data[classId];

  var meta = classMeta(classId);
  if (!meta) return null;

  var override = lsGet(classOverrideKey(classId), null);
  var trees, sideboard;
  if (override) {
    trees = normalizeTrees(deepClone(override.trees));
    sideboard = normalizeTrees([deepClone(override.sideboard || { n: 'Side Board', t: [] })])[0];
  } else if (BASE[classId]) {
    trees = normalizeTrees(deepClone(BASE[classId].trees));
    sideboard = blankSideboard();
  } else {
    trees = blankTrees();
    sideboard = blankSideboard();
  }

  var built = { classId: classId, name: override && override.name || meta.name, trees: trees, sideboard: sideboard };

  var build = lsGet(buildKey(classId), null);
  if (build) {
    trees.forEach(function (tree) {
      tree.t.forEach(function (t) {
        if (build[t.i] != null) t.k = clamp(build[t.i], 0, t.m);
      });
    });
  }

  STATE.data[classId] = built;
  return built;
}

function stripK(t) {
  var c = {};
  for (var k in t) if (k !== 'k') c[k] = t[k];
  return c;
}

function saveClassOverride(classId) {
  if (classId === 'workspace') { saveWorkspace(); return; }
  var cd = STATE.data[classId];
  var toSave = {
    name: cd.name,
    trees: cd.trees.map(function (tree) { return Object.assign({}, tree, { t: tree.t.map(stripK) }); }),
    sideboard: { n: cd.sideboard.n, t: cd.sideboard.t.map(stripK), layout: cd.sideboard.layout },
  };
  lsSet(classOverrideKey(classId), toSave);
}

function saveBuild(classId) {
  if (classId === 'workspace') { saveWorkspace(); return; }
  var cd = STATE.data[classId];
  var build = {};
  cd.trees.forEach(function (tree) {
    tree.t.forEach(function (t) { if (t.k > 0) build[t.i] = t.k; });
  });
  lsSet(buildKey(classId), build);
}

// A design workspace owns independent boards; it is not a custom game class.
function getWorkspace() {
  if (!STATE.workspace) {
    var saved = lsGet('twc.workspace', { trees: [], sideboard: { n: 'Side Board', t: [] } });
    STATE.workspace = { classId: 'workspace', name: 'Free Workspace',
      trees: normalizeTrees(saved.trees), sideboard: normalizeTrees([saved.sideboard])[0] };
  }
  return STATE.workspace;
}

function workspacePayload() {
  var cd = getWorkspace();
  return { format: 'twc-workspace', version: 1, trees: deepClone(cd.trees), sideboard: deepClone(cd.sideboard) };
}

function saveWorkspace() { lsSet('twc.workspace', workspacePayload()); }

function cloneWorkspaceTree(cd, source, name) {
  var tree = source ? deepClone(source) : { n: name, t: [] };
  tree.n = name;
  tree.selected = false;
  delete tree.layout;
  var next = nextCustomId(cd), ids = {};
  tree.t.forEach(function (t) { ids[t.i] = next++; });
  tree.t.forEach(function (t) {
    t.i = ids[t.i];
    t.k = 0;
    if (t.r) t.r = ids[t.r.reqId] != null ? { reqId: ids[t.r.reqId], reqRank: t.r.reqRank } : null;
  });
  return tree;
}

function addWorkspaceTree(source, name) {
  var cd = getWorkspace();
  cd.trees.push(cloneWorkspaceTree(cd, source, name));
  saveWorkspace();
  renderAll();
}

function importWorkspace(payload) {
  if (payload.version !== 1 || !Array.isArray(payload.trees) || !payload.sideboard) {
    throw new Error('Unsupported workspace format');
  }
  payload.trees.concat([payload.sideboard]).forEach(function (tree, index) {
    if (typeof tree.n !== 'string' || !Array.isArray(tree.t)) throw new Error('Invalid board');
    var ids = new Set(), cells = new Set();
    tree.t.forEach(function (t) {
      var cell = t.x + ',' + t.y;
      if (!Number.isInteger(t.i) || ids.has(t.i) || cells.has(cell) || typeof t.n !== 'string' ||
          !Number.isInteger(t.x) || t.x < 0 || (index < payload.trees.length && t.x >= COLS) ||
          !Number.isInteger(t.y) || t.y < 0 || !Number.isInteger(t.m) || t.m < 1 || t.m > 10 ||
          !Array.isArray(t.d) || (t.s != null && !Array.isArray(t.s))) throw new Error('Invalid talent in ' + tree.n);
      ids.add(t.i); cells.add(cell);
    });
    tree.t.forEach(function (t) {
      if (t.r && !ids.has(t.r.reqId)) throw new Error('Missing prerequisite in ' + tree.n);
    });
  });
  if (!confirm('Replace the current free workspace with this file? Your class designs are kept.')) return;
  STATE.workspace = { classId: 'workspace', name: 'Free Workspace',
    trees: normalizeTrees(deepClone(payload.trees)), sideboard: normalizeTrees([deepClone(payload.sideboard)])[0] };
  STATE.freeMode = true;
  STATE.editMode = true;
  saveWorkspace(); saveSettings(); applyEditModeButton(); renderAll();
}

function renderWorkspace() {
  var root = qs('#freeWorkspace'), cd = getWorkspace();
  var previousSource = qs('#workspaceSource') ? qs('#workspaceSource').value : '';
  var previousDestination = qs('#workspaceExportClass') ? qs('#workspaceExportClass').value : null;
  root.innerHTML = '';
  var controls = ce('div', 'workspace-controls');
  var source = ce('select');
  source.id = 'workspaceSource';
  var blank = ce('option'); blank.value = ''; blank.textContent = 'Blank spec'; source.appendChild(blank);
  allClassIds().forEach(function (id) {
    getClassData(id).trees.forEach(function (tree, index) {
      var option = ce('option'); option.value = JSON.stringify([id, index]);
      option.textContent = classMeta(id).name + ' / ' + tree.n; source.appendChild(option);
    });
  });
  source.value = previousSource;
  controls.appendChild(source);
  var add = ce('button', 'btn'); add.id = 'addWorkspaceSpec'; add.textContent = 'Add Spec Board';
  add.addEventListener('click', function () {
    var ref = source.value ? JSON.parse(source.value) : null;
    var tree = ref ? getClassData(ref[0]).trees[ref[1]] : null;
    addWorkspaceTree(tree, tree ? tree.n + ' Copy' : 'Spec ' + (cd.trees.length + 1));
  });
  controls.appendChild(add);
  var exportBtn = ce('button', 'btn'); exportBtn.textContent = 'Export Workspace';
  exportBtn.addEventListener('click', function () {
    downloadText('spec-workspace.json', JSON.stringify(workspacePayload(), null, 2));
  });
  controls.appendChild(exportBtn);
  var destination = ce('select'); destination.id = 'workspaceExportClass';
  destination.title = 'Class identity for the exported spec selection';
  allClassIds().forEach(function (id) {
    var option = ce('option'); option.value = id; option.textContent = classMeta(id).name;
    destination.appendChild(option);
  });
  if (previousDestination != null) destination.value = previousDestination;
  controls.appendChild(destination);
  var selectedExport = ce('button', 'btn'); selectedExport.textContent = 'Export Selected Specs as Class';
  selectedExport.addEventListener('click', function () {
    var selected = cd.trees.filter(function (tree) { return tree.selected; });
    if (!selected.length) { alert('Select the boards to export using their header checkboxes.'); return; }
    var out = classExportPayload(parseClassId(destination.value));
    out.trees = selected.map(function (tree) { return { n: tree.n, t: tree.t.map(stripK) }; });
    out.sideboard = { n: cd.sideboard.n, t: cd.sideboard.t.map(stripK) };
    downloadText('selected-specs-talents.json', JSON.stringify(out, null, 2));
  });
  controls.appendChild(selectedExport);
  var count = ce('span'); count.textContent = cd.trees.length + ' independent spec boards'; controls.appendChild(count);
  root.appendChild(controls);
  mountBoardCanvas(root, cd, true);
}

function mountBoardCanvas(root, cd, workspace) {
  var items = cd.trees.map(function (tree, index) {
    var board = renderTree(cd, tree, index);
    board.classList.add('spec-board');
    return { element: board, model: tree, cols: COLS, rows: Math.max(STATE.tiers, tree.t.reduce(function (n, t) { return Math.max(n, t.y + 1); }, 0)) };
  });
  var sideboard = renderSideboard(cd);
  items.push({ element: sideboard, model: cd.sideboard, get cols() { return sideboardCols(cd); }, get rows() { return sideboardRows(cd); }, resized: sideboard.refreshSlots });
  BoardCanvas.mount(root, items, {
    id: root.id,
    workspace: workspace,
    load: function () { return lsGet('twc.canvas.' + cd.classId, {}); },
    save: function (settings) { lsSet('twc.canvas.' + cd.classId, settings); },
    changed: function () { saveClassOverride(cd.classId); }
  });
}

// ------------------------------------------------------------ class CRUD --

function createClass(name, iconRef, startFromClassId) {
  var id = 'custom' + Date.now().toString(36) + Math.floor(Math.random() * 1000);
  STATE.customClasses.push({ id: id, name: name, icon: iconRef });
  saveCustomClasses();

  var trees, sideboard;
  if (startFromClassId != null) {
    var src = getClassData(startFromClassId);
    trees = src.trees.map(function (tree) { return { n: tree.n, t: tree.t.map(stripK) }; });
    sideboard = { n: src.sideboard.n, t: src.sideboard.t.map(stripK) };
  } else {
    trees = [{ n: 'Tree 1', t: [] }, { n: 'Tree 2', t: [] }, { n: 'Tree 3', t: [] }];
    sideboard = { n: 'Side Board', t: [] };
  }
  lsSet(classOverrideKey(id), { trees: trees, sideboard: sideboard });
  delete STATE.data[id];
  return id;
}

function updateClassMeta(id, name, iconRef) {
  var c = STATE.customClasses.filter(function (x) { return x.id === id; })[0];
  if (c) { c.name = name; c.icon = iconRef; saveCustomClasses(); }
}

function deleteClass(id) {
  STATE.customClasses = STATE.customClasses.filter(function (c) { return c.id !== id; });
  saveCustomClasses();
  lsDel(classOverrideKey(id));
  lsDel(buildKey(id));
  delete STATE.data[id];
  STATE.panes = STATE.panes.map(function (cid) { return cid === id ? null : cid; });
  savePanes();
}

// ------------------------------------------------------------- point math --

function pointsToSpend() { return Math.max(0, STATE.level - 9); }

function treeSpent(tree) {
  var s = 0;
  tree.t.forEach(function (t) { s += t.k; });
  return s;
}

function classSpent(cd) {
  var s = 0;
  cd.trees.forEach(function (tree) { s += treeSpent(tree); });
  return s;
}

function findById(tree, id) {
  for (var i = 0; i < tree.t.length; i++) if (tree.t[i].i === id) return tree.t[i];
  return null;
}

function dependentsOf(tree, id) {
  return tree.t.filter(function (t) { return t.r && t.r.reqId === id; });
}

function tierFor(y) { return y * STATE.pointsPerTier; }

function canIncrement(cd, tree, t) {
  if (t.k >= t.m) return false;
  if (classSpent(cd) >= pointsToSpend()) return false;
  if (treeSpent(tree) < tierFor(t.y)) return false;
  if (t.r) {
    var req = findById(tree, t.r.reqId);
    if (!req || req.k < t.r.reqRank) return false;
  }
  return true;
}

function canDecrement(cd, tree, t) {
  if (t.k <= 0) return false;
  var newK = t.k - 1;

  var deps = dependentsOf(tree, t.i);
  for (var i = 0; i < deps.length; i++) {
    if (deps[i].k > 0 && newK < deps[i].r.reqRank) return false;
  }

  var newSum = treeSpent(tree) - 1;
  var maxRequired = 0;
  tree.t.forEach(function (other) {
    var k = (other === t) ? newK : other.k;
    if (k > 0) maxRequired = Math.max(maxRequired, tierFor(other.y));
  });
  if (newSum < maxRequired) return false;

  return true;
}

function enforcePointCap(cd) {
  var guard = 0;
  while (classSpent(cd) > pointsToSpend() && guard++ < 5000) {
    var best = null;
    cd.trees.forEach(function (tree) {
      tree.t.forEach(function (t) {
        if (t.k > 0 && canDecrement(cd, tree, t)) {
          if (!best || t.y > best.y) best = t;
        }
      });
    });
    if (!best) break;
    best.k--;
  }
}

// ----------------------------------------------------------------- render --

function renderAll() {
  qs('#panesContainer').classList.toggle('hidden', STATE.freeMode);
  qs('#freeWorkspace').classList.toggle('hidden', !STATE.freeMode);
  qs('#freeModeBtn').classList.toggle('on', STATE.freeMode);
  qs('#freeModeBtn').textContent = 'Free Mode: ' + (STATE.freeMode ? 'On' : 'Off');
  qs('#splitViewBtn').disabled = STATE.freeMode;
  if (STATE.freeMode) renderWorkspace();
  if (!STATE.freeMode) renderPane(0);
  qs('#pane1').classList.toggle('hidden', !STATE.splitView);
  if (STATE.splitView && !STATE.freeMode) renderPane(1);
  qs('#editHint').classList.toggle('hidden', !STATE.editMode || lsGet('twc.helpDismissed', false));
  hideTooltip();
}

function renderPane(paneIndex) {
  var classId = STATE.panes[paneIndex];
  var paneEl = qs('#pane' + paneIndex);
  paneEl.innerHTML = '';
  paneEl.appendChild(buildClassPicker(paneIndex));

  if (classId == null || !classMeta(classId)) {
    if (classId != null) { STATE.panes[paneIndex] = null; savePanes(); }
    var empty = ce('p', 'empty-state');
    empty.textContent = 'Pick a class above to begin. Left-click a talent to spend a point, right-click to remove one.';
    paneEl.appendChild(empty);
    return;
  }

  var cd = getClassData(classId);
  enforcePointCap(cd);

  paneEl.appendChild(buildPaneActions(paneIndex, classId));

  var summary = ce('div', 'summary-bar');
  summary.id = 'summary' + paneIndex;
  paneEl.appendChild(summary);

  mountBoardCanvas(paneEl, cd, false);

  updateSummary(paneIndex, cd);
}

function buildClassPicker(paneIndex) {
  var nav = ce('nav', 'class-picker');
  allClassIds().forEach(function (classId) {
    var meta = classMeta(classId);
    var wrap = ce('span', 'class-btn-wrap');
    var btn = ce('button', 'class-btn');
    if (STATE.panes[paneIndex] === classId) btn.classList.add('active');
    var img = ce('img');
    img.src = meta.iconSrc;
    img.alt = meta.name;
    btn.appendChild(img);
    btn.appendChild(document.createTextNode(meta.name));
    btn.addEventListener('click', function () { selectClassForPane(paneIndex, classId); });
    wrap.appendChild(btn);

    if (meta.isCustom) {
      var editBtn = ce('button', 'class-edit-btn');
      editBtn.textContent = '✎';
      editBtn.title = 'Rename / change icon / delete';
      editBtn.addEventListener('click', function (e) {
        e.stopPropagation();
        openClassModal({ mode: 'edit', classId: classId });
      });
      wrap.appendChild(editBtn);
    }
    nav.appendChild(wrap);
  });

  var addBtn = ce('button', 'class-btn class-btn-new');
  addBtn.textContent = '+ New Class';
  addBtn.addEventListener('click', function () {
    openClassModal({ mode: 'create', pane: paneIndex });
  });
  nav.appendChild(addBtn);

  return nav;
}

function selectClassForPane(paneIndex, classId) {
  STATE.panes[paneIndex] = classId;
  savePanes();
  renderAll();
}

function buildPaneActions(paneIndex, classId) {
  var meta = classMeta(classId);
  var row = ce('div', 'pane-actions');

  var label = ce('span', 'pane-class-name');
  label.textContent = meta.name;
  row.appendChild(label);

  var resetBtn = ce('button', 'btn');
  resetBtn.textContent = 'Reset Points';
  resetBtn.addEventListener('click', function () {
    var cd = getClassData(classId);
    cd.trees.forEach(function (tree) { tree.t.forEach(function (t) { t.k = 0; }); });
    saveBuild(classId);
    renderAll();
  });
  row.appendChild(resetBtn);

  var restoreBtn = ce('button', 'btn btn-danger');
  restoreBtn.textContent = meta.isCustom ? 'Clear Class Data' : 'Restore Original Data';
  restoreBtn.title = meta.isCustom
    ? 'Wipes this custom class back to three empty trees'
    : 'Discards your edits for this class and restores the original talent data';
  restoreBtn.addEventListener('click', function () {
    if (!confirm(meta.isCustom ? 'Clear all talents from "' + meta.name + '"?' : 'Discard edits and restore original data for ' + meta.name + '?')) return;
    lsDel(classOverrideKey(classId));
    lsDel(buildKey(classId));
    delete STATE.data[classId];
    renderAll();
  });
  row.appendChild(restoreBtn);

  var dupBtn = ce('button', 'btn');
  dupBtn.textContent = 'Duplicate Class…';
  dupBtn.addEventListener('click', function () {
    openClassModal({ mode: 'create', pane: paneIndex, prefillName: meta.name + ' Copy', prefillStartFrom: classId });
  });
  row.appendChild(dupBtn);

  if (meta.isCustom) {
    var delBtn = ce('button', 'btn btn-danger');
    delBtn.textContent = 'Delete Class';
    delBtn.addEventListener('click', function () {
      if (!confirm('Delete the custom class "' + meta.name + '" and all its talent data? This cannot be undone.')) return;
      deleteClass(classId);
      renderAll();
    });
    row.appendChild(delBtn);
  }

  var menuWrap = ce('span', 'menu-wrap');
  var exportBtn = ce('button', 'btn');
  exportBtn.textContent = 'Export ▾';
  var menu = ce('div', 'menu hidden');
  menu.innerHTML =
    '<a href="#" data-action="export-build">Build String (share link)</a>' +
    '<a href="#" data-action="export-json-class">This Class as JSON</a>' +
    '<a href="#" data-action="export-json-all">All Classes as JSON</a>' +
    '<a href="#" data-action="export-dbc-ref">DBC Reference Sheet (CSV)</a>';
  exportBtn.addEventListener('click', function (e) {
    e.stopPropagation();
    document.querySelectorAll('.menu').forEach(function (m) { if (m !== menu) m.classList.add('hidden'); });
    menu.classList.toggle('hidden');
  });
  menu.addEventListener('click', function (e) {
    e.preventDefault();
    var action = e.target.dataset.action;
    if (action === 'export-build') exportBuildString(classId);
    else if (action === 'export-json-class') exportClassJson(classId);
    else if (action === 'export-json-all') exportAllJson();
    else if (action === 'export-dbc-ref') exportDbcReference(classId);
    menu.classList.add('hidden');
  });
  menuWrap.appendChild(exportBtn);
  menuWrap.appendChild(menu);
  row.appendChild(menuWrap);

  return row;
}

function updateSummary(paneIndex, cd) {
  var spent = classSpent(cd);
  var left = pointsToSpend() - spent;
  var parts = cd.trees.map(function (tree) { return treeSpent(tree) + '/' + tree.n; });
  var el = qs('#summary' + paneIndex);
  if (!el) return;
  el.innerHTML =
    '<div class="spec-summary"><b>' + escapeHtml(cd.name) + '</b> &nbsp; ' + escapeHtml(parts.join(' - ')) + '</div>' +
    '<div class="points-left">Points left: <b>' + left + '</b></div>';
}

function renderTree(cd, tree, treeIdx) {
  var maxY = 0;
  tree.t.forEach(function (t) { if (t.y > maxY) maxY = t.y; });
  var tiers = Math.max(STATE.tiers, maxY + 1);

  var block = ce('div', 'tree-block');

  var header = ce('div', 'tree-header');
  if (cd.classId === 'workspace') {
    var selected = ce('input'); selected.type = 'checkbox'; selected.checked = !!tree.selected;
    selected.title = 'Include this spec in the class export';
    selected.addEventListener('change', function () { tree.selected = this.checked; saveWorkspace(); });
    header.appendChild(selected);
  }
  var nameEl = ce('span', 'tree-name');
  nameEl.textContent = tree.n;
  if (STATE.editMode) {
    nameEl.contentEditable = 'true';
    nameEl.addEventListener('blur', function () {
      tree.n = nameEl.textContent.trim() || tree.n;
      nameEl.textContent = tree.n;
      saveClassOverride(cd.classId);
      STATE.panes.forEach(function (classId, paneIndex) { if (classId === cd.classId) updateSummary(paneIndex, cd); });
    });
  }
  header.appendChild(nameEl);
  if (STATE.editMode) {
    var copyBtn = ce('button', 'tree-copy-btn');
    copyBtn.textContent = 'Copy From…';
    copyBtn.title = 'Replace this tree with a copy of another class\'s tree';
    copyBtn.addEventListener('click', function () { openTreeCopyModal(cd, tree, treeIdx); });
    header.appendChild(copyBtn);
    if (cd.classId === 'workspace') {
      var duplicateBtn = ce('button', 'tree-copy-btn');
      duplicateBtn.textContent = 'Duplicate';
      duplicateBtn.addEventListener('click', function () { addWorkspaceTree(tree, tree.n + ' Copy'); });
      header.appendChild(duplicateBtn);
      var removeBtn = ce('button', 'tree-copy-btn');
      removeBtn.textContent = 'Remove';
      removeBtn.addEventListener('click', function () {
        if (!confirm('Remove board "' + tree.n + '" and its talents?')) return;
        cd.trees.splice(treeIdx, 1);
        saveWorkspace();
        renderAll();
      });
      header.appendChild(removeBtn);
    }
  }
  var maxPoints = tree.t.reduce(function (a, t) { return a + t.m; }, 0);
  var pts = ce('span', 'tree-points');
  pts.textContent = treeSpent(tree) + '/' + maxPoints;
  header.appendChild(pts);
  block.appendChild(header);

  var wrap = ce('div', 'tree-grid-wrap');
  wrap.style.width = (COLS * 50) + 'px';
  wrap.style.height = (tiers * 50) + 'px';
  if (BASE[cd.classId]) {
    wrap.style.backgroundImage = "url('backgrounds/" + BASE[cd.classId].file + '_' + (treeIdx + 1) + ".jpg')";
  }

  var grid = ce('div', 'tree-grid');
  grid.style.width = (COLS * 50) + 'px';
  grid.style.height = (tiers * 50) + 'px';

  tree.t.forEach(function (t) {
    if (t.r) {
      var req = findById(tree, t.r.reqId);
      if (req) grid.appendChild(makeConnector(req, t));
    }
  });

  var occupied = {};
  tree.t.forEach(function (t) { occupied[t.x + ',' + t.y] = t; });

  if (STATE.editMode) {
    for (var y = 0; y < tiers; y++) {
      for (var x = 0; x < COLS; x++) {
        if (!occupied[x + ',' + y]) {
          grid.appendChild(makeEmptySlot(cd, tree, treeIdx, x, y));
        }
      }
    }
  }

  tree.t.forEach(function (t) {
    grid.appendChild(makeTalentCell(cd, tree, treeIdx, t));
  });

  wrap.appendChild(grid);
  block.appendChild(wrap);
  return block;
}

function sideboardCols(cd) {
  return Math.max(12, Math.min(64, Math.floor((((cd.sideboard.layout || {}).width || 0) - 19) / 50)),
    cd.sideboard.t.reduce(function (max, t) { return Math.max(max, t.x + 1); }, 0));
}

function sideboardRows(cd) {
  return Math.max(40, Math.min(64, Math.floor(((cd.sideboard.layout || {}).height || 0) / 50)),
    cd.sideboard.t.reduce(function (max, t) { return Math.max(max, t.y + 3); }, 0));
}

function renderSideboard(cd) {
  var bucket = cd.sideboard;
  var rows = sideboardRows(cd);
  var cols = sideboardCols(cd);

  var block = ce('div', 'tree-block sideboard-block');

  var header = ce('div', 'tree-header');
  var nameEl = ce('span', 'tree-name');
  nameEl.textContent = bucket.n || 'Side Board';
  if (STATE.editMode) {
    nameEl.contentEditable = 'true';
    nameEl.addEventListener('blur', function () {
      bucket.n = nameEl.textContent.trim() || bucket.n || 'Side Board';
      nameEl.textContent = bucket.n;
      saveClassOverride(cd.classId);
    });
  }
  header.appendChild(nameEl);
  var count = ce('span', 'tree-points');
  count.textContent = bucket.t.length + ' parked';
  header.appendChild(count);
  block.appendChild(header);

  var wrap = ce('div', 'tree-grid-wrap sideboard-grid-wrap');
  wrap.style.height = (rows * 50) + 'px';

  var grid = ce('div', 'tree-grid');
  grid.style.width = (cols * 50) + 'px';
  grid.style.height = (rows * 50) + 'px';

  var lastSize = '';
  block.refreshSlots = function () {
    cols = sideboardCols(cd); rows = sideboardRows(cd);
    var size = cols + ':' + rows;
    if (size === lastSize) return;
    lastSize = size;
    grid.replaceChildren();
    grid.style.width = (cols * 50) + 'px'; grid.style.height = (rows * 50) + 'px';
    var occupied = {};
    bucket.t.forEach(function (t) { occupied[t.x + ',' + t.y] = t; });
    if (STATE.editMode) {
      for (var y = 0; y < rows; y++) {
        for (var x = 0; x < cols; x++) {
          if (!occupied[x + ',' + y]) grid.appendChild(makeEmptySlot(cd, bucket, 'sideboard', x, y));
        }
      }
    }
    bucket.t.forEach(function (t) { grid.appendChild(makeTalentCell(cd, bucket, 'sideboard', t)); });
  };
  block.refreshSlots();

  wrap.appendChild(grid);
  block.appendChild(wrap);
  return block;
}

function makeConnector(req, t) {
  var frag = document.createDocumentFragment();
  var active = t.k > 0;
  var rx = req.x * 50 + 25, ry = req.y * 50 + 25;
  var tx = t.x * 50 + 25, ty = t.y * 50 + 25;
  var THICK = 3;

  function seg(x1, y1, x2, y2) {
    var d = ce('div', 'connector' + (active ? ' active' : ''));
    if (x1 === x2) {
      d.style.left = (x1 - THICK / 2) + 'px';
      d.style.top = Math.min(y1, y2) + 'px';
      d.style.width = THICK + 'px';
      d.style.height = Math.abs(y2 - y1) + 'px';
    } else {
      d.style.left = Math.min(x1, x2) + 'px';
      d.style.top = (y1 - THICK / 2) + 'px';
      d.style.width = Math.abs(x2 - x1) + 'px';
      d.style.height = THICK + 'px';
    }
    return d;
  }

  if (rx === tx || ry === ty) {
    frag.appendChild(seg(rx, ry, tx, ty));
  } else {
    frag.appendChild(seg(rx, ry, rx, ty));
    frag.appendChild(seg(rx, ty, tx, ty));
  }
  return frag;
}

function makeEmptySlot(cd, bucket, treeIdx, x, y) {
  var isSideboard = treeIdx === 'sideboard';
  var cell = ce('div', 'talent-cell');
  cell.style.left = (x * 50) + 'px';
  cell.style.top = (y * 50) + 'px';
  var icon = ce('div', 'talent-icon empty-slot');
  icon.textContent = '+';
  icon.style.display = 'flex';
  icon.style.alignItems = 'center';
  icon.style.justifyContent = 'center';
  icon.style.fontSize = '20px';
  icon.title = isSideboard ? 'Park a talent here, or add one directly' : 'Add a talent here, or drop a dragged talent here';
  icon.dataset.classId = String(cd.classId);
  icon.dataset.treeIdx = String(treeIdx);
  icon.dataset.x = String(x);
  icon.dataset.y = String(y);
  icon.addEventListener('click', function () {
    if (suppressNextClick) { suppressNextClick = false; return; }
    openTalentEditor(cd, bucket, treeIdx, null, x, y);
  });
  cell.appendChild(icon);
  return cell;
}

function makeTalentCell(cd, bucket, treeIdx, t) {
  var isSideboard = treeIdx === 'sideboard';
  var cell = ce('div', 'talent-cell');
  cell.style.left = (t.x * 50) + 'px';
  cell.style.top = (t.y * 50) + 'px';

  var cls = 'talent-icon';
  var locked = false;
  if (isSideboard) {
    cls += ' sideboard-item';
  } else {
    var enabled = canIncrement(cd, bucket, t) || t.k > 0;
    if (!enabled && t.k === 0) cls += ' locked';
    if (t.k > 0) cls += ' has-points';
    if (t.k === t.m && t.m > 0) cls += ' maxed';
    locked = t.k === 0 && !canIncrement(cd, bucket, t);
  }

  var icon = ce('div', cls);
  icon.dataset.classId = String(cd.classId);
  icon.dataset.treeIdx = String(treeIdx);
  icon.dataset.x = String(t.x);
  icon.dataset.y = String(t.y);
  icon.dataset.talentId = String(t.i);

  var img = ce('img');
  img.src = 'icons/' + t.iconname + (locked ? '_disabled.png' : '.png');
  img.onerror = function () {
    if (img.dataset.fallback !== '1') {
      img.dataset.fallback = '1';
      img.src = 'icons/' + t.iconname + '.png';
    } else {
      img.onerror = null;
      img.src = 'icons/_placeholder.png';
    }
  };
  img.alt = t.n;
  icon.appendChild(img);

  if (!isSideboard) {
    var badge = ce('span', 'rank-badge');
    badge.textContent = t.k + '/' + t.m;
    icon.appendChild(badge);
  }

  var editBadge = ce('span', 'edit-badge');
  editBadge.textContent = '✎';
  icon.appendChild(editBadge);

  icon.addEventListener('mouseenter', function (e) { if (!DRAGGING) showTooltip(e, cd, bucket, t, isSideboard); });
  icon.addEventListener('mousemove', function (e) { positionTooltip(e); });
  icon.addEventListener('mouseleave', hideTooltip);

  icon.addEventListener('mousedown', function (e) {
    startDrag(e, cd, bucket, t, icon);
  });

  icon.addEventListener('click', function () {
    if (suppressNextClick) { suppressNextClick = false; return; }
    if (STATE.editMode) {
      openTalentEditor(cd, bucket, treeIdx, t, t.x, t.y);
      return;
    }
    if (isSideboard) return;
    if (canIncrement(cd, bucket, t)) {
      t.k++;
      afterAllocationChange(cd);
    }
  });
  icon.addEventListener('contextmenu', function (e) {
    e.preventDefault();
    if (STATE.editMode || isSideboard) return;
    if (canDecrement(cd, bucket, t)) {
      t.k--;
      afterAllocationChange(cd);
    }
  });

  cell.appendChild(icon);
  return cell;
}

function afterAllocationChange(cd) {
  saveBuild(cd.classId);
  renderAll();
}

// ------------------------------------------------------------- drag & drop --

var DRAGGING = false;

function startDrag(e, cd, bucket, t, iconEl) {
  if (!STATE.editMode) return;
  if (e.button !== 0) return;
  e.preventDefault();

  var startX = e.clientX, startY = e.clientY;
  var moved = false;
  var ghost = null;

  function onMove(ev) {
    var dx = ev.clientX - startX, dy = ev.clientY - startY;
    if (!moved && Math.sqrt(dx * dx + dy * dy) > 6) {
      moved = true;
      DRAGGING = true;
      iconEl.classList.add('drag-source');
      hideTooltip();
      ghost = ce('div', 'drag-ghost');
      var img = ce('img');
      img.src = 'icons/' + t.iconname + '.png';
      ghost.appendChild(img);
      document.body.appendChild(ghost);
    }
    if (moved) {
      ghost.style.left = (ev.clientX - 18) + 'px';
      ghost.style.top = (ev.clientY - 18) + 'px';
      updateDropHighlight(ev.clientX, ev.clientY, t.i);
    }
  }

  function onUp(ev) {
    document.removeEventListener('mousemove', onMove);
    document.removeEventListener('mouseup', onUp);
    clearDropHighlight();
    iconEl.classList.remove('drag-source');
    DRAGGING = false;

    if (moved) {
      if (ghost) ghost.remove();
      suppressNextClick = true;
      // Only suppress the click synthesized by this mouse-up, not a later edit.
      setTimeout(function () { suppressNextClick = false; }, 0);
      var target = resolveDropTarget(ev.clientX, ev.clientY);
      if (target) performDrop(cd, bucket, t, target);
    }
  }

  document.addEventListener('mousemove', onMove);
  document.addEventListener('mouseup', onUp);
}

function parseTreeIdx(raw) { return raw === 'sideboard' ? 'sideboard' : Number(raw); }

function updateDropHighlight(clientX, clientY, draggedTalentId) {
  clearDropHighlight();
  var el = document.elementFromPoint(clientX, clientY);
  var iconEl = el && el.closest('.talent-icon');
  if (!iconEl || iconEl.dataset.classId == null) return;
  var occupiedId = iconEl.dataset.talentId;
  if (!occupiedId || Number(occupiedId) === draggedTalentId) {
    iconEl.classList.add('drop-hover'); // empty, or the talent's own current cell (harmless no-op)
  } else {
    iconEl.classList.add('drop-replace'); // occupied by a different talent — dropping will prompt to replace it
  }
}
function clearDropHighlight() {
  document.querySelectorAll('.drop-hover,.drop-replace').forEach(function (el) {
    el.classList.remove('drop-hover', 'drop-replace');
  });
}
function resolveDropTarget(clientX, clientY) {
  var el = document.elementFromPoint(clientX, clientY);
  var iconEl = el && el.closest('.talent-icon');
  if (!iconEl || iconEl.dataset.classId == null) return null;
  return {
    classId: parseClassId(iconEl.dataset.classId),
    treeIdx: parseTreeIdx(iconEl.dataset.treeIdx),
    x: Number(iconEl.dataset.x),
    y: Number(iconEl.dataset.y),
  };
}

function clearDanglingRefs(bucket, talentId) {
  bucket.t.forEach(function (o) { if (o.r && o.r.reqId === talentId) o.r = null; });
}

function containsTalentId(cd, id, except) {
  return cd.trees.concat([cd.sideboard]).some(function (bucket) {
    return bucket.t.some(function (t) { return t !== except && t.i === id; });
  });
}

// Explicit transfer mode takes precedence over the legacy within-class move rule.
function performDrop(sourceCd, sourceBucket, talent, target) {
  var targetCd = getClassData(target.classId);
  if (!targetCd) return;
  var targetBucket = getBucket(targetCd, target.treeIdx);
  if (!targetBucket) return;

  var occupant = targetBucket.t.filter(function (o) { return o.x === target.x && o.y === target.y; })[0];
  if (occupant) {
    if (occupant === talent) return; // dropped back on its own current cell, nothing to do
    if (!confirm('Replace "' + occupant.n + '" with "' + talent.n + '" in this cell?\n\n"' + occupant.n + '" will be removed from this tree.')) return;
    var occIdx = targetBucket.t.indexOf(occupant);
    if (occIdx !== -1) targetBucket.t.splice(occIdx, 1);
    clearDanglingRefs(targetBucket, occupant.i);
  }

  var move = STATE.transferMode === 'move' ||
    (STATE.transferMode === 'auto' && sourceCd.classId === targetCd.classId);
  if (move) {
    if (sourceBucket !== targetBucket) {
      var idx = sourceBucket.t.indexOf(talent);
      if (idx !== -1) sourceBucket.t.splice(idx, 1);
      clearDanglingRefs(sourceBucket, talent.i);
      if (containsTalentId(targetCd, talent.i, talent)) talent.i = nextCustomId(targetCd);
      targetBucket.t.push(talent);
      talent.r = null; // the prerequisite it pointed to doesn't exist in the new tree/side board
    }
    talent.x = target.x;
    talent.y = target.y;
  } else {
    var copy = stripK(talent);
    copy.k = 0;
    copy.d = talent.d.slice();
    copy.s = talent.s ? talent.s.slice() : [];
    copy.r = null;
    if (containsTalentId(targetCd, copy.i)) copy.i = nextCustomId(targetCd);
    copy.x = target.x;
    copy.y = target.y;
    targetBucket.t.push(copy);
  }

  saveClassOverride(sourceCd.classId);
  saveBuild(sourceCd.classId);
  if (targetCd.classId !== sourceCd.classId) saveClassOverride(targetCd.classId);
  if (targetCd.classId !== sourceCd.classId) saveBuild(targetCd.classId);
  renderAll();
}

// --------------------------------------------------------------- tooltip --

function showTooltip(evt, cd, bucket, t, isSideboard) {
  var tt = qs('#tooltip');
  var html = '<b class="title">' + escapeHtml(t.n) + '</b>';
  html += '<div class="rank-line">Rank ' + t.k + '/' + t.m + '</div>';

  if (!isSideboard) {
    if (t.r) {
      var req = findById(bucket, t.r.reqId);
      if (req && req.k < t.r.reqRank) {
        html += '<div class="req-line">Requires ' + t.r.reqRank + ' point' + (t.r.reqRank > 1 ? 's' : '') + ' in ' + escapeHtml(req.n) + '</div>';
      }
    }
    if (treeSpent(bucket) < tierFor(t.y)) {
      html += '<div class="req-line">Requires ' + tierFor(t.y) + ' points in ' + escapeHtml(bucket.n) + ' talents</div>';
    }
  }
  if (t.t && t.t[0]) {
    html += '<div class="meta">' + escapeHtml(stripHtml(t.t[0])).replace(/\n/g, '<br>') + '</div>';
  }

  var descIdx = Math.max(0, t.k - 1);
  var desc = t.d[descIdx] || '';
  html += '<div class="desc">' + escapeHtml(desc) + '</div>';

  if (isSideboard) {
    html += '<div class="hint">Parked in the side board' + (STATE.editMode ? ' — drag onto a tree to use it, or click to edit' : '') + '</div>';
  } else if (!STATE.editMode) {
    if (t.k === 0) html += '<div class="hint">Click to learn</div>';
    else if (t.k === t.m) html += '<div class="hint">Right-click to unlearn</div>';
    if (t.k > 0 && t.k < t.m) {
      html += '<span class="next-label">Next rank:</span><div class="desc">' + escapeHtml(t.d[t.k] || '') + '</div>';
    }
  } else {
    html += '<div class="hint">Click to edit, or drag to move/copy it</div>';
  }

  tt.innerHTML = html;
  tt.classList.remove('hidden');
  positionTooltip(evt);
}
function positionTooltip(evt) {
  var tt = qs('#tooltip');
  if (tt.classList.contains('hidden')) return;
  var x = evt.clientX + 16, y = evt.clientY + 16;
  var maxX = window.innerWidth - tt.offsetWidth - 10;
  var maxY = window.innerHeight - tt.offsetHeight - 10;
  tt.style.left = Math.min(x, Math.max(10, maxX)) + 'px';
  tt.style.top = Math.min(y, Math.max(10, maxY)) + 'px';
}
function hideTooltip() { qs('#tooltip').classList.add('hidden'); }

// ---------------------------------------------------------------- editor --

function openTalentEditor(cd, bucket, treeIdx, talent, x, y) {
  var isNew = !talent;
  var isSideboard = treeIdx === 'sideboard';
  if (isNew) {
    talent = { i: nextCustomId(cd), n: '', m: 1, d: [''], s: [], x: x, y: y, iconname: '_placeholder', k: 0 };
  }

  qs('#editorTitle').textContent = isNew ? (isSideboard ? 'Add Talent (Side Board)' : 'Add Talent') : 'Edit Talent';
  qs('#editorBody').innerHTML = editorBodyHtml(cd, bucket, talent, isNew, isSideboard);
  wireEditorEvents(cd, bucket, talent, isNew, isSideboard);
  qs('#editorPanel').classList.remove('hidden');
}

function editorBodyHtml(cd, bucket, t, isNew, isSideboard) {
  var maxX = isSideboard ? sideboardCols(cd) - 1 : COLS - 1;

  var h = '';
  h += '<div class="ef-row">';
  h += '  <div class="ef-col" style="flex:0 0 90px">';
  h += '    <label>Icon</label>';
  h += '    <div class="icon-picker-trigger" id="iconTrigger"><img id="iconPreview" src="icons/' + t.iconname + '.png"><span>Change</span></div>';
  h += '    <div class="icon-picker-grid hidden" id="iconGrid"></div>';
  h += '  </div>';
  h += '  <div class="ef-col">';
  h += '    <label>Name</label><input type="text" id="efName" value="' + escapeHtml(t.n) + '">';
  h += '    <div class="ef-row" style="margin-top:8px">';
  h += '      <div class="ef-col"><label>Grid X (0-' + maxX + ')</label><input type="number" id="efX" min="0" max="' + maxX + '" value="' + t.x + '"></div>';
  h += '      <div class="ef-col"><label>Grid Y (tier row)</label><input type="number" id="efY" min="0"' + (isSideboard ? '' : ' max="29"') + ' value="' + t.y + '"></div>';
  h += '      <div class="ef-col"><label>Max Rank</label><input type="number" id="efMaxRank" min="1" max="10" value="' + t.m + '"></div>';
  h += '    </div>';
  h += '  </div>';
  h += '</div>';

  if (!isSideboard) {
    var otherTalents = bucket.t.filter(function (o) { return o.i !== t.i; });
    var reqOptions = '<option value="">— none —</option>' + otherTalents.map(function (o) {
      var sel = (t.r && t.r.reqId === o.i) ? ' selected' : '';
      return '<option value="' + o.i + '"' + sel + '>' + escapeHtml(o.n) + '</option>';
    }).join('');

    h += '<div class="ef-row">';
    h += '  <div class="ef-col"><label>Prerequisite talent</label><select id="efReqId">' + reqOptions + '</select></div>';
    h += '  <div class="ef-col"><label>Required rank</label><input type="number" id="efReqRank" min="1" max="10" value="' + (t.r ? t.r.reqRank : 1) + '"></div>';
    h += '</div>';
  } else {
    h += '<div class="form-help">Side board talents have no prerequisite or tier — they\'re just parked here. Drag onto a tree to make them real again.</div>';
  }

  h += '<div class="rank-editor" id="rankEditor"></div>';

  h += '<div class="editor-actions">';
  h += '  <button class="btn" id="efSave">Save</button>';
  if (!isNew) h += '  <button class="btn btn-danger" id="efDelete">Delete Talent</button>';
  h += '  <button class="btn" id="efCancel">Cancel</button>';
  h += '</div>';
  return h;
}

function rankEditorHtml(t) {
  var m = t.m;
  var rows = '';
  for (var r = 0; r < m; r++) {
    rows += '<div class="rank-row" data-rank="' + r + '">';
    rows += '  <div class="rank-num">R' + (r + 1) + '</div>';
    rows += '  <textarea class="efRankDesc" placeholder="Description for rank ' + (r + 1) + '">' + escapeHtml(t.d[r] || '') + '</textarea>';
    rows += '  <input class="efRankSpell" type="text" placeholder="Spell ID" value="' + escapeHtml((t.s && t.s[r]) != null ? t.s[r] : '') + '">';
    rows += '</div>';
  }
  return rows;
}

function wireEditorEvents(cd, bucket, t, isNew, isSideboard) {
  var maxX = isSideboard ? sideboardCols(cd) - 1 : COLS - 1;
  qs('#rankEditor').innerHTML = rankEditorHtml(t);

  qs('#efMaxRank').addEventListener('input', function () {
    var m = clamp(parseInt(this.value, 10) || 1, 1, 10);
    var descs = Array.prototype.map.call(document.querySelectorAll('.efRankDesc'), function (el) { return el.value; });
    var spells = Array.prototype.map.call(document.querySelectorAll('.efRankSpell'), function (el) { return el.value; });
    qs('#rankEditor').innerHTML = rankEditorHtml({ m: m, d: descs, s: spells });
  });

  var iconTrigger = qs('#iconTrigger');
  var iconGrid = qs('#iconGrid');
  var iconGridBuilt = false;
  iconTrigger.addEventListener('click', function () {
    if (!iconGridBuilt) {
      iconGrid.innerHTML = ICONS.map(function (name) {
        return '<img src="icons/' + name + '.png" data-name="' + name + '" title="' + name + '">';
      }).join('');
      iconGrid.addEventListener('click', function (e) {
        if (e.target.tagName === 'IMG') {
          var name = e.target.dataset.name;
          qs('#iconPreview').src = 'icons/' + name + '.png';
          iconGrid.dataset.selected = name;
          iconGrid.classList.add('hidden');
        }
      });
      iconGridBuilt = true;
    }
    iconGrid.classList.toggle('hidden');
  });

  qs('#efCancel').addEventListener('click', closeTalentEditor);

  var delBtn = qs('#efDelete');
  if (delBtn) {
    delBtn.addEventListener('click', function () {
      if (!confirm('Delete "' + t.n + '"? This cannot be undone (unless you restore original data).')) return;
      var idx = bucket.t.indexOf(t);
      if (idx !== -1) bucket.t.splice(idx, 1);
      clearDanglingRefs(bucket, t.i);
      saveClassOverride(cd.classId);
      closeTalentEditor();
      renderAll();
    });
  }

  qs('#efSave').addEventListener('click', function () {
    var name = qs('#efName').value.trim();
    if (!name) { alert('Name is required.'); return; }
    var x = clamp(parseInt(qs('#efX').value, 10) || 0, 0, maxX);
    var y = clamp(parseInt(qs('#efY').value, 10) || 0, 0, isSideboard ? Number.MAX_SAFE_INTEGER : 29);

    var occupant = bucket.t.filter(function (o) { return o !== t && o.x === x && o.y === y; })[0];
    if (occupant) { alert('That grid cell is already used by "' + occupant.n + '".'); return; }

    var m = clamp(parseInt(qs('#efMaxRank').value, 10) || 1, 1, 10);
    var descs = Array.prototype.map.call(document.querySelectorAll('.efRankDesc'), function (el) { return el.value; });
    var spells = Array.prototype.map.call(document.querySelectorAll('.efRankSpell'), function (el) {
      var v = el.value.trim();
      return v === '' ? null : (isNaN(Number(v)) ? v : Number(v));
    });

    t.n = name;
    t.x = x; t.y = y;
    t.m = m;
    t.d = descs.slice(0, m);
    t.s = spells.slice(0, m);
    t.iconname = iconGrid.dataset.selected || t.iconname;
    t.k = Math.min(t.k || 0, m);

    var reqSel = qs('#efReqId');
    t.r = (!reqSel || reqSel.value === '') ? null : { reqId: Number(reqSel.value), reqRank: clamp(parseInt(qs('#efReqRank').value, 10) || 1, 1, 10) };

    if (isNew) bucket.t.push(t);

    saveClassOverride(cd.classId);
    saveBuild(cd.classId);
    closeTalentEditor();
    renderAll();
  });
}

function closeTalentEditor() {
  qs('#editorPanel').classList.add('hidden');
}

// ------------------------------------------------------------ class modal --

var classModalState = null;

function openClassModal(opts) {
  classModalState = opts;
  var isEdit = opts.mode === 'edit';
  var meta = isEdit ? classMeta(opts.classId) : null;

  qs('#classModalTitle').textContent = isEdit ? 'Edit Class' : 'New Class';

  var startOptions = '<option value="">— Blank —</option>' + allClassIds().map(function (id) {
    var m = classMeta(id);
    var sel = opts.prefillStartFrom === id ? ' selected' : '';
    return '<option value="' + id + '"' + sel + '>' + escapeHtml(m.name) + '</option>';
  }).join('');

  var initialIconRef = isEdit ? (STATE.customClasses.filter(function (c) { return c.id === opts.classId; })[0] || {}).icon : 'spell:_placeholder';

  var h = '';
  h += '<div class="ef-row">';
  h += '  <div class="ef-col" style="flex:0 0 90px">';
  h += '    <label>Icon</label>';
  h += '    <div class="icon-picker-trigger" id="classIconTrigger"><img id="classIconPreview" src="' + iconRefToSrc(initialIconRef) + '"><span>Change</span></div>';
  h += '    <div class="icon-picker-grid hidden" id="classIconGrid"></div>';
  h += '  </div>';
  h += '  <div class="ef-col">';
  h += '    <label>Class Name</label><input type="text" id="cmName" value="' + escapeHtml(isEdit ? meta.name : (opts.prefillName || '')) + '">';
  if (!isEdit) {
    h += '    <div style="margin-top:8px"><label>Start from</label><select id="cmStartFrom">' + startOptions + '</select>';
    h += '    <div class="form-help">Copies all 3 trees from that class as a starting point — the source class is left untouched.</div></div>';
  }
  h += '  </div>';
  h += '</div>';

  h += '<div class="editor-actions">';
  h += '  <button class="btn" id="cmSave">Save</button>';
  if (isEdit) h += '  <button class="btn btn-danger" id="cmDelete">Delete Class</button>';
  h += '  <button class="btn" id="cmCancel">Cancel</button>';
  h += '</div>';

  qs('#classModalBody').innerHTML = h;
  wireClassModalEvents(opts, initialIconRef);
  qs('#classModal').classList.remove('hidden');
}

function wireClassModalEvents(opts, initialIconRef) {
  var isEdit = opts.mode === 'edit';
  var iconTrigger = qs('#classIconTrigger');
  var iconGrid = qs('#classIconGrid');
  iconGrid.dataset.selected = initialIconRef || 'spell:_placeholder';
  var built = false;

  iconTrigger.addEventListener('click', function () {
    if (!built) {
      var classIcons = CLASS_FILES.map(function (file) {
        return '<img src="classicons/' + file + '.jpg" data-ref="class:' + file + '" title="' + file + '">';
      }).join('');
      var spellIcons = ICONS.map(function (name) {
        return '<img src="icons/' + name + '.png" data-ref="spell:' + name + '" title="' + name + '">';
      }).join('');
      iconGrid.innerHTML = classIcons + spellIcons;
      iconGrid.addEventListener('click', function (e) {
        if (e.target.tagName === 'IMG') {
          var ref = e.target.dataset.ref;
          qs('#classIconPreview').src = iconRefToSrc(ref);
          iconGrid.dataset.selected = ref;
          iconGrid.classList.add('hidden');
        }
      });
      built = true;
    }
    iconGrid.classList.toggle('hidden');
  });

  qs('#cmCancel').addEventListener('click', closeClassModal);

  var delBtn = qs('#cmDelete');
  if (delBtn) {
    delBtn.addEventListener('click', function () {
      var meta = classMeta(opts.classId);
      if (!confirm('Delete the custom class "' + meta.name + '" and all its talent data? This cannot be undone.')) return;
      deleteClass(opts.classId);
      closeClassModal();
      renderAll();
    });
  }

  qs('#cmSave').addEventListener('click', function () {
    var name = qs('#cmName').value.trim();
    if (!name) { alert('Name is required.'); return; }
    var iconRef = iconGrid.dataset.selected;

    if (isEdit) {
      updateClassMeta(opts.classId, name, iconRef);
    } else {
      var startFromRaw = qs('#cmStartFrom').value;
      var startFrom = startFromRaw === '' ? null : parseClassId(startFromRaw);
      var id = createClass(name, iconRef, startFrom);
      STATE.panes[opts.pane] = id;
      savePanes();
    }
    closeClassModal();
    renderAll();
  });
}

function closeClassModal() {
  qs('#classModal').classList.add('hidden');
  classModalState = null;
}

// ------------------------------------------------------------- tree copy --

function openTreeCopyModal(cd, tree, treeIdx) {
  var options = [];
  allClassIds().forEach(function (classId) {
    var meta = classMeta(classId);
    var srcCd = getClassData(classId);
    srcCd.trees.forEach(function (srcTree, srcIdx) {
      if (classId === cd.classId && srcIdx === treeIdx) return; // can't copy a tree into itself
      options.push({
        value: classId + '::' + srcIdx,
        label: meta.name + ' — ' + srcTree.n + ' (' + srcTree.t.length + ' talents)',
      });
    });
  });

  var optionsHtml = options.map(function (o) {
    return '<option value="' + escapeHtml(o.value) + '">' + escapeHtml(o.label) + '</option>';
  }).join('');

  var h = '';
  h += '<div class="ef-col">';
  h += '  <label>Copy which tree into "' + escapeHtml(tree.n) + '"?</label>';
  h += '  <select id="tcSource">' + optionsHtml + '</select>';
  h += '</div>';
  h += '<div class="ef-col" style="margin-top:10px">';
  h += '  <label style="display:flex;align-items:center;gap:6px;text-transform:none;font-size:12px">';
  h += '    <input type="checkbox" id="tcCopyName" checked style="width:auto"> Also rename this tree to match the source';
  h += '  </label>';
  h += '</div>';
  h += '<div class="form-help">This replaces every talent currently in "' + escapeHtml(tree.n) + '" (' + tree.t.length + ' of them) with a fresh copy of the chosen tree. The source tree is never modified.</div>';
  h += '<div class="editor-actions">';
  h += '  <button class="btn" id="tcCopy">Copy</button>';
  h += '  <button class="btn" id="tcCancel">Cancel</button>';
  h += '</div>';

  qs('#treeCopyModalTitle').textContent = 'Copy Into "' + tree.n + '"';
  qs('#treeCopyModalBody').innerHTML = h;

  qs('#tcCancel').addEventListener('click', closeTreeCopyModal);

  qs('#tcCopy').addEventListener('click', function () {
    var raw = qs('#tcSource').value;
    if (!raw) { closeTreeCopyModal(); return; }
    var sep = raw.lastIndexOf('::');
    var srcClassId = parseClassId(raw.slice(0, sep));
    var srcTreeIdx = Number(raw.slice(sep + 2));
    var srcCd = getClassData(srcClassId);
    var srcTree = srcCd.trees[srcTreeIdx];
    if (!srcTree) { closeTreeCopyModal(); return; }

    if (tree.t.length > 0 && !confirm('Replace ' + tree.t.length + ' talent(s) currently in "' + tree.n + '" with a copy of "' + srcTree.n + '"?')) {
      return;
    }

    tree.t = cd.classId === 'workspace' ? cloneWorkspaceTree(cd, srcTree, tree.n).t : deepClone(srcTree.t.map(stripK));
    normalizeTrees([tree]); // give the copied-in talents a fresh k=0 (stripK removes it entirely)
    if (qs('#tcCopyName').checked) tree.n = srcTree.n;

    saveClassOverride(cd.classId);
    closeTreeCopyModal();
    renderAll();
  });

  qs('#treeCopyModal').classList.remove('hidden');
}

function closeTreeCopyModal() {
  qs('#treeCopyModal').classList.add('hidden');
}

// ----------------------------------------------------------------- export --

function showTextModal(title, text, actions) {
  qs('#textModalTitle').textContent = title;
  qs('#textModalBody').value = text;
  var actionsEl = qs('#textModalActions');
  actionsEl.innerHTML = '';
  (actions || []).forEach(function (a) {
    var b = ce('button', 'btn');
    b.textContent = a.label;
    b.addEventListener('click', a.onClick);
    actionsEl.appendChild(b);
  });
  qs('#modalOverlay').classList.remove('hidden');
  qs('#textModal').classList.remove('hidden');
}
function closeTextModal() {
  qs('#modalOverlay').classList.add('hidden');
  qs('#textModal').classList.add('hidden');
}
function downloadText(filename, text) {
  return window.StudioBridge.download(filename, text, 'text/plain');
}

function exportBuildString(classId) {
  var cd = getClassData(classId);
  var points = {};
  cd.trees.forEach(function (tree) { tree.t.forEach(function (t) { if (t.k > 0) points[t.i] = t.k; }); });
  var payload = { classId: classId, level: STATE.level, points: points };
  var encoded = btoa(unescape(encodeURIComponent(JSON.stringify(payload))));
  var url = location.href.split('#')[0] + '#build=' + encoded;
  showTextModal('Shareable Build Link', url, [
    { label: 'Copy', onClick: function () { navigator.clipboard.writeText(url); } },
  ]);
}

function classExportPayload(classId) {
  var cd = getClassData(classId);
  var meta = classMeta(classId);
  return {
    classId: classId,
    name: cd.name,
    isCustom: meta.isCustom,
    icon: meta.isCustom ? (STATE.customClasses.filter(function (c) { return c.id === classId; })[0] || {}).icon : undefined,
    trees: cd.trees.map(function (tree) { return Object.assign({}, tree, { t: tree.t.map(stripK) }); }),
    sideboard: { n: cd.sideboard.n, t: cd.sideboard.t.map(stripK) },
  };
}

function exportClassJson(classId) {
  var out = classExportPayload(classId);
  var text = JSON.stringify(out, null, 2);
  showTextModal(out.name + ' — Talent JSON', text, [
    { label: 'Copy', onClick: function () { navigator.clipboard.writeText(text); } },
    { label: 'Download', onClick: function () { downloadText((out.name || 'class').replace(/\s+/g, '-').toLowerCase() + '-talents.json', text); } },
  ]);
}

function exportAllJson() {
  var all = {};
  allClassIds().forEach(function (classId) { all[classId] = classExportPayload(classId); });
  var text = JSON.stringify(all, null, 2);
  showTextModal('All Classes — Talent JSON', text, [
    { label: 'Copy', onClick: function () { navigator.clipboard.writeText(text); } },
    { label: 'Download', onClick: function () { downloadText('all-classes-talents.json', text); } },
  ]);
}

function exportDbcReference(classId) {
  var cd = getClassData(classId);
  var lines = [];
  lines.push('# DBC reference sheet — NOT a SQL import.');
  lines.push('# WotLK talent trees are defined by Talent.dbc / TalentTab.dbc (client + server),');
  lines.push('# not by a database table, so there is no SQL statement that "applies" this to a');
  lines.push('# TrinityCore/AzerothCore-style core. Use this sheet as your source of truth while');
  lines.push('# hand-editing the DBCs in a DBC editor (e.g. WDBX Editor / MyDBCEditor).');
  lines.push('TalentId,Tree,TierRow,ColumnX,Name,MaxRank,RequiredTalentId,RequiredRank,IconName,SpellIds,Descriptions');
  cd.trees.forEach(function (tree) {
    tree.t.forEach(function (t) {
      var spellIds = (t.s || []).join('|');
      var descs = (t.d || []).map(function (d) { return d.replace(/"/g, "'").replace(/\n/g, ' '); }).join(' || ');
      var row = [
        t.i, tree.n, t.y, t.x, t.n.replace(/,/g, ';'), t.m,
        t.r ? t.r.reqId : '', t.r ? t.r.reqRank : '',
        t.iconname, spellIds, '"' + descs + '"',
      ];
      lines.push(row.join(','));
    });
  });
  var text = lines.join('\n');
  showTextModal(cd.name + ' — DBC Reference Sheet', text, [
    { label: 'Copy', onClick: function () { navigator.clipboard.writeText(text); } },
    { label: 'Download', onClick: function () { downloadText(cd.name.replace(/\s+/g, '-').toLowerCase() + '-dbc-reference.csv', text); } },
  ]);
}

function importJsonFile(file) {
  var reader = new FileReader();
  reader.onload = function () {
    try {
      var parsed = JSON.parse(reader.result);
      if (parsed.format === 'twc-workspace') {
        importWorkspace(parsed);
        return;
      }
      var payloads = (parsed.trees && parsed.classId != null) ? [parsed] : Object.keys(parsed).map(function (k) { return parsed[k]; });

      var count = 0;
      payloads.forEach(function (p) {
        if (!p || !p.trees) return;
        var classId = p.classId != null ? p.classId : null;
        if (classId == null) return;
        classId = parseClassId(String(classId));

        if (!BASE[classId] && !classMeta(classId)) {
          STATE.customClasses.push({ id: classId, name: p.name || String(classId), icon: p.icon || 'spell:_placeholder' });
          saveCustomClasses();
        }
        lsSet(classOverrideKey(classId), { trees: p.trees, sideboard: p.sideboard || { n: 'Side Board', t: [] } });
        delete STATE.data[classId];
        count++;
      });

      alert('Imported talent data for ' + count + ' class(es).');
      renderAll();
    } catch (e) {
      alert('Could not parse that file as talent JSON: ' + e.message);
    }
  };
  reader.readAsText(file);
}

// ------------------------------------------------------------------- wire --

function init() {
  qs('#dismissHint').addEventListener('click', function () { lsSet('twc.helpDismissed', true); qs('#editHint').classList.add('hidden'); });
  qs('#showHelp').addEventListener('click', function () { lsSet('twc.helpDismissed', false); qs('#editHint').classList.remove('hidden'); });
  loadSettings();

  renderAll();

  qs('#levelInput').value = STATE.level;
  qs('#pptInput').value = STATE.pointsPerTier;
  qs('#tiersInput').value = STATE.tiers;
  applyEditModeButton();
  applySplitViewButton();
  qs('#transferMode').value = STATE.transferMode;
  qs('#transferMode').addEventListener('change', function () {
    STATE.transferMode = this.value;
    saveSettings();
  });
  qs('#freeModeBtn').addEventListener('click', function () {
    STATE.freeMode = !STATE.freeMode;
    if (STATE.freeMode) STATE.editMode = true;
    saveSettings();
    applyEditModeButton();
    renderAll();
  });

  qs('#levelInput').addEventListener('change', function () {
    STATE.level = clamp(parseInt(this.value, 10) || 80, 10, 255);
    this.value = STATE.level;
    saveSettings();
    renderAll();
  });
  qs('#pptInput').addEventListener('change', function () {
    STATE.pointsPerTier = clamp(parseInt(this.value, 10) || 5, 1, 20);
    this.value = STATE.pointsPerTier;
    saveSettings();
    renderAll();
  });
  qs('#tiersInput').addEventListener('change', function () {
    STATE.tiers = clamp(parseInt(this.value, 10) || 11, 1, 30);
    this.value = STATE.tiers;
    saveSettings();
    renderAll();
  });

  qs('#editModeBtn').addEventListener('click', function () {
    STATE.editMode = !STATE.editMode;
    saveSettings();
    applyEditModeButton();
    renderAll();
  });

  qs('#splitViewBtn').addEventListener('click', function () {
    STATE.splitView = !STATE.splitView;
    savePanes();
    applySplitViewButton();
    renderAll();
  });

  qs('#importBtn').addEventListener('click', function () { qs('#importFile').click(); });
  qs('#importFile').addEventListener('change', function () {
    if (this.files[0]) importJsonFile(this.files[0]);
    this.value = '';
  });

  document.addEventListener('click', function () {
    document.querySelectorAll('.menu').forEach(function (m) { m.classList.add('hidden'); });
  });

  qs('#editorClose').addEventListener('click', closeTalentEditor);
  qs('#editorPanel').addEventListener('click', function (e) { if (e.target === this) closeTalentEditor(); });

  qs('#classModalClose').addEventListener('click', closeClassModal);
  qs('#classModal').addEventListener('click', function (e) { if (e.target === this) closeClassModal(); });

  qs('#treeCopyModalClose').addEventListener('click', closeTreeCopyModal);
  qs('#treeCopyModal').addEventListener('click', function (e) { if (e.target === this) closeTreeCopyModal(); });

  qs('#textModalClose').addEventListener('click', closeTextModal);
  qs('#modalOverlay').addEventListener('click', closeTextModal);

  if (location.hash.indexOf('#build=') === 0) {
    try {
      var payload = JSON.parse(decodeURIComponent(escape(atob(location.hash.slice(7)))));
      STATE.level = clamp(payload.level || 80, 10, 255);
      qs('#levelInput').value = STATE.level;
      var classId = parseClassId(String(payload.classId));
      STATE.panes[0] = classId;
      var cd = getClassData(classId);
      cd.trees.forEach(function (tree) { tree.t.forEach(function (t) {
        if (payload.points[t.i] != null) t.k = clamp(payload.points[t.i], 0, t.m);
      }); });
      saveBuild(classId);
      savePanes();
      renderAll();
    } catch (e) { /* ignore malformed hash */ }
  }
}

function applyEditModeButton() {
  var btn = qs('#editModeBtn');
  btn.classList.toggle('on', STATE.editMode);
  btn.textContent = 'Edit Mode: ' + (STATE.editMode ? 'On' : 'Off');
}
function applySplitViewButton() {
  var btn = qs('#splitViewBtn');
  btn.classList.toggle('on', STATE.splitView);
  btn.textContent = 'Split View: ' + (STATE.splitView ? 'On' : 'Off');
}

window.TalentStudio = {
  setToolbarsVisible: function (visible) { document.body.classList.toggle('toolbars-hidden', !visible); },
  importClass: function (payload) {
    var classId = payload.classId;
    if (!BASE[classId] && !classMeta(classId)) { STATE.customClasses.push({id:classId,name:payload.name,icon:'spell:_placeholder'}); saveCustomClasses(); }
    if (BASE[classId]) BASE[classId].name = payload.name;
    lsSet(classOverrideKey(classId), {name:payload.name,trees:payload.trees,sideboard:payload.sideboard||{n:'Side Board',t:[]}});
    lsDel(buildKey(classId)); delete STATE.data[classId]; STATE.panes[0] = classId;
    STATE.freeMode = false; saveSettings(); savePanes(); renderAll();
  },
  refresh: function () { STATE.data = {}; loadSettings(); renderAll(); }
};
document.addEventListener('DOMContentLoaded', function () { window.StudioBridge.ready.then(init); });
})();
