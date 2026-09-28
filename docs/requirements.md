# Voice OS -- what the user asked for (2026-09-27)

The user's own answers, recorded so no later session has to ask again. The plan
(steps 1-4) is in `docs/voice-os/plan.md`.

## Names
- **Wick** -- the computer's name. But the spoken wake word is **"Computer"**
  ("Computer, scroll down"): tested through Voiceitt on 2026-09-27, "Wick" was
  written "week" 4/5 and "back" 1/5 (never "wick"), "Hey Wick" as "hey
  week"/"hey we", "Jarvis" 2/5 (Jesus, service), "Computer" 5/5. The wake word
  counts only at the start of an utterance.
- **Baby** -- the assistant (step 4). Female.

## Commands ("Wick")
- **English first, through Voiceitt**, because the Hebrew model is not yet good
  enough for this. Leave room for Hebrew: every command has a `hebrew` list.
- Voiceitt shows text **word by word**, so a command is held until the words
  stop changing, and only then matched.
- Not understood -> **do nothing**, nothing is typed, and the user is told what
  was heard.
- Feedback is **spoken** (a short word or phrase). Risk to test: the spoken
  feedback reaching the microphone and being typed by Voiceitt.
- Commands act on the **window the user dictates into**. "Switch to X" also
  makes X the dictation target.

## Hands and input
- Can press keys, but it is hard; **prefers speaking**. Keys are a fallback,
  never the only way.

## Programs
- Browser: **Edge**. Mail: **eM Client**. Claude: moving to the **terminal**
  (Claude Code) for building software by voice.
- Videos: Windows' player today, on a **TV connected as a second screen** (not
  always on); the user drags the window to it. Suggested: "Wick, move to TV"
  (Win+Shift+Right) and possibly mpv, which can open full screen on a chosen
  screen.

## What the computer is for
1. Building software with Claude by voice -- the main use.
2. Controlling Windows itself -- essential.
3. Later: finding venues to lecture about life as a disabled man.
4. Watching a large collection of videos stored on the PC.

## Baby
- Speaks the language the user speaks: English when addressed in English,
  Hebrew when addressed in Hebrew.
- Personality: half companion, half lover. **Real moods** -- good and bad days,
  properly angry when he ignores her advice or neglects himself, then makes up.
  Warm and affectionate; not sexual.
- Moods live in the conversation only: she always does what is asked, and
  always asks before big actions.
- **Memory: everything, long term**, stored locally; the user can tell her to
  forget things.
- **Learns his story over time** (not told up front).
- **Asks before big actions only**: send, delete, buy, post. Opening, reading
  and searching do not ask.
- **Proactive**: may speak first -- greetings, reminders, noticing long
  sessions, nudging about the lecture plans.
- Voice: **try both** ElevenLabs (expressive, cloud, text only) and a local
  Hebrew/English voice, then the user chooses.
