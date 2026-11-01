// Local test site for the stream-catcher acceptance tests. Zero dependencies; media is generated once with ffmpeg.
// Each page exercises one way real sites hide a stream and sets window.__done = true when its player code has run.
import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { spawnSync } from 'node:child_process';

export const MEDIA_DIR = path.join(os.tmpdir(), 'bdm-corpus-media');

function ffmpeg(args, cwd = MEDIA_DIR) {
  const r = spawnSync('ffmpeg', ['-y', '-hide_banner', '-loglevel', 'error', ...args], { cwd, encoding: 'utf8' });
  if (r.status !== 0) throw new Error(`ffmpeg failed: ${r.stderr || r.error}`);
}

export function ensureMedia() {
  if (fs.existsSync(path.join(MEDIA_DIR, '.ready-v1'))) return;
  fs.rmSync(MEDIA_DIR, { recursive: true, force: true });
  for (const d of ['hls', 'hlsaes', 'dash', 'ig', 'mse']) fs.mkdirSync(path.join(MEDIA_DIR, d), { recursive: true });
  const video = ['-f', 'lavfi', '-i', 'testsrc=duration=6:size=320x240:rate=15'];
  const audio = ['-f', 'lavfi', '-i', 'sine=frequency=440:duration=6'];
  const enc = ['-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-g', '30', '-c:a', 'aac', '-shortest'];

  ffmpeg([...video, ...audio, ...enc, '-f', 'hls', '-hls_time', '2', '-hls_playlist_type', 'vod', '-hls_segment_filename', 'hls/seg%d.ts', 'hls/index.m3u8']);
  fs.writeFileSync(path.join(MEDIA_DIR, 'hls', 'master.m3u8'), '#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=900000,RESOLUTION=320x240\nindex.m3u8\n');

  const key = crypto.randomBytes(16);
  fs.writeFileSync(path.join(MEDIA_DIR, 'hlsaes', 'key.bin'), key);
  fs.writeFileSync(path.join(MEDIA_DIR, 'keyinfo.txt'), `key.bin\n${path.join(MEDIA_DIR, 'hlsaes', 'key.bin')}\n00112233445566778899aabbccddeeff\n`);
  ffmpeg([...video, ...audio, ...enc, '-f', 'hls', '-hls_time', '2', '-hls_playlist_type', 'vod', '-hls_key_info_file', path.join(MEDIA_DIR, 'keyinfo.txt'),
    '-hls_segment_filename', 'hlsaes/seg%d.ts', 'hlsaes/index.m3u8']);

  ffmpeg([...video, ...audio, ...enc, '-f', 'dash', '-seg_duration', '2', '-use_template', '1', '-use_timeline', '1', 'manifest.mpd'], path.join(MEDIA_DIR, 'dash'));

  // Instagram-story-like: video and audio are separate single files
  ffmpeg([...video, '-c:v', 'libx264', '-preset', 'ultrafast', '-pix_fmt', 'yuv420p', '-an', '-movflags', '+faststart', 'ig/video.mp4']);
  ffmpeg([...audio, '-c:a', 'aac', '-movflags', '+faststart', 'ig/audio.mp4']);

  // Something every Chromium build can actually play, for the MediaSource page
  ffmpeg(['-f', 'lavfi', '-i', 'testsrc=duration=2:size=160x120:rate=15', '-c:v', 'libvpx', '-an', '-f', 'webm', 'mse/clip.webm']);
  fs.writeFileSync(path.join(MEDIA_DIR, '.ready-v1'), 'ok');
}

const TYPES = {
  '.m3u8': 'application/vnd.apple.mpegurl', '.mpd': 'application/dash+xml', '.ts': 'video/mp2t', '.m4s': 'video/iso.segment',
  '.mp4': 'video/mp4', '.webm': 'video/webm', '.bin': 'application/octet-stream', '.html': 'text/html; charset=utf-8',
};

