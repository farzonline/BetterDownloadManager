// Isolated-world helper: IDM-style "Download this video" button over a playing <video>. Exposes BDM.floatButton.
(function (g) {
  'use strict';
  var BDM = (g.BDM = g.BDM || {});
  var GRADIENT = 'linear-gradient(135deg, #6d5df6, #4f46e5)';

  function install(opts) {
    var doc = opts.document;
    var host = null;
    var button = null;
    var current = null;

    function place() {
      if (!button || !current) return;
      var rect = current.getBoundingClientRect();
      button.style.top = rect.top + 8 + 'px';
      button.style.left = Math.max(0, rect.right - 168) + 'px';
    }
    function remove() {
      if (host) host.remove();
      host = null;
      button = null;
      current = null;
    }
    function show(video) {
      if (button && current === video) { place(); return; }
      remove();
      current = video;
      // A closed shadow root keeps the page's scripts from finding, restyling or clicking the button.
      host = doc.createElement('div');
      var root = host.attachShadow ? host.attachShadow({ mode: 'closed' }) : host;
      button = doc.createElement('button');
      button.textContent = 'Download this video';
      button.style.cssText = '';
      button.style.position = 'fixed';
      button.style.zIndex = '2147483647';
      button.style.padding = '7px 12px';
      button.style.font = '13px system-ui, sans-serif';
      button.style.color = '#fff';
      button.style.background = GRADIENT;
      button.style.border = 'none';
      button.style.borderRadius = '10px';
      button.style.boxShadow = '0 4px 14px rgba(79, 70, 229, 0.5)';
      button.style.fontWeight = '600';
      button.style.cursor = 'pointer';
      button.style.opacity = '0.92';
      var label = 'Download this video';
      var busy = false;
      button.style.maxWidth = '320px';
      button.style.whiteSpace = 'nowrap';
      button.style.overflow = 'hidden';
      button.style.textOverflow = 'ellipsis';
      var paint = function (text, background) { button.textContent = text; button.title = text; button.style.background = background; };
      button.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        if (!e.isTrusted || busy) return; // a click the page made up must not start a download
        busy = true;
        var self = button;
        paint('Sending to BDM…', GRADIENT);
        self.style.cursor = 'progress';
        Promise.resolve(opts.onClick()).then(function (r) { return r || { ok: false }; }, function (err) { return { ok: false, error: String(err) }; })
          .then(function (r) {
            if (self !== button) return; // the button was removed meanwhile
            if (r.ok) paint(r.viaBrowser ? '✓ Downloading in your browser' : '✓ Added to BDM', 'linear-gradient(135deg, #10b981, #059669)');
            else paint('✕ ' + (r.error || 'BDM did not take it'), 'linear-gradient(135deg, #f43f5e, #be123c)');
            setTimeout(function () {
              busy = false;
              if (self !== button) return;
              paint(label, GRADIENT);
              self.style.cursor = 'pointer';
            }, opts.resetMs == null ? 3000 : opts.resetMs);
          });
      });
      // Sinks while pressed and springs back, so the click is felt even before BDM answers.
      button.style.transition = 'transform .28s cubic-bezier(.34, 1.8, .64, 1), background .2s';
      var sink = function () { if (!busy) { button.style.transitionDuration = '.07s, .2s'; button.style.transform = 'scale(.92)'; } };
      var lift = function () { button.style.transitionDuration = '.28s, .2s'; button.style.transform = ''; };
      button.addEventListener('pointerdown', sink);
      button.addEventListener('pointerup', lift);
      button.addEventListener('pointerleave', lift);
      root.appendChild(button);
      (doc.body || doc.documentElement).appendChild(host);
      place();
    }

    doc.addEventListener('play', function (e) {
      if (!e.isTrusted) return; // a page can fake a play event to make the button appear
      var video = e.target;
      if (!video || String(video.tagName).toLowerCase() !== 'video') return;
      return Promise.all([opts.getSettings(), opts.hasItems()]).then(function (r) {
        if (r[0] && r[0].floatingButton && r[1]) show(video);
      });
    }, true);
    function stop(e) { if (e.target === current) remove(); }
    doc.addEventListener('pause', stop, true);
    doc.addEventListener('ended', stop, true);
    doc.addEventListener('scroll', place, true);
    if (g.addEventListener) g.addEventListener('resize', place);
  }

  BDM.floatButton = { install: install };
})(typeof window !== 'undefined' ? window : globalThis);
