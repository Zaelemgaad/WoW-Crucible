(function () {
'use strict';
var native = window.chrome && window.chrome.webview;
var connected = !!native, nextId = 0, pending = new Map(), timer, restored = !connected;
if (native) native.addEventListener('message', function (event) {
  var message = event.data, call = pending.get(message.id);
  if (!call) return;
  pending.delete(message.id);
  if (message.error) call.reject(new Error(message.error)); else call.resolve(message.result);
});
function api(route, body) {
  if (!connected) return Promise.reject(new Error('Open this editor inside Crucible to read local game data.'));
  return new Promise(function (resolve, reject) {
    var id = ++nextId, message = {id:id,route:route};
    if (body !== undefined) message.body = body;
    pending.set(id,{resolve:resolve,reject:reject});
    try { native.postMessage(message); } catch (error) { pending.delete(id); reject(error); }
  });
}
function storage() {
  var result = {};
  Object.keys(localStorage).filter(function (key) { return key.startsWith('twc.'); }).forEach(function (key) { result[key] = localStorage.getItem(key); });
  return result;
}
function prepareClose() { clearTimeout(timer); return restored ? JSON.stringify(storage()) : null; }
function changed() {
  if (!connected || !restored) return;
  clearTimeout(timer);
  timer = setTimeout(function () {
    api('state', storage()).catch(function (error) {
      alert('Crucible could not save the workspace: ' + error.message + '\nExport the workspace before closing.');
    });
  }, 200);
}
var ready = connected ? api('state').then(function (state) {
  if (state) { Object.keys(localStorage).filter(function (k) { return k.startsWith('twc.'); }).forEach(function (k) { localStorage.removeItem(k); });
    Object.keys(state).forEach(function (k) { localStorage.setItem(k, state[k]); }); }
  restored = true;
}) : Promise.resolve();
ready.catch(function (error) { alert('Workspace restore failed: ' + error.message + '\nYour saved workspace has not been replaced.'); });
function download(name, text, type) {
  if (connected) return api('export', {name:name,text:text}).catch(function (error) { alert('Export failed: ' + error.message); });
  var a = document.createElement('a'), url = URL.createObjectURL(new Blob([text], {type:type || 'application/json'}));
  a.href = url; a.download = name; a.click(); setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
}
window.StudioBridge = {connected:connected,api:api,ready:ready,changed:changed,storage:storage,prepareClose:prepareClose,download:download};
})();
