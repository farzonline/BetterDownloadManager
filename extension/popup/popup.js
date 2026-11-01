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
