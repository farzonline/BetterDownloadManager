import test from 'node:test';
import assert from 'node:assert/strict';
import { createDebuggerFallback } from '../src/detect/debugger-fallback.js';
import { createRegistry } from '../src/detect/registry.js';

function fakeApi() {
  const calls = [];
  return {
    calls,
    attach: async (target, version) => { calls.push(['attach', target.tabId, version]); },
    detach: async (target) => { calls.push(['detach', target.tabId]); },
    sendCommand: async (target, method) => { calls.push(['cmd', target.tabId, method]); return {}; },
  };
}

function setup(enabledHosts = []) {
  const api = fakeApi();
  const registry = createRegistry();
  const fb = createDebuggerFallback({
    api,
    registry,
    siteSettings: { get: async (host) => ({ debugger: enabledHosts.includes(host), takeover: true }) },
    getTab: () => ({ url: 'https://deep.example/watch', title: 'Deep' }),
  });
  return { api, registry, fb };
}

test('attach_only_for_enabled_hosts', async () => {
  const { api, fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/watch');
  await fb.onTabUpdated(2, 'https://plain.example/');
  assert.deepEqual(api.calls.filter((c) => c[0] === 'attach').map((c) => c[1]), [1]);
  assert.deepEqual(api.calls.filter((c) => c[0] === 'cmd'), [['cmd', 1, 'Network.enable']]);
});

test('repeated updates do not attach twice', async () => {
  const { api, fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/a');
  await fb.onTabUpdated(1, 'https://deep.example/b');
  assert.equal(api.calls.filter((c) => c[0] === 'attach').length, 1);
});

test('navigating to a host without deep scan detaches', async () => {
  const { api, fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/a');
  await fb.onTabUpdated(1, 'https://plain.example/');
  assert.deepEqual(api.calls.filter((c) => c[0] === 'detach').map((c) => c[1]), [1]);
  assert.equal(fb.isAttached(1), false);
});

test('detach_on_tab_close', async () => {
  const { api, fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/a');
  await fb.onTabRemoved(1);
  assert.deepEqual(api.calls.filter((c) => c[0] === 'detach').map((c) => c[1]), [1]);
  await fb.onTabRemoved(1); // already gone: no second detach, no throw
  assert.equal(api.calls.filter((c) => c[0] === 'detach').length, 1);
});

test('attach failure (another debugger open) is swallowed and leaves the tab detached', async () => {
  const { api, fb } = setup(['deep.example']);
  api.attach = async () => { throw new Error('Another debugger is already attached'); };
  await fb.onTabUpdated(1, 'https://deep.example/a');
  assert.equal(fb.isAttached(1), false);
});

test('responseReceived events are classified into registry items with request headers', async () => {
  const { registry, fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/watch');
  fb.onEvent({ tabId: 1 }, 'Network.requestWillBeSent', { requestId: 'r1', request: { headers: { Referer: 'https://deep.example/watch', 'X-Junk': '1' } } });
  fb.onEvent({ tabId: 1 }, 'Network.requestWillBeSentExtraInfo', { requestId: 'r1', headers: { cookie: 'a=b' } });
  fb.onEvent({ tabId: 1 }, 'Network.responseReceived', {
    requestId: 'r1',
    response: { url: 'https://cdn/master.m3u8', status: 200, mimeType: 'application/vnd.apple.mpegurl', headers: { 'content-length': '900' } },
  });
  const [it] = registry.list(1);
  assert.equal(it.kind, 'hls');
  assert.equal(it.via, 'debugger');
  assert.equal(it.headers.Referer, 'https://deep.example/watch');
  assert.equal(it.headers.Cookie, 'a=b');
  assert.equal(it.headers['X-Junk'], undefined);
  assert.equal(it.pageUrl, 'https://deep.example/watch');
});

test('events from tabs that are not attached, errors and segments are ignored', async () => {
  const { registry, fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/watch');
  const resp = (over) => ({ requestId: 'x', response: { url: 'https://cdn/v.mp4', status: 200, mimeType: 'video/mp4', headers: {}, ...over } });
  fb.onEvent({ tabId: 9 }, 'Network.responseReceived', resp({}));
  fb.onEvent({ tabId: 1 }, 'Network.responseReceived', resp({ status: 404 }));
  fb.onEvent({ tabId: 1 }, 'Network.responseReceived', resp({ url: 'https://cdn/s1.ts', mimeType: 'video/mp2t' }));
  fb.onEvent({ tabId: 1 }, 'Network.somethingElse', {});
  assert.equal(registry.list(1).length + registry.list(9).length, 0);
});

test('the user closing the debugging bar (detached event) forgets the attachment', async () => {
  const { fb } = setup(['deep.example']);
  await fb.onTabUpdated(1, 'https://deep.example/a');
  fb.onDetach({ tabId: 1 }, 'canceled_by_user');
  assert.equal(fb.isAttached(1), false);
});
