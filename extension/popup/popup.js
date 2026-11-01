const list = document.getElementById('list');
const empty = document.getElementById('empty');

function fmtSize(n) {
  if (!n) return '';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(i ? 1 : 0)} ${u[i]}`;
}

function nameOf(item) {
  try { return decodeURIComponent(new URL(item.url).pathname.split('/').pop()) || item.title || item.url; } catch { return item.url; }
}

function render(tabId, items) {
  empty.hidden = items.length > 0;
  list.replaceChildren(...items.map((item) => {
    const row = document.createElement('div');
    row.className = 'item';
    const title = Object.assign(document.createElement('div'), { className: 'title', textContent: item.title || nameOf(item), title: item.url });
    const meta = document.createElement('div');
    meta.className = 'meta';
    meta.append(Object.assign(document.createElement('span'), { className: 'tag', textContent: item.kind.toUpperCase() }),
      [fmtSize(item.size), item.protected ? 'Protected (DRM) — cannot be downloaded' : ''].filter(Boolean).join(' · '));
    const status = Object.assign(document.createElement('span'), { className: 'status' });

    const send = async (ask) => {
      status.textContent = 'Sending…';
      const r = await chrome.runtime.sendMessage({ type: 'download', tabId, itemId: item.id, ask });
      status.textContent = r?.ok ? 'Sent to BDM' : (r?.error ?? 'Failed');
    };
    const btn = (label, onClick, primary) => {
      const b = Object.assign(document.createElement('button'), { textContent: label, disabled: !!item.protected });
      if (primary) b.classList.add('primary');
      b.onclick = onClick;
      return b;
    };
    const buttons = [btn('Download', () => send(false), true)];
    if (item.kind === 'hls' || item.kind === 'dash') buttons.push(btn('Choose quality…', () => send(true)));
    buttons.push(Object.assign(btn('Copy URL', () => navigator.clipboard.writeText(item.url)), { disabled: false }));
    row.append(title, meta, ...buttons, status);
    return row;
  }));
}

const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
const { items } = await chrome.runtime.sendMessage({ type: 'list', tabId: tab.id });
render(tab.id, items);

// ---- per-site switches ----
import { createSiteSettings, hostKey } from '../src/siteSettings.js';
const siteSettings = createSiteSettings(chrome.storage.sync);
const host = hostKey(tab.url);
const deepBox = document.getElementById('deep');
if (host) {
  const site = await siteSettings.get(host);
  const label = document.createElement('label');
  const check = Object.assign(document.createElement('input'), { type: 'checkbox', checked: site.debugger });
  label.append(check, ` Deep scan ${host} (Chrome will show a “debugging this browser” bar)`);
  const take = Object.assign(document.createElement('input'), { type: 'checkbox', checked: site.takeover });
  const label2 = document.createElement('label');
  label2.style.display = 'block';
  label2.append(take, ` Let BDM take over downloads from ${host}`);
  check.onchange = async () => {
    if (check.checked && !(await chrome.permissions.request({ permissions: ['debugger'] }))) { check.checked = false; return; }
    await siteSettings.set(host, { debugger: check.checked });
  };
  take.onchange = () => siteSettings.set(host, { takeover: take.checked });
  deepBox.append(label, label2);
}

// ---- pages the other layers cannot read (YouTube-style sites): hand the whole page to yt-dlp ----
const pageBox = document.getElementById('page');
if (/^https?:/i.test(tab.url ?? '')) {
  const button = Object.assign(document.createElement('button'), { textContent: 'Download this page with yt-dlp' });
  const note = Object.assign(document.createElement('span'), { className: 'status' });
  button.onclick = async () => {
    note.textContent = 'Sending…';
    const r = await chrome.runtime.sendMessage({ type: 'download-page', tabId: tab.id });
    note.textContent = r?.ok ? 'Sent to BDM' : (r?.error ?? 'Failed');
  };
  pageBox.append(button, note);
}
