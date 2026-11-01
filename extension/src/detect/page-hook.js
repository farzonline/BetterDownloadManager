// Runs in the page's own JavaScript world (content script with "world": "MAIN") at document_start, in every frame.
// It watches how the page itself fetches media, so streams that never show up as a plain URL are still seen:
// fetch/XHR manifests, MediaSource-built (blob:) playback, and DRM use. Everything is wrapped so that no error
// can ever reach the page, and every wrapper calls the original function with the original arguments.
(function (win) {
  'use strict';
  if (win.__bdmHooked) return;
  try { Object.defineProperty(win, '__bdmHooked', { value: true }); } catch (e) { /* frozen window: carry on */ }

  var MANIFEST_OR_MEDIA = /^(video\/|audio\/)/;
  var AMBIGUOUS = ['', 'application/octet-stream', 'binary/octet-stream', 'text/plain', 'text/xml', 'application/xml'];
  var KNOWN = ['application/vnd.apple.mpegurl', 'application/x-mpegurl', 'audio/mpegurl', 'application/dash+xml', 'text/vtt'];
  var recent = []; // last media-looking responses {url, kind}
  var PREFIX_BYTES = 256;

  function post(msg) {
    try { msg.__bdm = 1; win.postMessage(msg, '*'); } catch (e) { /* page may have replaced postMessage */ }
  }

  function typeOf(ct) { return String(ct || '').split(';')[0].trim().toLowerCase(); }
  function isAmbiguous(ct) { return AMBIGUOUS.indexOf(ct) !== -1; }
  function interesting(ct, url) {
    return MANIFEST_OR_MEDIA.test(ct) || KNOWN.indexOf(ct) !== -1 || isAmbiguous(ct) || /\.(m3u8|mpd)(\?|#|$)/i.test(url);
  }
  function needsBody(ct, url) {
    return isAmbiguous(ct) || /^application\/(vnd\.apple\.mpegurl|x-mpegurl|dash\+xml)$/.test(ct) || /\.(m3u8|mpd)(\?|#|$)/i.test(url);
  }
  function mediaClass(ct) {
    if (/^video\//.test(ct)) return 'video';
    if (/^audio\//.test(ct)) return 'audio';
    return isAmbiguous(ct) ? 'any' : 'other';
  }
  function absolute(u) {
    try { return new win.URL(u, win.location && win.location.href).href; } catch (e) { return String(u); }
  }
  function remember(url, ct) {
    var cls = mediaClass(ct);
    if (cls === 'other') return;
    recent.push({ url: url, cls: cls });
    if (recent.length > 30) recent.shift();
  }
  function decode(bytes) {
    try { return new win.TextDecoder('latin1').decode(bytes.slice(0, PREFIX_BYTES)); } catch (e) { return undefined; }
  }

  // Makes a wrapper look like the function it replaces.
  function disguise(wrapper, orig) {
    try {
      Object.defineProperty(wrapper, 'name', { value: orig.name, configurable: true });
      Object.defineProperty(wrapper, 'length', { value: orig.length, configurable: true });
      Object.defineProperty(wrapper, 'toString', {
        value: function toString() { return Function.prototype.toString.call(orig); }, configurable: true, writable: true,
      });
    } catch (e) { /* cosmetic only */ }
    return wrapper;
  }

  function report(url, via, ct, status, bodyPrefix) {
    post({ type: 'media', url: url, via: via, contentType: ct, status: status, bodyPrefix: bodyPrefix });
  }

  // ---- fetch ----
  var origFetch = win.fetch;
  if (typeof origFetch === 'function') {
    win.fetch = disguise(function fetch(input, init) {
      var p = origFetch.apply(this, arguments);
      try {
        var reqUrl = absolute(typeof input === 'string' ? input : (input && input.url) || String(input));
        Promise.resolve(p).then(function (res) {
          try { inspectFetch(res, reqUrl); } catch (e) { /* never surface */ }
        }, function () { /* the page handles its own rejection */ });
      } catch (e) { /* never surface */ }
      return p;
    }, origFetch);
  }

  function inspectFetch(res, reqUrl) {
    if (!res || (!res.ok && res.status !== 206)) return;
    var ct = typeOf(res.headers && res.headers.get('content-type'));
    var url = res.url || reqUrl;
    if (!interesting(ct, url)) return;
    remember(url, ct);
    if (!needsBody(ct, url)) { report(url, 'fetch', ct, res.status); return; }
    // Read only the first chunk of a clone, then cancel it.
    var reader = res.clone().body.getReader();
    reader.read().then(function (r) {
      report(url, 'fetch', ct, res.status, r && r.value ? decode(r.value) : undefined);
      try { reader.cancel(); } catch (e) { /* ignore */ }
    }, function () { report(url, 'fetch', ct, res.status); });
  }

  // ---- XMLHttpRequest ----
  var XHR = win.XMLHttpRequest;
  if (XHR && XHR.prototype) {
    var origOpen = XHR.prototype.open;
    var origSend = XHR.prototype.send;
    XHR.prototype.open = disguise(function open(method, url) {
      try { this.__bdmUrl = absolute(url); } catch (e) { /* ignore */ }
      return origOpen.apply(this, arguments);
    }, origOpen);
    XHR.prototype.send = disguise(function send() {
      try {
        var xhr = this;
        xhr.addEventListener('loadend', function () {
          try { inspectXhr(xhr); } catch (e) { /* never surface */ }
        });
      } catch (e) { /* never surface */ }
      return origSend.apply(this, arguments);
    }, origSend);
  }

  function inspectXhr(xhr) {
    var ct = typeOf(xhr.getResponseHeader('content-type'));
    var url = xhr.responseURL || xhr.__bdmUrl;
    if (!url || !(xhr.status === 200 || xhr.status === 206) || !interesting(ct, url)) return;
    remember(url, ct);
    var prefix;
    if (needsBody(ct, url)) {
      if (xhr.responseType === '' || xhr.responseType === 'text') prefix = String(xhr.responseText || '').slice(0, PREFIX_BYTES);
      else if (xhr.responseType === 'arraybuffer' && xhr.response) prefix = decode(new Uint8Array(xhr.response, 0, Math.min(PREFIX_BYTES, xhr.response.byteLength)));
    }
    report(url, 'xhr', ct, xhr.status, prefix);
  }

  // ---- MediaSource: playback built from appended chunks has no URL of its own ----
  var MS = win.MediaSource;
  if (MS && MS.prototype && typeof MS.prototype.addSourceBuffer === 'function') {
    var origAdd = MS.prototype.addSourceBuffer;
    MS.prototype.addSourceBuffer = disguise(function addSourceBuffer(mime) {
      var sb = origAdd.apply(this, arguments);
      try { hookBuffer(sb, String(mime)); } catch (e) { /* never surface */ }
      return sb;
    }, origAdd);
  }

  function hookBuffer(sb, mime) {
    var origAppend = sb.appendBuffer;
    var reported = false;
    var want = mediaClass(typeOf(mime));
    sb.appendBuffer = disguise(function appendBuffer() {
      if (!reported) {
        reported = true;
        try {
          var urls = recent.filter(function (r) { return r.cls === want || r.cls === 'any'; }).map(function (r) { return r.url; });
          post({ type: 'mse', mime: mime, segmentUrls: urls.slice(-10) });
        } catch (e) { /* never surface */ }
      }
      return origAppend.apply(this, arguments);
    }, origAppend);
  }

  var URLCtor = win.URL;
  if (URLCtor && typeof URLCtor.createObjectURL === 'function') {
    var origCreate = URLCtor.createObjectURL;
    URLCtor.createObjectURL = disguise(function createObjectURL(obj) {
      var u = origCreate.apply(this, arguments);
      try { if (MS && obj instanceof MS) post({ type: 'blob', mediaSource: true, blobUrl: u }); } catch (e) { /* never surface */ }
      return u;
    }, origCreate);
  }

  // ---- DRM (EME): protected media cannot be downloaded; flag it ----
  var HME = win.HTMLMediaElement;
  if (HME && HME.prototype && typeof HME.prototype.setMediaKeys === 'function') {
    var origKeys = HME.prototype.setMediaKeys;
    HME.prototype.setMediaKeys = disguise(function setMediaKeys() {
      try { this.setAttribute('data-bdm-drm', '1'); post({ type: 'drm' }); } catch (e) { /* never surface */ }
      return origKeys.apply(this, arguments);
    }, origKeys);
  }
  var nav = win.navigator;
  if (nav && typeof nav.requestMediaKeySystemAccess === 'function') {
    var origRmksa = nav.requestMediaKeySystemAccess;
    nav.requestMediaKeySystemAccess = disguise(function requestMediaKeySystemAccess(keySystem) {
      try { post({ type: 'drm', keySystem: String(keySystem) }); } catch (e) { /* never surface */ }
      return origRmksa.apply(this, arguments);
    }, origRmksa);
  }
})(typeof window !== 'undefined' ? window : globalThis);
