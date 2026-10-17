const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
const {chromium}=require(process.argv[2]||'playwright');
(async()=>{
  const root=path.resolve(__dirname,'../../src/WoWCrucible.ClassStudio');
  const fixtures=path.resolve(__dirname,'../../.local/class-studio-test');
  const detail=JSON.parse(await fs.readFile(path.join(fixtures,'spell.json'),'utf8'));
  let state=null;const exports=[];const browser=await chromium.launch();
  try{
    const page=await browser.newPage({viewport:{width:1800,height:1100}}),errors=[];
    // Exercise the frontend's native-message protocol. The C# embedded suite verifies real WebView2.
    await page.exposeBinding('nativeRequest',async(_,message)=>{
      let result;const route=message.route.split('?')[0];
      if(route==='state'){if(message.body!==undefined)state=message.body;result=message.body===undefined?state:{saved:true};}
      else if(route==='settings')result={serverPath:'fixture',clientPath:'',sql:false};
      else if(route==='load')result={fixture:true};
      else if(route==='row')result=detail;
      else if(route==='export'){exports.push(message.body);result={saved:true};}
      else throw new Error('Unexpected native request: '+route);
      return JSON.stringify({id:message.id,result});
    });
    await page.addInitScript(()=>{
      const listeners=[];window.nativeExportCount=0;window.chrome=window.chrome||{};
      window.chrome.webview={addEventListener:(name,fn)=>listeners.push(fn),postMessage:message=>window.nativeRequest(message).then(async reply=>{
        const data=JSON.parse(reply);
        // A real file fixture avoids CDP expanding a 248 MB CoA object into a multi-GB binding graph.
        if(message.route==='load')data.result=JSON.parse(await document.querySelector('#catalogFixture').files[0].text());
        if(message.route==='load')window.fixtureHasCoA=data.result.tables.some(t=>t.name==='CharacterAdvancement');
        if(message.route==='export')window.nativeExportCount++;
        listeners.forEach(fn=>fn({data}));
      })};
    });
    page.on('pageerror',e=>errors.push(e.message));await page.goto(pathToFileURL(path.join(root,'index.html')).href);await page.click('#spellbookBtn');
    await page.evaluate(()=>{const input=document.createElement('input');input.type='file';input.id='catalogFixture';input.hidden=true;document.body.appendChild(input);});
    await page.locator('#catalogFixture').setInputFiles(path.join(fixtures,'catalog.json'));
    await page.getByRole('button',{name:'Load / refresh sources',exact:true}).click();
    await page.getByText(/^Loaded \d+ tables\. Staged edits were retained\.$/).waitFor();
    assert.equal(await page.locator('.studio-table tr').count(),101,'large catalogs are paged');
    await page.locator('.studio-table button').first().click();await page.locator('input[name="ManaCost"]').fill('321');
    await page.getByRole('button',{name:'Stage row',exact:true}).click();
    let edits=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.studio.edits')));assert.equal(Object.values(edits)[0].after.ManaCost,321);
    await page.getByRole('button',{name:'Export table CSV',exact:true}).click();
    await page.waitForFunction(()=>window.nativeExportCount===1);assert.equal(exports.length,1);
    const csvText=exports[0].text;assert(csvText.startsWith('$recordKey,ID,'));await fs.writeFile(path.join(fixtures,'edited-spell.csv'),csvText);
    // A staged edit must be the source of cloning even before reopening the row.
    await page.getByRole('button',{name:'Clone as new',exact:true}).click();await page.locator('input[name="ID"]').fill('2000000000');
    await page.getByRole('button',{name:'Stage row',exact:true}).click();edits=await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.studio.edits')));assert.equal(edits['server/Spell/2000000000'].after.ManaCost,321);
    await page.getByLabel('Class',{exact:true}).selectOption('2');
    assert(await page.locator('.studio-table tr').count()>1,'class learning list contains actual records');
    page.once('dialog',d=>d.accept());await page.getByRole('button',{name:'Use class talents',exact:true}).click();
    assert.equal(await page.evaluate(()=>JSON.parse(localStorage.getItem('twc.class.2')).trees.length),3);
    if(await page.evaluate(()=>window.fixtureHasCoA)) {
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
    await page.getByRole('button',{name:'Export changes JSON',exact:true}).click();
    await page.waitForFunction(()=>window.nativeExportCount===2);
    const plan=JSON.parse(exports[1].text);assert.equal(plan.changes.length,3);assert(plan.changes.every(c=>c.source&&c.fingerprint));
    await page.waitForTimeout(350);await page.reload();await page.click('#spellbookBtn');assert(await page.getByText('3 staged row changes',{exact:true}).isVisible(),'native state restore persists edits');
    await page.screenshot({path:path.join(fixtures,'spellbook.png')});assert.deepEqual(errors,[]);
    console.log('PASS live-data UI fixture: catalog paging, spell fields, clone, learning rule edits, talent import, CSV/JSON and native workspace persistence.');
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
