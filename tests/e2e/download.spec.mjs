// End to end: the browser catches a story, the exact message the extension builds goes to the REAL app over its pipe,
// and the app downloads it as one playable file. Needs the built app (dotnet build) and ffmpeg/ffprobe on PATH.
import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { startCorpus } from './corpus-server.mjs';
import { ROOT, EXTENSION, startBrowser } from './harness.mjs';

const APP = path.join(ROOT, 'src/BDM.App/bin/Debug/net8.0-windows/BDM.App.exe');
// Its own pipe, so the test app never talks to (or is mistaken for) a BDM the owner has running.
const PIPE_NAME = `bdm-e2e-${process.pid}`;
const PIPE = `\\\\.\\pipe\\${PIPE_NAME}`;

let corpus, browser, app, dataDir, downloadsDir, buildMessage;

/** What chrome.cookies gives the extension: the browser's own cookie jar for a URL. */
const cookieJar = () => ({
  getAll: async ({ url }) => (await browser.context.cookies(url)).map(({ name, value }) => ({ name, value })),
});

/** One JSON line to the app's pipe, one JSON line back. */
function sendToApp(message) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(PIPE);
    let buf = '';
    sock.setEncoding('utf8');
    sock.on('connect', () => sock.write(JSON.stringify(message) + '\n'));
    sock.on('data', (d) => { buf += d; if (buf.includes('\n')) { sock.end(); resolve(JSON.parse(buf.split('\n')[0])); } });
    sock.on('error', reject);
    setTimeout(() => reject(new Error('the app did not answer')), 60_000).unref();
  });
}

async function waitFor(fn, what, ms = 60_000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    const v = await Promise.resolve().then(fn).catch(() => undefined);
    if (v) return v;
    await new Promise((r) => setTimeout(r, 300));
  }
  assert.fail(`timed out waiting for ${what}`);
}

async function api(route) {
  const info = JSON.parse(fs.readFileSync(path.join(dataDir, 'api.json'), 'utf8'));
  const res = await fetch(`http://127.0.0.1:${info.Port}${route}`, { headers: { 'X-BDM-Token': info.Token } });
  return res.json();
}

const probe = (file) => {
  const r = spawnSync('ffprobe', ['-v', 'error', '-print_format', 'json', '-show_format', '-show_streams', file], { encoding: 'utf8' });
  const j = JSON.parse(r.stdout);
  return { duration: Number(j.format.duration), kinds: j.streams.map((s) => s.codec_type) };
};

before(async () => {
  assert.ok(fs.existsSync(APP), `build the app first: dotnet build (missing ${APP})`);
  corpus = await startCorpus();
  dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bdm-e2e-data-'));
  downloadsDir = fs.mkdtempSync(path.join(os.tmpdir(), 'bdm-e2e-dl-'));
  fs.writeFileSync(path.join(dataDir, 'settings.json'), JSON.stringify({ DownloadRoot: downloadsDir, ThemeMode: 'Dark' }));
  app = spawn(APP, ['--background'], { env: { ...process.env, BDM_DATA_DIR: dataDir, BDM_PIPE: PIPE_NAME }, stdio: 'ignore' });
  await waitFor(() => sendToApp({ op: 'ping' }).then((r) => r.ok), 'the app to answer ping', 30_000);
  await waitFor(() => fs.existsSync(path.join(dataDir, 'api.json')), 'the app to publish its API', 10_000);
  ({ buildMessage } = await import(pathToFileURL(path.join(EXTENSION, 'src/host.js')).href));
  browser = await startBrowser(corpus.url);
});

after(async () => {
  await browser?.close();
  if (app?.pid) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F']);
  await corpus?.close();
  await new Promise((r) => setTimeout(r, 500)); // let Windows release the killed app's files
  for (const d of [dataDir, downloadsDir]) if (d) try { fs.rmSync(d, { recursive: true, force: true }); } catch { /* temp folder: the OS cleans it later */ }
});

test('ig-story-like: the caught story downloads through the real app as one playable file with video and audio', async () => {
  const page = await browser.visit('/ig-story');
  const item = await browser.eventually((i) => i.kind === 'dash' && i.url.includes('/media/ig/manifest.mpd'), 'the story manifest');
  const message = await buildMessage(item, { cookies: cookieJar() }); // exactly what the extension sends to the host
  assert.equal(message.op, 'addStream');

  const reply = await sendToApp(message);
  assert.equal(reply.ok, true, `the app refused the story: ${reply.error}`);

  const done = await waitFor(async () => {
    const rows = await api('/downloads');
    const row = rows.find((r) => r.url.includes('/media/ig/manifest.mpd'));
    if (row?.state === 'Failed') assert.fail(`the download failed: ${row.error}`);
    return row?.state === 'Completed' ? row : undefined;
  }, 'the story to finish downloading', 40_000).catch(async (e) => {
    throw new Error(`${e.message}. The app reports: ${JSON.stringify(await api('/downloads'))}`);
  });

  const info = probe(done.filePath);
  assert.ok(info.kinds.includes('video') && info.kinds.includes('audio'), `expected video and audio, got ${info.kinds}`);
  assert.ok(Math.abs(info.duration - 6) < 1, `duration ${info.duration}`);
  await page.close();
});

test('the same story without its cookie is refused with a readable message', async () => {
  const page = await browser.visit('/ig-story');
  const item = await browser.eventually((i) => i.kind === 'dash' && i.url.includes('/media/ig/manifest.mpd'), 'the story manifest');
  const message = await buildMessage({ ...item, headers: {} }, {}); // no Cookie travels with it
  const reply = await sendToApp(message);
  assert.equal(reply.ok, false);
  assert.match(reply.error, /refused|expired|login/i);
  await page.close();
});

test('an expired signed link is refused the same way', async () => {
  const page = await browser.visit('/ig-story');
  const item = await browser.eventually((i) => i.kind === 'dash' && i.url.includes('/media/ig/manifest.mpd'), 'the story manifest');
  const stale = { ...item, url: item.url.replace(/exp=\d+/, 'exp=1000') };
  const reply = await sendToApp(await buildMessage(stale, {}));
  assert.equal(reply.ok, false);
  assert.match(reply.error, /refused|expired|login/i);
  await page.close();
});

test('a DRM-protected stream cannot even be sent to the app', async () => {
  const page = await browser.visit('/drm');
  const item = await browser.eventually((i) => i.kind === 'dash' && i.url.includes('/media/drm/manifest.mpd') && i.protected === true, 'the protected manifest');
  await assert.rejects(buildMessage(item, {}), /protected/i);
  await page.close();
});
