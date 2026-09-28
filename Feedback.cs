using System.IO;
using System.Media;
using System.Speech.Synthesis;

namespace VoiceOS;

internal enum FeedbackMode { Sound, Speech, Off }

internal enum FeedbackKind { Done, Listening, NotUnderstood, Failed }

/// <summary>
/// Tells the user a voice command was heard, without making them look at the screen.
///
/// Sounds are the default. The user first chose spoken words (2026-09-27), but the computer's
/// audio plays through a sound bar the microphone can hear, so Voiceitt could write the
/// spoken "copied" into the document. A tone has no words in it for Voiceitt to write.
/// Speech is still one command away ("Computer, talk to me") for use with headphones.
/// Everything is generated here: no sound files, nothing leaves the machine.
/// </summary>
internal sealed class Feedback : IDisposable
{
    private SpeechSynthesizer? _synth;
    private readonly Dictionary<FeedbackKind, byte[]> _tones = new()
    {
        // A short high note: done.
        [FeedbackKind.Done] = ToneWav.Make((880, 0.12)),
        // Rising: "yes? go on".
        [FeedbackKind.Listening] = ToneWav.Make((660, 0.09), (990, 0.12)),
        // Two low notes: didn't catch that, nothing was done.
        [FeedbackKind.NotUnderstood] = ToneWav.Make((330, 0.14), (0, 0.06), (330, 0.14)),
        // Falling: understood, but it couldn't be done.
        [FeedbackKind.Failed] = ToneWav.Make((520, 0.12), (260, 0.2)),
    };

    public FeedbackMode Mode { get; set; } = FeedbackMode.Sound;

    /// <summary>Until when our own voice may still be coming out of the speakers. Text Voiceitt
    /// writes before this may be that voice, picked up by the microphone.</summary>
    public DateTime SpeakingUntil { get; private set; } = DateTime.MinValue;

    public string? Error { get; private set; }

    /// <summary>Called just before the computer starts talking, and after it has finished
    /// (plus a short tail for the sound bar's delay). The microphone mute hangs off these.</summary>
    public Action? SpeakingStarted { get; set; }
    public Action? SpeakingEnded { get; set; }

    /// <summary>Sound-bar delay: audio still comes out this long after the voice has finished.</summary>
    private static readonly TimeSpan Tail = TimeSpan.FromMilliseconds(400);

    /// <summary>Tell the user. `words` is used in Speech mode; the kind picks the tone.</summary>
    public void Give(FeedbackKind kind, string? words)
    {
        try
        {
            switch (Mode)
            {
                case FeedbackMode.Sound:
                    using (var player = new SoundPlayer(new MemoryStream(_tones[kind]))) player.Play();
                    break;
                case FeedbackMode.Speech:
                    Say(words);
                    break;
            }
        }
        catch
        {
            // Feedback is a courtesy; a failure must never stop a command.
        }
    }

    private void Say(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_synth == null)
        {
            try
            {
                _synth = new SpeechSynthesizer();
                _synth.SetOutputToDefaultAudioDevice();
                try { _synth.SelectVoiceByHints(VoiceGender.Female, VoiceAge.Adult); } catch { /* keep the default voice */ }
                _synth.Rate = 1;
                _synth.SpeakCompleted += (_, _) =>
                {
                    // A cancelled sentence also "completes"; the next one has already muted again.
                    Task.Delay(Tail).ContinueWith(_ =>
                    {
                        if (_synth.State != SynthesizerState.Speaking) SpeakingEnded?.Invoke();
                    });
                };
            }
            catch (Exception ex)
            {
                _synth = null;
                Error = $"Spoken feedback is unavailable ({ex.Message}).";
                return;
            }
        }
        SpeakingStarted?.Invoke();
        _synth.SpeakAsyncCancelAll();
        _synth.SpeakAsync(text);
        // Roughly 12 characters a second at this rate, plus a margin for the audio device.
        SpeakingUntil = DateTime.Now.AddSeconds(0.6 + text.Length / 12.0);
    }

    public void Dispose() => _synth?.Dispose();
}
