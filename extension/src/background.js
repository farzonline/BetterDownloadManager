import { createRegistry } from './detect/registry.js';
import { createNetworkDetector } from './detect/network.js';
import { DEFAULT_SETTINGS, takeOverDownload } from './takeover.js';
import { buildMessage, sendToHost } from './host.js';

const ALT_WINDOW_MS = 10_000;
const altClicks = new Map(); // url -> timestamp
const tabs = new Map(); // tabId -> {url, title}

// MV3 service workers sleep and lose memory: persist the registry in session storage.
const persist = (tabId) =>
  chrome.storage.session.set({ [`tab:${tabId}`]: registry.list(tabId) }).catch(() => {});
const registry = createRegistry({
  onChange: (tabId, count) => {
    chrome.action.setBadgeText({ tabId, text: count ? String(count) : '' }).catch(() => {});
    persist(tabId);
  },
});
const ready = chrome.storage.session.get(null).then((all) => {
  for (const [k, items] of Object.entries(all)) if (k.startsWith('tab:')) registry.load(Number(k.slice(4)), items);
}).catch(() => {});

async function getSettings() {
  const { settings } = await chrome.storage.sync.get('settings');
  return { ...DEFAULT_SETTINGS, ...settings };
}

async function probe(url, headers) {
  const res = await fetch(url, { headers: { Range: 'bytes=0-255' }, credentials: 'include' });
  const reader = res.body.getReader();
  const { value } = await reader.read();
  reader.cancel().catch(() => {});
  return value?.slice(0, 256);
}

const detector = createNetworkDetector({
  registry,
  getTab: (id) => tabs.get(id),
  probe,
  selfOrigin: `chrome-extension://${chrome.runtime.id}`,
});
const filter = { urls: ['<all_urls>'] };
chrome.webRequest.onSendHeaders.addListener((d) => detector.onSendHeaders(d), filter, ['requestHeaders', 'extraHeaders']);
chrome.webRequest.onHeadersReceived.addListener(async (d) => { await ready; await detector.onHeadersReceived(d); }, filter, ['responseHeaders', 'extraHeaders']);

chrome.tabs.onUpdated.addListener((id, change, tab) => {
  tabs.set(id, { url: tab.url, title: tab.title });
  if (change.status === 'loading' && change.url) { registry.clear(id); chrome.storage.session.remove(`tab:${id}`).catch(() => {}); }
});
chrome.tabs.onRemoved.addListener((id) => { tabs.delete(id); registry.clear(id); chrome.storage.session.remove(`tab:${id}`).catch(() => {}); });

const notify = (message) =>
  chrome.notifications.create({ type: 'basic', iconUrl: 'icon.png', title: 'Better Download Manager', message }).catch(() => {});

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  (async () => {
    try {
      await takeOverDownload(item, {
        settings: await getSettings(),
        altRecent: (url) => Date.now() - (altClicks.get(url) ?? 0) < ALT_WINDOW_MS,
        sendToHost,
        buildMessage: (i) => buildMessage(i, { cookies: chrome.cookies }),
        cancel: (id) => chrome.downloads.cancel(id),
        erase: (id) => chrome.downloads.erase({ id }),
        notify,
      });
    } finally {
      suggest(); // always release the browser's own download flow
    }
  })();
  return true; // async suggest
});

chrome.runtime.onMessage.addListener((msg, sender, respond) => {
  (async () => {
    await ready;
    if (msg.type === 'alt-click') { altClicks.set(msg.url, Date.now()); return {}; }
    if (msg.type === 'list') return { items: registry.list(msg.tabId) };
    if (msg.type === 'debug-registry') return registry.snapshot(); // used by the acceptance tests
    if (msg.type === 'download') {
      const item = registry.get(msg.tabId, msg.itemId);
      if (!item) return { ok: false, error: 'That item is no longer available.' };
      try {
        return await sendToHost(await buildMessage(item, { cookies: chrome.cookies, askQuality: !!msg.ask }));
      } catch (e) { return { ok: false, error: String(e.message ?? e) }; }
    }
    return {};
  })().then(respond);
  return true;
});

// Context menus
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'bdm-link', title: 'Download with BDM', contexts: ['link', 'video', 'audio', 'image'] });
    chrome.contextMenus.create({ id: 'bdm-all-links', title: 'BDM: download all links on page', contexts: ['page'] });
    chrome.contextMenus.create({ id: 'bdm-all-media', title: 'BDM: download all images/media on page', contexts: ['page'] });
  });
});

const MAX_BULK = 500;
const collect = (selector, attr) => () =>
  [...document.querySelectorAll(selector)].map((e) => e[attr]).filter((u) => /^https?:/.test(u));

async function sendMany(tab, urls) {
  const unique = [...new Set(urls)].slice(0, MAX_BULK);
  for (const url of unique) {
    const msg = await buildMessage({ kind: 'file', url, pageUrl: tab.url, title: tab.title, headers: tab.url ? { Referer: tab.url } : {} }, { cookies: chrome.cookies });
    const r = await sendToHost(msg);
    if (!r.ok) { notify(r.error ?? 'BDM is not available.'); return; }
  }
  notify(`Sent ${unique.length} item(s) to BDM.`);
}

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'bdm-link') {
    const url = info.linkUrl ?? info.srcUrl;
    if (!url) return;
    const r = await sendToHost(await buildMessage({ kind: 'file', url, pageUrl: tab?.url, title: tab?.title, headers: tab?.url ? { Referer: tab.url } : {} }, { cookies: chrome.cookies }));
    if (!r.ok) notify(r.error ?? 'BDM is not available.');
    return;
  }
  const func = info.menuItemId === 'bdm-all-links' ? collect('a[href]', 'href') : collect('img[src],video[src],audio[src],source[src]', 'src');
  const [{ result }] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func });
  await sendMany(tab, result ?? []);
});
