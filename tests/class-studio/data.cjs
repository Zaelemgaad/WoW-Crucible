const assert=require('node:assert/strict');
const D=require('../../src/WoWCrucible.ClassStudio/studio-data.js');
const t=(name,rows,layer='server')=>({name,rows,layer,columns:[{name:'ID',key:true}]});
const snapshot={tables:[t('ChrClasses',[{ID:2,'Name_Lang[enUS]':'Templar'}]),
  t('SkillLineAbility',[{ID:1,Spell:10,ClassMask:2},{ID:2,Spell:11,ClassMask:2,ExcludeClass:2}]),
  t('TalentTab',[{ID:1,ClassMask:2,OrderIndex:2,'Name_Lang[enUS]':'Judgment'},{ID:2,ClassMask:2,OrderIndex:0,'Name_Lang[enUS]':'Divine'},{ID:3,ClassMask:2,OrderIndex:1,'Name_Lang[enUS]':'Aegis'}]),
  t('Talent',[{ID:1,TabID:1,ColumnIndex:0,TierID:0,'SpellRank[0]':20}]),
  t('Spell',[{ID:10,'Name[enUS]':'Test','Effect[0]':36,'EffectTriggerSpell[0]':12},{ID:20,'Name[enUS]':'Talent'}]),
  t('spell_learn_spell',[{entry:12,SpellID:13},{entry:13,SpellID:12}],'sql'),t('spell_ranks',[{first_spell_id:10,spell_id:14}],'sql')]};
assert.deepEqual([...D.learning(snapshot,'server',2).keys()].sort((a,b)=>a-b),[10,12,13,14,20]);
assert.deepEqual(D.talentClass(snapshot,'server',2).trees.map(t=>t.n),['Divine','Aegis','Judgment']);
const table=t('Spell',[{ID:1,'$recordKey':1},{ID:2,'$recordKey':2}]);
const edits={'server/Spell/1':{layer:'server',table:'Spell',before:{ID:1},after:null},'server/Spell/3':{layer:'server',table:'Spell',before:null,after:{ID:3}}};
assert.deepEqual(D.effective(table,edits).map(r=>r.ID),[2,3]);
assert.equal(D.csv([{Name:'a,"b"\nc',ID:1}],['ID','Name']),'ID,Name\r\n1,"a,""b""\nc"');
console.log('PASS class masks/exclusions, transitive learn graph including cycles, talent ordering, edits and CSV quoting.');
