// Isolated-world glue: relays what the page hook saw to the background worker, runs the DOM scan, and wires the
// floating button. Loaded after dom-scan.js and float-button.js.
(function () {
  'use strict';
  var send = function (msg) { try { chrome.runtime.sendMessage(msg).catch(function () {}); } catch (e) { /* extension reloaded */ } };

  addEventListener('message', function (e) {
    if (e.source !== window || !e.data || e.data.__bdm !== 1) return;
    var d = e.data;
    send({ type: 'page', payload: { type: d.type, via: d.via, url: d.url, contentType: d.contentType, status: d.status,
      bodyPrefix: d.bodyPrefix, mime: d.mime, segmentUrls: d.segmentUrls, keySystem: d.keySystem, mediaSource: d.mediaSource } });
  });

  function start() {
    BDM.domScan.observe(document, function (items) { send({ type: 'page', payload: { type: 'dom', items: items } }); });
    BDM.floatButton.install({
      document: document,
      getSettings: function () { return chrome.storage.sync.get('settings').then(function (r) { return Object.assign({ floatingButton: true }, r.settings); }); },
      hasItems: function () { return chrome.runtime.sendMessage({ type: 'has-items' }).then(function (r) { return !!(r && r.has); }, function () { return false; }); },
      onClick: function () { send({ type: 'download-best' }); },
    });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
