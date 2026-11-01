import { DEFAULT_SETTINGS } from '../src/takeover.js';

const $ = (id) => document.getElementById(id);
const { settings } = await chrome.storage.sync.get('settings');
const s = { ...DEFAULT_SETTINGS, ...settings };

$('enabled').checked = s.enabled;
$('floatingButton').checked = s.floatingButton;
$('minSize').value = Math.round(s.minSizeBytes / 1024);
$('types').value = s.types.join(' ');
$('excluded').value = s.excludedHosts.join('\n');

$('save').onclick = async () => {
  await chrome.storage.sync.set({
    settings: {
      enabled: $('enabled').checked,
      floatingButton: $('floatingButton').checked,
      minSizeBytes: Math.max(0, Number($('minSize').value) || 0) * 1024,
      types: $('types').value.toLowerCase().split(/[\s,.]+/).filter(Boolean),
      excludedHosts: $('excluded').value.split('\n').map((h) => h.trim()).filter(Boolean),
    },
  });
  $('status').textContent = 'Saved';
};
