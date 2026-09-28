// Runs inside Voiceitt's page. Reads the transcript text box directly from the page
// (no accessibility tree, no positions, no styling names — so nothing here breaks when
// Voiceitt restyles itself) and hands its text to the Voice OS app.
//
// The bridge can also ask us to clear the box (spoken "new paragraph"). We only do
// that if the box still holds exactly the text the bridge has already typed, so words
// spoken in the meantime are never wiped before they are typed.

const POLL_MS = 250;        // how often we look at the box
const HEARTBEAT_MS = 1000;  // re-send unchanged text this often so the bridge knows we're alive

// The real transcript box is a Material-UI multiline <textarea>. MUI also renders a
// hidden twin (aria-hidden, read-only) used only to measure height — skip that one.
function findBox(doc) {
  const boxes = Array.from(doc.querySelectorAll('textarea')).filter(
    (t) => t.getAttribute('aria-hidden') !== 'true' && !t.readOnly && t.getClientRects().length > 0
  );
  if (boxes.length === 0) return null;
  const score = (t) => (String(t.className).includes('inputMultiline') ? 1e9 : 0) + t.clientWidth * t.clientHeight;
  boxes.sort((a, b) => score(b) - score(a));
  return boxes[0];
}

// React ignores plain `el.value = ''` because it tracks the value itself; going through
// the native setter and firing a real input event is what makes it update its own state.
function setValue(win, el, value) {
  const setter = Object.getOwnPropertyDescriptor(win.HTMLTextAreaElement.prototype, 'value').set;
  setter.call(el, value);
  el.dispatchEvent(new win.Event('input', { bubbles: true }));
}

// Decides what to do with the bridge's reply. Returns true if the box was cleared.
function maybeClear(win, el, reply) {
  if (!reply || reply.clear !== true || typeof reply.expect !== 'string') return false;
  if (el.value !== reply.expect) return false; // more was said since — don't wipe it
  setValue(win, el, '');
  return true;
}

function start() {
  let lastText = null;
  let lastPostAt = 0;
  let busy = false;
  let dead = false;

  const post = (text) =>
    new Promise((resolve) => {
      try {
        chrome.runtime.sendMessage({ type: 'voiceitt-text', text }, (reply) => {
          resolve(chrome.runtime.lastError ? null : reply || null);
        });
      } catch (e) {
        dead = true; // extension was reloaded/removed; this page's copy is orphaned
        resolve(null);
      }
    });

  async function tick() {
    if (dead || busy) return;
    const el = findBox(document);
    if (!el) return;

    const text = el.value;
    const now = Date.now();
    if (text === lastText && now - lastPostAt < HEARTBEAT_MS) return;

    busy = true;
    try {
      const reply = await post(text);
      lastText = text;
      lastPostAt = Date.now();
      if (maybeClear(window, el, reply)) lastText = null; // next tick reports the emptied box
    } finally {
      busy = false;
    }
  }

  setInterval(tick, POLL_MS);
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { findBox, setValue, maybeClear };
} else {
  start();
}
