import test from 'node:test';
import assert from 'node:assert/strict';
import { createRegistry } from '../src/detect/registry.js';
import { createNetworkDetector } from '../src/detect/network.js';

const enc = (s) => new TextEncoder().encode(s);
const hdr = (name, value) => ({ name, value });

function setup({ probeBytes } = {}) {
  const registry = createRegistry();
  const probes = [];
  const det = createNetworkDetector({
    registry,
    getTab: () => ({ url: 'https://page/', title: 'My Page' }),
    probe: async (url) => { probes.push(url); return probeBytes; },
    selfOrigin: 'chrome-extension://abc',
  });
  return { registry, det, probes };
}

const sent = (det, over = {}) => det.onSendHeaders({ requestId: 'r1', requestHeaders: [hdr('Referer', 'https://page/'), hdr('Cookie', 'a=b'), hdr('X-Junk', 'z')], ...over });
const recv = (det, over = {}) => det.onHeadersReceived({
  requestId: 'r1', tabId: 1, url: 'https://cdn/x', statusCode: 200, initiator: 'https://page',
  responseHeaders: [hdr('Content-Type', 'application/vnd.apple.mpegurl')], ...over,
});

test('manifest response becomes an item with captured request headers', async () => {
  const { registry, det } = setup();
  sent(det);
  await recv(det);
  const [it] = registry.list(1);
  assert.equal(it.kind, 'hls');
  assert.equal(it.pageUrl, 'https://page/');
  assert.equal(it.title, 'My Page');
  assert.equal(it.headers.Referer, 'https://page/');
  assert.equal(it.headers.Cookie, 'a=b');
  assert.equal(it.headers['X-Junk'], undefined);
});

test('segments, html and error responses are ignored', async () => {
  const { registry, det } = setup();
  await recv(det, { responseHeaders: [hdr('Content-Type', 'video/mp2t')], url: 'https://cdn/s1.ts' });
  await recv(det, { responseHeaders: [hdr('Content-Type', 'text/html')] });
  await recv(det, { statusCode: 404 });
  assert.equal(registry.list(1).length, 0);
});

test('requests without a tab and the extension\'s own probes are ignored', async () => {
  const { registry, det } = setup();
  await recv(det, { tabId: -1 });
  await recv(det, { initiator: 'chrome-extension://abc' });
  assert.equal(registry.list(1).length, 0);
});

test('ambiguous content type is probed once and classified by bytes', async () => {
  const { registry, det, probes } = setup({ probeBytes: enc('#EXTM3U\n') });
  const r = { responseHeaders: [hdr('Content-Type', 'application/octet-stream')], url: 'https://cdn/play?id=1' };
  await recv(det, r);
  await recv(det, { ...r, requestId: 'r2' });
  assert.equal(registry.list(1)[0].kind, 'hls');
  assert.equal(probes.length, 1);
});

test('content-length becomes size', async () => {
  const { registry, det } = setup();
  await recv(det, { responseHeaders: [hdr('Content-Type', 'video/mp4'), hdr('Content-Length', '1234')], url: 'https://cdn/v' });
  assert.equal(registry.list(1)[0].size, 1234);
});

test('partial responses report the full size from Content-Range', async () => {
  const { registry, det } = setup();
  await recv(det, { statusCode: 206, responseHeaders: [hdr('Content-Type', 'video/mp4'), hdr('Content-Range', 'bytes 0-99/5000')], url: 'https://cdn/v' });
  assert.equal(registry.list(1)[0].size, 5000);
});

test('service-worker traffic (tabId -1) is attributed to tabs of the initiator origin', async () => {
  const registry = createRegistry();
  const det = createNetworkDetector({
    registry,
    getTab: (id) => ({ url: `https://site.example/tab${id}`, title: `T${id}` }),
    getTabsByOrigin: (origin) => (origin === 'https://site.example' ? [5, 6] : []),
    probe: async () => undefined,
    selfOrigin: 'chrome-extension://abc',
  });
  await det.onHeadersReceived({
    requestId: 'sw1', tabId: -1, url: 'https://cdn/m.m3u8', statusCode: 200, initiator: 'https://site.example',
    responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }],
  });
  await det.onHeadersReceived({
    requestId: 'sw2', tabId: -1, url: 'https://cdn/m2.m3u8', statusCode: 200, initiator: 'https://unrelated.example',
    responseHeaders: [{ name: 'Content-Type', value: 'application/vnd.apple.mpegurl' }],
  });
  assert.equal(registry.list(5).length, 1);
  assert.equal(registry.list(6).length, 1);
  assert.equal(registry.list(5)[0].pageUrl, 'https://site.example/tab5');
});
