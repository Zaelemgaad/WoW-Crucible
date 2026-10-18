(function () {
'use strict';
function element(tag, cls, text) {
  var el = document.createElement(tag);
  if (cls) el.className = cls;
  if (text != null) el.textContent = text;
  return el;
}
function mount(parent, items, options) {
  var settings = options.load() || {};
  settings.step = Math.max(1, Math.min(64, Number(settings.step) || 16));
  settings.zoom = Math.max(25, Math.min(200, Number(settings.zoom) || 100));
  var tools = element('div', 'canvas-tools');
  var viewport = element('div', 'board-canvas ' + (options.workspace ? 'workspace-layout' : 'trees-container'));
  viewport.canvasSettings = settings;
  var stage = element('div', 'board-stage' + (options.workspace ? ' workspace-boards' : ''));
  viewport.appendChild(stage);
  parent.appendChild(tools);
  parent.appendChild(viewport);
  var zoom = settings.zoom / 100;
  var z = items.reduce(function (max, item) { return Math.max(max, item.model.layout && Number(item.model.layout.z) || 0); }, 0);
  function remember() { options.save(settings); }
  function applyGrid() {
    stage.classList.toggle('canvas-grid', !!settings.grid);
    stage.style.setProperty('--canvas-grid-step', settings.step + 'px');
    stage.style.zoom = settings.zoom / 100;
    zoom = settings.zoom / 100;
    bounds();
  }
  function control(label, type, value, change, min, max) {
    var field = element('label', 'field', label + ' ');
    var input = element('input'); input.type = type;
    if (type === 'checkbox') input.checked = !!value; else input.value = value;
    if (min != null) { input.min = min; input.max = max; }
    input.addEventListener('change', function () {
      if (!input.checkValidity()) { input.reportValidity(); return; }
      change(type === 'checkbox' ? input.checked : Number(input.value));
      applyGrid(); remember();
    });
    field.appendChild(input); tools.appendChild(field);
  }
  control('Grid', 'checkbox', settings.grid, function (v) { settings.grid = v; });
  control('Snap', 'checkbox', settings.snap, function (v) { settings.snap = v; });
  control('Grid spacing', 'number', settings.step, function (v) { settings.step = v; }, 1, 64);
  control('Zoom %', 'number', settings.zoom, function (v) { settings.zoom = v; }, 25, 200);
  function bounds() {
    var width = 0, height = 0;
    items.forEach(function (item) {
      if (!item.model.layout) return;
      var p = item.model.layout;
      width = Math.max(width, p.x + p.width);
      height = Math.max(height, p.y + p.height);
    });
    // CSS resolves viewport fit, including scrollbars and fractional zoom.
    // Only board extents come from script; never measure and rewrite the viewport.
    stage.style.width = 'max(' + (100 / zoom) + '%, ' + width + 'px)';
    stage.style.height = 'max(' + (100 / zoom) + '%, ' + height + 'px)';
  }
  function snap(value) { return settings.snap ? Math.round(value / settings.step) * settings.step : Math.round(value); }
  function availablePosition(width, height) {
    var occupied = items.map(function (item) { return item.model.layout; }).filter(Boolean);
    var x = 16, y = 16;
    for (;;) {
      var collision = occupied.find(function (q) { return x < q.x + q.width + 16 && x + width + 16 > q.x && y < q.y + q.height + 16 && y + height + 16 > q.y; });
      if (!collision) return {x:x,y:y};
      x = collision.x + collision.width + 16;
      if (x + width > viewport.clientWidth / zoom) {
        x = 16;
        y = Math.min.apply(null, occupied.filter(function (q) { return q.y + q.height + 16 > y; }).map(function (q) { return q.y + q.height + 16; }));
      }
    }
  }
  items.forEach(function (item) {
    var board = item.element;
    board.classList.add('canvas-board');
    var header = board.querySelector('.tree-header');
    var body = board.querySelector('.tree-grid-wrap');
    body.style.width = ''; body.style.height = '';
    var width = Math.min(Math.max(240, item.cols * 50 + 20), Math.max(240, viewport.clientWidth - 32));
    var height = Math.min(item.rows * 50 + 90, Math.max(180, viewport.clientHeight - 32));
    if (!item.model.layout) item.model.layout = Object.assign(availablePosition(width, height), {width:width,height:height,z:++z});
    var p = item.model.layout;
    p.x = Math.max(0, Number(p.x) || 0); p.y = Math.max(0, Number(p.y) || 0);
    p.width = Math.max(140, Number(p.width) || width); p.height = Math.max(120, Number(p.height) || height);
    z = Math.max(z, Number(p.z) || 0);
    function place() {
      board.style.left = p.x + 'px'; board.style.top = p.y + 'px';
      board.style.width = p.width + 'px'; board.style.height = p.height + 'px';
      board.style.zIndex = p.z || 0;
      if (item.resized) item.resized();
    }
    var move = element('button', 'board-move', '⠿');
    move.type = 'button'; move.title = 'Drag to move board; arrow keys move it';
    move.setAttribute('aria-label', 'Move ' + item.model.n);
    header.prepend(move);
    var size = element('button', 'board-resize', '↘');
    size.type = 'button'; size.title = 'Drag to resize; arrow keys adjust size';
    size.setAttribute('aria-label', 'Resize ' + item.model.n);
    board.appendChild(size);
    var fit = element('button', 'tree-copy-btn', 'Fit');
    fit.title = 'Fit this window to its board and the viewport';
    fit.addEventListener('click', function () {
      p.width = item.cols * 50 + body.offsetWidth - body.clientWidth + 2;
      place();
      p.height = Math.max(120, Math.min(item.rows * 50 + header.offsetHeight + 2, viewport.clientHeight / zoom - 32));
      place(); bounds(); options.changed();
    });
    var actions = element('div', 'board-actions');
    actions.setAttribute('popover', 'auto');
    actions.id = 'board-actions-' + options.id + '-' + items.indexOf(item);
    header.querySelectorAll('button.tree-copy-btn').forEach(function (button) { actions.appendChild(button); });
    actions.appendChild(fit);
    var menu = element('button', 'board-menu', '⋯');
    menu.setAttribute('aria-label', 'Actions for ' + item.model.n);
    menu.setAttribute('popovertarget', actions.id);
    menu.addEventListener('click', function () {
      var rect = menu.getBoundingClientRect();
      actions.style.left = Math.max(0, Math.min(rect.right - 160, innerWidth - 170)) + 'px';
      actions.style.top = Math.min(rect.bottom, innerHeight - 150) + 'px';
    });
    actions.addEventListener('click', function (event) { if (event.target.closest('button') && actions.matches(':popover-open')) actions.hidePopover(); });
    header.appendChild(menu); board.appendChild(actions);
    function snapPosition(value, horizontal) {
      var extent = horizontal ? p.width : p.height;
      var offset = (horizontal ? viewport.scrollLeft : viewport.scrollTop) / zoom;
      var viewportSize = (horizontal ? viewport.clientWidth : viewport.clientHeight) / zoom;
      var others = [];
      items.forEach(function (other) {
        if (other === item || !other.model.layout) return;
        var q = other.model.layout;
        others.push({ start: horizontal ? q.x : q.y, size: horizontal ? q.width : q.height });
      });
      return window.WindowLayout.snapAxis(value, extent, offset, viewportSize, others, Object.assign({}, settings, {zoom:zoom}));
    }
    function wire(handle, resizing) {
      var start;
      handle.addEventListener('pointerdown', function (event) {
        if (event.button !== 0) return;
        event.preventDefault(); handle.focus();
        start = { x: event.clientX, y: event.clientY, left: p.x, top: p.y, width: p.width, height: p.height };
        p.z = ++z; place();
        handle.setPointerCapture(event.pointerId);
        board.classList.add('board-manipulating');
      });
      handle.addEventListener('pointermove', function (event) {
        if (!start) return;
        var dx = (event.clientX - start.x) / zoom, dy = (event.clientY - start.y) / zoom;
        if (resizing) { p.width = Math.max(140, snap(start.width + dx)); p.height = Math.max(120, snap(start.height + dy)); }
        else { p.x = snapPosition(start.left + dx, true); p.y = snapPosition(start.top + dy, false); }
        place(); bounds();
      });
      handle.addEventListener('lostpointercapture', function () {
        if (!start) return;
        start = null; board.classList.remove('board-manipulating'); options.changed();
      });
      handle.addEventListener('keydown', function (event) {
        var delta = {ArrowLeft:[-1,0],ArrowRight:[1,0],ArrowUp:[0,-1],ArrowDown:[0,1]}[event.key];
        if (!delta) return;
        event.preventDefault();
        var step = event.shiftKey ? 1 : (settings.snap ? settings.step : 10);
        if (resizing) { p.width = Math.max(140, p.width + delta[0] * step); p.height = Math.max(120, p.height + delta[1] * step); }
        else { p.x = Math.max(0, p.x + delta[0] * step); p.y = Math.max(0, p.y + delta[1] * step); }
        place(); bounds(); options.changed();
      });
    }
    wire(move, false); wire(size, true);
    place(); stage.appendChild(board);
    body.scrollLeft = p.scrollX || 0; body.scrollTop = p.scrollY || 0;
    var scrollTimer;
    body.addEventListener('scroll', function () {
      p.scrollX = body.scrollLeft; p.scrollY = body.scrollTop;
      clearTimeout(scrollTimer); scrollTimer = setTimeout(options.changed, 150);
    });
  });
  applyGrid();
}
window.BoardCanvas = {
  mount: mount
};
})();
