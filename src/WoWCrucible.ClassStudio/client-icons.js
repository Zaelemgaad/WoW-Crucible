(function(root){
'use strict';
var cache=new Map(),pending=new Map(),scheduled=false;
async function flush(){
  scheduled=false;var batch=Array.from(pending.entries()).slice(0,512);
  batch.forEach(function(p){pending.delete(p[0]);});
  try {var icons=await StudioBridge.api('icons',batch.map(function(p){return p[0];}));batch.forEach(function(p){p[1].resolve(icons[p[0]]||null);});}
  catch(error){batch.forEach(function(p){cache.delete(p[0]);p[1].reject(error);});console.error('Client icon read failed',error);}
  if(pending.size&&!scheduled){scheduled=true;queueMicrotask(flush);}
}
function get(name){
  if(!cache.has(name))cache.set(name,new Promise(function(resolve,reject){pending.set(name,{resolve:resolve,reject:reject});if(!scheduled){scheduled=true;queueMicrotask(flush);}}));
  return cache.get(name);
}
root.StudioIcons={ready:function(){return Promise.all(Array.from(cache.values()));},reset:function(){cache.clear();},apply:function(img,t,disabled){
  img.style.filter=disabled?'grayscale(1)':'';
  var fallback='icons/'+t.iconname+'.png';
  img.onerror=function(){img.onerror=null;img.src='icons/_placeholder.png';};
  if(!t.sourceLayer){img.src=fallback;return;}
  img.dataset.clientIcon=t.iconname;img.dataset.iconStatus='loading';
  img.src='icons/_placeholder.png';
  get(t.iconname).then(function(src){img.dataset.iconStatus=src?'ready':'missing';if(src)img.src=src;else img.title='Icon missing from the selected client';},function(){img.dataset.iconStatus='error';img.title='Client icon could not be read';});
}};
})(window);
