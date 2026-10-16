(function () {
'use strict';
function snapAxis(value, extent, offset, viewportSize, others, settings) {
  if(!settings.snap)return Math.max(0,Math.round(value));
  var targets=[offset,offset+viewportSize-extent,offset+(viewportSize-extent)/2];
  others.forEach(function(q){targets.push(q.start,q.start+q.size,q.start-extent,q.start+q.size-extent,q.start+(q.size-extent)/2);});
  var closest=targets.reduce(function(best,t){return Math.abs(t-value)<Math.abs(best-value)?t:best;});
  if(Math.abs(closest-value)<=8/(settings.zoom||1))return Math.max(0,closest);
  return Math.max(0,settings.grid?Math.round((value+extent/2)/settings.step)*settings.step-extent/2:Math.round(value));
}
function bindFloating(node,handle,key) {
  var stored=JSON.parse(localStorage.getItem(key)||'null'), start=null;
  function place(rect) {
    node.style.position='fixed';node.style.left=Math.max(0,Math.min(rect.x,innerWidth-60))+'px';node.style.top=Math.max(0,Math.min(rect.y,innerHeight-40))+'px';
    node.style.width=Math.min(rect.width,innerWidth)+'px';node.style.height=Math.min(rect.height,innerHeight)+'px';node.style.margin='0';
  }
  function save() {if(!node.getClientRects().length)return;var r=node.getBoundingClientRect();localStorage.setItem(key,JSON.stringify({x:r.x,y:r.y,width:r.width,height:r.height}));window.StudioBridge.changed();}
  if(stored)place(stored);
  handle.style.cursor='move';handle.style.touchAction='none';
  handle.addEventListener('pointerdown',function(e){
    if(e.button!==0||e.target.closest('button,input,select,textarea'))return;
    var r=node.getBoundingClientRect();place(r);start={x:e.clientX,y:e.clientY,left:r.x,top:r.y};handle.setPointerCapture(e.pointerId);e.preventDefault();
  });
  handle.addEventListener('pointermove',function(e){
    if(!start)return;var r=node.getBoundingClientRect(), canvas=document.querySelector('.board-canvas');
    var settings=Object.assign({},canvas&&canvas.canvasSettings||{snap:false});
    settings.step=(settings.step||16)*(settings.zoom||100)/100;settings.zoom=1;
    var other=Array.from(document.querySelectorAll('.canvas-board,.studio-window,.editor-panel-inner,.text-modal-inner')).filter(function(n){return n!==node&&n.getClientRects().length;}).map(function(n){return n.getBoundingClientRect();});
    place({x:snapAxis(start.left+e.clientX-start.x,r.width,0,innerWidth,other.map(function(q){return {start:q.x,size:q.width};}),settings),
      y:snapAxis(start.top+e.clientY-start.y,r.height,0,innerHeight,other.map(function(q){return {start:q.y,size:q.height};}),settings),width:r.width,height:r.height});
  });
  handle.addEventListener('lostpointercapture',function(){if(start){start=null;save();}});
  var timer;new ResizeObserver(function(){clearTimeout(timer);timer=setTimeout(save,200);}).observe(node);
}
window.WindowLayout={snapAxis:snapAxis};
document.addEventListener('DOMContentLoaded',function(){window.StudioBridge.ready.then(function(){
  document.querySelectorAll('.studio-window,.editor-panel-inner,.text-modal-inner').forEach(function(n){var handle=n.querySelector('.studio-title,h2');if(handle)bindFloating(n,handle,'twc.window.'+(n.id||n.parentElement.id));});
});});
})();
