(function () {
'use strict';
var D=window.StudioData, B=window.StudioBridge, snapshot=null, edits={}, selectedTable=null, page=0;
var host, status, layer, classSelect, mode, search, tableSelect, results, editor, sources, count;
function el(tag, cls, text) { var e=document.createElement(tag); if(cls)e.className=cls; if(text!=null)e.textContent=text; return e; }
function button(text, fn) { var b=el('button','btn',text); b.type='button'; b.onclick=function(){Promise.resolve().then(fn).catch(fail);}; return b; }
function fail(error) { status.textContent=error.message; status.classList.add('studio-error'); }
function say(text) { status.textContent=text; status.classList.remove('studio-error'); }
function save() { localStorage.setItem('twc.studio.edits',JSON.stringify(edits)); B.changed(); updateCount(); }
function download(name,text,type) { return B.download(name,text,type); }
function option(select,value,text) { var o=el('option',null,text);o.value=value;select.appendChild(o); }
function field(parent,label,input) { var l=el('label','studio-field',label);input.setAttribute('aria-label',label);l.appendChild(input);parent.appendChild(l);return input; }
function select(parent,label) { return field(parent,label,el('select')); }
function updateCount() { count.textContent=Object.keys(edits).length+' staged row changes'; }
function persistSources() { localStorage.setItem('twc.studio.sources',JSON.stringify(sources));B.changed(); }
function editedSnapshot() { return {tables:snapshot.tables.map(function(t){return Object.assign({},t,{rows:D.effective(t,edits)});})}; }
function currentTable() { return snapshot && snapshot.tables[Number(tableSelect.value)]; }
function populate() {
  layer.replaceChildren(); var layers=Array.from(new Set(snapshot.tables.filter(function(t){return t.name==='Spell';}).map(function(t){return t.layer;})));
  layers.forEach(function(l){option(layer,l,l);});
  tableSelect.replaceChildren(); snapshot.tables.forEach(function(t,i){option(tableSelect,i,t.layer+' / '+t.name+' ('+t.rows.length+')');});
  populateClasses(); showRows();
}
function populateClasses() {
  classSelect.replaceChildren(); option(classSelect,'','All spells');
  D.rows(snapshot,layer.value,'ChrClasses').forEach(function(r){option(classSelect,r.ID,D.label(r)||('Class '+r.ID));});
}
function showRows() {
  if(!snapshot)return;
  results.replaceChildren(); var active=editedSnapshot(), t;
  if(mode.value==='spells') t=D.table(active,layer.value,'Spell'); else t=active.tables[Number(tableSelect.value)];
  if(!t) {results.textContent='This source has no selected table.';return;}
  var learns=classSelect.value&&mode.value==='spells'?D.learning(active,layer.value,Number(classSelect.value)):null;
  var query=search.value.trim().toLowerCase();
  var candidates=t.rows.map(function(r){return {row:r,table:t};});
  var sql=D.table(active,'sql','spell_dbc'), overrides=new Set(sql?sql.rows.map(function(r){return Number(r.ID);}):[]);
  if(mode.value==='spells'&&sql) {
    var dbcIds=new Set(t.rows.map(function(r){return Number(r.ID);}));
    sql.rows.filter(function(r){return !dbcIds.has(Number(r.ID));}).forEach(function(r){candidates.push({row:r,table:sql});});
  }
  var entries=candidates.filter(function(e){var r=e.row;return (!learns||learns.has(Number(r.ID)))&&(!query||Object.values(r).some(function(v){return String(v).toLowerCase().includes(query);}));});
  page=Math.min(page,Math.max(0,Math.ceil(entries.length/100)-1));
  var controls=el('div','studio-actions');
  controls.append(button('Previous',function(){page=Math.max(0,page-1);showRows();}),el('span',null,entries.length+' records · page '+(page+1)),button('Next',function(){if((page+1)*100<entries.length)page++;showRows();}));
  results.appendChild(controls);
  var list=el('table','studio-table'), head=el('tr');
  ['ID / key','Name / values','Learn source'].forEach(function(c){head.appendChild(el('th',null,c));});list.appendChild(head);
  entries.slice(page*100,page*100+100).forEach(function(entry){
    var r=entry.row, source=entry.table;
    var tr=el('tr'), a=el('td'), b=el('td'), c=el('td');
    a.appendChild(button(String(D.key(source,r)),function(){return openRow(D.table(snapshot,source.layer,source.name),r,false);}));
    b.textContent=D.label(r)||Object.entries(r).filter(function(p){return !p[0].startsWith('$');}).slice(0,5).map(function(p){return p[0]+'='+p[1];}).join(' · ');
    c.textContent=(learns?Array.from(learns.get(Number(r.ID))||[]).join(', '):source.source)+(mode.value==='spells'&&overrides.has(Number(r.ID))?' · SQL override':'');
    tr.append(a,b,c);list.appendChild(tr);
  });results.appendChild(list);
}
async function openRow(t,row,asNew) {
  selectedTable=t; var original=row, existing=row&&edits[D.editKey(t,row)];
  if(row&&t.name==='Spell'&&!existing) {
    if(!B.connected) throw new Error('Full spell fields require Crucible. Open Class Studio there, load the same sources, then select this spell.');
    row=await B.api('row?layer='+encodeURIComponent(t.layer)+'&table=Spell&id='+row.ID);
  }
  if(existing) row=existing.after||existing.before;
  if(!row) {row={};t.columns.forEach(function(c){row[c.name]=isText(c)?'':0;});}
  row=JSON.parse(JSON.stringify(row));
  var before=asNew?null:(existing?existing.before:row);
  var identity=asNew?null:D.editKey(t,original||row);
  editor.replaceChildren();
  editor.appendChild(el('h3',null,(asNew?'New / cloned row':'Edit row')+' · '+t.layer+'/'+t.name));
  if(t.name==='Spell'&&snapshot) {
    var sqlTable=D.table(snapshot,'sql','spell_dbc');
    var sqlRow=sqlTable&&D.effective(sqlTable,edits).find(function(r){return Number(r.ID)===Number(row.ID);});
    if(sqlRow)editor.appendChild(button('Open server SQL override',function(){return openRow(sqlTable,sqlRow,false);}));
  }
  editor.appendChild(el('p','form-help','Edits are staged. Source files and the live database are unchanged.'));
  if(t.format&&t.format.endsWith('-plan'))editor.appendChild(el('p','form-help','CoA implementation plan: export JSON with source hashes and field offsets. This table is not a stock DBC CSV import.'));
  var filter=el('input');filter.placeholder='Filter fields…';filter.setAttribute('aria-label','Filter spell fields');editor.appendChild(filter);
  var form=el('form','studio-record'), fields=[];
  t.columns.forEach(function(c){
    var input=el(isText(c)?'textarea':'input');input.name=c.name;
    if(input.tagName==='INPUT') {input.type='number';input.step=/float|double|decimal/i.test(c.type)?'any':'1';}
    input.value=row[c.name]==null?'':String(row[c.name]);
    if(!asNew&&(c.key||c.name==='ID')&&(D.isDbc(t)||t.columns.some(function(k){return k.key;}))) input.readOnly=true;
    var wrapper=el('label','studio-field',c.name+' · '+c.type+(c.key?' · key':''));wrapper.appendChild(input);form.appendChild(wrapper);fields.push({column:c,input:input,wrapper:wrapper});
  });
  filter.oninput=function(){fields.forEach(function(f){f.wrapper.hidden=!f.column.name.toLowerCase().includes(filter.value.toLowerCase());});};
  var actions=el('div','studio-actions');var submit=el('button','btn','Stage row');submit.type='submit';actions.appendChild(submit);
  if(!asNew) {
    actions.appendChild(button('Clone as new',function(){return openRow(t,row,true);}));
    actions.appendChild(button('Remove row',function(){
      if(!confirm('Stage removal of this '+t.name+' row?'))return;
      if(before) edits[identity]={layer:t.layer,table:t.name,format:t.format,source:t.source,fingerprint:t.fingerprint,columns:t.columns,before:before,after:null};else delete edits[identity];
      save();showRows();editor.replaceChildren();say('Removal staged. JSON includes the exact original row.');
    }));
  }
  if(identity&&existing) actions.appendChild(button('Discard this edit',function(){delete edits[identity];save();showRows();editor.replaceChildren();}));
  form.appendChild(actions);editor.appendChild(form);
  form.onsubmit=function(event){event.preventDefault();try{
    var after={}; fields.forEach(function(f){
      var text=f.input.value, c=f.column;
      if(isText(c)) {
        if(/bigint/i.test(c.type)&&!(/unsigned/i.test(c.type)?/^\d+$/:/^-?\d+$/).test(text))throw new Error(c.name+' needs an integer.');
        if(/decimal/i.test(c.type)&&!/^[-+]?\d+(\.\d+)?$/.test(text))throw new Error(c.name+' needs an exact decimal.');
        after[c.name]=text;
      }
      else {
        if(!text.trim()) {if(row[c.name]===null){after[c.name]=null;return;}throw new Error(c.name+' needs a number.');}
        var n=Number(text);if(!Number.isFinite(n)||(!/float|double|decimal/i.test(c.type)&&!Number.isSafeInteger(n)))throw new Error(c.name+' is not a valid '+c.type+'.');
        if(c.type==='UInt32'&&(n<0||n>4294967295)||c.type==='Int32'&&(n< -2147483648||n>2147483647)||c.type==='Byte'&&(n<0||n>255))throw new Error(c.name+' is outside '+c.type+' range.');
        after[c.name]=n;
      }
    });
    if(D.isDbc(t)) {
      if(!(after.ID>0))throw new Error('Record ID must be positive.');after.$recordKey=after.ID;
      if(!asNew)after.$rowIndex=row.$rowIndex;
    }
    var newIdentity=D.editKey(t,after);
    if(asNew&&D.effective(t,edits).some(function(r){return D.editKey(t,r)===newIdentity;}))throw new Error('That key already exists. Choose a new ID/key.');
    if(!asNew&&newIdentity!==identity)throw new Error('Change the key using Clone as new, then remove the old row if intended.');
    edits[identity||newIdentity]={layer:t.layer,table:t.name,format:t.format,source:t.source,fingerprint:t.fingerprint,columns:t.columns,before:before,after:after};
    save();showRows();say('Row staged. Export JSON for the change plan or CSV for this table.');
  }catch(error){fail(error);}};
}
function isText(c) {return /StringOffset|char|text|enum|set|date|time|blob|bigint|decimal/i.test(c.type);}
function exportPlan() {
  download('class-studio-changes.json',JSON.stringify({format:'crucible-class-studio-changes',version:1,sources:sources,readUtc:snapshot&&snapshot.readUtc,changes:Object.values(edits)},null,2));
}
function exportCsv() {
  var t=selectedTable||currentTable(); if(!t)throw new Error('Select a row or table first.');
  var changes=Object.values(edits).filter(function(e){return e.layer===t.layer&&e.table===t.name;});
  var rows=changes.filter(function(e){return e.after;}).map(function(e){return e.after;});
  if(!rows.length)throw new Error('No added or edited rows in '+t.name+'. Removals are in the JSON change plan.');
  var columns=(D.isDbc(t)?['$recordKey']:[]).concat(t.columns.map(function(c){return c.name;}));
  download(t.layer+'-'+t.name+'.csv',D.csv(rows,columns),'text/csv');
  say('Exported '+rows.length+' rows. Deletions remain in JSON; CSV contains upserts only.');
}
function init() {
  host=document.querySelector('#spellbookBody'); edits=JSON.parse(localStorage.getItem('twc.studio.edits')||'{}');sources=JSON.parse(localStorage.getItem('twc.studio.sources')||'{}');
  document.querySelector('#spellbookBtn').onclick=function(){document.querySelector('#spellbook').classList.remove('hidden');};
  document.querySelector('#closeSpellbook').onclick=function(){document.querySelector('#spellbook').classList.add('hidden');};
  var sourcePanel=el('details','studio-sources');sourcePanel.open=true;sourcePanel.appendChild(el('summary',null,'Data sources'));
  var paths={};['server','client','core'].forEach(function(kind){
    var line=el('div','studio-source-line'), input=el('input');input.value=sources[kind+'Path']||'';input.placeholder=kind==='server'?'Server data / DBC folder':kind==='core'?'CoA core source (compiled class learn lists)':'Client root / Data / extracted DBC folder';
    input.setAttribute('aria-label',kind+' folder');input.onchange=function(){sources[kind+'Path']=input.value;persistSources();};
    paths[kind]=input;line.append(el('span',null,kind),input,button('Browse',async function(){var result=await B.api('pick?kind='+kind,{});if(result.path){input.value=result.path;input.onchange();}}));sourcePanel.appendChild(line);
  });
  var sql=el('input');sql.type='checkbox';sql.checked=!!sources.includeSql;sql.onchange=function(){sources.includeSql=sql.checked;persistSources();};field(sourcePanel,'Include connected world SQL',sql);
  var order=select(sourcePanel,'Client loader');option(order,'CoA','CoA / Ascension');option(order,'StandardWrath','Standard Wrath');order.value=sources.clientOrder||'CoA';sources.clientOrder=order.value;
  order.onchange=function(){sources.clientOrder=order.value;persistSources();};
  sourcePanel.appendChild(button('Load / refresh sources',async function(){
    say('Reading source tables…'); sources.serverPath=paths.server.value;sources.clientPath=paths.client.value;sources.corePath=paths.core.value;persistSources();
    var loaded=await B.api('load',sources);snapshot=loaded;populate();
    notices.replaceChildren(el('summary',null,'Source coverage and limitations'));
    var stale=Object.values(edits).filter(function(e){var t=D.table(loaded,e.layer,e.table);return !t||(e.fingerprint&&e.fingerprint!==t.fingerprint);});
    if(stale.length)loaded.notices.unshift(stale.length+' staged edits were created against different source files. Their original values and fingerprints remain in the JSON plan; review them before importing.');
    loaded.notices.forEach(function(n){notices.appendChild(el('p',null,n));});
    say('Loaded '+loaded.tables.length+' tables. Staged edits were retained.');
  }));
  var notices=el('details','studio-notices');notices.appendChild(el('summary',null,'Source coverage and limitations'));
  sourcePanel.appendChild(notices);host.appendChild(sourcePanel);
  status=el('p','studio-status',B.connected?'Select sources, then Load / refresh.':'Open Class Studio from Crucible to load folders and SQL. Existing talent designs still work here.');status.setAttribute('role','status');host.appendChild(status);
  var toolbar=el('div','studio-actions');layer=select(toolbar,'Layer');classSelect=select(toolbar,'Class');mode=select(toolbar,'View');option(mode,'spells','Spellbook');option(mode,'tables','Learning rules / tables');
  tableSelect=select(toolbar,'Table');search=el('input');search.placeholder='Search ID, name or field value…';search.setAttribute('aria-label','Search spells');toolbar.appendChild(search);
  [layer,classSelect,mode,tableSelect].forEach(function(s){s.onchange=function(){page=0;if(s===layer&&snapshot)populateClasses();showRows();};});
  search.oninput=function(){page=0;showRows();};host.appendChild(toolbar);
  var actions=el('div','studio-actions');count=el('span');actions.append(
    button('New row',function(){if(!snapshot)throw new Error('Load sources first.');var t=mode.value==='spells'?D.table(snapshot,layer.value,'Spell'):currentTable();if(!t)throw new Error('Load sources first.');return openRow(t,null,true);}),
    button('Use class talents',function(){if(!snapshot||!classSelect.value)throw new Error('Choose a class first.');var payload=D.talentClass(editedSnapshot(),layer.value,Number(classSelect.value));
      var note=payload.layoutSource==='planning'?' CoA entries will be arranged as planning boards; native UI coordinates are not inferred. Source IDs and requirement fields are preserved.':'';
      if(confirm('Replace the working '+payload.name+' talent layout with these source tables? Export your existing design first if you want to retain both.'+note))window.TalentStudio.importClass(payload);}),
    button('Export changes JSON',exportPlan),button('Export table CSV',exportCsv),button('Discard all row edits',function(){if(confirm('Discard all staged spell/table edits?')){edits={};save();showRows();editor.replaceChildren();}}),count);
  host.appendChild(actions);updateCount();
  var split=el('div','studio-split');results=el('div','studio-results');editor=el('div','studio-editor');split.append(results,editor);host.appendChild(split);
  if(B.connected) B.api('settings').then(function(s){['server','client','core'].forEach(function(k){if(!sources[k+'Path']){paths[k].value=s[k+'Path']||'';}});}).catch(fail);
  document.querySelector('#backupStudio').onclick=function(){download('class-studio-workspace.json',JSON.stringify({format:'crucible-class-studio-workspace',version:1,storage:B.storage()},null,2));};
  document.querySelector('#restoreStudio').onclick=function(){document.querySelector('#restoreStudioFile').click();};
  document.querySelector('#restoreStudioFile').onchange=async function(){try{
    if(!this.files[0])return;var data=JSON.parse(await this.files[0].text());
    if(data.format!=='crucible-class-studio-workspace'||!data.storage)throw new Error('Not a Class Studio workspace. Use Import JSON for individual talent exports.');
    if(Object.entries(data.storage).some(function(p){return !p[0].startsWith('twc.')||typeof p[1]!=='string';}))throw new Error('Invalid workspace keys.');
    Object.values(data.storage).forEach(function(v){JSON.parse(v);});
    if(!confirm('Replace this editor workspace with the imported one?'))return;
    Object.keys(B.storage()).forEach(function(k){localStorage.removeItem(k);});Object.entries(data.storage).forEach(function(p){localStorage.setItem(p[0],p[1]);});
    if(B.connected){B.prepareClose();await B.api('state',B.storage());}location.reload();
  }catch(error){alert(error.message);}finally{this.value='';}};
}
document.addEventListener('DOMContentLoaded',function(){B.ready.then(init);});
})();
