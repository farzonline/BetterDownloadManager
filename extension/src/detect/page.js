// Turns messages from the page layer (hook + DOM scan) into registry items. Page messages are untrusted:
// any script on the page can post them, so every field is validated and only http(s) URLs are accepted.
import { classify } from './classify.js';

const MAX_URL = 4096;
const RANGE_PARAMS = /^(bytestart|byteend|range)$/i;

export function stripRangeParams(url) {
  try {
    const u = new URL(url);
    for (const k of [...u.searchParams.keys()]) if (RANGE_PARAMS.test(k)) u.searchParams.delete(k);
    return u.href;
  } catch { return url; }
}

function validUrl(u) {
  if (typeof u !== 'string' || u.length > MAX_URL) return false;
  try { return ['http:', 'https:'].includes(new URL(u).protocol); } catch { return false; }
}

const str = (v, max = 512) => (typeof v === 'string' ? v.slice(0, max) : undefined);

export function handlePageMessage(msg, sender, { registry }) {
  if (!msg || typeof msg !== 'object' || !sender?.tab || sender.tab.id < 0) return;
  const tabId = sender.tab.id;
  const base = {
    frameUrl: sender.url,
    pageUrl: sender.tab.url,
    title: sender.tab.title,
    headers: sender.tab.url ? { Referer: sender.tab.url } : {},
  };

  switch (msg.type) {
    case 'media': {
      if (!validUrl(msg.url)) return;
      const kind = classify({ url: msg.url, contentType: str(msg.contentType), firstBytes: str(msg.bodyPrefix, 256) });
      if (kind && kind !== 'segment') registry.add(tabId, { ...base, kind, url: msg.url, via: msg.via === 'xhr' ? 'xhr' : 'fetch' });
      return;
    }
    case 'mse': {
      const urls = Array.isArray(msg.segmentUrls) ? msg.segmentUrls.filter(validUrl) : [];
      if (!urls.length) return;
      const kind = /^audio\//i.test(str(msg.mime) ?? '') ? 'audio' : 'video';
      registry.add(tabId, { ...base, kind, url: stripRangeParams(urls[0]), via: 'mse', mse: true });
      return;
    }
    case 'dom': {
      if (!Array.isArray(msg.items)) return;
      for (const it of msg.items.slice(0, 50)) {
        if (!it || !validUrl(it.url) || !['video', 'audio', 'subtitle'].includes(it.kind)) continue;
        registry.add(tabId, { ...base, kind: it.kind, url: it.url, via: 'dom', protected: !!it.protected });
      }
      return;
    }
    case 'drm':
      registry.markProtected(tabId, sender.url);
      return;
    default:
  }
}

/** The item the floating button should download: a stream manifest first, else the largest video, never DRM/subtitles. */
export function pickBest(items) {
  const ok = items.filter((i) => !i.protected && ['hls', 'dash', 'video', 'audio'].includes(i.kind));
  return ok.find((i) => i.kind === 'hls' || i.kind === 'dash')
    ?? ok.filter((i) => i.kind === 'video').sort((a, b) => (b.size ?? 0) - (a.size ?? 0))[0]
    ?? ok[0];
}