function serveFile(req, res, file, type) {
  if (!fs.existsSync(file)) { res.writeHead(404).end('not found'); return; }
  const size = fs.statSync(file).size;
  const m = /^bytes=(\d+)-(\d*)$/.exec(req.headers.range ?? '');
  if (m) {
    const start = Number(m[1]);
    const end = m[2] ? Math.min(Number(m[2]), size - 1) : size - 1;
    res.writeHead(206, { 'Content-Type': type, 'Content-Range': `bytes ${start}-${end}/${size}`, 'Content-Length': end - start + 1, 'Accept-Ranges': 'bytes' });
    if (req.method === 'HEAD') return res.end();
    fs.createReadStream(file, { start, end }).pipe(res);
  } else {
    res.writeHead(200, { 'Content-Type': type, 'Content-Length': size, 'Accept-Ranges': 'bytes' });
    if (req.method === 'HEAD') return res.end();
    fs.createReadStream(file).pipe(res);
  }
}

const page = (body) => `<!doctype html><meta charset="utf-8"><title>corpus</title><body>${body}`;

const PAGES = {
  '/hls-clear': page(`<script>
    (async () => {
      const master = await (await fetch('/media/hls/master.m3u8')).text();
      const variant = await (await fetch('/media/hls/index.m3u8')).text();
      for (const seg of variant.split('\\n').filter((l) => l.endsWith('.ts')).slice(0, 2)) await (await fetch('/media/hls/' + seg)).arrayBuffer();
      window.__done = true;
    })();</script>`),
  '/hls-aes128': page(`<script>
    (async () => {
      await (await fetch('/media/hlsaes/index.m3u8')).text();
      await (await fetch('/media/hlsaes/key.bin')).arrayBuffer();
      window.__done = true;
    })();</script>`),
  '/dash-xhr': page(`<script>
    const x = new XMLHttpRequest();
    x.open('GET', '/media/dash/manifest.mpd');
    x.onloadend = () => { window.__done = true; };
    x.send();</script>`),
  '/mse-blob': page(`<video id="v" muted></video><script>
    (async () => {
      const ms = new MediaSource();
      document.getElementById('v').src = URL.createObjectURL(ms);
      await new Promise((r) => ms.addEventListener('sourceopen', r, { once: true }));
      const sb = ms.addSourceBuffer('video/webm; codecs="vp8"');
      const data = await (await fetch('/media/mse/clip.webm?player=mse')).arrayBuffer();
      sb.appendBuffer(data);
      await new Promise((r) => sb.addEventListener('updateend', r, { once: true }));
      window.__done = true;
    })();</script>`),
  '/iframe-embed': page(`<iframe src="/player-frame" width="320" height="240"></iframe><script>
    addEventListener('message', (e) => { if (e.data === 'frame-done') window.__done = true; });</script>`),
  '/player-frame': page(`<script>
    (async () => {
      await (await fetch('/media/hls/master.m3u8?from=frame')).text();
      parent.postMessage('frame-done', '*');
    })();</script>`),
  '/service-worker': page(`<script>
    (async () => {
      const reg = await navigator.serviceWorker.register('/sw.js');
      await navigator.serviceWorker.ready;
      const sw = reg.active;
      await new Promise((resolve) => {
        navigator.serviceWorker.addEventListener('message', () => resolve());
        sw.postMessage({ url: '/media/hls/master.m3u8?via=sw' });
      });
      window.__done = true;
    })();</script>`),
  '/extensionless': page(`<video src="/v/12345" muted preload="auto"></video><script>
    setTimeout(() => { window.__done = true; }, 1500);</script>`),
  '/ig-story': page(`<script>
    (async () => {
      document.cookie = 'sessionid=abc; path=/';
      const exp = Math.floor(Date.now() / 1000) + 600;
      const mpd = await (await fetch('/media/ig/manifest.mpd?exp=' + exp + '&sig=ok')).text();
      const urls = [...mpd.matchAll(/<BaseURL>([^<]+)<\\/BaseURL>/g)].map((m) => m[1].replaceAll('&amp;', '&'));
      for (const u of urls) await fetch(u, { headers: { Range: 'bytes=0-1023' } });
      window.__done = true;
    })();</script>`),
  '/drm': page(`<video id="v"></video><script>
    (async () => {
      const access = await navigator.requestMediaKeySystemAccess('org.w3.clearkey',
        [{ initDataTypes: ['webm', 'cenc'], videoCapabilities: [{ contentType: 'video/webm; codecs="vp8"' }] }]);
      const keys = await access.createMediaKeys();
      await document.getElementById('v').setMediaKeys(keys);
      await (await fetch('/media/drm/manifest.mpd')).text();
      window.__done = true;
    })();</script>`),
};

