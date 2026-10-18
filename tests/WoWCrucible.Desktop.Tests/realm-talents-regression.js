// Run against the snapshot produced by --class-studio's read-only corpus check.
const assert=require('node:assert/strict'),fs=require('node:fs');
const D=require('../../src/WoWCrucible.ClassStudio/studio-data.js');
const s=JSON.parse(fs.readFileSync(process.argv[2]||'.local/class-studio-test/catalog.json','utf8'));
let count=0;
for(const profile of ['CoA','WarcraftReborn','WoWRemix','Season9','Season10']){
  s.realmProfile=profile;
  const classes=D.realmClasses(s,'server').map(c=>D.talentClass(s,'server',c.ID));
  assert.equal(classes.length,profile==='CoA'?21:profile==='WarcraftReborn'?10:profile==='WoWRemix'?9:1);
  for(const c of classes)for(const tree of c.trees){
    assert(!/^(Spec \d|Advancement tab)/.test(tree.n));
    for(const t of tree.t){
      assert.equal(t.sourceRow.Tab,tree.sourceTabId);
      assert.equal(t.sourceRow.Flags&0x20809,0);
      assert.equal(t.x,c.classId>=12?t.sourceRow.PositionX:Math.max(0,t.sourceRow.Column-1));
      assert.equal(t.y,c.classId>=12?t.sourceRow.PositionY:t.sourceRow.Row);
      assert(t.s.length>0); count++;
      if(profile!=='WoWRemix')assert(t.i<47000||t.i>47999);
    }
  }
  if(profile==='CoA')assert.deepEqual(classes.find(c=>c.classId===12).trees.map(t=>t.n),['Headhunting','Brutality','Ancestry']);
  if(profile==='WoWRemix'){
    const templar=classes.find(c=>c.classId===2);assert.equal(templar.name,'Templar');
    assert.deepEqual(templar.trees.map(t=>t.n),['Divine','Aegis','Judgement']);
    assert.equal(templar.trees.reduce((n,t)=>n+t.t.length,0),71);
    assert(!classes.some(c=>c.classId===5));
  }
  if(profile.startsWith('Season')){
    assert.equal(classes[0].trees.length,30);
    for(const tree of classes[0].trees)assert(tree.t.every(t=>t.sourceRow.ClassGroup===tree.sourceClassGroup));
  }
}
console.log('PASS five realms: names, class isolation, original coordinates, hidden entries, 30 separate Hero trees; '+count+' talent placements checked.');
