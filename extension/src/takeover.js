// Decides which browser downloads BDM takes over, and hands them over without ever dropping one:
// the browser download is cancelled only after the host confirmed it accepted the job.

export const DEFAULT_SETTINGS = {
  enabled: true,
  minSizeBytes: 512 * 1024,
  floatingButton: true,
  excludedHosts: [],
  types: [
    'zip', 'rar', '7z', 'gz', 'tgz', 'bz2', 'xz', 'tar', 'iso', 'img', 'dmg', 'exe', 'msi', 'msix', 'appx', 'apk', 'deb', 'rpm', 'jar',
    'cab', 'bin', 'mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm', 'm4v', 'mp3', 'flac', 'wav', 'aac', 'm4a', 'ogg', 'opus',
    'pdf', 'epub', 'docx', 'xlsx', 'pptx',
  ],
};

const MIME_PREFIXES = ['video/', 'audio/'];
const MIME_EXACT = new Set([
  'application/zip', 'application/x-zip-compressed', 'application/x-rar-compressed', 'application/vnd.rar',
  'application/x-7z-compressed', 'application/x-msdownload', 'application/x-msi', 'application/x-iso9660-image',
  'application/vnd.android.package-archive', 'application/x-gzip', 'application/gzip', 'application/x-tar',
]);

function extensionOf(item) {
  const fromName = /\.([a-z0-9]+)$/i.exec(item.filename ?? '');
  if (fromName) return fromName[1].toLowerCase();
  try {
    const fromUrl = /\.([a-z0-9]+)$/i.exec(new URL(item.url).pathname);
    return fromUrl ? fromUrl[1].toLowerCase() : '';
  } catch { return ''; }
}

function hostExcluded(url, excluded) {
  let host;
  try { host = new URL(url).hostname.toLowerCase(); } catch { return false; }
  return excluded.some((e) => host === e.toLowerCase() || host.endsWith('.' + e.toLowerCase()));
}

export function shouldTakeOver(item, settings, { altKey = false } = {}) {
  if (!settings.enabled || altKey) return false;
  if (hostExcluded(item.url, settings.excludedHosts ?? [])) return false;
  if (item.fileSize > 0 && item.fileSize < settings.minSizeBytes) return false; // unknown sizes (<= 0) are taken
  const mime = (item.mime ?? '').toLowerCase();
  return settings.types.includes(extensionOf(item)) || MIME_EXACT.has(mime) || MIME_PREFIXES.some((p) => mime.startsWith(p));
}

/** @returns {Promise<'taken'|'skipped'|'fallback'>} */
export async function takeOverDownload(item, deps) {
  const url = item.finalUrl || item.url;
  const alt = deps.altRecent(url) || deps.altRecent(item.url);
  if (!shouldTakeOver(item, deps.settings, { altKey: alt })) return 'skipped';

  let reply;
  try {
    const msg = await deps.buildMessage({
      kind: 'file', url, pageUrl: item.referrer || undefined, title: item.filename || undefined,
      headers: item.referrer ? { Referer: item.referrer } : {},
    });
    reply = await deps.sendToHost(msg);
  } catch (e) {
    reply = { ok: false, error: String(e?.message ?? e) };
  }
  if (!reply?.ok) {
    deps.notify(`BDM could not take this download (${reply?.error ?? 'no reply'}). The browser will download it instead.`);
    return 'fallback';
  }
  await deps.cancel(item.id);
  await deps.erase(item.id);
  return 'taken';
}
