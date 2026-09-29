using System.Windows.Forms;

namespace VoiceOS;

internal sealed class MainForm : Form
{
    private const int HotkeyCalibrate = 1;
    private const int HotkeyPause = 2;
    private const int HotkeyDictate = 3;
    private const int HotkeyFix = 4;
    private const int HotkeyTeach = 5;

    private DictationEngine? _engine;

    private readonly HudStatus _statusLabel = new();
    private readonly Label _targetLabel = new();
    private readonly HudButton _calibrateButton = new();
    private readonly HudButton _pauseButton = new();
    private readonly HudSwitch _allowCorrectionsCheck = new();
    private readonly HudSwitch _clearVoiceittCheck = new();
    private readonly HudSwitch _punctuationCheck = new();
    private readonly HudSwitch _spokenMarksCheck = new();
    private readonly HudSwitch _fastCheck = new();
    private readonly TextBox _log = new();
    private readonly ComboBox _windowPicker = new();
    private readonly HudButton _refreshWindowsButton = new();
    private readonly HudButton _hideLogButton = new();
    private readonly HudButton _dictateButton = new();
    private readonly HudButton _fixButton = new();
    private readonly HudButton _teachButton = new();
    private readonly HudButton _connectorButton = new();
    private readonly HudFeed _feed = new();
    private readonly HudStepper _stepper = new();
    private readonly System.Windows.Forms.Timer _hudTimer = new() { Interval = 5000 };
    private bool _hudBusy;
    private int _hudTicks;
    private bool _voiceittSeen;
    private string _engineState = "off · loads on use";
    private string _gpu = "checking";
    private string _phrasePause = "0.8 s";
    private string _idleRelease = $"{LocalDictationClient.IdleExitSeconds / 60} min";
    private readonly Label _dictationStatus = new();
    private readonly LocalDictationClient _dictation = new();
    private bool _dictating;
    private bool _dictationBusy;
    /// <summary>How many finished phrases have already been typed in this dictation.
    /// The service hands out phrases by index, so nothing is ever typed twice.</summary>
    private int _typedPhrases;
    private CancellationTokenSource? _phrasePolling;
    private bool _suppressPickerEvent;
    private int _expandedHeight;

