import test from 'node:test';
import assert from 'node:assert/strict';
import { createRegistry, dedupeKey } from '../src/detect/registry.js';

const item = (url, extra = {}) => ({ kind: 'video', url, pageUrl: 'https://p/', title: 't', headers: {}, ...extra });

test('same url twice is one item', () => {
  const r = createRegistry();
  assert.equal(r.add(1, item('https://x/a.mp4')).isNew, true);
  assert.equal(r.add(1, item('https://x/a.mp4')).isNew, false);
  assert.equal(r.list(1).length, 1);
});

test('signature params are ignored for dedupe and the newest url wins', () => {
  const r = createRegistry();
  r.add(1, item('https://x/a.mp4?token=old&id=5'));
  r.add(1, item('https://x/a.mp4?id=5&token=new'));
  assert.equal(r.list(1).length, 1);
  assert.equal(r.list(1)[0].url, 'https://x/a.mp4?id=5&token=new');
});

test('different non-signature params are different items', () => {
  assert.notEqual(dedupeKey('https://x/a.mp4?id=1'), dedupeKey('https://x/a.mp4?id=2'));
  assert.equal(dedupeKey('https://x/a.mp4#frag'), dedupeKey('https://x/a.mp4'));
});

test('tabs are isolated', () => {
  const r = createRegistry();
  r.add(1, item('https://x/a.mp4'));
  r.add(2, item('https://x/b.mp4'));
  assert.deepEqual(r.list(1).map((i) => i.url), ['https://x/a.mp4']);
  assert.deepEqual(r.list(2).map((i) => i.url), ['https://x/b.mp4']);
});

test('clear empties a tab only', () => {
  const r = createRegistry();
  r.add(1, item('https://x/a.mp4'));
  r.add(2, item('https://x/b.mp4'));
  r.clear(1);
  assert.equal(r.list(1).length, 0);
  assert.equal(r.list(2).length, 1);
});

test('onChange reports the count per tab', () => {
  const seen = [];
  const r = createRegistry({ onChange: (tabId, count) => seen.push([tabId, count]) });
  r.add(7, item('https://x/a.mp4'));
  r.add(7, item('https://x/b.mp4'));
  r.clear(7);
  assert.deepEqual(seen, [[7, 1], [7, 2], [7, 0]]);
});

test('items get unique ids and can be fetched by id', () => {
  const r = createRegistry();
  const a = r.add(1, item('https://x/a.mp4')).item;
  const b = r.add(1, item('https://x/b.mp4')).item;
  assert.notEqual(a.id, b.id);
  assert.equal(r.get(1, b.id).url, 'https://x/b.mp4');
});

test('protected flag sticks once set', () => {
  const r = createRegistry();
  r.add(1, item('https://x/a.mpd', { kind: 'dash' }));
  r.add(1, item('https://x/a.mpd', { kind: 'dash', protected: true }));
  assert.equal(r.list(1)[0].protected, true);
});

test('load restores a persisted list and still dedupes against it', () => {
  const r = createRegistry();
  r.load(3, [{ id: '9', kind: 'video', url: 'https://x/a.mp4?token=1', headers: {} }]);
  assert.equal(r.list(3).length, 1);
  assert.equal(r.add(3, item('https://x/a.mp4?token=2')).isNew, false);
  assert.equal(r.add(3, item('https://x/b.mp4')).item.id !== '9', true);
  assert.deepEqual(Object.keys(r.snapshot()), ['3']);
});

test('markProtected flags only the matching frame\'s items', () => {
  const r = createRegistry();
  r.add(1, item('https://x/a.mpd', { kind: 'dash', frameUrl: 'https://f/one#a' }));
  r.add(1, item('https://x/b.mpd', { kind: 'dash', frameUrl: 'https://f/two' }));
  r.markProtected(1, 'https://f/one#zzz');
  assert.deepEqual(r.list(1).map((i) => !!i.protected), [true, false]);
});

test('a later report with fewer headers does not erase the headers already captured (the cookie that unlocks a signed url)', () => {
  const r = createRegistry();
  r.add(1, item('https://x/a.mpd?exp=1&sig=ok', { kind: 'dash', headers: { Cookie: 'sessionid=abc', Referer: 'https://p/1' } }));
  r.add(1, item('https://x/a.mpd?exp=2&sig=ok', { kind: 'dash', headers: { Referer: 'https://p/2' }, size: undefined, title: undefined }));
  const [it] = r.list(1);
  assert.equal(it.url, 'https://x/a.mpd?exp=2&sig=ok'); // newest signed url
  assert.equal(it.headers.Cookie, 'sessionid=abc'); // survives
  assert.equal(it.headers.Referer, 'https://p/2'); // newest value wins per header
  assert.equal(it.title, 't'); // an undefined field never erases a known one
});

test('an item reported after its frame signalled DRM is protected from the start', () => {
  const r = createRegistry();
  r.markProtected(1, 'https://site/page#x'); // EME was set up first
  r.add(1, item('https://site/m.mpd', { kind: 'dash', frameUrl: 'https://site/page' }));
  r.add(1, item('https://site/other.mpd', { kind: 'dash', frameUrl: 'https://elsewhere/frame' }));
  assert.deepEqual(r.list(1).map((i) => !!i.protected), [true, false]);
});

test('a drm signal without a frame url protects everything in the tab, including later items; clear forgets it', () => {
  const r = createRegistry();
  r.markProtected(2, undefined);
  r.add(2, item('https://site/m.mpd', { kind: 'dash', frameUrl: 'https://any/' }));
  assert.equal(r.list(2)[0].protected, true);
  r.clear(2);
  r.add(2, item('https://site/m2.mpd', { kind: 'dash', frameUrl: 'https://any/' }));
  assert.equal(!!r.list(2)[0].protected, false);
});
