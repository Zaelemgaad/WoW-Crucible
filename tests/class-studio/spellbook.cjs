const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const http=require('node:http');
const {chromium}=require(process.argv[2]||'playwright');
(async()=>{
  const root=path.resolve(__dirname,'../../src/WoWCrucible.ClassStudio');
  const fixtures=path.resolve(__dirname,'../../.local/class-studio-test');
  const catalog=await fs.readFile(path.join(fixtures,'catalog.json'),'utf8'), detail=JSON.parse(await fs.readFile(path.join(fixtures,'spell.json'),'utf8'));
  let state=null;
  const server=http.createServer(async(req,res)=>{
    try{
      const url=new URL(req.url,'http://localhost');let body='';for await(const chunk of req)body+=chunk;
      res.setHeader('Content-Type','application/json');
      if(url.pathname==='/api/state'){if(req.method==='POST')state=JSON.parse(body);res.end(JSON.stringify(req.method==='GET'?state:{saved:true}));}
      else if(url.pathname==='/api/settings')res.end(JSON.stringify({serverPath:'fixture',clientPath:'',sql:false}));
      else if(url.pathname==='/api/load')res.end(catalog);
      else if(url.pathname==='/api/row')res.end(JSON.stringify(detail));
      else{const file=path.join(root,url.pathname==='/'?'index.html':url.pathname);res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':file.endsWith('.html')?'text/html':'image/png');res.end(await fs.readFile(file));}
    }catch(e){res.statusCode=500;res.end(JSON.stringify({error:e.message}));}
  });
  await new Promise(r=>server.listen(0,'127.0.0.1',r));const browser=await chromium.launch();
  try{
    const page=await browser.newPage({viewport:{width:1800,height:1100}}),errors=[];
    page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/');await page.click('#spellbookBtn');
    await page.getByRole('button',{name:'Load / refresh sources',exact:true}).click();
    await page.getByText(/^Loaded \d+ tables\. Staged edits were retained\.$/).waitFor();
    assert.equal(await page.locator('.studio-table tr').count(),101,'large catalogs are paged');
    await page.locator('.studio-table button').first().click();await page.locator('input[name="ManaCost"]').fill('321');
    await page.getByRole('button',{name:'Stage row',exact:true}).click();
    let edits=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.studio.edits')));assert.equal(Object.values(edits)[0].after.ManaCost,321);
    const csvPromise=page.waitForEvent('download');await page.getByRole('button',{name:'Export table CSV',exact:true}).click();const csv=await csvPromise;
    const csvText=await fs.readFile(await csv.path(),'utf8');assert(csvText.startsWith('$recordKey,ID,'));await fs.copyFile(await csv.path(),path.join(fixtures,'edited-spell.csv'));
    // A staged edit must be the source of cloning even before reopening the row.
    await page.getByRole('button',{name:'Clone as new',exact:true}).click();await page.locator('input[name="ID"]').fill('2000000000');
    await page.getByRole('button',{name:'Stage row',exact:true}).click();edits=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.studio.edits')));assert.equal(edits['server/Spell/2000000000'].after.ManaCost,321);
    await page.getByLabel('Class',{exact:true}).selectOption('2');
    assert(await page.locator('.studio-table tr').count()>1,'class learning list contains actual records');
    page.once('dialog',d=>d.accept());await page.getByRole('button',{name:'Use class talents',exact:true}).click();
    assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.class.2')).trees.length),3);
    if(JSON.parse(catalog).tables.some(t=>t.name==='CharacterAdvancement')) {
      await page.getByLabel('Class',{exact:true}).selectOption('19');
      assert(await page.locator('.studio-table tr').count()>1,'CoA Templar learns are present');
      page.once('dialog',d=>d.accept());await page.getByRole('button',{name:'Use class talents',exact:true}).click();
      const imported=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.class.19')));
      assert.equal(imported.name,'Templar');assert.equal(imported.trees.length,3);assert(imported.trees.every(t=>t.sourceSpecId&&t.layoutSource==='planning'));
      assert(imported.trees.every(t=>t.t.every(x=>x.sourceRow.ID===x.i)),'CoA IDs preserved without 16-bit truncation');
    }
    await page.getByLabel('View',{exact:true}).selectOption('tables');
    const sla=await page.getByLabel('Table',{exact:true}).locator('option').evaluateAll(os=>os.find(o=>o.textContent.includes('/ SkillLineAbility ')).value);
    await page.getByLabel('Table',{exact:true}).selectOption(sla);await page.locator('.studio-table button').first().click();await page.locator('input[name="Spell"]').fill('2000000000');await page.getByRole('button',{name:'Stage row',exact:true}).click();
    const planPromise=page.waitForEvent('download');await page.getByRole('button',{name:'Export changes JSON',exact:true}).click();const plan=JSON.parse(await fs.readFile(await(await planPromise).path(),'utf8'));assert.equal(plan.changes.length,3);assert(plan.changes.every(c=>c.source&&c.fingerprint));
    await page.waitForTimeout(350);await page.reload();await page.click('#spellbookBtn');assert(await page.getByText('3 staged row changes',{exact:true}).isVisible(),'native state restore persists edits');
    await page.screenshot({path:path.join(fixtures,'spellbook.png')});assert.deepEqual(errors,[]);
    console.log('PASS live-data UI fixture: catalog paging, spell fields, clone, learning rule edits, talent import, CSV/JSON and native workspace persistence.');
  }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
