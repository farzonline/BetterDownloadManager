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

const buttons = (doc) => doc.body.children.filter((c) => c.tagName === 'BUTTON');

test('floating_button_appears_over_playing_video_with_found_item_and_respects_off_toggle', async () => {
  const on = setup();
  await on.doc.fire('play', { target: on.video });
  const [btn] = buttons(on.doc);
  assert.match(btn.textContent, /Download this video/);
  assert.equal(btn.style.position, 'fixed');
  assert.equal(btn.style.top, '108px'); // video top (100) + 8
  btn.listeners.click[0]({ preventDefault() {}, stopPropagation() {} });
  assert.equal(on.clicks.length, 1);

  const off = setup({ settings: { floatingButton: false } });
  await off.doc.fire('play', { target: off.video });
  assert.equal(buttons(off.doc).length, 0);
});

test('no button when nothing downloadable was found', async () => {
  const s = setup({ items: false });
  await s.doc.fire('play', { target: s.video });
  assert.equal(buttons(s.doc).length, 0);
});

test('button is removed on pause and not duplicated on repeated play', async () => {
  const s = setup();
  await s.doc.fire('play', { target: s.video });
  await s.doc.fire('play', { target: s.video });
  assert.equal(buttons(s.doc).length, 1);
  await s.doc.fire('pause', { target: s.video });
  assert.equal(buttons(s.doc).length, 0);
});

test('non-video play events are ignored', async () => {
  const s = setup();
  await s.doc.fire('play', { target: new FakeEl('audio') });
  assert.equal(buttons(s.doc).length, 0);
});
