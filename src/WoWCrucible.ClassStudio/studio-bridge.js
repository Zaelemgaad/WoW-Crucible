(function () {
'use strict';
var connected = location.protocol === 'http:' && location.hostname === '127.0.0.1';
var timer, saving = Promise.resolve();
async function api(route, body) {
  if (!connected) throw new Error('Open Class Studio from Crucible to read local game data.');
  var response = await fetch('api/' + route, body === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  var result = await response.json();
  if (!response.ok) throw new Error(result.error || response.statusText);
  return result;
}
function storage() {
  var result = {};
  Object.keys(localStorage).filter(function (key) { return key.startsWith('twc.'); }).forEach(function (key) { result[key] = localStorage.getItem(key); });
  return result;
}
function changed() {
  if (!connected) return;
  clearTimeout(timer);
  timer = setTimeout(function () {
    var snapshot = storage();
    saving = saving.then(function () { return api('state', snapshot); }).catch(function (error) {
      alert('Crucible could not save the workspace: ' + error.message + '\nExport the workspace before closing.');
    });
  }, 200);
}
var ready = connected ? api('state').then(function (state) {
  if (state) { Object.keys(localStorage).filter(function (k) { return k.startsWith('twc.'); }).forEach(function (k) { localStorage.removeItem(k); });
    Object.keys(state).forEach(function (k) { localStorage.setItem(k, state[k]); }); }
}).catch(function (error) { alert('Workspace restore failed: ' + error.message); }) : Promise.resolve();
window.addEventListener('pagehide', function () {
  if (connected) navigator.sendBeacon('api/state', new Blob([JSON.stringify(storage())], {type:'application/json'}));
});
window.StudioBridge = { connected:connected, api:api, ready:ready, changed:changed, storage:storage };
})();
