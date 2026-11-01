import test from 'node:test';
import assert from 'node:assert/strict';
import { loadScript, FakeDocument, FakeEl } from './fakedom.mjs';

const lib = () => loadScript('detect/float-button.js').BDM.floatButton;

function setup({ settings = { floatingButton: true }, items = true } = {}) {
  const doc = new FakeDocument();
  const clicks = [];
  lib().install({
    document: doc,
    getSettings: async () => settings,
    hasItems: async () => items,
    onClick: () => clicks.push(1),
  });
  const video = new FakeEl('video');
  doc.body.append(video);
  return { doc, video, clicks };
}

// Real events from the browser are trusted; a page script can only make untrusted ones.
const trusted = (extra) => ({ isTrusted: true, ...extra });
const click = (isTrusted) => ({ isTrusted, preventDefault() {}, stopPropagation() {} });

// The button lives inside its host's closed shadow root, out of the page's reach.
const hosts = (doc) => doc.body.children.filter((c) => c.tagName === 'DIV');
const buttons = (doc) => hosts(doc).flatMap((h) => h.closedShadow.children.filter((c) => c.tagName === 'BUTTON'));

test('floating_button_appears_over_playing_video_with_found_item_and_respects_off_toggle', async () => {
  const on = setup();
  await on.doc.fire('play', trusted({ target: on.video }));
  const [btn] = buttons(on.doc);
  assert.match(btn.textContent, /Download this video/);
  assert.equal(btn.style.position, 'fixed');
  assert.equal(btn.style.top, '108px'); // video top (100) + 8
  btn.listeners.click[0](click(true));
  assert.equal(on.clicks.length, 1);

  const off = setup({ settings: { floatingButton: false } });
  await off.doc.fire('play', trusted({ target: off.video }));
  assert.equal(buttons(off.doc).length, 0);
});

test('no button when nothing downloadable was found', async () => {
  const s = setup({ items: false });
  await s.doc.fire('play', trusted({ target: s.video }));
  assert.equal(buttons(s.doc).length, 0);
});

test('button is removed on pause and not duplicated on repeated play', async () => {
  const s = setup();
  await s.doc.fire('play', trusted({ target: s.video }));
  await s.doc.fire('play', trusted({ target: s.video }));
  assert.equal(buttons(s.doc).length, 1);
  assert.equal(hosts(s.doc).length, 1);
  await s.doc.fire('pause', trusted({ target: s.video }));
  assert.equal(buttons(s.doc).length, 0);
  assert.equal(hosts(s.doc).length, 0);
});

test('non-video play events are ignored', async () => {
  const s = setup();
  await s.doc.fire('play', trusted({ target: new FakeEl('audio') }));
  assert.equal(buttons(s.doc).length, 0);
});

test('a play event forged by the page does not bring the button up', async () => {
  const s = setup();
  await s.doc.fire('play', { isTrusted: false, target: s.video });
  assert.deepEqual(s.doc.body.children, [s.video]); // nothing was added to the page
});

test('a click forged by the page does not start a download', async () => {
  const s = setup();
  await s.doc.fire('play', trusted({ target: s.video }));
  const [btn] = buttons(s.doc);
  btn.listeners.click[0](click(false));
  assert.equal(s.clicks.length, 0);
  btn.listeners.click[0](click(true));
  assert.equal(s.clicks.length, 1);
});

test('the button sits in a closed shadow root the page cannot reach into', async () => {
  const s = setup();
  await s.doc.fire('play', trusted({ target: s.video }));
  const [host] = hosts(s.doc);
  assert.equal(host.shadowInit.mode, 'closed');
  assert.equal(host.shadowRoot, null);
  assert.equal(host.children.length, 0); // nothing the page could query or click directly
});
