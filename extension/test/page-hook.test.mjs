import test from 'node:test';
import assert from 'node:assert/strict';
import { loadScript, settle, FakeEl } from './fakedom.mjs';

const enc = (s) => new TextEncoder().encode(s);

function fakeRes(ct, body = '', url = 'https://cdn/x') {
  const res = {
    ok: true, status: 200, url,
    headers: { get: (n) => (n.toLowerCase() === 'content-type' ? ct : null) },
    clone: () => ({ body: { getReader: () => ({ read: async () => ({ value: enc(body) }), cancel: async () => {} }) } }),
  };
  return res;
}

const makeXHR = () => class FakeXHR {
  constructor() { this.listeners = {}; this.status = 200; this.responseType = ''; this.responseText = ''; this.responseURL = ''; this.headers = {}; }
  open(method, url) { this.url = url; }
  send() {}
  addEventListener(t, fn) { this.listeners[t] = fn; }
  getResponseHeader(n) { return this.headers[n.toLowerCase()] ?? null; }
  finish() { this.listeners.loadend?.(); }
};

const makeMediaSource = () => class FakeMediaSource {
  addSourceBuffer(mime) { return { mime, appendBuffer() { return 'appended'; } }; }
};

function install(extra = {}) {
  const posted = [];
  const origFetch = async () => fakeRes('application/vnd.apple.mpegurl', '#EXTM3U\n#EXT-X-VERSION:3');
  const win = loadScript('detect/page-hook.js', {
    postMessage: (m) => posted.push(m),
    fetch: origFetch,
    XMLHttpRequest: makeXHR(),
    MediaSource: makeMediaSource(),
    URL: class { static createObjectURL() { return 'blob:https://p/abc'; } },
    HTMLMediaElement: class FakeMediaElement { setMediaKeys() { return 'keys-set'; } setAttribute(n, v) { (this.attrs ??= {})[n] = v; } },
    navigator: {},
    ...extra,
  });
  return { win, posted, origFetch };
}

test('fetch_wrapper_reports_m3u8_response_and_still_returns_original_response', async () => {
  const res = fakeRes('application/vnd.apple.mpegurl', '#EXTM3U\n#EXT-X-VERSION:3', 'https://cdn/master.m3u8');
  const { win, posted } = install({ fetch: async () => res });
  const got = await win.fetch('https://cdn/master.m3u8');
  assert.equal(got, res);
  await settle();
  const m = posted.find((p) => p.type === 'media');
  assert.equal(m.__bdm, 1);
  assert.equal(m.via, 'fetch');
  assert.equal(m.url, 'https://cdn/master.m3u8');
  assert.match(m.bodyPrefix, /^#EXTM3U/);
});

test('fetch of a definite media type reports without reading the body', async () => {
  let cloned = false;
  const res = { ...fakeRes('video/mp4', '', 'https://cdn/v.mp4'), clone() { cloned = true; return null; } };
  const { win, posted } = install({ fetch: async () => res });
  await win.fetch('https://cdn/v.mp4');
  await settle();
  assert.equal(cloned, false);
  assert.equal(posted.find((p) => p.type === 'media').contentType, 'video/mp4');
});

test('fetch of json is not reported', async () => {
  const { win, posted } = install({ fetch: async () => fakeRes('application/json', '{}') });
  await win.fetch('https://cdn/api');
  await settle();
  assert.equal(posted.length, 0);
});

test('xhr_wrapper_reports_mpd', async () => {
  const { win, posted } = install();
  const x = new win.XMLHttpRequest();
  x.open('GET', 'https://cdn/manifest.mpd');
  x.send();
  x.headers['content-type'] = 'application/dash+xml';
  x.responseText = '<?xml version="1.0"?><MPD xmlns="urn:mpeg:dash:schema:mpd:2011">';
  x.responseURL = 'https://cdn/manifest.mpd';
  x.finish();
  await settle();
  const m = posted.find((p) => p.type === 'media');
  assert.equal(m.via, 'xhr');
  assert.equal(m.url, 'https://cdn/manifest.mpd');
  assert.match(m.bodyPrefix, /<MPD/);
});

test('mse_appendBuffer_links_blob_to_recent_segment_urls', async () => {
  const bodies = { 'https://cdn/v1.m4s': 'video/mp4', 'https://cdn/a1.m4s': 'audio/mp4' };
  const { win, posted } = install({ fetch: async (u) => fakeRes(bodies[u], '', u) });
  await win.fetch('https://cdn/v1.m4s');
  await win.fetch('https://cdn/a1.m4s');
  await settle();
  const ms = new win.MediaSource();
  const video = ms.addSourceBuffer('video/mp4; codecs="avc1.64001f"');
  assert.equal(video.appendBuffer(new Uint8Array(4)), 'appended', 'original appendBuffer still runs');
  video.appendBuffer(new Uint8Array(4));
  const mse = posted.filter((p) => p.type === 'mse');
  assert.equal(mse.length, 1, 'reported once per buffer');
  assert.deepEqual([...mse[0].segmentUrls], ['https://cdn/v1.m4s']);
  assert.match(mse[0].mime, /^video\/mp4/);
});

test('createObjectURL_blob_with_MediaSource_not_reported_as_file', () => {
  const { win, posted } = install();
  const url = win.URL.createObjectURL(new win.MediaSource());
  assert.equal(url, 'blob:https://p/abc');
  assert.equal(posted.some((p) => p.type === 'media'), false);
  assert.equal(posted.find((p) => p.type === 'blob').mediaSource, true);
});

test('hook_never_throws_into_page', async () => {
  const res = { ...fakeRes('application/octet-stream', 'x'), headers: { get: () => { throw new Error('boom'); } } };
  const { win } = install({ fetch: async () => res, postMessage: () => { throw new Error('post blocked'); } });
  assert.equal(await win.fetch('https://cdn/x'), res);
  await settle();
  const x = new win.XMLHttpRequest();
  x.open('GET', 'https://cdn/y');
  x.send();
  assert.doesNotThrow(() => x.finish());
  const ms = new win.MediaSource();
  assert.doesNotThrow(() => ms.addSourceBuffer('video/mp4').appendBuffer(new Uint8Array(1)));
});

test('wrapped functions keep their native-looking shape', () => {
  const { win, origFetch } = install();
  assert.equal(String(win.fetch), String(origFetch));
  assert.equal(win.fetch.name, origFetch.name);
});

test('hook installs only once per window', () => {
  const { win } = install();
  const first = win.fetch;
  const code = new URL('../src/detect/page-hook.js', import.meta.url);
  // running the script again in the same window must not wrap again
  return import('node:fs').then(({ readFileSync }) =>
    import('node:vm').then((vm) => {
      vm.runInContext(readFileSync(code, 'utf8'), win);
      assert.equal(win.fetch, first);
    }));
});

test('drm_element_marked_protected', () => {
  const { win, posted } = install();
  const el = new win.HTMLMediaElement();
  assert.equal(el.setMediaKeys({}), 'keys-set');
  assert.equal(el.attrs['data-bdm-drm'], '1');
  assert.equal(posted.some((p) => p.type === 'drm'), true);
});

test('requestMediaKeySystemAccess is reported as drm', async () => {
  const navigator = { requestMediaKeySystemAccess: async () => 'access' };
  const { win, posted } = install({ navigator });
  assert.equal(await win.navigator.requestMediaKeySystemAccess('com.widevine.alpha', []), 'access');
  assert.equal(posted.find((p) => p.type === 'drm').keySystem, 'com.widevine.alpha');
});

test('unused FakeEl export stays importable', () => assert.ok(FakeEl));
