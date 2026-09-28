using System.Runtime.InteropServices;

namespace VoiceOS;

/// <summary>
/// Mutes the microphones while the computer is talking, so Voiceitt (or the Hebrew engine)
/// doesn't hear the computer's own voice through the sound bar and type it. Asked for by
/// the user on 2026-09-27; Baby will talk through the same guard.
///
/// Windows-level mute rather than pressing Voiceitt's button: it doesn't depend on
/// Voiceitt's page layout, and it covers every program listening at once.
///
/// A microphone left muted locks a voice-only user out of the computer, so:
///  * only microphones that were ON are muted, and only those are turned back on;
///  * the mute lifts on its own after <see cref="MaxMute"/> whatever happens;
///  * which microphones are muted is written to the settings file first, so a crash
///    mid-sentence is undone the next time the app starts.
/// </summary>
internal sealed class MicGuard : IDisposable
{
    public static readonly TimeSpan MaxMute = TimeSpan.FromSeconds(15);

    private readonly AppSettings _settings;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly System.Threading.Timer _failsafe;
    private List<string> _muted = new();

    public bool Enabled { get; set; } = true;

    public MicGuard(AppSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
        _failsafe = new System.Threading.Timer(_ => Release("safety timeout"));
    }

    /// <summary>Undo a mute left behind by a crash. Call once at startup.</summary>
    public void RecoverFromCrash()
    {
        var left = _settings.MutedMicIds;
        if (left == null || left.Count == 0) return;
        lock (_gate) _muted = new List<string>(left);
        Release("left muted by an earlier run");
    }

    /// <summary>Mute every microphone that is currently on. Safe to call repeatedly.</summary>
    public void Hold()
    {
        if (!Enabled) return;
        lock (_gate)
        {
            try
            {
                if (_muted.Count == 0)
                {
                    var on = CoreAudio.ActiveCaptureDevices().Where(d => !CoreAudio.GetMute(d)).ToList();
                    if (on.Count == 0) return;
                    // Written down BEFORE muting: if we die in between, the next start undoes it.
                    _settings.MutedMicIds = on;
                    _settings.Save();
                    foreach (string id in on) CoreAudio.SetMute(id, true);
                    _muted = on;
                }
                _failsafe.Change(MaxMute, Timeout.InfiniteTimeSpan);
            }
            catch (Exception ex)
            {
                _log($"Couldn't mute the microphone while talking ({ex.Message}) — Voiceitt may type the computer's words.");
            }
        }
    }

    /// <summary>Turn back on exactly the microphones <see cref="Hold"/> turned off.</summary>
    public void Release(string why = "")
    {
        lock (_gate)
        {
            _failsafe.Change(Timeout.Infinite, Timeout.Infinite);
            if (_muted.Count == 0) return;
            var failed = new List<string>();
            foreach (string id in _muted)
            {
                try { CoreAudio.SetMute(id, false); }
                catch { failed.Add(id); }
            }
            _muted = new List<string>();
            _settings.MutedMicIds = failed.Count > 0 ? failed : null;
            _settings.Save();
            if (failed.Count > 0)
                _log("WARNING: couldn't turn a microphone back on. Unmute it in Windows sound settings.");
            else if (why.Length > 0)
                _log($"Microphone back on ({why}).");
        }
    }

    public void Dispose()
    {
        Release();
        _failsafe.Dispose();
    }

    /// <summary>The few Windows Core Audio calls needed to mute a microphone.</summary>
    private static class CoreAudio
    {
        private const int eCapture = 1;
        private const int DEVICE_STATE_ACTIVE = 1;
        private const int CLSCTX_ALL = 23;
        private static Guid _context = Guid.NewGuid();

        public static List<string> ActiveCaptureDevices()
        {
            var ids = new List<string>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out var devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out uint count));
            for (uint i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(devices.Item(i, out var device));
                Marshal.ThrowExceptionForHR(device.GetId(out string id));
                ids.Add(id);
            }
            return ids;
        }

        public static bool GetMute(string id)
        {
            Marshal.ThrowExceptionForHR(Volume(id).GetMute(out bool muted));
            return muted;
        }

        public static void SetMute(string id, bool mute) =>
            Marshal.ThrowExceptionForHR(Volume(id).SetMute(mute, ref _context));

        private static IAudioEndpointVolume Volume(string id)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out var device));
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o));
            return (IAudioEndpointVolume)o;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                       [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            // Declared in vtable order; only SetMute/GetMute are called.
            [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
            [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
            [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
            [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
            [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }
    }
}
