import test from 'node:test';
import assert from 'node:assert/strict';
import { shouldTakeOver, takeOverDownload, DEFAULT_SETTINGS } from '../src/takeover.js';

const s = { ...DEFAULT_SETTINGS };
const dl = (o = {}) => ({ url: 'https://files.example/big.zip', mime: 'application/zip', fileSize: 50_000_000, filename: 'big.zip', ...o });

test('takes over a large listed file', () => {
  assert.equal(shouldTakeOver(dl(), s), true);
});

test('honors minimum size but takes unknown sizes', () => {
  assert.equal(shouldTakeOver(dl({ fileSize: 1000 }), s), false);
  assert.equal(shouldTakeOver(dl({ fileSize: -1 }), s), true);
  assert.equal(shouldTakeOver(dl({ fileSize: 0 }), s), true);
});

test('excluded host (and its subdomains) is skipped', () => {
  const st = { ...s, excludedHosts: ['example'] };
  assert.equal(shouldTakeOver(dl({ url: 'https://example/big.zip' }), st), false);
  assert.equal(shouldTakeOver(dl({ url: 'https://cdn.example/big.zip' }), st), false);
  assert.equal(shouldTakeOver(dl({ url: 'https://notexample/big.zip' }), st), true);
});

test('altKey skips takeover', () => {
  assert.equal(shouldTakeOver(dl(), s, { altKey: true }), false);
});

test('unlisted mime and extension is ignored', () => {
  assert.equal(shouldTakeOver(dl({ mime: 'text/html', filename: 'page.html', url: 'https://x/page.html' }), s), false);
});

test('listed extension wins even when mime is octet-stream', () => {
  assert.equal(shouldTakeOver(dl({ mime: 'application/octet-stream', filename: 'setup.exe', url: 'https://x/dl?id=1' }), s), true);
});

test('disabled setting never takes over', () => {
  assert.equal(shouldTakeOver(dl(), { ...s, enabled: false }), false);
});

function deps(over = {}) {
  const calls = [];
  return {
    calls,
    deps: {
      settings: s,
      altRecent: () => false,
      sendToHost: async (m) => { calls.push(['send', m]); return { ok: true }; },
      buildMessage: async (item) => ({ op: 'add', url: item.url }),
      cancel: async (id) => calls.push(['cancel', id]),
      erase: async (id) => calls.push(['erase', id]),
      notify: (m) => calls.push(['notify', m]),
      ...over,
    },
  };
}

test('success: sends to host, then cancels and erases the browser download', async () => {
  const { calls, deps: d } = deps();
  assert.equal(await takeOverDownload({ id: 5, ...dl() }, d), 'taken');
  assert.deepEqual(calls.map((c) => c[0]), ['send', 'cancel', 'erase']);
});

test('takeover reuses the headers the network layer saw for that file (Chrome may report about:client as referrer)', async () => {
  let built;
  const seen = { pageUrl: 'https://site/watch', title: 'Clip', headers: { Referer: 'https://player/', 'User-Agent': 'UA' } };
  const { deps: d } = deps({
    lookup: (url) => (url === dl().url ? seen : undefined),
    buildMessage: async (item) => { built = item; return { op: 'add', url: item.url }; },
  });
  assert.equal(await takeOverDownload({ id: 5, ...dl(), referrer: 'about:client' }, d), 'taken');
  assert.deepEqual(built.headers, { Referer: 'https://player/', 'User-Agent': 'UA' });
  assert.equal(built.pageUrl, 'https://site/watch');
});

test('HostFailure_DoesNotCancelBrowserDownload', async () => {
  const { calls, deps: d } = deps({ sendToHost: async () => ({ ok: false, error: 'BDM not running' }) });
  assert.equal(await takeOverDownload({ id: 5, ...dl() }, d), 'fallback');
  assert.equal(calls.some((c) => c[0] === 'cancel'), false);
  assert.equal(calls.some((c) => c[0] === 'notify'), true);
});

test('a site that refuses BDM: the browser keeps its own download, and the note says so plainly', async () => {
  const { calls, deps: d } = deps({ sendToHost: async () => ({ ok: false, useBrowser: true, error: 'This site only lets your browser download the file.' }) });
  assert.equal(await takeOverDownload({ id: 5, ...dl() }, d), 'fallback');
  assert.equal(calls.some((c) => c[0] === 'cancel'), false);
  assert.deepEqual(calls.filter((c) => c[0] === 'notify').map((c) => c[1]), ['This site only lets your browser download the file.']);
});

test('host call that throws also falls back without cancelling', async () => {
  const { calls, deps: d } = deps({ sendToHost: async () => { throw new Error('port closed'); } });
  assert.equal(await takeOverDownload({ id: 5, ...dl() }, d), 'fallback');
  assert.equal(calls.some((c) => c[0] === 'cancel'), false);
});

test('not eligible: skipped without contacting the host', async () => {
  const { calls, deps: d } = deps();
  assert.equal(await takeOverDownload({ id: 5, ...dl({ fileSize: 10 }) }, d), 'skipped');
  assert.equal(calls.length, 0);
});

test('recent alt-click on the url skips takeover', async () => {
  const { calls, deps: d } = deps({ altRecent: () => true });
  assert.equal(await takeOverDownload({ id: 5, ...dl() }, d), 'skipped');
  assert.equal(calls.length, 0);
});
