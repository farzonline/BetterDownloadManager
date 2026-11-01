// Last-resort detection layer: attaches Chrome's debugger to tabs on sites the owner enabled "deep scan" for and
// reads the Network domain, which sees traffic the other layers cannot. Chrome shows a "debugging this browser"
// bar while attached, so this is strictly opt-in per site.
import { classify } from './classify.js';
import { hostKey } from '../siteSettings.js';

const KEEP = { referer: 'Referer', origin: 'Origin', 'user-agent': 'User-Agent', authorization: 'Authorization', cookie: 'Cookie' };
const PROTOCOL = '1.3';

function keepHeaders(raw, into = {}) {
  for (const [name, value] of Object.entries(raw ?? {})) {
    const canonical = KEEP[name.toLowerCase()];
    if (canonical) into[canonical] = value;
  }
  return into;
}

export function createDebuggerFallback({ api, registry, siteSettings, getTab }) {
  const attached = new Set();
  const pending = new Map(); // `${tabId}:${requestId}` -> kept headers

  async function detach(tabId) {
    attached.delete(tabId);
    try { await api.detach({ tabId }); } catch { /* tab already gone */ }
  }

  return {
    isAttached: (tabId) => attached.has(tabId),

    async onTabUpdated(tabId, url) {
      const want = (await siteSettings.get(hostKey(url))).debugger;
      const have = attached.has(tabId);
      if (want && !have) {
        try {
          await api.attach({ tabId }, PROTOCOL);
          await api.sendCommand({ tabId }, 'Network.enable');
          attached.add(tabId);
        } catch { /* another debugger (DevTools) is attached, or the tab is not debuggable */ }
      } else if (!want && have) {
        await detach(tabId);
      }
    },

    async onTabRemoved(tabId) {
      if (attached.has(tabId)) await detach(tabId);
    },

    /** chrome.debugger.onDetach: the user closed the bar or DevTools took over. */
    onDetach(source) { attached.delete(source.tabId); },

    onEvent(source, method, params) {
      const tabId = source.tabId;
      if (!attached.has(tabId)) return;
      const key = `${tabId}:${params?.requestId}`;
      if (method === 'Network.requestWillBeSent') {
        pending.set(key, keepHeaders(params.request?.headers));
      } else if (method === 'Network.requestWillBeSentExtraInfo') {
        pending.set(key, keepHeaders(params.headers, pending.get(key) ?? {}));
      } else if (method === 'Network.responseReceived') {
        const headers = pending.get(key) ?? {};
        pending.delete(key);
        const r = params.response;
        if (!r || (r.status !== 200 && r.status !== 206)) return;
        const lower = Object.fromEntries(Object.entries(r.headers ?? {}).map(([k, v]) => [k.toLowerCase(), v]));
        const kind = classify({ url: r.url, contentType: r.mimeType ?? lower['content-type'] });
        if (!kind || kind === 'segment') return;
        const tab = getTab(tabId);
        registry.add(tabId, {
          kind, url: r.url, frameUrl: tab?.url, pageUrl: tab?.url, title: tab?.title, headers,
          size: lower['content-length'] ? Number(lower['content-length']) : undefined, via: 'debugger',
        });
      }
    },
  };
}
