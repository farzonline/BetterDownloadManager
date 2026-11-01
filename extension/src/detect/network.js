// Network layer: watches response headers (observe-only) and feeds classified media into the registry.
import { classify, isAmbiguous } from './classify.js';
import { dedupeKey } from './registry.js';

const KEEP_HEADERS = {
  referer: 'Referer', origin: 'Origin', 'user-agent': 'User-Agent', authorization: 'Authorization', cookie: 'Cookie',
};
const MAX_PENDING = 500;

export function createNetworkDetector({ registry, getTab, getTabsByOrigin = () => [], probe, selfOrigin }) {
  const pending = new Map(); // requestId -> kept request headers
  const probed = new Set(); // dedupeKeys already probed

  return {
    onSendHeaders(d) {
      const kept = {};
      for (const { name, value } of d.requestHeaders ?? []) {
        const canonical = KEEP_HEADERS[name.toLowerCase()];
        if (canonical) kept[canonical] = value;
      }
      pending.set(d.requestId, kept);
      if (pending.size > MAX_PENDING) pending.delete(pending.keys().next().value);
    },

    async onHeadersReceived(d) {
      const headers = pending.get(d.requestId) ?? {};
      pending.delete(d.requestId);
      if (selfOrigin && d.initiator?.startsWith(selfOrigin)) return;
      // Service-worker requests carry no tab: attribute them to the open tabs of the worker's origin.
      const tabIds = d.tabId >= 0 ? [d.tabId] : d.initiator ? getTabsByOrigin(d.initiator) : [];
      if (!tabIds.length) return;
      if (d.statusCode !== 200 && d.statusCode !== 206) return;

      const h = {};
      for (const { name, value } of d.responseHeaders ?? []) h[name.toLowerCase()] = value;
      let kind = classify({ url: d.url, contentType: h['content-type'] });

      if (kind === null && isAmbiguous(h['content-type'])) {
        const key = dedupeKey(d.url);
        if (!probed.has(key)) {
          probed.add(key);
          const bytes = await probe(d.url, headers).catch(() => undefined);
          if (bytes) kind = classify({ url: d.url, contentType: h['content-type'], firstBytes: bytes });
        }
      }
      if (!kind || kind === 'segment') return;

      const range = /\/(\d+)$/.exec(h['content-range'] ?? '');
      const size = range ? Number(range[1]) : h['content-length'] ? Number(h['content-length']) : undefined;
      for (const tabId of tabIds) {
        const tab = getTab(tabId);
        registry.add(tabId, {
          kind, url: d.url, frameUrl: d.documentUrl ?? d.initiator, pageUrl: tab?.url, title: tab?.title, headers, size, via: 'network',
        });
      }
    },
  };
}
