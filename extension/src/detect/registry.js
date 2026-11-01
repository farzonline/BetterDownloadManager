// Per-tab list of found media, de-duplicated by URL with short-lived signature parameters ignored.

const SIGNATURE_PARAM =
  /^(x-amz-[\w-]+|token|sig|signature|expires?|exp|e|hdnts|policy|key-pair-id|se|sp|sv|sr|st|oh|oe|bytestart|byteend|range|_nc_[\w]+)$/i;

export function dedupeKey(url) {
  try {
    const u = new URL(url);
    const params = [...u.searchParams.entries()]
      .filter(([k]) => !SIGNATURE_PARAM.test(k))
      .sort(([a], [b]) => a.localeCompare(b));
    return `${u.origin}${u.pathname}?${params.map(([k, v]) => `${k}=${v}`).join('&')}`;
  } catch {
    return url;
  }
}

const frameKey = (u) => { try { const x = new URL(u); return x.origin + x.pathname; } catch { return u; } };

export function createRegistry({ onChange } = {}) {
  const tabs = new Map(); // tabId -> Map(dedupeKey -> item)
  const drmFrames = new Map(); // tabId -> Set of frame keys that used encrypted media ('*' = the whole tab)
  let seq = 0;
  const frameIsProtected = (tabId, item) => {
    const set = drmFrames.get(tabId);
    return !!set && (set.has('*') || (!!item.frameUrl && set.has(frameKey(item.frameUrl))));
  };

  const bucket = (tabId) => {
    if (!tabs.has(tabId)) tabs.set(tabId, new Map());
    return tabs.get(tabId);
  };

  return {
    /** @returns {{item: object, isNew: boolean}} */
    add(tabId, item) {
      const b = bucket(tabId);
      const key = dedupeKey(item.url);
      const existing = b.get(key);
      if (existing) {
        // Signed URLs expire: keep the newest URL, but merge rather than replace: the network layer sees request headers
        // (Cookie) that the page layer cannot, and a later report must not erase them. DRM is sticky.
        const known = Object.fromEntries(Object.entries(item).filter(([, v]) => v !== undefined));
        const merged = {
          ...existing, ...known, headers: { ...existing.headers, ...item.headers }, id: existing.id,
          protected: existing.protected || item.protected || frameIsProtected(tabId, item) || undefined,
        };
        b.set(key, merged);
        return { item: merged, isNew: false };
      }
      const created = { ...item, id: String(++seq) };
      if (frameIsProtected(tabId, item)) created.protected = true;
      b.set(key, created);
      onChange?.(tabId, b.size);
      return { item: created, isNew: true };
    },
    list: (tabId) => [...(tabs.get(tabId)?.values() ?? [])],
    get: (tabId, id) => [...(tabs.get(tabId)?.values() ?? [])].find((i) => i.id === id),
    clear(tabId) {
      tabs.delete(tabId);
      drmFrames.delete(tabId);
      onChange?.(tabId, 0);
    },
    /** Flags DRM for the items found in one frame (matched by origin + path; the fragment is ignored). */
    markProtected(tabId, frameUrl) {
      const key = frameUrl ? frameKey(frameUrl) : '*';
      if (!drmFrames.has(tabId)) drmFrames.set(tabId, new Set());
      drmFrames.get(tabId).add(key); // also covers items that are reported later
      for (const it of tabs.get(tabId)?.values() ?? []) {
        if (key === '*' || (it.frameUrl && frameKey(it.frameUrl) === key)) it.protected = true;
      }
    },
    /** Restores a persisted list (service workers lose memory when they sleep). */
    load(tabId, items) {
      const b = bucket(tabId);
      for (const it of items) {
        b.set(dedupeKey(it.url), it);
        seq = Math.max(seq, Number(it.id) || 0);
      }
    },
    snapshot: () => Object.fromEntries([...tabs].map(([id, b]) => [id, [...b.values()]])),
  };
}
