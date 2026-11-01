// Alt+click on a link tells the background worker to let the browser download it itself
// (the downloads API cannot see modifier keys).
addEventListener('click', (e) => {
  if (!e.altKey) return;
  const a = e.target?.closest?.('a[href]');
  if (a) chrome.runtime.sendMessage({ type: 'alt-click', url: a.href }).catch(() => {});
}, true);
