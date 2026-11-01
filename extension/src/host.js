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

const STREAM_KINDS = new Set(['hls', 'dash']);

/** Builds the host message for a registry item. Throws for DRM-protected streams. */
export async function buildMessage(item, { cookies, askQuality = false } = {}) {
  if (item.protected) throw new Error('This stream is protected (DRM) and cannot be downloaded.');
  const headers = { ...item.headers };
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
    pageUrl: item.pageUrl,
    title: item.title,
    headers,
    cookie,
    kind: stream ? item.kind : undefined,
    manifestBody: item.manifestBody,
  };
}
