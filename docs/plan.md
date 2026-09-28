# Plan: Voice control of Windows, steps 1–3

## Context

The user wants to control Windows entirely by voice. Windows' built-in voice
features can't handle their speech, and Hebrew has no support there at all. The
pieces already exist: VoiceittBridge (C#: hotkeys, `SendInput`, UI Automation,
app activation) and the local Hebrew dictation service
(`src/ui/dictation_server.py`), which gets 4.7% of words wrong on the user's own
held-out recordings. This plan covers steps 1–3: stable dictation, a voice
command mode, and clicking by number. The AI agent (step 4) comes later and sits
on top of this.

**The user's choices (2026-09-25):**
- Commands in **both** Hebrew and English. Hebrew goes through our model;
  English goes through Voiceitt, then the bridge.
- Command mode is entered with a **spoken prefix word**, not a hotkey.
- The C# work happens **in this session**, after attaching
  `korenam40-cyber/voiceitt-bridge`.

**Two design rules come from the project's history.** The worst failure is
plausible wrong output. Here that means a misheard command that silently does
the wrong thing.

1. Commands are recognized **against a closed list, with a "none of these"
   answer**. Commands are never free text run through the 7B corrector, because
   the corrector can rewrite one command into another.
2. The prefix works **by position, not by vocabulary**. This is the same rule
   `src/language/spoken_punct.py` uses. The prefix counts only when it is a
   whole phrase on its own: say "מחשב", pause, then the command. A "מחשב"
   inside a dictated sentence is typed like any other word.

## Research (2026-09-27): "JARVIS / Her" assistants built with Claude

People have built these, including on Windows. Many build videos exist. All of
the builds share the same four-part loop:

**ears (speech-to-text) → brain (Claude) → hands (computer control) → mouth
(text-to-speech)**

Three examples:
- **Julian-Ivanov/jarvis-voice-assistant (Windows):**
  - ears: Chrome's Web Speech API;
  - brain: Claude Haiku;
  - hands: Playwright and PowerShell/Win32;
  - mouth: ElevenLabs;
  - wake trigger: a double clap;
  - no confirmation step before actions.
- **ethanplusai/jarvis (macOS):**
  - brain: a long-running Claude Code process on the user's own subscription;
  - mouth: Fish Audio, one sentence at a time;
  - no wake word: you click to talk.
- **VoiceMode**, and Claude's own voice mode: general voice layers for Claude.
  They send audio to the cloud.

