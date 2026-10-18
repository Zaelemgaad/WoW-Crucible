(function(){
'use strict';
var layer='server',cache=new Map(),generation=0;
function needsData(text){return /\$(?:\{|<|\?|@|[A-Za-z]|\d+[A-Za-z])|@[A-Za-z]+:|\|(?:c[0-9a-fA-F]|[rHTAhtn])/.test(text);}
window.StudioDescriptions={
  setLayer:function(value){layer=value;generation++;cache.clear();},
  render:function(text,id,sourceLayer){
    text=String(text||'');if(!needsData(text))return Promise.resolve(text);
    if(!StudioBridge.connected)return Promise.resolve('Connect spell data in Crucible to preview this description.');
    var request={text:text,spellId:Number(id)||0,layer:sourceLayer||layer},key=JSON.stringify([generation,request]);
    if(!cache.has(key))cache.set(key,StudioBridge.api('tooltip',request).then(function(result){
      if(needsData(result.text))throw new Error('The description renderer returned unresolved instructions.');
      return result.text;
    }).catch(function(error){cache.delete(key);throw error;}));
    return cache.get(key);
  }
};
})();
