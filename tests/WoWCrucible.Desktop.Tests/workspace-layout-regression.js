(async function () {
  const wait = () => new Promise(resolve => setTimeout(resolve, 60));
  const check = (value, message) => { if (!value) throw new Error(message); };
  await StudioBridge.ready;
  TalentStudio.importClass({classId:'layout-test',name:'Layout test',trees:[{n:'A long talent specialization name',t:[]}],sideboard:{n:'Side Board',t:[]}});
  if (document.querySelector('#editModeBtn').textContent.includes('Off')) document.querySelector('#editModeBtn').click();
  await wait();
  const board = () => document.querySelector('#pane0 .spec-board');
  const fit = () => [...board().querySelectorAll('button')].find(b => b.textContent === 'Fit');
  board().querySelector('.board-menu').click();
  check(board().querySelector('.board-actions').matches(':popover-open'), 'Board actions did not open');
  fit().click(); await wait();
  const dimensions = () => { const b=board(), body=b.querySelector('.tree-grid-wrap'); return [b.style.width,b.style.height,body.clientWidth,body.clientHeight]; };
  const original = JSON.stringify(dimensions());
  for (const id of ['levelInput','pptInput']) {
    const input=document.getElementById(id); input.value=Number(input.value)+1; input.dispatchEvent(new Event('change')); await wait();
    check(JSON.stringify(dimensions()) === original, id + ' changed fitted board dimensions');
  }
  for (let i=0;i<4;i++) {
    document.getElementById('editModeBtn').click(); await wait();
    check(JSON.stringify(dimensions()) === original, 'Edit mode changed fitted board or content size');
  }
  TalentStudio.refresh(); await wait();
  check(JSON.stringify(dimensions()) === original, 'Reloading saved layout changed fitted size');
  const content = board().querySelector('.tree-grid-wrap');
  check(content.scrollWidth <= content.clientWidth, 'Fit leaves horizontal overflow');
  const side = document.querySelector('#pane0 .sideboard-block');
  const slots = () => side.querySelectorAll('.talent-icon.empty-slot').length;
  check(slots() >= 480, 'Normal sideboard still has a small fixed capacity');
  const before = slots();
  for (let i=0;i<55;i++) side.querySelector('.board-resize').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));
  check(slots() > before, 'Widening sideboard did not add slots');
  const canvas = () => document.querySelector('#pane0 .board-canvas');
  const height = canvas().clientHeight;
  TalentStudio.setToolbarsVisible(false); await wait();
  check(canvas().clientHeight > height + 60, 'Hidden toolbars did not return usable canvas space');
  check(JSON.stringify(dimensions()) === original, 'Hiding toolbars resized fitted board');
  TalentStudio.setToolbarsVisible(true); await wait();
  check(canvas().clientHeight === height, 'Restoring toolbars did not restore layout');
  check(JSON.stringify(dimensions()) === original, 'Restoring toolbars resized fitted board');
  return {stableBoard:dimensions(),normalSideboardSlots:slots(),canvasHeight:height};
})()