    public MainForm()
    {
        Text = "Voice OS";
        Width = S(460);
        Height = S(950);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        BackColor = Hud.Ground;
        if (Hud.AppIcon != null) Icon = Hud.AppIcon;
        ForeColor = Hud.Ink;
        Padding = new Padding(S(8));
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Height = Math.Min(Height, wa.Height - 40);
        Location = new System.Drawing.Point(wa.Right - Width - 20, wa.Bottom - Height - 20);
        _expandedHeight = Height;

        // --- Terminator vision: everything about setup ---------------------------
        var vision = new HudVisionPanel { Dock = DockStyle.Top, Height = S(262), Padding = new Padding(S(10)) };
        _feed.Dock = DockStyle.Top;
        _feed.Height = S(62);
        _stepper.Dock = DockStyle.Top;
        _stepper.Height = S(142);
        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = S(26);
        vision.Controls.Add(_statusLabel);
        vision.Controls.Add(_stepper);
        vision.Controls.Add(Spacer(10, transparent: true));
        vision.Controls.Add(_feed);

        // --- Type into ------------------------------------------------------------
        var pickerPanel = new Panel { Dock = DockStyle.Top, Height = S(34), Padding = new Padding(0, S(4), 0, S(2)) };
        var pickerLabel = new Label
        {
            Text = "TYPE INTO",
            Dock = DockStyle.Left,
            Width = S(76),
            Font = Hud.Mono,
            ForeColor = Hud.Dim,
            TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        };
        _windowPicker.Dock = DockStyle.Fill;
        _windowPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _windowPicker.FlatStyle = FlatStyle.Flat;
        _windowPicker.BackColor = Color.FromArgb(2, 6, 11);
        _windowPicker.ForeColor = Hud.Ink;
        _windowPicker.Font = Hud.Mono;
        _windowPicker.DrawMode = DrawMode.OwnerDrawFixed;
        _windowPicker.ItemHeight = S(18);
        _windowPicker.DrawItem += DrawPickerItem;
        _windowPicker.DropDown += (_, _) => RefreshWindowList();
        _windowPicker.SelectedIndexChanged += (_, _) => OnWindowPicked();
        _refreshWindowsButton.Text = "Refresh";
        _refreshWindowsButton.Dock = DockStyle.Right;
        _refreshWindowsButton.Width = S(84);
        _refreshWindowsButton.Click += (_, _) => RefreshWindowList();
        pickerPanel.Controls.Add(_windowPicker);
        pickerPanel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = S(6) });
        pickerPanel.Controls.Add(_refreshWindowsButton);
        pickerPanel.Controls.Add(pickerLabel);

        // --- Actions ----------------------------------------------------------------
        var actionRow = new TableLayoutPanel { Dock = DockStyle.Top, Height = S(40), ColumnCount = 2, RowCount = 1, BackColor = Hud.Ground };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _calibrateButton.Text = "Calibrate (F8)";
        _calibrateButton.Dock = DockStyle.Fill;
        _calibrateButton.Margin = new Padding(0, 0, S(3), 0);
        _calibrateButton.Click += (_, _) => ArmCalibration();
        _pauseButton.Text = "Resume (Ctrl+Alt+P)";
        _pauseButton.Dock = DockStyle.Fill;
        _pauseButton.Margin = new Padding(S(3), 0, 0, 0);
        _pauseButton.Click += (_, _) => _engine?.TogglePause();
        actionRow.Controls.Add(_calibrateButton, 0, 0);
        actionRow.Controls.Add(_pauseButton, 1, 0);

        _connectorButton.Text = "Set up Chrome connector (one time)";
        _connectorButton.Dock = DockStyle.Top;
        _connectorButton.Height = S(40);
        _connectorButton.Click += (_, _) => SetUpConnector();

        _dictateButton.Text = "Hebrew dictation: start (Ctrl+Alt+D)";
        _dictateButton.Primary = true;
        _dictateButton.Dock = DockStyle.Top;
        _dictateButton.Height = S(48);
        _dictateButton.Click += (_, _) => ToggleDictation();

        _fixButton.Text = "Fix what it just heard (Ctrl+Alt+F)";
        _fixButton.Dock = DockStyle.Top;
        _fixButton.Height = S(40);
        _fixButton.Enabled = false;
        _fixButton.Click += (_, _) => FixLastDictation();

        _teachButton.Text = "Teach a word it keeps getting wrong (Ctrl+Alt+W)";
        _teachButton.Dock = DockStyle.Top;
        _teachButton.Height = S(40);
        _teachButton.Click += (_, _) => TeachWord();

        _dictationStatus.Dock = DockStyle.Top;
        _dictationStatus.Height = S(40);
        _dictationStatus.Font = Hud.Mono;
        _dictationStatus.ForeColor = Hud.Dim;
        _dictationStatus.Padding = new Padding(S(4), S(6), S(4), 0);
        _dictationStatus.Text = "Hebrew engine: loads when you dictate, frees the GPU after 10 idle minutes";

        // --- Hebrew dictation options --------------------------------------------------
        // Automatic punctuation was built, measured and then dropped at the user's
        // request on 2026-09-25: they would rather say the marks than have a model
        // guess them, which is also the only version that is predictable. The engine
        // can still do it (--punctuation, and the measurements are in
        // CONTINUE_HERE.md), but it is not a switch here any more.
        var punctHeader = new Label
        {
            Text = "HEBREW DICTATION",
            Dock = DockStyle.Top,
            Height = S(22),
            Font = Hud.Mono,
            ForeColor = Hud.Dim,
            TextAlign = System.Drawing.ContentAlignment.BottomLeft,
        };

        // Saying the mark is the user telling the engine what they want, rather
        // than a model guessing from grammar -- so it is its own switch, and it
        // works whether or not automatic punctuation is on.
        _spokenMarksCheck.Text = _spokenMarksCheck.Title = "Say \"נקודה\", \"פסיק\" to get the mark";
        _spokenMarksCheck.Note = "Say it at the end of a phrase, then pause";
        _spokenMarksCheck.Experimental = true;
        // On by default: with automatic punctuation gone this is the only way to
        // punctuate, and it is also what stops the recognizer ending every pause
        // with a full stop of its own.
        _spokenMarksCheck.Checked = true;
        _spokenMarksCheck.Dock = DockStyle.Top;
        _spokenMarksCheck.Height = S(46);
        _spokenMarksCheck.CheckedChanged += (_, _) => ApplySpokenMarks();

        // Measured 2026-09-25: one model is a median 0.49 s per phrase against
        // 1.09 s for the pair, and 6.1% word error against 4.7%. Which side of
        // that is worth it depends on what the user is writing, so it is a switch.
        _fastCheck.Text = _fastCheck.Title = "Faster, slightly less accurate";
        _fastCheck.Note = "One model: ~0.6 s quicker per phrase";
        _fastCheck.Dock = DockStyle.Top;
        _fastCheck.Height = S(46);
        _fastCheck.CheckedChanged += (_, _) => ApplyFastMode();

        // --- Options ------------------------------------------------------------------
        var optionsHeader = new Label
        {
            Text = "TYPING OPTIONS",
            Dock = DockStyle.Top,
            Height = S(22),
            Font = Hud.Mono,
            ForeColor = Hud.Dim,
            TextAlign = System.Drawing.ContentAlignment.BottomLeft,
        };

        _allowCorrectionsCheck.Text = _allowCorrectionsCheck.Title = "Allow backspace corrections";
        _allowCorrectionsCheck.Note = "Off = never deletes typed text";
        _allowCorrectionsCheck.Dock = DockStyle.Top;
        _allowCorrectionsCheck.Height = S(46);
        _allowCorrectionsCheck.CheckedChanged += (_, _) =>
        {
            if (_engine != null) _engine.AllowBackspaceCorrections = _allowCorrectionsCheck.Checked;
        };

        _clearVoiceittCheck.Text = _clearVoiceittCheck.Title = "Say \"new paragraph\" to clear Voiceitt";
        _clearVoiceittCheck.Note = "Clears Voiceitt's text box";
        _clearVoiceittCheck.Dock = DockStyle.Top;
        _clearVoiceittCheck.Height = S(46);
        _clearVoiceittCheck.Checked = true;
        _clearVoiceittCheck.CheckedChanged += (_, _) =>
        {
            if (_engine != null) _engine.ClearOnTriggerWord = _clearVoiceittCheck.Checked;
        };

        _punctuationCheck.Text = _punctuationCheck.Title = "Spoken \"comma\", \"period\" to symbols";
        _punctuationCheck.Note = "Can misfire on real words";
        _punctuationCheck.Experimental = true;
        _punctuationCheck.Dock = DockStyle.Top;
        _punctuationCheck.Height = S(46);
        _punctuationCheck.CheckedChanged += (_, _) =>
        {
            if (_engine != null) _engine.ConvertSpokenPunctuation = _punctuationCheck.Checked;
        };

        // --- Target line and log ---------------------------------------------------------
        _targetLabel.Dock = DockStyle.Top;
        _targetLabel.Height = S(24);
        _targetLabel.Font = Hud.Mono;
        _targetLabel.ForeColor = Hud.Dim;
        _targetLabel.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
        _targetLabel.Text = "Types into: (none selected)";

        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Dock = DockStyle.Fill;
        _log.Font = new System.Drawing.Font("Consolas", 8.5f);
        _log.BackColor = Hud.RedGround;
        _log.ForeColor = Color.FromArgb(255, 128, 128);
        _log.BorderStyle = BorderStyle.FixedSingle;

        _hideLogButton.Text = "Hide Log";
        _hideLogButton.Dock = DockStyle.Top;
        _hideLogButton.Height = S(32);
        _hideLogButton.Click += (_, _) => ToggleLogVisibility();

        // Docked controls lay out last-added first, so these read bottom to top.
        Controls.Add(_log);
        Controls.Add(_hideLogButton);
        Controls.Add(_targetLabel);
        Controls.Add(Spacer(6));
        Controls.Add(_punctuationCheck);
        Controls.Add(_clearVoiceittCheck);
        Controls.Add(_allowCorrectionsCheck);
        Controls.Add(optionsHeader);
        Controls.Add(_spokenMarksCheck);
        Controls.Add(_fastCheck);
        Controls.Add(punctHeader);
        Controls.Add(_dictationStatus);
        Controls.Add(_teachButton);
        Controls.Add(_fixButton);
        Controls.Add(Spacer(4));
        Controls.Add(_dictateButton);
        Controls.Add(Spacer(8));
        Controls.Add(_connectorButton);
        Controls.Add(Spacer(4));
        Controls.Add(actionRow);
        Controls.Add(pickerPanel);
        Controls.Add(Spacer(8));
        Controls.Add(vision);

        _hudTimer.Tick += (_, _) => HudTick();
        FormClosed += (_, _) => _hudTimer.Dispose();

        Load += (_, _) => OnLoaded();
        FormClosed += (_, _) => _engine?.Dispose();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Hud.DarkTitleBar(Handle);
    }

    /// <summary>Design pixels to device pixels, so the layout holds at any display scale.</summary>
    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    private Panel Spacer(int px, bool transparent = false) => new()
    {
        Dock = DockStyle.Top,
        Height = S(px),
        BackColor = transparent ? Color.Transparent : Hud.Ground,
    };

    private void DrawPickerItem(object? sender, DrawItemEventArgs e)
    {
        bool highlighted = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        using (var bg = new SolidBrush(highlighted ? Hud.Line : Color.FromArgb(2, 6, 11)))
            e.Graphics.FillRectangle(bg, e.Bounds);
        if (e.Index >= 0)
            TextRenderer.DrawText(e.Graphics, _windowPicker.Items[e.Index]?.ToString() ?? "", Hud.Mono, e.Bounds, Hud.Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
    }

    /// <summary>Refreshes the live readout every few seconds: engine state, GPU memory,
    /// and whether a Voiceitt window is open. Everything it shows is measured.</summary>
    private async void HudTick()
    {
        if (_hudBusy || _engine == null || IsDisposed) return;
        _hudBusy = true;
        try
        {
            var health = await _dictation.GetHealthAsync();
            _engineState = health == null ? "off · loads on use"
                         : !health.Ready ? "loading"
                         : health.Error != null ? "error — see log"
                         : health.Recording || _dictating ? "listening"
                         : "ready";
            if (health?.PhraseSilence is double pause) _phrasePause = $"{pause:0.0} s";
            if (health?.IdleExitSeconds is double idle) _idleRelease = $"{idle / 60:0} min";
            if (_hudTicks++ % 2 == 0) _gpu = await GpuProbe.ReadAsync();
            IntPtr own = Handle;
            _voiceittSeen = await Task.Run(() => WindowPicker.EnumerateCandidateWindows(own)
                .Any(w => w.Title.Contains("voiceitt", StringComparison.OrdinalIgnoreCase)));
            if (!IsDisposed) UpdateHud();
        }
        catch
        {
            // The readout is decoration around real state; it must never break dictation.
        }
        finally
        {
            _hudBusy = false;
        }
    }

    private void UpdateHud()
    {
        if (_engine == null) return;
        bool target = _engine.TargetWindowHandle != IntPtr.Zero;
        bool linked = _engine.IsCalibrated;
        bool voiceitt = linked || _voiceittSeen;
        _stepper.SetStep(0, target ? Shorten(_engine.TargetWindowTitle, 40) : "Choose it under TYPE INTO", target);
        _stepper.SetStep(1, linked ? "Voiceitt linked" : voiceitt ? "Voiceitt window found" : "Open it in Chrome or Edge", voiceitt);
        _stepper.SetStep(2, linked ? "Watching the transcript" : "Point at Voiceitt's text, then F8", linked);
        _connectorButton.Text = _engine.ConnectorLive ? "Chrome connector: connected" : "Set up Chrome connector (one time)";
        _connectorButton.Primary = !_engine.ConnectorLive;
        _connectorButton.Positive = _engine.ConnectorLive;
        string lastSynced = _engine.LastSuccessfulPoll is DateTime t
            ? $"{Math.Max(0, (DateTime.Now - t).TotalSeconds):0}s ago"
            : "never";
        _feed.SetRows(new[]
        {
            ("Target window", target ? Shorten(_engine.TargetWindowTitle, 24) : "none"),
            ("Voice link", linked ? Shorten(_engine.VoiceittWindowTitle, 24) : "not linked"),
            ("Last synced", linked ? lastSynced : "—"),
            ("Chrome connector", _engine.ConnectorLive ? "connected" : "not connected"),
            ("Hebrew engine", _engineState),
            ("GPU memory", _gpu),
            ("Phrase pause", _phrasePause),
            ("Idle release", _idleRelease),
        });
    }

    private void SetUpConnector()
    {
        string folder = _engine!.ConnectorFolder;
        try { Clipboard.SetText(folder); } catch { /* clipboard busy — the path is also in the log */ }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("chrome", "chrome://extensions") { UseShellExecute = true }); }
        catch { AppendLog("Open Chrome and go to chrome://extensions."); }

        AppendLog($"Connector folder: {folder}");
        MessageBox.Show(this,
            "Chrome is opening its extensions page. Do this once:\n\n" +
            "1.  Turn on \"Developer mode\" (switch at the top right).\n" +
            "2.  Click \"Load unpacked\".\n" +
            "3.  Paste the folder path (already copied for you) and choose that folder.\n\n" +
            "Then reload the Voiceitt page once. This button will turn to \"connected\".\n\n" + folder,
            "Set up Chrome connector", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private CommandsPanel? _commandsPanel;
    private PracticeForm? _practiceForm;
    private PracticeSession? _lastPractice;
    private readonly System.Windows.Forms.Timer _bridgeWatch = new() { Interval = 2000 };

    private void ShowCommandsPanel()
    {
        if (_engine?.Commands == null) return;
        if (_commandsPanel == null || _commandsPanel.IsDisposed) _commandsPanel = new CommandsPanel();
        _commandsPanel.ShowCommands(_engine.Commands);
    }

    private void OnLoaded()
    {
        _engine = new DictationEngine();
        _engine.Log += AppendLog;
        _engine.StatusChanged += RefreshStatus;
        _engine.StartConnector();
        _engine.CommandPanelRequested += show => { if (show) ShowCommandsPanel(); else _commandsPanel?.Hide(); };
        _engine.CommandsChanged += () => { if (_commandsPanel?.Visible == true) ShowCommandsPanel(); };
        _engine.PracticeUpdated += attempt =>
        {
            var session = _engine.Practice;
            if (session == null && attempt == null) return;
            if (_practiceForm == null || _practiceForm.IsDisposed) _practiceForm = new PracticeForm();
            // The session is cleared once finished; the form still shows its summary.
            _practiceForm.ShowState(session ?? _lastPractice!, attempt);
            if (session != null) _lastPractice = session;
        };
        _engine.LoadVoiceCommands();
        // Shown at every start: the user can't be expected to remember "show commands" either.
        ShowCommandsPanel();

        // The bridge doesn't know about Voice OS, so if it is opened while Voice OS runs, Voice
        // OS is the one that must step aside -- otherwise every word is typed twice.
        _bridgeWatch.Tick += (_, _) =>
        {
            if (_engine == null || _engine.IsPaused) return;
            if (System.Diagnostics.Process.GetProcessesByName("VoiceittBridge").Length == 0) return;
            _engine.TogglePause();
            AppendLog("Voiceitt Bridge was opened, so Voice OS paused itself (both would type every word). "
                      + "Close the bridge, then resume with Ctrl+Alt+P.");
        };
        _bridgeWatch.Start();

        Interop.RegisterHotKey(Handle, HotkeyCalibrate, Interop.MOD_NOREPEAT, (uint)Keys.F8);
        Interop.RegisterHotKey(Handle, HotkeyPause, Interop.MOD_CONTROL | Interop.MOD_ALT | Interop.MOD_NOREPEAT, (uint)Keys.P);
        // Ctrl+Alt+D, not a single F-key: F9 is "update fields" in Word and
        // "send/receive" in Outlook, and a global hotkey would steal it from them.
        if (!Interop.RegisterHotKey(Handle, HotkeyDictate, Interop.MOD_CONTROL | Interop.MOD_ALT | Interop.MOD_NOREPEAT, (uint)Keys.D))
            AppendLog("Couldn't register Ctrl+Alt+D (another app may be using it) — use the dictation button instead.");
        if (!Interop.RegisterHotKey(Handle, HotkeyFix, Interop.MOD_CONTROL | Interop.MOD_ALT | Interop.MOD_NOREPEAT, (uint)Keys.F))
            AppendLog("Couldn't register Ctrl+Alt+F — use the fix button instead.");
        if (!Interop.RegisterHotKey(Handle, HotkeyTeach, Interop.MOD_CONTROL | Interop.MOD_ALT | Interop.MOD_NOREPEAT, (uint)Keys.W))
            AppendLog("Couldn't register Ctrl+Alt+W — use the teach-a-word button instead.");

        AppendLog("Ready.");

        // Try to pick up right where last session left off, instead of forcing the
        // user to re-pick a target and recalibrate on Voiceitt every single launch.
        var restoredTarget = _engine.TryFindRestoredTarget(Handle);
        if (restoredTarget != null)
        {
            _engine.SetTypingTarget(restoredTarget.Handle, restoredTarget.Title);
        }

        RefreshWindowList();

        if (!_engine.TryAutoRestoreCalibration(Handle) && !_engine.TryAutoCalibrate(Handle))
        {
            AppendLog("Hover Voiceitt's transcript text and press F8 to calibrate.");
        }

        RefreshStatus();
        _hudTimer.Start();
        HudTick();
    }

    private void RefreshWindowList()
    {
        var choices = WindowPicker.EnumerateCandidateWindows(Handle);

        IntPtr previouslySelected = (_windowPicker.SelectedItem as WindowChoice)?.Handle ?? _engine?.TargetWindowHandle ?? IntPtr.Zero;

        _suppressPickerEvent = true;
        _windowPicker.Items.Clear();
        foreach (var choice in choices) _windowPicker.Items.Add(choice);

        if (previouslySelected != IntPtr.Zero)
        {
            int idx = choices.FindIndex(c => c.Handle == previouslySelected);
            if (idx >= 0) _windowPicker.SelectedIndex = idx;
        }
        _suppressPickerEvent = false;
    }

    private void OnWindowPicked()
    {
        if (_suppressPickerEvent) return;
        if (_windowPicker.SelectedItem is not WindowChoice choice) return;
        _engine!.SetTypingTarget(choice.Handle, choice.Title);
        RefreshStatus();
    }

    private void ToggleLogVisibility()
    {
        if (_log.Visible)
        {
            _expandedHeight = Height;
            _log.Visible = false;
            int chrome = Height - ClientSize.Height;
            Height = _hideLogButton.Bottom + chrome + 8; // shrink to just fit the controls above the log
            _hideLogButton.Text = "Show Log";
        }
        else
        {
            _log.Visible = true;
            Height = _expandedHeight;
            _hideLogButton.Text = "Hide Log";
        }
    }

    private void ArmCalibration()
    {
        AppendLog("Hover your mouse over Voiceitt's transcript text now, then press F8...");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Interop.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyCalibrate)
            {
                DoCalibrate();
                return;
            }
            if (id == HotkeyPause)
            {
                _engine?.TogglePause();
                return;
            }
            if (id == HotkeyDictate)
            {
                ToggleDictation();
                return;
            }
            if (id == HotkeyFix)
            {
                FixLastDictation();
                return;
            }
            if (id == HotkeyTeach)
            {
                TeachWord();
                return;
            }
        }
        base.WndProc(ref m);
    }

    private void DoCalibrate()
    {
        Interop.GetCursorPos(out var pt);
        var cursor = new System.Drawing.Point(pt.X, pt.Y);
        var nearCursor = new Rectangle(cursor.X - S(110), cursor.Y - S(14), S(220), S(28));
        var target = ElementLocator.Calibrate(pt);
        if (target == null)
        {
            IntPtr under = Interop.GetAncestor(Interop.WindowFromPoint(pt), Interop.GA_ROOT);
            string underExe = under == IntPtr.Zero ? "" : ElementLocator.GetProcessExeName(under);
            PlayTargetOverlay(nearCursor, cursor, false,
                "ANALYSIS: NO READABLE TEXT", $"SOURCE: {Upper(underExe, "unknown")}", "MATCH: NONE");
            AppendLog("Couldn't read that element. Make sure your mouse pointer is resting directly on top of the visible transcript text at the moment you press F8, then try again.");
            return;
        }

        var bounds = ElementBounds(target) ?? nearCursor;
        if (!ElementLocator.IsBrowserWindow(target.WindowHandle, out var exeName))
        {
            PlayTargetOverlay(bounds, cursor, false,
                "ANALYSIS: NOT A BROWSER", $"SOURCE: {Upper(exeName, "unknown app")}", "MATCH: NONE");
            AppendLog($"That window (\"{Shorten(target.WindowTitle)}\", running {(string.IsNullOrEmpty(exeName) ? "an unknown app" : exeName)}) doesn't look like a browser. " +
                      "Voiceitt runs inside a browser (Chrome/Edge/Firefox), so this can't be it. " +
                      "Move your mouse onto the browser window showing Voiceitt's transcript text and press F8 again.");
            return;
        }

        bool isVoiceitt = target.WindowTitle.Contains("voiceitt", StringComparison.OrdinalIgnoreCase);
        PlayTargetOverlay(bounds, cursor, true,
            "ANALYSIS: TEXT ELEMENT",
            $"SOURCE: {Upper(exeName, "browser")}{(isVoiceitt ? " / VOICEITT" : "")}",
            $"CHARS: {target.TextPreview.Length}");

        AppendLog($"Captured from \"{Shorten(target.WindowTitle)}\", text there right now: \"{Shorten(target.TextPreview, 80)}\"" +
                  (string.IsNullOrWhiteSpace(target.TextPreview) ? "  (empty — that's fine if Voiceitt hasn't transcribed anything yet)" : ""));

        _engine!.SetCalibration(target);
        RefreshStatus();
    }

    private void PlayTargetOverlay(Rectangle bounds, System.Drawing.Point cursor, bool ok, params string[] readout)
    {
        try
        {
            TargetOverlay.Play(bounds, cursor, ok, readout, DeviceDpi / 96f);
        }
        catch (Exception ex)
        {
            AppendLog($"(Couldn't draw the F8 indicator: {ex.Message})");
        }
    }

    private static string Upper(string? s, string fallback) =>
        (string.IsNullOrWhiteSpace(s) ? fallback : s).ToUpperInvariant();

    /// <summary>Where the captured element is on screen, so the F8 box lands on the real
    /// text. Null when UI Automation can't say; the box then frames the pointer.</summary>
    private static Rectangle? ElementBounds(CalibratedTarget target)
    {
        try
        {
            var r = target.LiveElement?.Current.BoundingRectangle;
            if (r is not { } rect || rect.IsEmpty || double.IsInfinity(rect.Width) || rect.Width < 4 || rect.Height < 4)
                return null;
            var box = new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
            box.Intersect(SystemInformation.VirtualScreen);
            return box.Width > 0 && box.Height > 0 ? box : null;
        }
        catch
        {
            return null;
        }
    }

    private async void ToggleDictation()
    {
        if (_dictationBusy) return;
        _dictationBusy = true;
        try
        {
            if (_dictating)
            {
                await StopDictationAsync();
                return;
            }

            if (_engine == null || _engine.TargetWindowHandle == IntPtr.Zero)
            {
                SetDictationStatus("Pick the window to type into first (dropdown above)");
                return;
            }

            var health = await _dictation.GetHealthAsync();
            if (health == null)
            {
                if (!_dictation.TryStartService(out string message))
                {
                    SetDictationStatus("Hebrew engine couldn't start — see log");
                    AppendLog(message);
                    return;
                }
                AppendLog("Started the local Hebrew dictation service (model trained on your voice).");
            }
            if (health == null || !health.Ready)
            {
                health = await WaitForReadyAsync();
                if (health == null)
                {
                    SetDictationStatus("Hebrew engine didn't start — see log");
                    AppendLog($"Hebrew engine didn't become ready. Details: {LocalDictationClient.LogPath}");
                    return;
                }
            }
            if (health.Error != null)
            {
                SetDictationStatus("Hebrew engine failed to load — see log");
                AppendLog($"Hebrew engine error: {health.Error}");
                return;
            }

            string? error = await _dictation.StartRecordingAsync();
            if (error != null && await _dictation.GetHealthAsync() == null)
            {
                // The engine went away between the check above and now -- usually by
                // exiting for being idle, which frees the GPU: start it again and retry
                // once, so a reload never looks like a failure.
                SetDictationStatus("Reloading the Hebrew engine...");
                if (_dictation.TryStartService(out _) && await WaitForReadyAsync() is { Error: null })
                    error = await _dictation.StartRecordingAsync();
            }
            if (error != null)
            {
                SetDictationStatus("Couldn't start recording — see log");
                AppendLog($"Dictation start failed: {error}");
                return;
            }
            // The engine may have been started (or restarted after an idle exit)
            // just now, in which case it is running with punctuation off: send the
            // user's choice before the first phrase is recognized.
            await _dictation.SetSpokenPunctuationAsync(_spokenMarksCheck.Checked);
            await _dictation.SetFastModeAsync(_fastCheck.Checked);

            _dictating = true;
            _dictateButton.Active = true;
            _typedPhrases = 0;
            System.Media.SystemSounds.Asterisk.Play();
            _dictateButton.Text = "Stop and type (Ctrl+Alt+D)";
            SetDictationStatus("● Listening… your words appear as you pause");
            StartPhrasePolling();
        }
        finally
        {
            _dictationBusy = false;
        }
    }

    /// <summary>
    /// Types each phrase the moment the service finishes it, while the user is still
    /// speaking. Asked for on 2026-09-16: dictating a long sentence with nothing on
    /// screen until the end is hard. The unit is the phrase, not the word — Whisper
    /// does not stream, and a lone word would reach the correction layer without the
    /// sentence context that fixes it.
    /// </summary>
    private void StartPhrasePolling()
    {
        _phrasePolling?.Cancel();
        var cts = new CancellationTokenSource();
        _phrasePolling = cts;
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // Every tick is dead time between the user finishing a
                    // phrase and seeing it typed. The request is a local GET
                    // that returns nothing most of the time; 150 ms costs
                    // nothing and removes a quarter-second of the wait the user
                    // reported on 2026-09-25.
                    await Task.Delay(150, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                var phrases = await _dictation.GetSegmentsAsync(_typedPhrases);
                if (cts.IsCancellationRequested) return;
                foreach (string phrase in phrases)
                {
                    TypePhrase(phrase);
                    _typedPhrases++;
                }
            }
        }, cts.Token);
    }

    /// <summary>Types one phrase on the UI thread, so window activation and the
    /// keystrokes go out from the thread that owns this form, exactly as they do
    /// for Voiceitt text.</summary>
    private bool TypePhrase(string text)
    {
        if (InvokeRequired) return (bool)Invoke(() => TypePhrase(text));
        bool typed = _engine?.TypeDictation(text) ?? false;
        if (typed)
        {
            SetDictationStatus($"● Listening… typed: {Shorten(text, 35)}");
            AppendLog($"Phrase: {text}");
        }
        else
        {
            AppendLog($"Couldn't type this phrase: {text}");
        }
        return typed;
    }

    /// <summary>Sends the spoken-marks choice to the dictation service.</summary>
    /// <remarks>Quiet on failure for the same reason as ApplyPunctuation: the
    /// service is usually not running when the user flips this, and
    /// ToggleDictation sends it again once the engine is up.</remarks>
    private async void ApplySpokenMarks()
    {
        string? error = await _dictation.SetSpokenPunctuationAsync(_spokenMarksCheck.Checked);
        if (error == null)
        {
            AppendLog(_spokenMarksCheck.Checked
                ? "Spoken marks: on (say נקודה, פסיק, סימן שאלה at a pause)"
                : "Spoken marks: off");
        }
    }

    /// <summary>Sends the speed/accuracy choice to the dictation service.</summary>
    private async void ApplyFastMode()
    {
        string? error = await _dictation.SetFastModeAsync(_fastCheck.Checked);
        if (error == null)
        {
            AppendLog(_fastCheck.Checked
                ? "Speed: one model (quicker, 6.1% word error)"
                : "Speed: both models (4.7% word error)");
        }
    }

    private async Task StopDictationAsync()
    {
        _dictating = false;
        _dictateButton.Active = false;
        _phrasePolling?.Cancel();
        _phrasePolling = null;
        _dictateButton.Text = "Hebrew dictation: start (Ctrl+Alt+D)";
        System.Media.SystemSounds.Beep.Play();
        SetDictationStatus("Recognizing the last phrase…");

        var result = await _dictation.StopAndRecognizeAsync();
        if (result.Error != null)
        {
            SetDictationStatus("Recognition failed — see log");
            AppendLog($"Dictation failed: {result.Error}");
            return;
        }

        // Phrases the poll never picked up (a slow or failed poll): stop returns them
        // all, and _typedPhrases says where this app got to.
        int missed = 0;
        for (int i = _typedPhrases; i < result.Segments.Count; i++)
        {
            TypePhrase(result.Segments[i]);
            _typedPhrases++;
            missed++;
        }

        if (string.IsNullOrWhiteSpace(result.Text))
        {
            _fixButton.Enabled = _typedPhrases > 0;
            SetDictationStatus(result.Reason switch
            {
                "already_typed" => $"Done — {_typedPhrases} phrase(s) typed",
                "too_short" => "Too short — speak a little longer before stopping",
                "no_speech" => "No speech heard — check the microphone",
                _ => _typedPhrases > 0 ? $"Done — {_typedPhrases} phrase(s) typed" : "Nothing recognized — try again",
            });
            return;
        }

        _fixButton.Enabled = true;
        bool typed = TypePhrase(result.Text);
        SetDictationStatus(typed
            ? $"Done — typed: {Shorten(result.Text, 40)}"
            : "Recognized, but couldn't type it — see log");
        AppendLog($"Last phrase ({result.Seconds:0.0}s): {result.Text}" +
                  (result.Corrected ? $"   [before correction: {result.RawAsr}]" : "") +
                  (missed > 0 ? $"   [{missed} phrase(s) caught up at the end]" : ""));
    }

    /// <summary>
    /// Review the last dictation and save what the user actually said.
    ///
    /// This is the only thing in the app that writes a recording to disk, and it
    /// only ever happens because the user asked for it here. Every pair saved is
    /// training data of a kind reading prompts cannot produce: the user's own
    /// words, with the errors the model really makes on them.
    /// </summary>
    private async void FixLastDictation()
    {
        var phrases = await _dictation.GetLastAsync();
        if (phrases.Count == 0)
        {
            SetDictationStatus("Nothing to fix yet — dictate something first");
            return;
        }

        using var dialog = new FixDictationForm(phrases);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ToSave.Count == 0)
        {
            SetDictationStatus("Nothing saved");
            return;
        }

        int saved = 0;
        foreach (var (index, text) in dialog.ToSave)
        {
            string? error = await _dictation.CorrectAsync(index, text);
            if (error == null) saved++;
            else AppendLog($"Couldn't save that phrase: {error}");
        }
        SetDictationStatus(saved > 0
            ? $"Saved {saved} phrase(s) to train on"
            : "Couldn't save — see log");
        AppendLog($"Training data: {dialog.Summary()}");
    }

    /// <summary>Teach one word: say it a few times, type what it should be.
    ///
    /// For the words that come out wrong every single time — a name, a loanword —
    /// where fixing them one dictation at a time never catches up. Needs the
    /// engine running, because each take is played back to it so the user can see
    /// what it hears today.</summary>
    private async void TeachWord()
    {
        if (_dictating)
        {
            SetDictationStatus("Stop the dictation first (Ctrl+Alt+D)");
            return;
        }
        if (!await EnsureEngineReadyAsync()) return;

        using var dialog = new WordTrainerForm(_dictation);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            SetDictationStatus("Nothing taught");
            return;
        }
        SetDictationStatus("Word taught — it trains into the model next round");
        AppendLog(dialog.Summary);
    }

    /// <summary>Starts the Hebrew engine if it isn't running, and waits for it.
    /// Reports what went wrong and returns false if it never became ready.</summary>
    private async Task<bool> EnsureEngineReadyAsync()
    {
        var health = await _dictation.GetHealthAsync();
        if (health == null)
        {
            if (!_dictation.TryStartService(out string message))
            {
                SetDictationStatus("Hebrew engine couldn't start — see log");
                AppendLog(message);
                return false;
            }
            AppendLog("Started the local Hebrew dictation service (model trained on your voice).");
        }
        if (health == null || !health.Ready)
        {
            SetDictationStatus("Hebrew engine loading…");
            health = await WaitForReadyAsync();
            if (health == null)
            {
                SetDictationStatus("Hebrew engine didn't start — see log");
                AppendLog($"Hebrew engine didn't become ready. Details: {LocalDictationClient.LogPath}");
                return false;
            }
        }
        if (health.Error != null)
        {
            SetDictationStatus("Hebrew engine failed to load — see log");
            AppendLog($"Hebrew engine error: {health.Error}");
            return false;
        }
        return true;
    }

    private async Task<LocalDictationClient.Health?> WaitForReadyAsync()
    {
        var started = DateTime.Now;
        while (DateTime.Now - started < TimeSpan.FromMinutes(10))
        {
            var health = await _dictation.GetHealthAsync();
            if (health != null && health.Ready) return health;
            int seconds = (int)(DateTime.Now - started).TotalSeconds;
            SetDictationStatus($"Loading the Hebrew engine… {seconds}s (the first start takes a few minutes)");
            await Task.Delay(1000);
        }
        return null;
    }

    private void SetDictationStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetDictationStatus(text)); return; }
        _dictationStatus.Text = text;
    }

    private void RefreshStatus()
    {
        if (InvokeRequired) { BeginInvoke(RefreshStatus); return; }
        if (_engine == null) return;

        bool linked = _engine.IsCalibrated;
        if (!string.IsNullOrEmpty(_engine.ActionNeeded))
        {
            // Something needs the user's attention — replace the normal status with
            // exactly what's wrong and what to do about it, instead of a quiet log
            // line that's easy to miss while looking somewhere else entirely.
            _statusLabel.Text = "⚠ " + _engine.ActionNeeded;
            _statusLabel.Accent = Hud.Red;
        }
        else
        {
            _statusLabel.Text = !linked ? "NO LINK — AWAITING TARGET"
                              : _engine.IsPaused ? "LINKED — PAUSED"
                              : !_engine.TypingEnabled ? "LINKED — TYPING OFF, LISTENING FOR COMMANDS"
                              : "LINKED — TYPING WHAT VOICEITT HEARS";
            _statusLabel.Accent = linked && !_engine.IsPaused ? Hud.Cyan : Hud.Red;
        }
        _targetLabel.Text = $"Types into: {Shorten(_engine.TargetWindowTitle)}";
        _pauseButton.Text = _engine.IsPaused ? "Resume (Ctrl+Alt+P)" : "Pause (Ctrl+Alt+P)";
        UpdateHud();
    }

    private static string Shorten(string s, int max = 50) => s.Length <= max ? s : s[..(max - 3)] + "...";

    private void AppendLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\r\n");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Interop.UnregisterHotKey(Handle, HotkeyCalibrate);
        Interop.UnregisterHotKey(Handle, HotkeyPause);
        Interop.UnregisterHotKey(Handle, HotkeyDictate);
        Interop.UnregisterHotKey(Handle, HotkeyFix);
        _hudTimer.Stop();
        // Free the GPU when the app closes, even mid-dictation.
        _phrasePolling?.Cancel();
        _dictation.ShutdownService();
        base.OnFormClosing(e);
    }
}
