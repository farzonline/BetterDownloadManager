// The offscreen document that runs BDM's browser relay (see src/relay.js). Only chrome.runtime is available here,
// so the relay's address and token come from the service worker.
import { createRelay } from '../src/relay.js';

const relay = createRelay({
  fetch: (...a) => fetch(...a),
  onAuthError: () => chrome.runtime.sendMessage({ type: 'relay-refresh' }).catch(() => {}), // BDM restarted: new token
});

chrome.runtime.onMessage.addListener((msg) => {
  if (msg?.type === 'relay-config' && msg.base && msg.token) relay.configure(msg);
});

chrome.runtime.sendMessage({ type: 'relay-get-config' }).then((c) => c?.base && relay.configure(c)).catch(() => {});
relay.start();
