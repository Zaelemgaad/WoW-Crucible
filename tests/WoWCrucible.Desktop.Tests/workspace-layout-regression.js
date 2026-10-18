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
  const fitted=dimensions();
  const baseline={classId:99,name:'Source class',trees:[{n:'Source tree',t:[{i:900001,n:'Original talent',m:1,x:0,y:0,s:[1],d:['Original'],iconname:'_placeholder'}]}]};
  TalentStudio.useSource('test-realm-a',[baseline]);
  TalentStudio.importClass({...baseline,trees:[{n:'Edited tree',t:[]}]});await wait();
  for(let i=0;i<5;i++)board().querySelector('.board-move').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowRight',bubbles:true}));
  board().querySelector('.board-resize').dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',bubbles:true}));
  const geometry=()=>[...document.querySelectorAll('#pane0 .canvas-board')].map(b=>[b.style.left,b.style.top,b.style.width,b.style.height]);
  const savedGeometry=JSON.stringify(geometry());
  const confirmBefore=window.confirm;window.confirm=()=>true;
  try{[...document.querySelectorAll('#pane0 .pane-actions button')].find(b=>b.textContent==='Restore Original Data').click();}finally{window.confirm=confirmBefore;}
  await wait();
  check(JSON.stringify(geometry())===savedGeometry,'Restoring class data changed board or sideboard geometry');
  check(document.querySelector('#pane0 .tree-name').textContent==='Source tree','Restore did not restore selected source data');
  check(!localStorage.getItem('twc.source.test-realm-a.class.99'),'Restore retained class edits');
  TalentStudio.refresh();await wait();
  check(JSON.stringify(geometry())===savedGeometry,'Layout did not survive reloading after restore');
  TalentStudio.importClass({...baseline,trees:[{n:'Realm A edit',t:[]}]});
  TalentStudio.useSource('test-realm-b',[{...baseline,trees:[{n:'Realm B original',t:[]}]}]);
  check(document.querySelector('#pane0 .tree-name').textContent==='Realm B original','Realm switch reused another realm edits');
  TalentStudio.useSource('test-realm-a',[baseline]);
  check(document.querySelector('#pane0 .tree-name').textContent==='Realm A edit','Realm switch lost the previous realm edits');
  TalentStudio.useSource('',null);
  const apiBefore=StudioBridge.api;
  StudioBridge.api=async function(route,body){if(route==='load')throw new Error('Fixture source read failed');return apiBefore(route,body);};
  document.querySelector('#realmSource').value='manual';
  try {
    let failed=false;try{await StudioSources.reload();}catch(error){failed=error.message==='Fixture source read failed';}
    check(failed,'Source read failure was suppressed');
    let rejected=false;try{TalentStudio.importData(baseline);}catch(error){rejected=error.message==='Fixture source read failed';}
    check(rejected,'Import continued into the wrong dataset after source loading failed');
  } finally {
    StudioBridge.api=apiBefore;document.querySelector('#realmSource').value='builtin';await StudioSources.reload();
  }
  return {restorePreservedLayout:true,realmEditsIsolated:true,stableBoard:fitted,normalSideboardSlots:before,canvasHeight:height};
})()
