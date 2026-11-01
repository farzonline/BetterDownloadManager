// Isolated-world helper: IDM-style "Download this video" button over a playing <video>. Exposes BDM.floatButton.
(function (g) {
  'use strict';
  var BDM = (g.BDM = g.BDM || {});

  function install(opts) {
    var doc = opts.document;
    var button = null;
    var current = null;

    function place() {
      if (!button || !current) return;
      var rect = current.getBoundingClientRect();
      button.style.top = rect.top + 8 + 'px';
      button.style.left = Math.max(0, rect.right - 168) + 'px';
    }
    function remove() {
      if (button) button.remove();
      button = null;
      current = null;
    }
    function show(video) {
      if (button && current === video) { place(); return; }
      remove();
      current = video;
      button = doc.createElement('button');
      button.textContent = 'Download this video';
      button.style.cssText = '';
      button.style.position = 'fixed';
      button.style.zIndex = '2147483647';
      button.style.padding = '6px 10px';
      button.style.font = '13px system-ui, sans-serif';
      button.style.color = '#fff';
      button.style.background = '#16a34a';
      button.style.border = 'none';
      button.style.borderRadius = '6px';
      button.style.cursor = 'pointer';
      button.style.opacity = '0.92';
      button.addEventListener('click', function (e) {
        e.preventDefault();
        e.stopPropagation();
        opts.onClick();
      });
      (doc.body || doc.documentElement).appendChild(button);
      place();
    }

    doc.addEventListener('play', function (e) {
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
