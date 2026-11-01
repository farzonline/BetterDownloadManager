import test from 'node:test';
import assert from 'node:assert/strict';
import { createRelay } from '../src/relay.js';

const BASE = 'http://127.0.0.1:4321';
const bytes = (n) => Uint8Array.from({ length: n }, (_, i) => i % 251);

/** A fake fetch: the site answers from `site`, the relay answers from `relay` (path -> status), and every call is kept. */
function fakeFetch({ site, relay = () => 204 }) {
  const calls = [];
  const fn = async (url, init = {}) => {
    calls.push({ url, init });
    if (url.startsWith(BASE)) {
      const path = url.slice(BASE.length);
      const status = relay(path, init);
      return new Response(status === 204 ? null : status === 200 ? JSON.stringify(init.reply ?? {}) : '', { status });
    }
    return site(url, init);
  };
  fn.calls = calls;
  return fn;
}

const relayPosts = (f) => f.calls.filter((c) => c.url.startsWith(BASE)).map((c) => c.url.slice(BASE.length));

test('a job is fetched in the browser (its range, the owner\'s cookies) and streamed back to BDM in chunks', async () => {
  const body = bytes(10);
  const f = fakeFetch({ site: async () => new Response(body, { status: 206, headers: { 'content-range': 'bytes 0-9/100', 'accept-ranges': 'bytes' } }) });
  const relay = createRelay({ fetch: f, chunkSize: 4 });
  relay.configure({ base: BASE, token: 't' });

  await relay.runJob({ id: 'j1', method: 'GET', url: 'https://cdn.example/v.mp4', range: 'bytes=0-9' });

  const site = f.calls[0];
  assert.equal(site.url, 'https://cdn.example/v.mp4');
  assert.equal(site.init.headers.Range, 'bytes=0-9');
  assert.equal(site.init.credentials, 'include');
  assert.deepEqual(relayPosts(f), ['/relay/j1/head', '/relay/j1/data', '/relay/j1/data', '/relay/j1/data', '/relay/j1/end']);
  const head = JSON.parse(f.calls[1].init.body);
  assert.equal(head.status, 206);
  assert.equal(head.headers['content-range'], 'bytes 0-9/100');
  const sent = f.calls.filter((c) => c.url.endsWith('/data')).flatMap((c) => [...c.init.body]);
  assert.deepEqual(sent, [...body]);
  assert.ok(f.calls.slice(1).every((c) => c.init.headers['X-BDM-Token'] === 't'));
});

test('when BDM answers 410 (paused, or done with that part), the browser stops fetching', async () => {
  let signal;
  const f = fakeFetch({
    site: async (url, init) => { signal = init.signal; return new Response(bytes(100), { status: 200 }); },
    relay: (path) => (path.endsWith('/data') ? 410 : 204),
  });
  const relay = createRelay({ fetch: f, chunkSize: 10 });
  relay.configure({ base: BASE, token: 't' });
  await relay.runJob({ id: 'j2', method: 'GET', url: 'https://cdn.example/v.mp4' });
  assert.equal(signal.aborted, true);
  assert.deepEqual(relayPosts(f), ['/relay/j2/head', '/relay/j2/data']); // no end: BDM already let go
});

test('a fetch the browser cannot make is reported to BDM', async () => {
  const f = fakeFetch({ site: async () => { throw new TypeError('Failed to fetch'); } });
  const relay = createRelay({ fetch: f });
  relay.configure({ base: BASE, token: 't' });
  await relay.runJob({ id: 'j3', method: 'GET', url: 'https://cdn.example/v.mp4' });
  assert.deepEqual(relayPosts(f), ['/relay/j3/end']);
  assert.match(JSON.parse(f.calls.at(-1).init.body).error, /Failed to fetch/);
});

test('a HEAD job sends only the answer, no body', async () => {
  const f = fakeFetch({ site: async () => new Response(null, { status: 200, headers: { 'content-length': '5000' } }) });
  const relay = createRelay({ fetch: f });
  relay.configure({ base: BASE, token: 't' });
  await relay.runJob({ id: 'j4', method: 'HEAD', url: 'https://cdn.example/v.mp4' });
  assert.equal(f.calls[0].init.method, 'HEAD');
  assert.deepEqual(relayPosts(f), ['/relay/j4/head', '/relay/j4/end']);
});

test('polling: a job is taken and run; 401 (BDM restarted with a new token) asks for a fresh one', async () => {
  let authErrors = 0;
  const job = { id: 'j5', method: 'GET', url: 'https://cdn.example/v.mp4' };
  let answer = 200;
  const f = fakeFetch({
    site: async () => new Response(bytes(3), { status: 200 }),
    relay: (path, init) => {
      if (path.startsWith('/relay/next')) { init.reply = job; return answer; }
      return 204;
    },
  });
  const relay = createRelay({ fetch: f, onAuthError: () => authErrors++ });
  relay.configure({ base: BASE, token: 't' });

  assert.equal(await relay.pollOnce(), true);
  await relay.idle();
  assert.ok(relayPosts(f).includes('/relay/j5/end'));

  answer = 401;
  assert.equal(await relay.pollOnce(), false);
  assert.equal(authErrors, 1);
});

test('polling without a configured relay does nothing', async () => {
  const f = fakeFetch({ site: async () => assert.fail('no fetch') });
  assert.equal(await createRelay({ fetch: f }).pollOnce(), false);
  assert.equal(f.calls.length, 0);
});
