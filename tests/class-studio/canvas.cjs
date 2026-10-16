const assert = require('node:assert/strict');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const {chromium} = require(process.argv[2] || 'playwright');
(async () => {
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage({viewport:{width:1700,height:1000}}), errors=[];
    page.on('pageerror', e=>errors.push(e.message));
    await page.goto(pathToFileURL(path.resolve(__dirname,'../../src/WoWCrucible.ClassStudio/index.html')).href);
    await page.click('#freeModeBtn'); await page.click('#dismissHint');
    const board=page.locator('.sideboard-block'), move=board.locator('.board-move');
    const metrics=await page.evaluate(()=>({page:document.documentElement.scrollHeight,height:innerHeight,canvas:document.querySelector('.board-canvas').clientHeight,scroll:document.querySelector('.board-canvas').scrollHeight,body:document.querySelector('.sideboard-grid-wrap').clientWidth,grid:document.querySelector('.sideboard-grid-wrap').scrollWidth}));
    assert.equal(metrics.page,metrics.height,'no page scrollbar'); assert.equal(metrics.canvas,metrics.scroll,'no unnecessary canvas scrollbar'); assert.equal(metrics.body,metrics.grid,'no default horizontal sideboard scrollbar');
    await move.focus();await page.keyboard.press('ArrowRight');await page.keyboard.press('ArrowDown');
    let r=await board.boundingBox(); const x=r.x,y=r.y;
    await page.reload();r=await board.boundingBox();assert.equal(r.x,x);assert.equal(r.y,y);assert(await page.locator('#editHint').isHidden());
    const tools=page.locator('.canvas-tools');await tools.getByLabel('Grid',{exact:true}).check();await tools.getByLabel('Snap',{exact:true}).check();await tools.getByLabel('Grid spacing').fill('64');await tools.getByLabel('Grid spacing').dispatchEvent('change');
    let handle=await move.boundingBox();await page.mouse.move(handle.x+5,handle.y+5);await page.mouse.down();await page.mouse.move(handle.x+260,handle.y+145,{steps:8});await page.mouse.up();
    const layout=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.workspace')).sideboard.layout);
    assert.equal((layout.x+layout.width/2)%64,0,'board center snaps to vertical grid line');assert.equal((layout.y+layout.height/2)%64,0,'board center snaps to horizontal grid line');
    const geometry=await page.evaluate(()=>{const s={snap:true,grid:true,step:64,zoom:1};return [WindowLayout.snapAxis(4,200,0,1000,[],s),WindowLayout.snapAxis(795,200,0,1000,[],s),WindowLayout.snapAxis(403,200,0,1000,[],s),WindowLayout.snapAxis(303,200,0,1000,[{start:100,size:200}],s)];});
    assert.deepEqual(geometry,[0,800,400,300],'screen edges, center, adjacent window snapping');
    const size=board.locator('.board-resize');await size.focus();await page.keyboard.press('ArrowRight');await page.keyboard.press('ArrowDown');
    const resized=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.workspace')).sideboard.layout);assert.equal(resized.width,layout.width+64);
    await page.click('#spellbookBtn');const panel=page.locator('#spellbook'), title=panel.locator('.studio-title');const initial=await panel.boundingBox();handle=await title.boundingBox();
    await page.mouse.move(handle.x+100,handle.y+15);await page.mouse.down();await page.mouse.move(handle.x+50,handle.y+50,{steps:5});await page.mouse.up();
    assert.notEqual((await panel.boundingBox()).x,initial.x,'spell editor is movable');
    await page.click('#closeSpellbook');assert(await panel.isHidden());
    await page.screenshot({path:path.resolve(__dirname,'../../.local/class-studio-test/canvas.png')});
    assert.deepEqual(errors,[]);console.log('PASS canvas sizing, close persistence, keyboard move/resize, grid-center, edge and neighbor snapping, movable spell editor.');
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
