# Voice OS -- status

## 2026-09-27

- **Step 1 (dictation):** per-phrase log `data/dictation/phrases.jsonl` added.
  The user dictates normally for a week; then fix only what the log shows.
- **Step 2 (commands), English via Voiceitt -- built, not yet tried on Windows.**
  Built first inside the bridge, then moved here (2026-09-28); the bridge's
  pull request was closed unmerged, so the bridge is unchanged:
  - `commands.json`: wake word "computer" (Voiceitt never wrote "wick") + 56 commands (windows, apps, scrolling,
    tabs, editing, typing on/off, video and volume, "move to TV").
    A copy in `%APPDATA%\VoiceittBridge\commands.json` overrides it.
  - `CommandListener.cs`: prefix counts only at the start of an utterance
    (after a 1 s pause or an empty box); words are held, never typed, until
    they stop changing for 1.2 s, then matched. "Wick" alone arms for 6 s.
  - `VoiceCommands.cs`: closed-set matching; runs only when the best command
    beats every other by a margin. Not understood -> nothing happens.
  - `CommandExecutor.cs`: acts on the dictation target window; "open X"
    also makes X the dictation target.
  - `Feedback.cs`: confirmation by **tones** (default), speech, or off --
    "Wick, use sounds / talk to me / be quiet". Tones because the computer's
    audio plays through a sound bar the microphone hears, so spoken words
    could be typed by Voiceitt (user, 2026-09-27).
  - Connecting a command to a program: "Wick, this is my mail" (browser,
    terminal) while that program is the dictation window; or "Wick, edit
    commands", which opens the list in Notepad and reloads it on close.
  - `MicGuard.cs`: while the computer talks (speech feedback now, Baby
    later), every microphone that was on is muted in Windows and unmuted
    0.4 s after; lifts by itself after 15 s; a crash is undone at next start.
    Asked for by the user so Voiceitt never types the computer's own voice.
  - `tests/CommandTests`: 36 checks, all pass.
- **To find out on first use:** what Voiceitt writes for "Wick" (add any
  spelling it uses to `prefixes`, or choose a new wake word), and whether
  "open mail" / "open terminal" start the right programs (if not: "Wick,
  this is my mail").

## Where we stopped (2026-09-27, evening) -- start here tomorrow

The user tests through Voiceitt by dictating into the chat; what arrives is
exactly what the bridge will see. They stopped because their speech was too
tired for Voiceitt -- **fatigue is real: test early in the day, keep sessions
short.**

- **Test 1 (wake word) -- done.** "Computer" 5/5. "Wick" 0/5 (week x4,
  back), "Hey Wick" 0/5 (hey week), "Jarvis" 2/5. Wake word is now Computer.
- **Test 2 (commands) -- done.** 11/14 written correctly. "open edge" came
  out "open up" (added as a phrasing); "close windows" still matches.
- **Voiceitt's own commands** (never written as text; user's list):
  undo, clear, share, finish, play, switch mode. "copy" worked after a
  Voiceitt restart. Alternatives added: undo -> "oops", "take that back";
  play -> "pause", "start video", "stop video"; copy/paste got "grab that",
  "drop it" in case.
- **Test 3 -- done (2026-09-28).** The user's working phrasings: copy =
  "grab that", paste = "drop it" (cannot say "paste"), undo = "take that
  back" (or "undo that", written "under that"), "cut that", "save", "press
  enter", "delete" (new command), video = "start video" / "stop video"
  ("pause" is written "powers"). Did not come through: "copy that", "copy
  text", "paste ...", "oops", "redo". "put it back" added for redo, untested.
- **Next: the first run on Windows** (the user tries it on 2026-09-29). Build
  with `dotnet publish -c Release`, close the bridge, run VoiceOS.exe, set up
  the connector once, say "Computer, scroll down". Ask what they saw and heard.
- **Open questions for Baby** (answer when ready): voice age and English
  accent; look (orb / drawn face / 3D); appear on the TV or not.

## 2026-09-29

- **Download instead of build.** GitHub builds `VoiceOS.exe` on every push to
  main and puts it on the "latest" release (`.github/workflows/build.yml`).
  The user had looked for an exe and found none: they are not expected to build.
- **Practice mode** (user asked; option 2 over a Hebrew command bank):
  "Computer, practice commands". Nothing is carried out; misses show what
  Voiceitt wrote. Attempts logged to `%APPDATA%\VoiceOS\practice.jsonl` --
  ask the user for its misses to tune phrasings.
- **Not built, deliberately deferred:** the Hebrew command bank in the
  recorder (plan step 2e). It belongs to Hebrew commands, which wait until the
  Hebrew model is good enough; the user chose English-through-Voiceitt first.
  The recorder being "empty" is because all 540 prompts are recorded.

## First run on Windows (2026-09-29) -- it works

- The user downloaded VoiceOS.exe, set up the connector, and the panel's
  light is **green**: Voiceitt connected, typing into the chosen window.
  The connector at first did not connect; the Desktop log and the panel light
  were added while finding out. It connected after the user redid the steps.
- **Next:** first real commands ("Computer, scroll down"), then practice mode.

## First practice (2026-09-29): 59 right of 81 tries

Misses were nearly all Voiceitt near-spellings, now added as phrasings:
open age, open male, minimal, crawl up, paid up, close up, cut back, water
(quieter), keep forward / keep back (Voiceitt writes "skip" as "keep"),
use sound. "open edge" also came out "open health" and, eight times, "keep";
the user prefers "open browser", now the primary phrasing (it worked first
try). These real misses are regression checks in tests/CommandTests.

## Second practice + numbers (2026-09-29, afternoon)

- Second practice: 59/79 again, but **none of the first practice's misses
  recurred** -- the fixes held; new misses were other commands. Several were
  noise/fatigue ("{", "x", "xxx", "no", "is"). Added: snap list, knapsack,
  paid down, new tube, new pub, top key, dropped it, cat, keep for, reader/read.
- Once "snap left" came out as "close window" -- a command heard as another
  command. That is the dangerous case.
- **Numbers** (user's idea -- "numbers are easier for me to say"): every
  command has a number shown in the panel; "Computer, 43" works like the words.
  First digit = group (1-9 programs, 11-19 windows, 21-36 moving, 41-51
  editing, 61-62 typing, 71-77 video, 81-88 settings). Numbers match exactly
  or not at all; digits, words ("forty three") and "number 43" all work.
- **Next:** a practice with numbers, while fresh, to see how Voiceitt writes
  them (digits? words? "for" for 4?). Add what it writes to the number parser
  only if it can't be confused with another number.

## Third practice, numbers (2026-09-29 13:30): 59 right of 72

- Single digits come out as words: 2 -> "to", 3 -> "sorry", 4 -> "for" (x3).
  Not parsed as numbers, so harmless, but they don't work.
- **A number heard as a different number**: 35 -> "45" (would run redo),
  51 -> "five" (would run #5, "this is my browser", which rewrites the
  command list). 32 -> "is 42", 44 -> "four people".
  So thirty/forty and fifty-one/five get confused for this user's voice.
- Done now: risky commands (close window, close tab, delete, cut, this is my
  browser/mail/terminal, edit commands) have **no number** -- words only.
  Added: open man, open face, high command.
- **Next:** a number test through the chat (user rested) to find which
  numbers Voiceitt writes reliably for this voice, then renumber using only
  those, avoiding single digits and 30/40, 50/5 pairs.
