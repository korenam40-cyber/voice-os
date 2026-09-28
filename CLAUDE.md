# Project context for Claude

Voice OS: full voice control of Windows for one user with a speech impairment.
Read `docs/status.md` first (where we are), then `docs/requirements.md` (what
the user asked for -- do not re-ask what is recorded there).

## How to work on this

- **Test with the user's real voice, early.** Voiceitt never once wrote "Wick"
  (it wrote "week"), and several words ("undo", "play", "copy") are Voiceitt's
  own commands that never arrive as text. Only tests through Voiceitt showed it.
  The user can dictate into the chat through Voiceitt: what arrives is exactly
  what Voice OS will see.
- **The user tires.** Keep voice tests short and early in the day.
- **A wrong action is worse than no action.** Commands run only when they
  clearly beat every other; otherwise nothing happens. Never widen matching to
  "be helpful".
- **Never leave the microphone muted** -- it locks a voice-only user out.
  MicGuard's failsafes (15 s cap, crash recovery) must stay.
- **Plain language with the user.** They are not a programmer; explain choices
  in everyday words, one question at a time.

## Standing constraints

- Recordings of the user's voice never leave the machine.
- The bridge (`voiceitt-bridge`) stays unchanged; Voice OS replaces it only when
  proven. Never run both.
- Commit only when asked or when the session requires it.

## Layout

- `commands.json` -- the command list (wake word, phrasings, groups, actions).
- `VoiceCommands.cs` / `CommandListener.cs` -- matching and word-by-word timing
  (pure, tested in `tests/CommandTests`).
- `CommandExecutor.cs`, `KeyChord.cs` -- carrying commands out.
- `Feedback.cs`, `ToneWav.cs`, `MicGuard.cs` -- confirmation and mic mute.
- `CommandsPanel.cs` -- the always-visible command list.
- The rest is the bridge's dictation machinery, carried over unchanged.
