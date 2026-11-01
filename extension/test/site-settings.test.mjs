import test from 'node:test';
import assert from 'node:assert/strict';
import { createSiteSettings, hostKey, withSiteOverrides } from '../src/siteSettings.js';
import { DEFAULT_SETTINGS } from '../src/takeover.js';

function memoryStorage() {
  const data = {};
  return {
    data,
    get: async (key) => (key in data ? { [key]: structuredClone(data[key]) } : {}),
    set: async (obj) => { Object.assign(data, structuredClone(obj)); },
  };
}

test('defaults_are_off', async () => {
  const s = createSiteSettings(memoryStorage());
  assert.deepEqual(await s.get('example.com'), { debugger: false, takeover: true });
});

test('enable_persists_per_host_not_per_origin_port', async () => {
  const storage = memoryStorage();
  await createSiteSettings(storage).set('https://Video.Example.com:8443/watch?v=1', { debugger: true });
  const fresh = createSiteSettings(storage); // a new instance proves persistence
  assert.equal((await fresh.get('video.example.com')).debugger, true);
  assert.equal((await fresh.get('http://video.example.com/other')).debugger, true);
  assert.equal((await fresh.get('other.example.com')).debugger, false);
});

test('hostKey normalises urls and hosts, and rejects junk', () => {
  assert.equal(hostKey('https://A.B.com:99/x'), 'a.b.com');
  assert.equal(hostKey('a.b.com'), 'a.b.com');
  assert.equal(hostKey('chrome://extensions'), 'extensions');
  assert.equal(hostKey(''), '');
  assert.equal(hostKey(undefined), '');
});

test('turning a setting back to its default removes the stored entry', async () => {
  const storage = memoryStorage();
  const s = createSiteSettings(storage);
  await s.set('a.com', { debugger: true });
  await s.set('a.com', { debugger: false });
  assert.deepEqual(storage.data.siteSettings, {});
});

test('withSiteOverrides excludes hosts where takeover is off', () => {
  const out = withSiteOverrides(DEFAULT_SETTINGS, 'files.example.com', { debugger: false, takeover: false });
  assert.deepEqual(out.excludedHosts, ['files.example.com']);
  assert.equal(withSiteOverrides(DEFAULT_SETTINGS, 'a.com', { debugger: false, takeover: true }), DEFAULT_SETTINGS);
});
