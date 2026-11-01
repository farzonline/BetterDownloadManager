import test from 'node:test';
import assert from 'node:assert/strict';
import { sendToHost, buildMessage } from '../src/host.js';

function fakeApi({ reply, lastError } = {}) {
  const api = { runtime: { lastError: undefined, sendNativeMessage: (name, msg, cb) => { api.sent = [name, msg]; api.runtime.lastError = lastError; cb(reply); } } };
  return api;
}

test('sendToHost passes the reply through', async () => {
  const api = fakeApi({ reply: { ok: true, appStarted: false } });
  assert.deepEqual(await sendToHost({ op: 'ping' }, api), { ok: true, appStarted: false });
  assert.equal(api.sent[0], 'com.bdm.host');
});

test('sendToHost turns lastError into ok:false', async () => {
  const api = fakeApi({ lastError: { message: 'Specified native messaging host not found.' } });
  const r = await sendToHost({ op: 'ping' }, api);
  assert.equal(r.ok, false);
  assert.match(r.error, /not found/);
});

test('sendToHost turns an empty reply into ok:false', async () => {
  assert.equal((await sendToHost({ op: 'ping' }, fakeApi({ reply: undefined }))).ok, false);
});

const cookies = { getAll: async ({ url }) => (url.startsWith('https://cdn') ? [{ name: 'sid', value: '1' }, { name: 'k', value: 'v w' }] : []) };

test('cookie_header_attached_for_item_url_in_host_message', async () => {
  const m = await buildMessage({ kind: 'video', url: 'https://cdn/v.mp4', pageUrl: 'https://p/', title: 'T', headers: { Referer: 'https://p/' } }, { cookies });
  assert.equal(m.op, 'add');
  assert.equal(m.cookie, 'sid=1; k=v w');
  assert.equal(m.headers.Referer, 'https://p/');
  assert.equal(m.pageUrl, 'https://p/');
});

test('the browser User-Agent is passed along unless the request already carried one', async () => {
  const m = await buildMessage({ kind: 'video', url: 'https://cdn/v.mp4', headers: {} }, { cookies, userAgent: 'UA/1' });
  assert.equal(m.headers['User-Agent'], 'UA/1');
  const kept = await buildMessage({ kind: 'video', url: 'https://cdn/v.mp4', headers: { 'User-Agent': 'seen' } }, { cookies, userAgent: 'UA/1' });
  assert.equal(kept.headers['User-Agent'], 'seen');
});

test('a referrer that is not a web address (about:client) is dropped', async () => {
  const m = await buildMessage({ kind: 'file', url: 'https://cdn/v.mp4', pageUrl: 'about:client', headers: { Referer: 'about:client' } }, { cookies });
  assert.equal(m.headers.Referer, undefined);
  assert.equal(m.pageUrl, undefined);
});

import { deliver } from '../src/host.js';

test('deliver: when the site refuses BDM, the browser downloads the file and takeover leaves that download alone', async () => {
  const calls = [];
  const r = await deliver({ op: 'add', url: 'https://cdn/v.mp4' }, {
    send: async () => ({ ok: false, useBrowser: true, error: 'refused' }),
    download: async (o) => calls.push(['download', o.url]),
    skipTakeover: (url) => calls.push(['skip', url]),
  });
  assert.deepEqual(r, { ok: true, viaBrowser: true });
  assert.deepEqual(calls, [['skip', 'https://cdn/v.mp4'], ['download', 'https://cdn/v.mp4']]);
});

test('deliver: an ordinary failure or success is passed through untouched', async () => {
  const download = async () => assert.fail('must not download');
  assert.deepEqual(await deliver({ op: 'add', url: 'u' }, { send: async () => ({ ok: true }), download, skipTakeover() {} }), { ok: true });
  assert.deepEqual(await deliver({ op: 'add', url: 'u' }, { send: async () => ({ ok: false, error: 'BDM not running' }), download, skipTakeover() {} }),
    { ok: false, error: 'BDM not running' });
});

test('captured Cookie request header wins over the cookie jar', async () => {
  const m = await buildMessage({ kind: 'video', url: 'https://cdn/v.mp4', headers: { Cookie: 'captured=1' } }, { cookies });
  assert.equal(m.cookie, 'captured=1');
  assert.equal(m.headers.Cookie, undefined);
});

test('stream items become addStream with kind; ask flag becomes addStreamAsk', async () => {
  const hls = { kind: 'hls', url: 'https://cdn/m.m3u8', headers: {} };
  assert.deepEqual([(await buildMessage(hls, { cookies })).op, (await buildMessage(hls, { cookies })).kind], ['addStream', 'hls']);
  assert.equal((await buildMessage(hls, { cookies, askQuality: true })).op, 'addStreamAsk');
  assert.equal((await buildMessage({ kind: 'dash', url: 'https://cdn/m.mpd', headers: {} }, { cookies })).kind, 'dash');
});

test('protected items cannot be sent', async () => {
  await assert.rejects(buildMessage({ kind: 'dash', url: 'https://cdn/m.mpd', headers: {}, protected: true }, { cookies }), /protected/i);
});

test('cookie lookup failure still builds the message', async () => {
  const bad = { getAll: async () => { throw new Error('denied'); } };
  const m = await buildMessage({ kind: 'video', url: 'https://cdn/v.mp4', headers: {} }, { cookies: bad });
  assert.equal(m.cookie, undefined);
});

import { buildPageMessage } from '../src/host.js';

test('buildPageMessage asks the app to fetch the tab page with yt-dlp, with the tab cookies', async () => {
  const m = await buildPageMessage({ url: 'https://cdn/watch?v=1', title: 'My page' }, { cookies });
  assert.equal(m.op, 'addPageAsk');
  assert.equal(m.url, 'https://cdn/watch?v=1');
  assert.equal(m.title, 'My page');
  assert.equal(m.cookie, 'sid=1; k=v w');
  assert.equal(m.headers.Referer, 'https://cdn/watch?v=1');
});

test('buildPageMessage refuses browser-internal and non-web tabs', async () => {
  await assert.rejects(buildPageMessage({ url: 'chrome://extensions' }, { cookies }), /web page/i);
  await assert.rejects(buildPageMessage({ url: undefined }, { cookies }), /web page/i);
  await assert.rejects(buildPageMessage({ url: 'file:///c:/a.html' }, { cookies }), /web page/i);
});

test('buildPageMessage still works when cookies cannot be read', async () => {
  const bad = { getAll: async () => { throw new Error('denied'); } };
  const m = await buildPageMessage({ url: 'https://cdn/watch', title: 't' }, { cookies: bad });
  assert.equal(m.cookie, undefined);
});
