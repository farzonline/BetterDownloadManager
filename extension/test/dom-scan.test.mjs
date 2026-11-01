import test from 'node:test';
import assert from 'node:assert/strict';
import { loadScript, FakeDocument, FakeEl } from './fakedom.mjs';

const scanner = () => loadScript('detect/dom-scan.js').BDM.domScan;

test('scan finds video, audio, source and track elements', () => {
  const doc = new FakeDocument();
  doc.body.append(
    new FakeEl('video', { src: 'https://cdn/a.mp4', currentSrc: 'https://cdn/a.mp4' }),
    new FakeEl('audio', { src: 'https://cdn/a.mp3' }),
    new FakeEl('video', { children: [new FakeEl('source', { src: 'https://cdn/b.webm' }), new FakeEl('track', { src: 'https://cdn/b.vtt' })] }),
  );
  const found = scanner().scan(doc);
  assert.deepEqual([...found].map((f) => [f.kind, f.url]), [
    ['video', 'https://cdn/a.mp4'], ['audio', 'https://cdn/a.mp3'], ['video', 'https://cdn/b.webm'], ['subtitle', 'https://cdn/b.vtt'],
  ]);
});

test('blob: and data: sources are skipped (not downloadable from outside the page)', () => {
  const doc = new FakeDocument();
  doc.body.append(new FakeEl('video', { src: 'blob:https://p/abc' }), new FakeEl('video', { src: 'data:video/mp4;base64,AAAA' }), new FakeEl('video', { src: '' }));
  assert.deepEqual([...scanner().scan(doc)], []);
});

test('dom_scan_finds_source_inside_open_shadow_root', () => {
  const doc = new FakeDocument();
  const inner = new FakeEl('div', { children: [new FakeEl('video', { src: 'https://cdn/shadow.mp4' })] });
  const host = new FakeEl('my-player');
  host.shadowRoot = new FakeEl('#shadow-root', { children: [inner] });
  doc.body.append(host);
  assert.deepEqual([...scanner().scan(doc)].map((f) => f.url), ['https://cdn/shadow.mp4']);
});

test('nested shadow roots are searched too', () => {
  const doc = new FakeDocument();
  const innerHost = new FakeEl('inner-player');
  innerHost.shadowRoot = new FakeEl('#shadow-root', { children: [new FakeEl('video', { src: 'https://cdn/deep.mp4' })] });
  const outer = new FakeEl('outer-player');
  outer.shadowRoot = new FakeEl('#shadow-root', { children: [innerHost] });
  doc.body.append(outer);
  assert.deepEqual([...scanner().scan(doc)].map((f) => f.url), ['https://cdn/deep.mp4']);
});

test('elements marked by the EME hook are protected', () => {
  const doc = new FakeDocument();
  const v = new FakeEl('video', { src: 'https://cdn/drm.mp4' });
  v.setAttribute('data-bdm-drm', '1');
  doc.body.append(v);
  assert.equal(scanner().scan(doc)[0].protected, true);
});

test('dom_scan_sees_video_added_after_load', () => {
  const doc = new FakeDocument();
  let observerCb;
  class FakeObserver { constructor(cb) { observerCb = cb; } observe() {} }
  const found = [];
  scanner().observe(doc, (items) => found.push(...items), { MutationObserver: FakeObserver, schedule: (fn) => fn() });
  assert.equal(found.length, 0);
  doc.body.append(new FakeEl('video', { src: 'https://cdn/late.mp4' }));
  observerCb([]);
  assert.deepEqual(found.map((f) => f.url), ['https://cdn/late.mp4']);
  observerCb([]); // nothing new: no duplicate report
  assert.equal(found.length, 1);
});

test('a source that becomes protected is reported again', () => {
  const doc = new FakeDocument();
  const v = new FakeEl('video', { src: 'https://cdn/a.mp4' });
  doc.body.append(v);
  let observerCb;
  class FakeObserver { constructor(cb) { observerCb = cb; } observe() {} }
  const found = [];
  scanner().observe(doc, (items) => found.push(...items), { MutationObserver: FakeObserver, schedule: (fn) => fn() });
  v.setAttribute('data-bdm-drm', '1');
  observerCb([]);
  assert.deepEqual(found.map((f) => !!f.protected), [false, true]);
});
