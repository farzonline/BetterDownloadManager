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