**Every one of them uses stock speech recognition,** which fails on this user's
speech. Our recognizer (4.7% of words wrong on the user's voice) is exactly the
missing part: we replace the "ears" and keep the rest.

**Building blocks found:**
- **Hands on Windows:** CursorTouch/Windows-MCP (MIT licence, based on UI
  Automation, widely used). Claude Desktop's computer use also runs on Windows;
  the CLI version is macOS only.
- **Hebrew voice (mouth):**
  - **Phonikud + Piper:** open source, runs locally, real time, presented at
    Interspeech 2026. Private and free.
  - **ElevenLabs v3:** the most natural Hebrew voice, but it is a cloud service.
- **Brain:** Claude through the Agent SDK (Python). It runs in the cloud, so it
  frees the GPU conflict flagged earlier: the local GPU keeps only
  recognition and a small TTS model.

**What won't match the films:**
- Delay of about 3–5 s per turn (1.6 s recognition, plus Claude, plus TTS).
- No talking over it mid-sentence at first. Interrupting while it speaks
  (barge-in) is a later addition.
- The user's typed text goes to Anthropic. Audio never leaves the PC.

## Step 1: make dictation usable (short, first)

Two bugs were fixed today and haven't been tried yet: glued punctuation
(`spoken_punct.unglue`) and the leading newline eaten by `TypeDictation`.

- Attach the bridge repo (`add_repo`, then clone) and check that the
  `TypeDictation` trim fix is committed there.
- Add a structured per-phrase log, `data/dictation/phrases.jsonl`, written in
  `live_worker` and `/api/stop` in `src/ui/dictation_server.py`. Each line holds
  the raw ASR text, the final text, the timings (pause wait, recognition, typed)
  and any spoken marks that were applied. **No audio.** Today's two bugs were
  found in the log, so make the log complete.
- The user dictates for about a week. Fix only what the log shows.
- **Exit criterion:** the user says dictation is usable for mail and Word.
  Step 2 builds on the same phrase pipeline, so it has to be trusted first.

## Step 2: command mode

### 2a. The command list, as data

- New file: `data/personal/commands.json`, the same pattern as
  `spoken_marks.json`. The list is data, not code, so adding a command needs no
  code release.
- Each entry has an `id`, Hebrew phrasings, English phrasings, and an `action`
  that the bridge executes.
- About 30 commands to start, built from the user's daily tasks:
  - open or switch to an app;
  - close window, minimize, maximize;
  - scroll up or down, page up or down;
  - Enter, Escape, Tab, undo, copy, paste, select all;
  - start or stop dictation;
  - show numbers, show grid;
  - cancel.
- The **prefix word** is chosen by the user: something distinct that they say
  easily. There is one Hebrew prefix and one English prefix.
- A loader goes next to `spoken_punct`'s loader, falls back to defaults when the
  file is missing, and has its own tests.

### 2b. Closed-set recognition (Python)

- New method `WhisperHF.score_texts(audio, texts)` in `src/asr/whisper_hf.py`.
  - It runs one teacher-forced decoder pass per candidate, batched, and returns
    the average log-probability of each.
  - It reuses the feature extraction already in `transcribe`.
  - It uses the main model only: no corrector, and no extra listener.
- New module `src/language/commands.py` with
  `match(audio_or_text, candidates) -> CommandMatch | None`. The decision rule:
  - the best score must clear a threshold;
  - it must beat the runner-up by a set margin;
  - and it must not fall far below the free decode of the same audio. That last
    check is the "none of these" answer.
- **English path:** the bridge sends Voiceitt's text to
  `POST /api/command/match_text`. It is matched by normalized edit distance with
  the same threshold and margin rule. This keeps one matcher and one decision
  rule for both languages.

### 2c. Service integration (`src/ui/dictation_server.py`)

- **Always-listening mode:** one hotkey or voice start, then every phrase goes
  through a router in `live_worker`:
  - if the phrase is only the prefix (both the closed-set scorer and the text
    agree), the service enters an `awaiting_command` state with a short timeout;
  - in that state, the next phrase is scored against the command list;
  - the result is a command event or a rejection. It is never typed text.
- Command events go through the same `/api/segments` queue as typed phrases,
  with a `type` field: `{"type":"command","id":...,"score":...}` or
  `{"type":"rejected"}`. The bridge already polls this queue.
- `--idle-exit-seconds` has to change for this mode, because the model now stays
  loaded while listening. That is 19.5 GB of GPU memory, which is fine until
  step 4 adds an AI model.

### 2d. Bridge (C#)

- Execute command `action`s with the existing `SendInput` and app-activation
  code.
- Show a small **status panel** with the current mode, what was heard, and what
  ran. A rejection shows "not understood" and does nothing.
- English: route Voiceitt text that starts with the English prefix to
  `/api/command/match_text`.

### 2e. Recording, calibration and measurement

- **Command bank** in the calibration recorder:
  - each Hebrew command phrase 5 times;
  - the prefix 10 times;
  - the numbers 1–30 three times each (for step 3).
- Takes are saved with `mode="command"` in `src/storage/personal_dataset.py`.
  They are held out from ASR training until calibration is done.
- **Metrics are added to `src/evaluation/`, the one benchmark harness, never as
  a separate scorer:**
  - command accuracy;
  - false rejects;
  - **false accepts**, measured by running the existing 647 dictation takes
    through the prefix and command detector. They are a ready-made set of
    "not a command" audio.
- Set the threshold and margin on part of the takes. Report on the rest.
  - **Target:** 0 false accepts on the dictation set, and command accuracy of
    95% or better.
  - If the target isn't met, raise the threshold. Fewer commands accepted is
    better than wrong ones.

## Step 3: clicking by number (mostly C#, in the bridge)

- The "show numbers" command enumerates elements of the foreground window
  through UI Automation (`FindAll`):
  - the element types are Button, Hyperlink, MenuItem, TabItem, ListItem,
    CheckBox, Edit and ComboBox;
  - only elements that are on screen and enabled are included, capped at about
    99.
- The numbers are drawn on a topmost, click-through, transparent overlay window.
- While the overlay is showing, the service is in a **modal state**. The next
  phrase is scored only against the numbers 1–N plus "cancel", with no prefix
  needed. Numbers are passed to `score_texts` as the candidate list for that
  moment.
- A spoken number acts on its element:
  - `InvokePattern`, `SelectionItemPattern` or `TogglePattern` if the element
    supports one;
  - otherwise a `SendInput` click at the element's centre;
  - Edit fields get focus, then dictation resumes.
- **"Show grid" fallback**, for elements UI Automation can't see (some apps and
  canvases): a 3×3 numbered grid that narrows down recursively, with "click",
  "double click" and "right click" at the end.
