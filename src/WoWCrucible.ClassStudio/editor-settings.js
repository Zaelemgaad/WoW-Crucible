(function () {
'use strict';
document.addEventListener('DOMContentLoaded', function () {
  StudioBridge.ready.then(function () {
    var button=document.getElementById('studioSettings'), panel=document.getElementById('studioSettingsPanel');
    function show(visible) {
      panel.hidden=!visible;button.setAttribute('aria-expanded',String(visible));
      localStorage.setItem('twc.settingsVisible',JSON.stringify(visible));StudioBridge.changed();
    }
    show(JSON.parse(localStorage.getItem('twc.settingsVisible')||'false'));
    button.onclick=function(){show(panel.hidden);};
    document.addEventListener('keydown',function(e){if(e.key==='Escape'&&!panel.hidden){show(false);button.focus();}});
  });
});
})();
