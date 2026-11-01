// BDM's browser relay, the extension's side. Some sites (Cloudflare-style bot protection) only serve a real browser,
// so BDM's engine queues those requests at a loopback endpoint in the app; this worker takes them, fetches them here
// in Chrome with the owner's own session, and streams the answers back. Runs in the offscreen document (it outlives
// the service worker). Pure apart from the injected fetch, so it is tested in Node.

export function createRelay({ fetch, chunkSize = 1 << 20, parallel = 12, wait = 20, onAuthError = () => {} }) {
  let base = null;
  let token = null;
  const running = new Set();

  const post = async (path, body, json) => {
    const headers = { 'X-BDM-Token': token };
    if (json) headers['Content-Type'] = 'application/json';
    const r = await fetch(base + path, { method: 'POST', headers, body: json ? JSON.stringify(body) : body });
    return r.status !== 410; // 410: BDM no longer wants this (paused, or done with that part)
  };

  async function runJob(job) {
    const stop = new AbortController();
    let res;
    try {
      res = await fetch(job.url, {
        method: job.method, headers: job.range ? { Range: job.range } : {}, credentials: 'include', cache: 'no-store', signal: stop.signal,
      });
    } catch (e) {
      await post(`/relay/${job.id}/end`, { error: String(e?.message ?? e) }, true).catch(() => {});
      return;
    }
    try {
      const headers = {};
      res.headers.forEach((v, k) => { headers[k] = v; });
      if (!(await post(`/relay/${job.id}/head`, { status: res.status, headers }, true))) { stop.abort(); return; }
      if (job.method !== 'HEAD' && res.body) {
        const reader = res.body.getReader();
        let parts = [];
        let size = 0;
        for (;;) {
          const { done, value } = await reader.read();
          if (value?.length) { parts.push(value); size += value.length; }
          while (size >= chunkSize || (done && size > 0)) {
            const all = concat(parts, size);
            const n = Math.min(chunkSize, size);
            if (!(await post(`/relay/${job.id}/data`, all.subarray(0, n)))) { stop.abort(); return; }
            parts = n < size ? [all.subarray(n)] : [];
            size -= n;
          }
          if (done) break;
        }
      }
      await post(`/relay/${job.id}/end`, {}, true);
    } catch (e) {
      stop.abort();
      await post(`/relay/${job.id}/end`, { error: String(e?.message ?? e) }, true).catch(() => {});
    }
  }

  /** Asks BDM for the next request (long poll). True when one was taken. */
  async function pollOnce() {
    if (!base) return false;
    const r = await fetch(`${base}/relay/next?wait=${wait}`, { headers: { 'X-BDM-Token': token } });
    if (r.status === 401) { onAuthError(); return false; }
    if (r.status !== 200) return false;
    const job = await r.json();
    const p = runJob(job).finally(() => running.delete(p));
    running.add(p);
    return true;
  }

  let looping = false;
  /** Polls for as long as the document lives; backs off while BDM isn't running. */
  async function start() {
    if (looping) return;
    looping = true;
    let backoff = 1000;
    for (;;) {
      if (!base || running.size >= parallel) { await sleep(base ? 50 : 1000); continue; }
      try {
        await pollOnce();
        backoff = 1000;
      } catch {
        await sleep(backoff); // BDM closed: try again later
        backoff = Math.min(backoff * 2, 30_000);
      }
    }
  }

  return {
    configure(c) { base = c.base; token = c.token; },
    runJob,
    pollOnce,
    start,
    /** Resolves when every running job has finished (tests). */
    idle: () => Promise.all([...running]),
  };
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function concat(parts, size) {
  if (parts.length === 1) return parts[0];
  const all = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.length; }
  return all;
}
