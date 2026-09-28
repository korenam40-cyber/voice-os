// Relays Voiceitt's text from the page to the Voice OS app on this PC.
// config.js (written by the app, holds the port and a private token) is not in the
// repo — the app generates it next to these files.
importScripts('config.js');

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || msg.type !== 'voiceitt-text') return;

  fetch(`http://127.0.0.1:${BRIDGE.port}/voiceitt`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Bridge-Token': BRIDGE.token },
    body: JSON.stringify({ text: msg.text }),
  })
    .then((r) => (r.ok ? r.json() : { error: r.status }))
    .then(sendResponse)
    .catch((e) => sendResponse({ error: String(e) }));

  return true; // keep the channel open for the async response
});
