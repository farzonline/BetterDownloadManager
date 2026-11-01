// Per-site switches, keyed by hostname (not origin or port) and kept in chrome.storage.sync.

const DEFAULTS = { debugger: false, takeover: true };
const KEY = 'siteSettings';

/** Lower-cased hostname of a URL or bare host; '' when there is none. */
export function hostKey(urlOrHost) {
  if (!urlOrHost) return '';
  try {
    return new URL(String(urlOrHost).includes('://') ? urlOrHost : `https://${urlOrHost}`).hostname.toLowerCase();
  } catch { return ''; }
}

/** @param storage a chrome.storage area (or any {get(key), set(obj)} promise API) */
export function createSiteSettings(storage) {
  const read = async () => (await storage.get(KEY))[KEY] ?? {};
  return {
    async get(urlOrHost) {
      return { ...DEFAULTS, ...(await read())[hostKey(urlOrHost)] };
    },
    async set(urlOrHost, patch) {
      const host = hostKey(urlOrHost);
      if (!host) return;
      const all = await read();
      const next = { ...DEFAULTS, ...all[host], ...patch };
      if (Object.keys(DEFAULTS).every((k) => next[k] === DEFAULTS[k])) delete all[host];
      else all[host] = next;
      await storage.set({ [KEY]: all });
    },
  };
}

/** Turns "don't take over downloads from this site" into the existing excluded-hosts mechanism. */
export function withSiteOverrides(settings, host, site) {
  return site.takeover === false ? { ...settings, excludedHosts: [...(settings.excludedHosts ?? []), host] } : settings;
}
