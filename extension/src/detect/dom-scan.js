// Isolated-world helper: finds <video>/<audio>/<source>/<track> elements, including inside open shadow roots,
// and watches for ones added later. Classic script (content scripts cannot be modules); exposes BDM.domScan.
(function (g) {
  'use strict';
  var BDM = (g.BDM = g.BDM || {});
  var SELECTOR = 'video,audio,source,track';

  function collectRoots(root) {
    var roots = [root];
    var all = root.querySelectorAll ? root.querySelectorAll('*') : [];
    for (var i = 0; i < all.length; i++) {
      if (all[i].shadowRoot) roots = roots.concat(collectRoots(all[i].shadowRoot));
    }
    return roots;
  }

  function tagOf(el) { return String(el.tagName || '').toLowerCase(); }

  function itemFor(el) {
    var tag = tagOf(el);
    var url = tag === 'source' || tag === 'track' ? el.src : (el.currentSrc || el.src);
    if (!url || /^(blob|data):/i.test(url)) return null; // not reachable from outside the page
    var parentTag = el.parentElement ? tagOf(el.parentElement) : '';
    var kind = tag === 'track' ? 'subtitle' : tag === 'audio' || (tag === 'source' && parentTag === 'audio') ? 'audio' : 'video';
    var drm = (el.getAttribute && el.getAttribute('data-bdm-drm') === '1') ||
      (el.parentElement && el.parentElement.getAttribute && el.parentElement.getAttribute('data-bdm-drm') === '1');
    return { kind: kind, url: url, protected: !!drm };
  }

  function scan(root) {
    var out = [];
    var seen = {};
    var roots = collectRoots(root);
    for (var r = 0; r < roots.length; r++) {
      var els = roots[r].querySelectorAll(SELECTOR);
      for (var i = 0; i < els.length; i++) {
        var item = itemFor(els[i]);
        if (item && !seen[item.url]) { seen[item.url] = true; out.push(item); }
      }
    }
    return out;
  }

  /** Reports every element once (again if it later turns out to be DRM-protected). */
  function observe(doc, onFound, opts) {
    opts = opts || {};
    var Obs = opts.MutationObserver || g.MutationObserver;
    var schedule = opts.schedule || function (fn) { setTimeout(fn, 150); };
    var seen = {};
    var pending = false;
    function run() {
      pending = false;
      var fresh = scan(doc).filter(function (i) {
        var key = i.url + '|' + i.protected;
        if (seen[key]) return false;
        seen[key] = true;
        return true;
      });
      if (fresh.length) onFound(fresh);
    }
    run();
    new Obs(function () {
      if (!pending) { pending = true; schedule(run); }
    }).observe(doc.documentElement || doc, { childList: true, subtree: true, attributes: true, attributeFilter: ['src', 'data-bdm-drm'] });
  }

  BDM.domScan = { scan: scan, observe: observe };
})(typeof window !== 'undefined' ? window : globalThis);
