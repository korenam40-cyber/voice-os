# Voice OS

Control Windows entirely by voice. Built for one user with a speech impairment,
who dictates through **Voiceitt** (English) and a **personal Hebrew recognizer**
([hebrew-atypical-asr](https://github.com/korenam40-cyber/hebrew-atypical-asr)).

Voice OS grew out of [Voiceitt Bridge](https://github.com/korenam40-cyber/voiceitt-bridge)
and does everything the bridge does, plus voice commands and, later, the
assistant **Baby**. It replaces the bridge **only once it is proven to work**;
until then the bridge stays the everyday tool, unchanged.

**Never run both at once** -- both would type every word twice. Voice OS won't
start while the bridge is open, and pauses itself if the bridge is opened.

## What it does

- **Dictation** -- reads Voiceitt's text (Chrome connector, with a screen-reading
  fallback: F8) and types it into the window you pick. Hebrew dictation with the
  local engine: Ctrl+Alt+D; fix the last dictation: Ctrl+Alt+F; teach a word:
  Ctrl+Alt+W; pause: Ctrl+Alt+P.
- **Voice commands** -- say **"Computer"**, then a command: "Computer, scroll
  down", "Computer, open mail", "Computer, grab that". The command panel lists
  them all ("Computer, show commands" / "hide commands").
  - The wake word counts only at the start of what you say.
  - Command words are never typed. When a command isn't understood, nothing happens.
  - Confirmation by tones (default), speech or silence ("Computer, use sounds /
    talk to me / be quiet"). While the computer talks, the microphones are muted,
    so Voiceitt never types its voice.
  - "Computer, this is my mail" connects a command to the program in front.
    "Computer, edit commands" opens the list in Notepad.
- **Practice** -- "Computer, practice commands" shows one command at a time;
  say it and see whether it was understood. Nothing is carried out. Every try
  is saved as text in `%APPDATA%\VoiceOS\practice.jsonl` for tuning phrasings.
- **Baby** (planned) -- see `docs/requirements.md` and `docs/plan.md`.

## Setup

- **Download**: https://github.com/korenam40-cyber/voice-os/releases/tag/latest --
  `VoiceOS.exe`, rebuilt by GitHub on every change. (To build yourself:
  `dotnet publish -c Release`, Windows, .NET 9 SDK.)
- Chrome connector: Voice OS has its **own** connector (port 47623; the bridge
  uses 47613). Use the connector button once and follow the steps ("Load
  unpacked" in chrome://extensions).
- Settings live in `%APPDATA%\VoiceOS`. On first start they are copied from the
  bridge, so the typing window and Voiceitt calibration carry over.
- Tests for the command logic: `dotnet run --project tests/CommandTests`.

## Docs

- `docs/requirements.md` -- what the user asked for, in their words.
- `docs/plan.md` -- steps 1-4 (dictation, commands, click by number, Baby).
- `docs/status.md` -- what is built, test results, where to pick up.
