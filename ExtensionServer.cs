using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VoiceOS;

/// <summary>
/// Receives Voiceitt's transcript from the Chrome connector extension over a local-only
/// HTTP endpoint, and can ask the extension to clear Voiceitt's box.
///
/// Anything on the machine can reach 127.0.0.1, including web pages the user visits, and
/// whatever arrives here gets typed into the user's applications — so requests are only
/// accepted with the private token the app generated for the extension, from a
/// chrome-extension origin, addressed to 127.0.0.1 (which also blocks DNS rebinding).
/// </summary>
internal sealed class ExtensionServer : IDisposable
{
    public const int Port = 47623; // the bridge uses 47613

    /// <summary>How long after the last message the extension still counts as connected.</summary>
    private static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ClearGiveUp = TimeSpan.FromSeconds(8);

    public enum ClearOutcome { None, Done, TimedOut }

    private readonly HttpListener _listener = new();
    private readonly byte[] _token;
    private readonly object _gate = new();

    private string _text = "";
    private DateTime _seenUtc = DateTime.MinValue;

    private bool _clearWanted;
    private string? _clearExpect;
    private DateTime _clearAskedUtc;
    private ClearOutcome _outcome = ClearOutcome.None;

    public ExtensionServer(string token) => _token = Encoding.UTF8.GetBytes(token);

    public bool Start(out string error)
    {
        error = "";
        try
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        _ = Task.Run(AcceptLoop);
        return true;
    }

    /// <summary>True while the extension has reported in recently.</summary>
    public bool IsLive
    {
        get { lock (_gate) return DateTime.UtcNow - _seenUtc < LiveWindow; }
    }

    /// <summary>The transcript box's current text, if the extension is connected.</summary>
    public bool TryGetText(out string text)
    {
        lock (_gate)
        {
            text = _text;
            return DateTime.UtcNow - _seenUtc < LiveWindow;
        }
    }

    /// <summary>Asks the extension to empty Voiceitt's box once it is safe to.</summary>
    public void RequestClear()
    {
        lock (_gate)
        {
            _clearWanted = true;
            _clearExpect = null;
            _clearAskedUtc = DateTime.UtcNow;
            _outcome = ClearOutcome.None;
        }
    }

    /// <summary>
    /// The exact text the box must still hold for clearing to be safe — i.e. what has
    /// already been typed. Null means "not yet", so the extension leaves the box alone.
    /// </summary>
    public void SetClearExpect(string? expect)
    {
        lock (_gate)
        {
            if (_clearWanted) _clearExpect = expect;
        }
    }

    /// <summary>Reports (once) how a requested clear ended.</summary>
    public ClearOutcome TakeOutcome()
    {
        lock (_gate)
        {
            if (_clearWanted && DateTime.UtcNow - _clearAskedUtc > ClearGiveUp)
            {
                _clearWanted = false;
                _outcome = ClearOutcome.TimedOut;
            }
            var o = _outcome;
            _outcome = ClearOutcome.None;
            return o;
        }
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped

            try { Handle(ctx); }
            catch { TryRespond(ctx.Response, 500, "{}"); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;

        if (!IsAuthorized(req)) { TryRespond(res, 403, "{}"); return; }

        string path = req.Url?.AbsolutePath ?? "";
        if (req.HttpMethod == "GET" && path == "/ping") { TryRespond(res, 200, "{\"ok\":true}"); return; }
        if (req.HttpMethod != "POST" || path != "/voiceitt") { TryRespond(res, 404, "{}"); return; }
        if (req.ContentLength64 > 1_000_000) { TryRespond(res, 413, "{}"); return; }

        string text;
        try
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            if (!doc.RootElement.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String)
            {
                TryRespond(res, 400, "{}");
                return;
            }
            text = t.GetString() ?? "";
        }
        catch { TryRespond(res, 400, "{}"); return; }

        string reply;
        lock (_gate)
        {
            _text = text;
            _seenUtc = DateTime.UtcNow;

            if (_clearWanted && text.Length == 0)
            {
                _clearWanted = false;
                _outcome = ClearOutcome.Done;
            }

            if (_clearWanted && _clearExpect != null)
            {
                reply = JsonSerializer.Serialize(new { clear = true, expect = _clearExpect });
            }
            else
            {
                reply = "{\"clear\":false}";
            }
        }
        TryRespond(res, 200, reply);
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        // Addressed to loopback by IP, never by a hostname a web page could rebind.
        if (req.Url?.Host != "127.0.0.1") return false;

        // Ordinary web pages send an http(s) Origin; only the extension sends this one.
        string? origin = req.Headers["Origin"];
        if (origin != null && !origin.StartsWith("chrome-extension://", StringComparison.Ordinal)) return false;

        string? sent = req.Headers["X-Bridge-Token"];
        if (sent == null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(sent), _token);
    }

    private static void TryRespond(HttpListenerResponse res, int status, string json)
    {
        try
        {
            res.StatusCode = status;
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch { /* client went away */ }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
