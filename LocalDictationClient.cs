using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace VoiceOS;

/// <summary>
/// Talks to the local Hebrew dictation service (hebrew-atypical-asr,
/// src/ui/dictation_server.py): push-to-talk recognition with the speech model
/// trained on this user's own voice. Everything stays on this computer — the
/// service listens on 127.0.0.1 only, and this client never goes through a proxy
/// (a proxy is what makes loopback addresses look "blocked" in a browser).
/// </summary>
internal sealed class LocalDictationClient : IDisposable
{
    public const int Port = 7870;

    /// <summary>The engine exits -- freeing all of its GPU memory -- after this long
    /// with no dictation, even while this app stays open. The user would rather wait
    /// ~15 s for a reload than leave 18 GB parked on the card between uses.</summary>
    public const int IdleExitSeconds = 600;

    public const string RepoDir = @"C:\Users\Admin\clude projects\hebrew-atypical-asr";
    public static string LogPath => Path.Combine(RepoDir, "data", "dictation", "server.log");

    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{Port}/"),
        // Recognition of a long dictation plus the correction layer can take a while.
        Timeout = TimeSpan.FromMinutes(5),
    };

    internal sealed record Health(bool Ready, string? Error, bool Recording,
                                  double? PhraseSilence = null, double? IdleExitSeconds = null);

    internal sealed record Result(string Text, string RawAsr, bool Corrected, double Seconds,
                                  string? Error, string? Reason, IReadOnlyList<string> Segments);

    /// <summary>The service's state, or null if nothing is answering.</summary>
    public async Task<Health?> GetHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            string json = await _http.GetStringAsync("api/health", cts.Token);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new Health(
                IsTrue(r, "ready"),
                GetString(r, "error"),
                IsTrue(r, "recording"),
                GetNumber(r, "phrase_silence"),
                GetNumber(r, "idle_exit_seconds"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Launches the service in the background, tied to this app. It frees all
    /// of its GPU memory the moment this app closes -- even on a crash, because it
    /// watches this process id -- and also after <see cref="IdleExitSeconds"/> with no
    /// dictation. Either way the next Ctrl+Alt+D starts it again.</summary>
    public bool TryStartService(out string message)
    {
        string python = Path.Combine(RepoDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python))
        {
            message = $"Hebrew engine not found (expected {python}).";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var psi = new ProcessStartInfo(python,
                $"-m src.ui.dictation_server --owner-pid {Environment.ProcessId} " +
                $"--idle-exit-seconds {IdleExitSeconds} --log-file data\\dictation\\server.log")
            {
                WorkingDirectory = RepoDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            Process.Start(psi);
            message = "Starting the Hebrew engine…";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Couldn't start the Hebrew engine: {ex.Message}";
            return false;
        }
    }

    /// <summary>The phrases finished since index <paramref name="since"/>, ready to type
    /// while the user is still speaking. Empty on any failure: a missed poll costs
    /// nothing, because /api/stop returns every phrase again.</summary>
    public async Task<List<string>> GetSegmentsAsync(int since)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string json = await _http.GetStringAsync($"api/segments?since={since}", cts.Token);
            using var doc = JsonDocument.Parse(json);
            return ReadSegments(doc.RootElement);
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> ReadSegments(JsonElement root)
    {
        var texts = new List<string>();
        if (root.TryGetProperty("segments", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                string? text = GetString(el, "text");
                if (!string.IsNullOrWhiteSpace(text)) texts.Add(text!);
            }
        }
        return texts;
    }

    /// <summary>Opens the microphone. Returns null on success, otherwise the reason.</summary>
    public async Task<string?> StartRecordingAsync()
    {
        try
        {
            using var resp = await _http.PostAsync("api/start", EmptyJson());
            if (resp.IsSuccessStatusCode) return null;
            string body = await resp.Content.ReadAsStringAsync();
            return ReadError(body) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Closes the microphone and returns the recognized text.</summary>
    public async Task<Result> StopAndRecognizeAsync()
    {
        try
        {
            using var resp = await _http.PostAsync("api/stop", EmptyJson());
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode)
            {
                return new Result("", "", false, 0, GetString(r, "error") ?? $"HTTP {(int)resp.StatusCode}",
                                  null, Array.Empty<string>());
            }
            double seconds = r.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            return new Result(
                GetString(r, "text") ?? "",
                GetString(r, "raw_asr") ?? "",
                IsTrue(r, "corrected"),
                seconds,
                null,
                GetString(r, "reason"),
                ReadSegments(r));
        }
        catch (Exception ex)
        {
            return new Result("", "", false, 0, ex.Message, null, Array.Empty<string>());
        }
    }

    /// <summary>The phrases of the last dictation, for the user to confirm or fix.
    /// Held in the service's memory only -- nothing is on disk until a correction
    /// is saved.</summary>
    public async Task<List<(int Index, string Text, double Seconds)>> GetLastAsync()
    {
        var phrases = new List<(int, string, double)>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string json = await _http.GetStringAsync("api/last", cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("segments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string? text = GetString(el, "text");
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    int index = el.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number
                        ? i.GetInt32() : phrases.Count;
                    double seconds = el.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetDouble() : 0;
                    phrases.Add((index, text!, seconds));
                }
            }
        }
        catch
        {
            // Nothing to fix; the caller says so.
        }
        return phrases;
    }

    /// <summary>Saves one phrase as a training pair: this audio, these words.
    /// Returns null on success, otherwise the reason.</summary>
    public async Task<string?> CorrectAsync(int index, string text)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { index, text });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/correct", content);
            if (resp.IsSuccessStatusCode) return null;
            return ReadError(await resp.Content.ReadAsStringAsync()) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // -- teaching one word -------------------------------------------------
    // The service holds the takes in memory; nothing is written until Save.

    internal sealed record WordPrompt(int Index, string Text, bool Alone);

    internal sealed record WordTake(bool Kept, string Heard, bool Matched, string? Reason,
                                    double Seconds);

    /// <summary>Begins teaching a word, and returns the takes it suggests recording
    /// (the word alone first, then short sentences the user can rewrite).</summary>
    public async Task<(List<WordPrompt> Prompts, string? Error)> WordStartAsync(string word)
    {
        var prompts = new List<WordPrompt>();
        try
        {
            string body = JsonSerializer.Serialize(new { word });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/word/start", content);
            string json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return (prompts, ReadError(json) ?? $"HTTP {(int)resp.StatusCode}");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("prompts", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    prompts.Add(new WordPrompt(
                        el.TryGetProperty("index", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : prompts.Count,
                        GetString(el, "text") ?? word,
                        IsTrue(el, "alone")));
                }
            }
            return (prompts, null);
        }
        catch (Exception ex)
        {
            return (prompts, ex.Message);
        }
    }

    /// <summary>Opens the microphone for one take.</summary>
    public async Task<string?> WordRecordStartAsync()
    {
        try
        {
            using var resp = await _http.PostAsync("api/word/record/start", EmptyJson());
            if (resp.IsSuccessStatusCode) return null;
            return ReadError(await resp.Content.ReadAsStringAsync()) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Ends the take and keeps it, with what the model made of it. A silent
    /// or very short take comes back Kept=false and is not stored.</summary>
    public async Task<WordTake> WordRecordStopAsync(string text)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { text });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/word/record/stop", content);
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode)
                return new WordTake(false, "", false, GetString(r, "error") ?? $"HTTP {(int)resp.StatusCode}", 0);
            return new WordTake(IsTrue(r, "kept"), GetString(r, "heard") ?? "", IsTrue(r, "matched"),
                                GetString(r, "reason"), GetNumber(r, "seconds") ?? 0);
        }
        catch (Exception ex)
        {
            return new WordTake(false, "", false, ex.Message, 0);
        }
    }

    /// <summary>Sets the experimental Hebrew punctuation level on the running
    /// service: "off", "ends", "commas" or "lists".</summary>
    /// <remarks>The service applies it to the next phrase without reloading, so the
    /// user can turn it on mid-dictation and off again the moment it annoys them.
    /// If the engine is still warming up the service remembers the choice and
    /// applies it when the model is ready, so a click is never silently lost.</remarks>
    public async Task<string?> SetPunctuationAsync(string level)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { punctuation = level });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/options", content);
            if (resp.IsSuccessStatusCode) return null;
            return ReadError(await resp.Content.ReadAsStringAsync()) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Turns spoken mark names into marks: saying נקודה types ".".</summary>
    /// <remarks>Separate from the automatic punctuation level, because this is the
    /// user saying what they want rather than a model guessing it. The service
    /// only converts a name where punctuation can go -- at the end of a phrase or
    /// before another mark -- so זו נקודה חשובה stays a sentence.</remarks>
    public async Task<string?> SetSpokenPunctuationAsync(bool on)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { spoken_punctuation = on });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/options", content);
            if (resp.IsSuccessStatusCode) return null;
            return ReadError(await resp.Content.ReadAsStringAsync()) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>One model instead of two: about 0.6 s quicker per phrase, and a
    /// little less accurate (4.7% word error against 6.1% on the test set).</summary>
    public async Task<string?> SetFastModeAsync(bool on)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { fast = on });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/options", content);
            if (resp.IsSuccessStatusCode) return null;
            return ReadError(await resp.Content.ReadAsStringAsync()) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Throws one take away (said it wrong, or the phone rang).</summary>
    public async Task<string?> WordDropAsync(int index)
    {
        try
        {
            string body = JsonSerializer.Serialize(new { index });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/word/drop", content);
            if (resp.IsSuccessStatusCode) return null;
            return ReadError(await resp.Content.ReadAsStringAsync()) ?? $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Saves the takes as training pairs and adds the word to the personal
    /// vocabulary. Returns how many takes were saved, or the reason none were.</summary>
    public async Task<(int Saved, bool Vocabulary, string? Error)> WordSaveAsync()
    {
        try
        {
            using var resp = await _http.PostAsync("api/word/save", EmptyJson());
            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (!resp.IsSuccessStatusCode)
                return (0, false, GetString(r, "error") ?? $"HTTP {(int)resp.StatusCode}");
            int saved = r.TryGetProperty("saved", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
            return (saved, IsTrue(r, "vocabulary"), null);
        }
        catch (Exception ex)
        {
            return (0, false, ex.Message);
        }
    }

    /// <summary>Throws away everything recorded for the word being taught.</summary>
    public async Task WordDiscardAsync()
    {
        try
        {
            using var _ = await _http.PostAsync("api/word/discard", EmptyJson());
        }
        catch
        {
            // Nothing was on disk anyway.
        }
    }

    /// <summary>Closes the microphone and throws the audio away.</summary>
    public async Task CancelAsync()
    {
        try
        {
            using var _ = await _http.PostAsync("api/cancel", EmptyJson());
        }
        catch
        {
            // Best-effort: nothing is typed either way.
        }
    }

    /// <summary>Asks the service to exit, releasing its GPU memory. Waits at most
    /// 1.5 s, so closing the app never hangs.</summary>
    public void ShutdownService()
    {
        try
        {
            Task.Run(() => _http.PostAsync("api/shutdown", EmptyJson())).Wait(TimeSpan.FromSeconds(1.5));
        }
        catch
        {
            // Not running, or already gone.
        }
    }

    private static StringContent EmptyJson() => new("{}", Encoding.UTF8, "application/json");

    private static double? GetNumber(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool IsTrue(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement r, string key) =>
        r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return GetString(doc.RootElement, "error");
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
