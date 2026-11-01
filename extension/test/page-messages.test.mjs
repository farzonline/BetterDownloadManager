import test from 'node:test';
import assert from 'node:assert/strict';
import { createRegistry } from '../src/detect/registry.js';
import { handlePageMessage, stripRangeParams } from '../src/detect/page.js';

const sender = { tab: { id: 4, url: 'https://page/watch', title: 'Watch' }, url: 'https://page/frame', frameId: 2 };
const setup = () => { const registry = createRegistry(); return { registry, handle: (msg, s = sender) => handlePageMessage(msg, s, { registry }) }; };

test('fetch-reported manifest becomes an item attributed to the sender tab and frame', () => {
  const { registry, handle } = setup();
  handle({ type: 'media', via: 'fetch', url: 'https://cdn/master.m3u8', contentType: 'text/plain', bodyPrefix: '#EXTM3U\n' });
  const [it] = registry.list(4);
  assert.equal(it.kind, 'hls');
  assert.equal(it.frameUrl, 'https://page/frame');
  assert.equal(it.pageUrl, 'https://page/watch');
  assert.equal(it.headers.Referer, 'https://page/watch');
  assert.equal(it.via, 'fetch');
});

test('page-reported segments and html are ignored', () => {
  const { registry, handle } = setup();
  handle({ type: 'media', url: 'https://cdn/s.ts', contentType: 'video/mp2t' });
  handle({ type: 'media', url: 'https://cdn/page', contentType: 'text/html' });
  assert.equal(registry.list(4).length, 0);
});

test('mse report becomes a video/audio item pointing at the whole file', () => {
  const { registry, handle } = setup();
  handle({ type: 'mse', mime: 'video/mp4; codecs="avc1"', segmentUrls: ['https://cdn/v.mp4?bytestart=0&byteend=999&efg=v', 'https://cdn/v.mp4?bytestart=1000&byteend=1999&efg=v'] });
  handle({ type: 'mse', mime: 'audio/mp4', segmentUrls: ['https://cdn/a.mp4?bytestart=0&byteend=99&efg=a'] });
  const items = registry.list(4);
  assert.deepEqual(items.map((i) => [i.kind, i.url, i.mse]), [
    ['video', 'https://cdn/v.mp4?efg=v', true], ['audio', 'https://cdn/a.mp4?efg=a', true],
  ]);
});

test('mse report without segment urls adds nothing', () => {
  const { registry, handle } = setup();
  handle({ type: 'mse', mime: 'video/mp4', segmentUrls: [] });
  assert.equal(registry.list(4).length, 0);
});

test('stripRangeParams removes only the range slice parameters', () => {
  assert.equal(stripRangeParams('https://c/v.mp4?a=1&bytestart=5&byteend=9&range=0-9'), 'https://c/v.mp4?a=1');
  assert.equal(stripRangeParams('https://c/v.mp4'), 'https://c/v.mp4');
});

test('dom items are added with their kind and protected flag', () => {
  const { registry, handle } = setup();
  handle({ type: 'dom', items: [{ kind: 'video', url: 'https://cdn/a.mp4', protected: true }, { kind: 'subtitle', url: 'https://cdn/a.vtt' }] });
  assert.deepEqual(registry.list(4).map((i) => [i.kind, i.protected ?? false]), [['video', true], ['subtitle', false]]);
});

test('drm marks the frame\'s items protected', () => {
  const { registry, handle } = setup();
  handle({ type: 'media', url: 'https://cdn/m.mpd', contentType: 'application/dash+xml' });
  registry.add(4, { kind: 'dash', url: 'https://other/m.mpd', frameUrl: 'https://elsewhere/', headers: {} });
  handle({ type: 'drm' });
  const by = Object.fromEntries(registry.list(4).map((i) => [i.url, !!i.protected]));
  assert.deepEqual(by, { 'https://cdn/m.mpd': true, 'https://other/m.mpd': false });
});

test('forged or malformed messages are ignored', () => {
  const { registry, handle } = setup();
  handle({ type: 'media', url: 'javascript:alert(1)', contentType: 'video/mp4' });
  handle({ type: 'media', url: 'file:///c:/x.mp4', contentType: 'video/mp4' });
  handle({ type: 'media', url: 'https://cdn/' + 'a'.repeat(5000), contentType: 'video/mp4' });
  handle({ type: 'media', url: 42, contentType: 'video/mp4' });
  handle({ type: 'nope' });
  handle(null);
  handle({ type: 'media', url: 'https://cdn/v.mp4', contentType: 'video/mp4' }, { url: 'https://x/' }); // no tab
  assert.equal(registry.list(4).length, 0);
});

import { pickBest } from '../src/detect/page.js';

test('pickBest prefers a downloadable stream manifest, then the largest video, skipping DRM and subtitles', () => {
  const v = (kind, url, extra = {}) => ({ kind, url, headers: {}, ...extra });
  assert.equal(pickBest([v('video', 'a', { size: 5 }), v('hls', 'm')]).url, 'm');
  assert.equal(pickBest([v('video', 'a', { size: 5 }), v('video', 'b', { size: 50 })]).url, 'b');
  assert.equal(pickBest([v('dash', 'm', { protected: true }), v('video', 'a', { size: 5 })]).url, 'a');
  assert.equal(pickBest([v('subtitle', 's'), v('dash', 'm', { protected: true })]), undefined);
  assert.equal(pickBest([]), undefined);
});
