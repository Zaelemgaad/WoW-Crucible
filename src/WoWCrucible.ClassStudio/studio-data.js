(function (root) {
'use strict';
function value(row, names, fallback) {
  for (var name of names) {
    var key = Object.keys(row).find(function (k) { return k.toLowerCase() === name.toLowerCase(); });
    if (key !== undefined) return row[key];
  }
  return fallback;
}
function label(row) { return value(row, ['Name[enUS]','Name_Lang[enUS]','SpellName[enUS]','SpellName0','SpellName_0','Name_Lang_enUS','Name'], ''); }
function table(snapshot, layer, name) { return snapshot.tables.find(function (t) { return t.layer === layer && t.name === name; }); }
function rows(snapshot, layer, name) { return (table(snapshot, layer, name) || {rows:[]}).rows; }
function key(t, row) {
  if (isDbc(t)) return String(row.$recordKey == null ? row.ID : row.$recordKey);
  var keys = t.columns.filter(function (c) { return c.key; });
  if (!keys.length) return JSON.stringify(row);
  return JSON.stringify(keys.map(function (c) { return row[c.name]; }));
}
function isDbc(t) { return t.format ? t.format === 'dbc' : t.layer !== 'sql'; }
function editKey(t, row) { return t.layer + '/' + t.name + '/' + key(t, row); }
function effective(t, edits) {
  var result = t.rows.map(function (r) { var edit = edits[editKey(t,r)]; return edit ? edit.after : r; }).filter(Boolean);
  Object.values(edits).filter(function (e) { return e.layer === t.layer && e.table === t.name && !e.before && e.after; }).forEach(function (e) { result.push(e.after); });
  return result;
}
function learning(snapshot, layer, classId) {
  var mask = Math.pow(2, classId - 1), found = new Map();
  function add(id, reason) { id = Number(id); if (!id) return false; var fresh = !found.has(id); if (fresh) found.set(id, new Set()); found.get(id).add(reason); return fresh; }
  function matches(m) { return (Number(m) & mask) !== 0; }
  var skills = new Set(rows(snapshot, layer, 'SkillRaceClassInfo').filter(function (r) { return matches(r.ClassMask); }).map(function (r) { return r.SkillID; }));
  var categories=new Map(rows(snapshot,layer,'SkillLine').map(function(r){return [r.ID,Number(r.CategoryID)];}));
  rows(snapshot,layer,'SkillLineAbility').forEach(function (r) {
    // Profession recipes are browsable in All spells, but are not class spellbook grants.
    if(categories.get(r.SkillLine)===9||categories.get(r.SkillLine)===11)return;
    if (!matches(r.ExcludeClass) && (matches(r.ClassMask) || (!r.ClassMask && skills.has(r.SkillLine)))) add(r.Spell, 'SkillLineAbility ' + r.ID);
  });
  var tabs = new Set(rows(snapshot,layer,'TalentTab').filter(function (r) { return matches(r.ClassMask); }).map(function (r) { return r.ID; }));
  rows(snapshot,layer,'Talent').filter(function (r) { return tabs.has(r.TabID); }).forEach(function (r) {
    for (var i=0;i<9;i++) add(r['SpellRank['+i+']'], 'Talent ' + r.ID);
  });
  var groups=new Set(rows(snapshot,layer,'CharacterAdvancementClassTypes').filter(function(r){return Number(r.ClassID)===classId;}).map(function(r){return r.ID;}));
  rows(snapshot,layer,'CharacterAdvancement').filter(function(r){return groups.has(r.ClassGroup);}).forEach(function(r){
    for(var i=0;i<9;i++)add(r['SpellRank['+i+']'],'CoA advancement '+r.ID+(r.AvailableCoA?'':' (disabled in CoA)'));
  });
  ['CoAClassSpells','CoARankSpells'].forEach(function(name){rows(snapshot,'core',name).filter(function(r){return Number(r.ClassID)===classId;}).forEach(function(r){add(r.SpellID,name+' level '+r.RequiredLevel);});});
  ['playercreateinfo_spell','playercreateinfo_spell_custom','playercreateinfo_cast_spell','player_levelup_spell'].forEach(function (name) {
    rows(snapshot,'sql',name).forEach(function (r) {
      var cls = value(r,['class'],null), clsMask = value(r,['classMask'],null);
      if ((cls !== null && (Number(cls) === 0 || Number(cls) === classId)) || (clsMask !== null && (!Number(clsMask) || matches(clsMask)))) add(value(r,['spell','spellId'],0), name);
    });
  });
  rows(snapshot,'sql','quest_template').forEach(function (r) { if (matches(value(r,['AllowableClasses','RequiredClasses'],0))) add(value(r,['RewardSpell'],0), 'Quest ' + value(r,['ID','entry'],'')); });
  var sqlSpells = new Map(rows(snapshot,'sql','spell_dbc').map(function(r){return [Number(r.ID),r];}));
  var spells = rows(snapshot,layer,'Spell').filter(function(r){return !sqlSpells.has(Number(r.ID));}).concat(Array.from(sqlSpells.values()));
  var learn = rows(snapshot,'sql','spell_learn_spell'), ranks = rows(snapshot,'sql','spell_ranks');
  var changed = true;
  while (changed) {
    changed = false;
    learn.forEach(function (r) { if (found.has(Number(value(r,['entry'],0)))) changed = add(value(r,['SpellID'],0),'spell_learn_spell') || changed; });
    ranks.forEach(function (r) { if (found.has(Number(r.first_spell_id))) changed = add(r.spell_id,'spell_ranks') || changed; });
    rows(snapshot,layer,'SpellRank').forEach(function(r){if(found.has(Number(r.FirstSpellID)))changed=add(r.SpellID,'CoA SpellRank '+r.Rank)||changed;});
    rows(snapshot,'core','CoATaughtAbilities').forEach(function(r){if((!r.ClassID||Number(r.ClassID)===classId)&&found.has(Number(r.ParentSpellID)))changed=add(r.SpellID,'CoA taught by '+r.ParentSpellID)||changed;});
    spells.forEach(function (r) { if (!found.has(Number(r.ID))) return; for(var i=0;i<3;i++) if (Number(value(r,['Effect['+i+']','Effect_'+(i+1)],0)) === 36) changed = add(value(r,['EffectTriggerSpell['+i+']','EffectTriggerSpell_'+(i+1)],0),'Learn effect of ' + r.ID) || changed; });
  }
  ['trainer_spell','npc_trainer'].forEach(function (name) { rows(snapshot,'sql',name).forEach(function (r) { var id=Number(value(r,['SpellId','SpellID'],0)); if(found.has(id)) add(id,name); }); });
  return found;
}
function realmClasses(snapshot, layer) {
  var classes=rows(snapshot,layer,'ChrClasses'), profile=snapshot.realmProfile;
  if(!profile || !classes.length)return classes;
  var types=rows(snapshot,layer,'CharacterAdvancementClassTypes');
  var policy=rows(snapshot,'core','CoARemixPolicy')[0];
  if(profile==='WoWRemix'&&(!policy||!rows(snapshot,'core','CoARemixPresentation').length))throw new Error('Remix requires its core admission policy and client presentation hooks. Select the matching core source.');
  if(!types.length)throw new Error('The selected realm needs CharacterAdvancementClassTypes. Check source coverage.');
  var allowed=new Set(types.filter(function(t){
    if(profile==='CoA')return t.CoA && t.ClassID>=12 && t.ClassID<=32;
    if(profile==='Season9'||profile==='Season10')return t.Classless && t.ClassID===10;
    if(profile==='WarcraftReborn'||profile==='WoWRemix')return t.WarcraftReborn && !t.Classless && t.ClassID>0 && t.ClassID<=11 && t.ClassID!==10;
    throw new Error('Unsupported realm profile: '+profile);
  }).map(function(t){return Number(t.ClassID);}));
  return classes.filter(function(c){return allowed.has(Number(c.ID)) && !(profile==='WoWRemix' && types.some(function(t){return t.ID===policy.PriestClassType && t.ClassID===c.ID;}));});
}
function talentClass(snapshot, layer, classId) {
  var cls = rows(snapshot,layer,'ChrClasses').find(function (r) { return r.ID === classId; });
  if (!cls) throw new Error('ChrClasses has no selected class.');
  var spells = new Map(rows(snapshot,layer,'Spell').map(function (r) { return [r.ID,r]; }));
  var icons = new Map(rows(snapshot,layer,'SpellIcon').map(function (r) { return [r.ID,String(r.TextureFilename).split(/[\\/]/).pop().toLowerCase()]; }));
  if((classId>=12||snapshot.realmProfile)&&rows(snapshot,layer,'CharacterAdvancement').length) {
    var groups=new Set(rows(snapshot,layer,'CharacterAdvancementClassTypes').filter(function(r){return Number(r.ClassID)===classId || (classId===10 && r.Classless && r.ID===1);}).map(function(r){return r.ID;}));
    var policy=rows(snapshot,'core','CoARemixPolicy')[0];
    var entries=rows(snapshot,layer,'CharacterAdvancement').filter(function(r){
      if(!groups.has(r.ClassGroup))return false;
      if(!policy)return true;
      var imported=r.ID>=policy.FirstEntry && r.ID<=policy.LastEntry;
      if(imported)return snapshot.realmProfile==='WoWRemix';
      return snapshot.realmProfile!=='WoWRemix'||(r.ClassGroup!==policy.PaladinClassType && r.ClassGroup!==policy.PriestClassType);
    });
    var byId=new Map(entries.map(function(r){return [r.ID,r];}));
    // Root entries identify custom specs; their spell names are not spec names.
    var custom=classId>=12, seasonal=/^Season/.test(snapshot.realmProfile||'');
    var specs=rows(snapshot,layer,'ChrSpecs').filter(function(r){return custom ? byId.has(r.RootEntry) : classId===10 ? !r.RootEntry : r.ClassToken===cls.Filename;});
    var tabs=rows(snapshot,layer,'CharacterAdvancementTabTypes');
    if(!tabs.length)tabs=rows(snapshot,'client','CharacterAdvancementTabTypes');
    if(!tabs.length)throw new Error('Client CharacterAdvancementTabTypes is required to identify talent trees.');
    var visible=entries.filter(function(r){return (seasonal?r.AvailableSeasonal:r.AvailableCoA) && !(r.Flags & 0x20809) && !(classId===10 && (r.Flags & 0x1800000));});
    var assigned=new Set();
    function talent(r,index,parked) {
      var ids=[];for(var i=0;i<9;i++)if(r['SpellRank['+i+']'])ids.push(r['SpellRank['+i+']']);
      var spell=spells.get(ids[0])||{}, icon=(r.TextureFilename?String(r.TextureFilename).split(/[\\/]/).pop().toLowerCase():icons.get(spell.SpellIconID))||'_placeholder';
      var result={i:r.ID,n:r.Name||label(spell)||('Entry '+r.ID),m:Math.max(1,ids.length),s:ids,
        x:parked?index%12:custom?r.PositionX:Math.max(0,r.Column-1),y:parked?Math.floor(index/12):custom?r.PositionY:r.Row,
        iconname:icon.replace(/\.blp$/i,''),d:ids.map(function(id){return value(spells.get(id)||{},['Description[enUS]','Description_Lang[enUS]'],'');}),sourceLayer:layer,sourceRow:r,sourceFormat:'coa-plan'};
      if(!Number.isFinite(result.x)||!Number.isFinite(result.y))throw new Error('Missing source coordinates for advancement '+r.ID);
      var parent=r['RequiredEntry[0]'] || (!custom?r['ConnectedEntry[0]']:0);
      if(parent && byId.has(parent))result.r={reqId:parent,reqRank:Math.max(1,Array.from({length:9},function(_,i){return byId.get(parent)['SpellRank['+i+']'];}).filter(Boolean).length)};
      if(custom)result.connections=Array.from({length:15},function(_,i){return r['ConnectedEntry['+i+']'];}).filter(Boolean);
      return result;
    }
    var presentation=snapshot.realmProfile==='WoWRemix'?rows(snapshot,'core','CoARemixPresentation'):[];
    var boards=[];
    specs.forEach(function(spec){
      var heroGroup=classId===10?rows(snapshot,layer,'CharacterAdvancementClassTypes').find(function(g){return groups.has(g.ID) && String(g.Token).toUpperCase()===spec.ClassToken;}):null;
      if(classId===10 && !heroGroup)return;
      var root=byId.get(spec.RootEntry), tab=custom?tabs.find(function(t){return t.ID===root.Tab;}):tabs.find(function(t){return String(t.Token).toUpperCase()===spec.Token;});
      if(!tab)return;
      var members=visible.filter(function(r){return r.Tab===tab.ID && (!heroGroup || r.ClassGroup===heroGroup.ID) && (custom || r.Type==='Talent' || r.Type==='TalentAbility');});
      if(!members.length)return;
      // Hero specs share tokens (for example Protection); retain the class group in the board identity.
      if(boards.some(function(b){return b.sourceTabId===tab.ID && b.sourceClassGroup===(heroGroup?heroGroup.ID:null);}))return;
      members.forEach(function(r){assigned.add(r.ID);});
      var rename=presentation.find(function(p){return p.ClassID===classId && p.SpecToken===spec.Token;});
      boards.push({n:heroGroup?heroGroup.Token+' · '+spec.Name:rename?rename.SpecName:spec.Name,sourceClassGroup:heroGroup?heroGroup.ID:null,sourceSpecId:spec.ID,sourceTabId:tab.ID,sourceLayer:layer,layoutSource:custom?'coa-graph':'advancement-grid',t:members.map(function(r,i){return talent(r,i,false);})});
    });
    if(!boards.length)throw new Error('Class '+classId+' has no supported talent definitions in the selected realm.');
    var rename=presentation.find(function(p){return p.ClassID===classId;});
    return {classId:classId,name:rename?rename.ClassName:label(cls),trees:boards,sideboard:{n:'Class abilities',t:visible.filter(function(r){return !assigned.has(r.ID);}).map(function(r,i){return talent(r,i,true);})},layoutSource:custom?'coa-graph':'advancement-grid'};
  }
  var trees = rows(snapshot,layer,'TalentTab').filter(function (r) { return (r.ClassMask & Math.pow(2,classId-1)) !== 0; }).sort(function(a,b){ return a.OrderIndex-b.OrderIndex; }).map(function (tab) {
    return {n:label(tab),sourceTabId:tab.ID,sourceLayer:layer,t:rows(snapshot,layer,'Talent').filter(function(r){return r.TabID===tab.ID;}).map(function (r) {
      var ids = []; for(var i=0;i<9;i++) if(r['SpellRank['+i+']']) ids.push(r['SpellRank['+i+']']);
      var spell = spells.get(ids[0]) || {};
      var talent = {i:r.ID,n:label(spell)||('Spell '+ids[0]),m:Math.max(1,ids.length),s:ids,x:r.ColumnIndex,y:r.TierID,
        iconname:icons.get(spell.SpellIconID)||'_placeholder',d:ids.map(function(id){return value(spells.get(id)||{},['Description[enUS]','Description_Lang[enUS]'],'');}),sourceLayer:layer,sourceRow:r};
      if(r['PrereqTalent[0]']) talent.r = {reqId:r['PrereqTalent[0]'],reqRank:Number(r['PrereqRank[0]'])+1};
      return talent;
    })};
  });
  if (!trees.length) throw new Error('No talent tabs for this class in the selected source.');
  return {classId:classId,name:label(cls),trees:trees};
}
function csv(rows, columns) {
  function cell(v) { var s=v==null?'':String(v); return /[",\r\n]/.test(s)?'"'+s.replace(/"/g,'""')+'"':s; }
  return [columns.map(cell).join(',')].concat(rows.map(function(r){return columns.map(function(c){return cell(r[c]);}).join(',');})).join('\r\n');
}
var api = {value:value,label:label,table:table,rows:rows,key:key,editKey:editKey,effective:effective,learning:learning,talentClass:talentClass,realmClasses:realmClasses,csv:csv,isDbc:isDbc};
if (typeof module !== 'undefined' && module.exports) module.exports=api; else root.StudioData=api;
})(typeof window === 'undefined' ? globalThis : window);
