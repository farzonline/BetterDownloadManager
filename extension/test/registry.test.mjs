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
