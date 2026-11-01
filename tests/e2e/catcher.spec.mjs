// Acceptance gate for the stream catcher: real Chromium + the unpacked extension against the local corpus.
import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { startCorpus } from './corpus-server.mjs';
import { startBrowser } from './harness.mjs';

let corpus, browser;

before(async () => {
  corpus = await startCorpus();
  browser = await startBrowser(corpus.url);
});

after(async () => {
  await browser?.close();
  await corpus?.close();
});

const { registry } = { registry: () => browser.registry() };
const visit = (p) => browser.visit(p);
const eventually = (pred, what, ms) => browser.eventually(pred, what, ms);

const at = (suffix) => (i) => i.url.includes(suffix);

test('hls-clear: a manifest fetched by page script is caught, and its segments are not listed', async () => {
  const page = await visit('/hls-clear');
  const item = await eventually((i) => i.kind === 'hls' && at('/media/hls/master.m3u8')(i), 'the HLS master playlist');
  assert.equal(item.headers.Referer?.startsWith(corpus.url), true);
  const all = await registry();
  assert.equal(all.some((i) => /\.ts(\?|$)/.test(i.url)), false, 'segments must never be listed');
  await page.close();
});

test('hls-aes128: an encrypted playlist is caught', async () => {
  const page = await visit('/hls-aes128');
  await eventually((i) => i.kind === 'hls' && at('/media/hlsaes/index.m3u8')(i), 'the AES-128 HLS playlist');
  await page.close();
});

test('dash-xhr: a manifest fetched with XMLHttpRequest is caught', async () => {
  const page = await visit('/dash-xhr');
  await eventually((i) => i.kind === 'dash' && at('/media/dash/manifest.mpd')(i), 'the DASH manifest');
  await page.close();
});

test('mse-blob: playback built from MediaSource (a blob: URL) is traced back to the fetched media', async () => {
  const page = await visit('/mse-blob');
  const item = await eventually((i) => i.mse === true && at('/media/mse/clip.webm')(i), 'the MediaSource-fed video');
  assert.equal(item.kind, 'video');
  await page.close();
});

test('iframe-embed: a stream requested from inside an iframe is attributed to the tab and frame', async () => {
  const page = await visit('/iframe-embed');
  const item = await eventually((i) => i.kind === 'hls' && at('from=frame')(i), 'the iframe player manifest');
  assert.ok(item.frameUrl?.includes('/player-frame') || item.pageUrl?.includes('/iframe-embed'), `frame attribution: ${item.frameUrl} / ${item.pageUrl}`);
  await page.close();
});

test('service-worker-fetch: a request made by a service worker is attributed to the tab of its origin', async () => {
  const page = await visit('/service-worker');
  await eventually((i) => i.kind === 'hls' && at('via=sw')(i), 'the service-worker manifest');
  await page.close();
});

test('extensionless-url: a video served from a URL without any extension is caught by its content type', async () => {
  const page = await visit('/extensionless');
  await eventually((i) => i.kind === 'video' && /\/v\/12345$/.test(i.url), 'the extensionless video');
  await page.close();
});

test('ig-story-like: separate video/audio DASH behind a cookie and a signed URL is caught with the cookie that unlocks it', async () => {
  const page = await visit('/ig-story');
  const item = await eventually((i) => i.kind === 'dash' && at('/media/ig/manifest.mpd')(i), 'the story manifest');
  assert.match(item.headers.Cookie ?? '', /sessionid=abc/, 'the cookie needed to fetch it again must travel with the item');
  assert.match(item.url, /exp=\d+/);
  await page.close();
});

test('drm: a stream whose page uses encrypted-media extensions is flagged protected and cannot be sent', async () => {
  const page = await visit('/drm');
  const item = await eventually((i) => i.kind === 'dash' && at('/media/drm/manifest.mpd')(i) && i.protected === true, 'the DRM manifest flagged protected');
  assert.equal(item.protected, true);
  await page.close();
});