const SW = `
self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
self.addEventListener('message', (e) => {
  e.waitUntil(fetch(e.data.url).then((r) => r.text()).then(() => e.source.postMessage('done')));
});`;

const DRM_MPD = `<?xml version="1.0"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT4S"><Period>
  <AdaptationSet mimeType="video/mp4"><ContentProtection schemeIdUri="urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed"/>
    <SegmentTemplate timescale="1" duration="4" media="e$Number$.m4s"/><Representation id="enc" bandwidth="1000000"/></AdaptationSet>
</Period></MPD>`;

/** Instagram-like gate: needs the session cookie and an unexpired signed URL. */
function gate(req, url) {
  const cookie = /(?:^|;\s*)sessionid=abc(?:;|$)/.test(req.headers.cookie ?? '');
  const exp = Number(url.searchParams.get('exp'));
  return cookie && url.searchParams.get('sig') === 'ok' && exp > Date.now() / 1000;
}

export async function startCorpus() {
  ensureMedia();
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://127.0.0.1');
    const p = url.pathname;
    if (PAGES[p]) { res.writeHead(200, { 'Content-Type': TYPES['.html'] }).end(PAGES[p]); return; }
    if (p === '/sw.js') { res.writeHead(200, { 'Content-Type': 'text/javascript', 'Service-Worker-Allowed': '/' }).end(SW); return; }
    if (p === '/v/12345') { serveFile(req, res, path.join(MEDIA_DIR, 'ig', 'video.mp4'), 'video/mp4'); return; }
    if (p === '/media/drm/manifest.mpd') { res.writeHead(200, { 'Content-Type': TYPES['.mpd'] }).end(DRM_MPD); return; }
    if (p === '/media/ig/manifest.mpd') {
      if (!gate(req, url)) { res.writeHead(403).end('forbidden'); return; }
      const base = `http://${req.headers.host}/media/ig`;
      const q = `exp=${url.searchParams.get('exp')}&amp;sig=ok`;
      res.writeHead(200, { 'Content-Type': TYPES['.mpd'] }).end(`<?xml version="1.0"?>
<MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT6S"><Period>
  <AdaptationSet contentType="video" mimeType="video/mp4"><Representation id="v1" bandwidth="800000" width="320" height="240" codecs="avc1.42c01e">
    <BaseURL>${base}/video.mp4?${q}</BaseURL><SegmentBase indexRange="0-800"/></Representation></AdaptationSet>
  <AdaptationSet contentType="audio" mimeType="audio/mp4"><Representation id="a1" bandwidth="64000" codecs="mp4a.40.2">
    <BaseURL>${base}/audio.mp4?${q}</BaseURL><SegmentBase indexRange="0-500"/></Representation></AdaptationSet>
</Period></MPD>`);
      return;
    }
    if (p === '/media/ig/video.mp4' || p === '/media/ig/audio.mp4') {
      if (!gate(req, url)) { res.writeHead(403).end('forbidden'); return; }
      serveFile(req, res, path.join(MEDIA_DIR, p.replace('/media/', '')), 'video/mp4');
      return;
    }
    const m = /^\/media\/(hls|hlsaes|dash|mse)\/([\w.-]+)$/.exec(p);
    if (m) { serveFile(req, res, path.join(MEDIA_DIR, m[1], m[2]), TYPES[path.extname(m[2])] ?? 'application/octet-stream'); return; }
    res.writeHead(404).end('not found');
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  return { port, url: `http://127.0.0.1:${port}`, close: () => new Promise((r) => server.close(r)) };
}
