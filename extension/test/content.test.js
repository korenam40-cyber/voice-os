// Tests for the Chrome connector's page logic (extension/content.js).
// Run:  cd extension/test && npm install jsdom && node content.test.js
const { JSDOM } = require('jsdom');
const assert = require('assert');
const { findBox, setValue, maybeClear } = require('../content.js');

let passed = 0;
const t = (name, fn) => { fn(); passed++; console.log('  ok  -', name); };

// Mirrors what Voiceitt renders: a Material-UI multiline field = the real textarea
// plus a hidden, read-only, aria-hidden twin MUI uses to measure height. jsdom has no
// layout, so give elements a client rect the way a rendered page would.
function page() {
  const dom = new JSDOM(`<body>
    <input class="omnibox" value="web.voiceitt.com/dictate">
    <textarea id="search" class="MuiInputBase-input"></textarea>
    <div class="MuiInputBase-root">
      <textarea id="real" class="MuiInputBase-input MuiOutlinedInput-input MuiInputBase-inputMultiline css-u2vo5a"></textarea>
      <textarea id="twin" aria-hidden="true" readonly tabindex="-1" class="MuiInputBase-input MuiInputBase-inputMultiline"></textarea>
    </div></body>`);
  const { window } = dom;
  for (const el of window.document.querySelectorAll('textarea')) el.getClientRects = () => [{}];
  return window;
}

// A React-like controlled input: it owns the state and only believes 'input' events.
function controlled(win, el) {
  const state = { value: '', changes: 0 };
  el.addEventListener('input', () => { state.value = el.value; state.changes++; });
  return state;
}

t('findBox picks the real multiline box, not its hidden twin or other fields', () => {
  const w = page();
  assert.strictEqual(findBox(w.document).id, 'real');
});

t('findBox ignores a box that is not rendered', () => {
  const w = page();
  w.document.getElementById('real').getClientRects = () => [];
  assert.notStrictEqual((findBox(w.document) || {}).id, 'real');
});

t('findBox returns null when the page has no text box', () => {
  const w = new JSDOM('<body><p>loading</p></body>').window;
  assert.strictEqual(findBox(w.document), null);
});

t('setValue clears the box AND fires the input event React listens for', () => {
  const w = page(); const el = w.document.getElementById('real'); const st = controlled(w, el);
  el.value = 'hello there'; st.value = 'hello there';
  setValue(w, el, '');
  assert.strictEqual(el.value, '');
  assert.strictEqual(st.value, '', 'the app-side state must see the change');
  assert.strictEqual(st.changes, 1);
});

t('maybeClear clears when the box still holds exactly what was typed', () => {
  const w = page(); const el = w.document.getElementById('real');
  el.value = 'typed already';
  assert.strictEqual(maybeClear(w, el, { clear: true, expect: 'typed already' }), true);
  assert.strictEqual(el.value, '');
});

t('maybeClear REFUSES when more was spoken since (never wipes untyped words)', () => {
  const w = page(); const el = w.document.getElementById('real');
  el.value = 'typed already and some new words';
  assert.strictEqual(maybeClear(w, el, { clear: true, expect: 'typed already' }), false);
  assert.strictEqual(el.value, 'typed already and some new words');
});

t('maybeClear does nothing without a clear request, or with a malformed reply', () => {
  const w = page(); const el = w.document.getElementById('real'); el.value = 'abc';
  for (const reply of [null, undefined, {}, { clear: false, expect: 'abc' }, { clear: true }, { clear: true, expect: 5 }, { error: 500 }])
    assert.strictEqual(maybeClear(w, el, reply), false);
  assert.strictEqual(el.value, 'abc');
});

console.log(`\n${passed} tests passed`);
