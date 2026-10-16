'use strict';
const research = window.CRUCIBLE_UX_RESEARCH;
const byId = id => document.getElementById(id);
const sourceMap = new Map(research.sources.map(s => [s[0],s]));
function element(tag,text,className){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(className)n.className=className;return n;}
function refs(ids){const box=element('p',undefined,'refs');for(const id of ids){const source=sourceMap.get(id);if(!source)throw new Error('Missing source '+id);const a=element('a',id+' · '+source[2]);a.href='#'+id;box.append(a);}return box;}
function pair(list,label,text){list.append(element('dt',label),element('dd',text));}
function workflow(w){const box=element('article',undefined,'workflow');box.append(element('h3',w.name),element('blockquote',w.question));const dl=element('dl');pair(dl,'Current handoff',w.current);box.append(dl);const list=element('ol');for(const step of w.steps)list.append(element('li',step));box.append(list);const more=element('dl');pair(more,'Interaction contract',w.details);pair(more,'Failure and recovery',w.failure);pair(more,'Existing foundation / remaining work',w.existing);pair(more,'Completion criterion',w.acceptance);box.append(more,refs(w.sources));return box;}
byId('scope-count').textContent=`${research.comparisons.length} editor families and ${research.sources.length} linked sources were reviewed.`;
const categories=[...new Set(research.comparisons.map(t=>t.category))].sort();for(const category of categories){const o=element('option',category);o.value=category;byId('tool-category').append(o);}
for(const tool of research.comparisons){const detail=element('details');detail.dataset.category=tool.category;detail.dataset.search=JSON.stringify(tool).toLowerCase();const summary=element('summary',tool.name);summary.append(element('small',tool.category));detail.append(summary);const box=element('div',undefined,'detail-content');const dl=element('dl');pair(dl,'Observed in the source',tool.observed);pair(dl,'Candidate for Crucible',tool.adopt);pair(dl,'Do not copy blindly',tool.avoid);pair(dl,'Question to validate',tool.test);box.append(dl,refs(tool.sources));detail.append(box);byId('tools').append(detail);}
function filterTools(){const query=byId('tool-search').value.toLowerCase().trim(),category=byId('tool-category').value;let count=0;for(const card of byId('tools').children){card.hidden=!(card.dataset.search.includes(query)&&(!category||card.dataset.category===category));if(!card.hidden)count++;}byId('tool-count').textContent=`${count} of ${research.comparisons.length} comparisons shown`;}
byId('tool-search').addEventListener('input',filterTools);byId('tool-category').addEventListener('change',filterTools);filterTools();
byId('expand-tools').addEventListener('click',()=>{const cards=[...byId('tools').children].filter(c=>!c.hidden);const expand=cards.some(c=>!c.open);cards.forEach(c=>c.open=expand);byId('expand-tools').textContent=expand?'Collapse comparisons':'Expand all comparisons';});
for(const f of research.findings){const d=element('details');d.open=f.severity==='Critical';const title=element('summary');title.append(element('span',f.severity,'tag'),document.createTextNode(f.id+' · '+f.title));const box=element('div',undefined,'detail-content'),dl=element('dl');pair(dl,'Observed',f.evidence);pair(dl,'Likely consequence',f.impact);pair(dl,'Proposed replacement',f.change);const a=element('a',f.file+':'+f.line);a.href=`https://github.com/Zaelemgaad/WoW-Crucible/blob/${research.baseline}/${f.file}#L${f.line}`;box.append(dl,a,refs(f.sources));d.append(title,box);byId('findings').append(d);}
for(const w of research.workflows){const o=element('option',w.name);o.value=w.id;byId('workflow-select').append(o);byId('all-workflows').append(workflow(w));}
function selectWorkflow(){const w=research.workflows.find(w=>w.id===byId('workflow-select').value);byId('workflow').replaceChildren(workflow(w));}
byId('workflow-select').addEventListener('change',selectWorkflow);selectWorkflow();
const groups=[...new Set(research.requirements.map(r=>r[0]))];for(const group of groups){byId('requirements-list').append(element('h3',group));const list=element('ul');for(const r of research.requirements.filter(r=>r[0]===group)){const li=element('li',r[1]);li.append(refs(r[2].split(' ')));list.append(li);}byId('requirements-list').append(list);}
for(const p of research.implementation){const box=element('article',undefined,'phase');box.append(element('h3',p[0]),element('p',p[1]),element('p',p[2]));byId('phases').append(box);}
for(const t of research.trials){const row=element('tr');for(const v of t)row.append(element('td',v));byId('trial-rows').append(row);}
for(const s of research.sources){const box=element('article',undefined,'source');box.id=s[0];const heading=element('h3');const a=element('a',`${s[0]} · ${s[2]} — ${s[1]}`);a.href=s[3];heading.append(a);box.append(heading,element('p',s[4],'meta'),element('p',s[5]));byId('source-list').append(box);}
function filterSources(){const query=byId('source-search').value.toLowerCase().trim();let count=0;for(const s of byId('source-list').children){s.hidden=!s.textContent.toLowerCase().includes(query);if(!s.hidden)count++;}byId('source-count').textContent=`${count} of ${research.sources.length} sources shown`;}
byId('source-search').addEventListener('input',filterSources);filterSources();
function showLinkedSource(){const id=location.hash.slice(1);if(sourceMap.has(id)){byId('source-search').value='';filterSources();byId(id).scrollIntoView();}}
window.addEventListener('hashchange',showLinkedSource);showLinkedSource();
document.addEventListener('click',event=>{const link=event.target.closest('a[href^="#S"]');if(link&&sourceMap.has(link.hash.slice(1))){byId('source-search').value='';filterSources();}});
for(const [id,property,unit] of [['text-size','--font','px'],['report-width','--width','rem']]){const input=byId(id);input.addEventListener('input',()=>{document.documentElement.style.setProperty(property,input.value+unit);byId(id+'-value').textContent=input.value+' '+unit;});}
let printState;
window.addEventListener('beforeprint',()=>{printState=[...document.querySelectorAll('details')].map(d=>[d,d.open]);printState.forEach(([d])=>d.open=true);});
window.addEventListener('afterprint',()=>{if(printState)printState.forEach(([d,open])=>d.open=open);});
byId('print').addEventListener('click',()=>window.print());
