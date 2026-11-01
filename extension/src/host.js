// Talks to the native messaging host (com.bdm.host).

export const HOST_NAME = 'com.bdm.host';

/** @returns {Promise<{ok: boolean, error?: string, appStarted?: boolean}>} */
export function sendToHost(msg, api = globalThis.chrome) {
  return new Promise((resolve) => {
    api.runtime.sendNativeMessage(HOST_NAME, msg, (reply) => {
      const err = api.runtime.lastError;
      if (err || !reply) resolve({ ok: false, error: err?.message ?? 'The BDM host did not reply.' });
      else resolve(reply);
    });
  });
}

/**
 * Sends a found file to BDM. Sites behind bot protection refuse anything but a real browser; then BDM answers
 * useBrowser and the browser downloads the file itself (the way 4saved works), so a found file is never lost.
 * @returns {Promise<{ok: boolean, error?: string, viaBrowser?: boolean}>}
 */
export async function deliver(msg, { send = sendToHost, download, skipTakeover }) {
  const r = await send(msg);
  if (r.ok || !r.useBrowser || msg.op !== 'add') return r;
  skipTakeover(msg.url); // our own browser download must not be handed straight back to BDM
  try {
    await download({ url: msg.url });
    return { ok: true, viaBrowser: true };
  } catch (e) {
    return { ok: false, error: String(e?.message ?? e) };
  }
}

const STREAM_KINDS = new Set(['hls', 'dash']);
const isWeb = (u) => /^https?:\/\//i.test(u ?? '');

/** Builds the host message for a registry item. Throws for DRM-protected streams. */
export async function buildMessage(item, { cookies, askQuality = false, userAgent = globalThis.navigator?.userAgent } = {}) {
  if (item.protected) throw new Error('This stream is protected (DRM) and cannot be downloaded.');
  const headers = { ...item.headers };
  if (!isWeb(headers.Referer)) delete headers.Referer; // e.g. Chrome's "about:client"
  // Many video CDNs refuse requests that do not look like the browser that found the link.
  if (userAgent && !headers['User-Agent']) headers['User-Agent'] = userAgent;
  let cookie = headers.Cookie ?? headers.cookie;
  delete headers.Cookie;
  delete headers.cookie;
  if (!cookie && cookies) {
    try {
      const jar = await cookies.getAll({ url: item.url });
      if (jar.length) cookie = jar.map((c) => `${c.name}=${c.value}`).join('; ');
    } catch { /* no cookie access for this URL: send without */ }
  }
  const stream = STREAM_KINDS.has(item.kind);
  return {
    op: stream ? (askQuality ? 'addStreamAsk' : 'addStream') : 'add',
    url: item.url,
    pageUrl: isWeb(item.pageUrl) ? item.pageUrl : undefined,
    title: item.title,
    headers,
    cookie,
    kind: stream ? item.kind : undefined,
    manifestBody: item.manifestBody,
  };
}

/** Message asking the app to download the video on a web page with yt-dlp (the app shows its quality chooser). */
export async function buildPageMessage(tab, { cookies } = {}) {
  if (!/^https?:\/\//i.test(tab?.url ?? '')) throw new Error('This tab is not a web page.');
  let cookie;
  if (cookies) {
    try {
      const jar = await cookies.getAll({ url: tab.url });
      if (jar.length) cookie = jar.map((c) => `${c.name}=${c.value}`).join('; ');
    } catch { /* send without cookies */ }
  }
  return { op: 'addPageAsk', url: tab.url, pageUrl: tab.url, title: tab.title, headers: { Referer: tab.url }, cookie };
}