- **Known limits:**
  - Admin windows and UAC prompts don't respond unless the bridge runs elevated.
    Document this and don't work around it silently.
  - Games and canvas apps expose nothing to UI Automation, so they fall back to
    the grid.

## Step 4: talk to the assistant (the JARVIS / Her part, after steps 2–3 work)

It reuses the step-2 router: a **second prefix, the assistant's name** (chosen
and trained by the user), sends the next phrases to Claude instead of the
command list.

- **Ears:** the full engine, corrector included, since instructions are free
  text. The recognized text is shown on the status panel before it is sent.
- **Brain:** new `src/assistant/agent.py`, using the Claude Agent SDK. The model
  is chosen at build time using the claude-api reference. It has three tools:
  - Windows-MCP (UI Automation);
  - the step-2 command actions;
  - mail and calendar.
  It keeps short conversation memory, and a system prompt says to reply briefly
  and in Hebrew.
- **Confirmation is structural, not a prompt instruction.** The SDK's
  tool-permission callback stops every action that changes something (send,
  delete, type, click a submit button). The assistant speaks its plan. The user
  answers כן/לא (yes/no), and that answer is scored against a closed set of
  yes/no. A "no", or silence, cancels. Read-only tools run without asking.
- **Mouth:** new `src/assistant/tts.py`.
  - Phonikud + Piper by default: local and private.
  - ElevenLabs optional: the user's choice, sent only as text.
  - It speaks sentence by sentence as the reply streams in.
  - The microphone is muted while it speaks, so the assistant never hears itself.
- **Measure:**
  - turn delay (target 5 s or less to first spoken word);
  - task success on the user's own list of 20 tasks;
  - zero unconfirmed actions that change something.

## Critical files

- **Python:**
  - `src/asr/whisper_hf.py`: `score_texts`
  - `src/language/commands.py`: new
  - `src/ui/dictation_server.py`: router, event types, phrase log,
    `/api/command/*`
  - `src/storage/personal_dataset.py`: `mode="command"`
  - `src/evaluation/`: command metrics
  - `src/ui/calibration_server.py`: command bank
  - `data/personal/commands.json`
- **C#:** in `voiceitt-bridge`, the command executor, status panel, number
  overlay and grid, and English prefix routing.
- **Reuse:**
  - `has_speech` and `find_phrase_end` (phrase cutting);
  - the `spoken_punct` data-file loader pattern;
  - the `/api/segments` polling in the bridge;
  - the existing `SendInput` and UI Automation code in the bridge.

## Verification

- **Unit tests,** as plain scripts in `tests/`:
  - `tests/test_commands.py`: the decision rule, prefix position (a prefix
    inside a sentence is typed, a prefix alone arms command mode) and the file
    loader;
  - extend `tests/test_dictation_server.py` with a fake engine, covering the
    command event flow, the rejection flow and the timeout.
- **Measured, on the user's takes:** command accuracy, false rejects, and
  false accepts on the 647 dictation takes, all run through the benchmark
  harness.
- **On Windows, by the user:**
  - open Word by voice, dictate a line, say "מחשב" then "select all", then
    "copy";
  - "show numbers", then say a number to click a button in Outlook;
  - a sentence containing the prefix word mid-sentence types normally.
- Nothing is committed or pushed until the user asks.
