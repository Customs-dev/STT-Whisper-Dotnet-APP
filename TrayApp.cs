using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Velopack;
using Velopack.Sources;

namespace SttApp;

public sealed class TrayApp : ApplicationContext, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _trayIcon;
    private readonly HotkeyManager _hotkey;
    private readonly AudioRecorder _recorder;
    private readonly WhisperTranscriber _transcriber;
    private WebSocketServer? _wsServer;

    private ToolStripMenuItem _statusItem = null!;
    private bool _processing;
    private readonly SynchronizationContext _uiContext;
    private readonly UpdateManager _updateManager;
    private readonly TranscriptionHistory _history = new();

    // HWND aplikace, která měla focus při zahájení nahrávání
    private IntPtr _targetHwnd = IntPtr.Zero;
    // Focused child edit-control v momentu stisku klávesy (napr. Outlook compose _WwG)
    // Pouziva se jako hint pro WM_PASTE – obchazi problem kdy RestoreForeground
    // obnovi focus na jiny (read-only) child nez byl aktivni pri nahravani.
    private IntPtr _targetChildHwnd = IntPtr.Zero;
    // Timer pro live chunked přepis
    private System.Threading.Timer? _chunkTimer;
    // Serialize chunk transcription (jen jeden chunk současně)
    private readonly System.Threading.SemaphoreSlim _chunkSem = new(1, 1);
    // Serialize live chunk paste operations (zabrání překryvu paste vláken)
    private readonly System.Threading.SemaphoreSlim _livePasteSem = new(1, 1);
    // Akumulátor chunků – text se vkládá NAJEDNOU na konci (chunk paste by selhával pro Outlook/Copilot)
    private readonly System.Text.StringBuilder _chunkAccum = new();
    private bool _liveInsertEnabled;
    private bool _liveInsertAny;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    public TrayApp()
    {
        _uiContext = SynchronizationContext.Current
                     ?? new WindowsFormsSynchronizationContext();

        _settings = AppSettings.Load();
        _recorder = new AudioRecorder();
        _transcriber = new WhisperTranscriber(_settings);
        _hotkey = new HotkeyManager();

        _updateManager = new UpdateManager(
            new GithubSource("https://github.com/Customs-dev/STT-Whisper-Dotnet-APP", null, false));

        _trayIcon = BuildTrayIcon();

        // WebSocket (volitelné)
        if (_settings.EnableWebSocket)
        {
            _wsServer = new WebSocketServer(_settings.WebSocketPort);
            try
            {
                _wsServer.Start();
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ => ShowBalloon("Prompto – WebSocket chyba",
                    $"Server nešlo spustit na portu {_settings.WebSocketPort}.\n{ex.Message}",
                    ToolTipIcon.Error, 8000), null);
                _wsServer = null;
            }
        }

        // Inicializace Whisperu na pozadí (načítání modelu trvá)
        // Zobraz notifikaci o načítání ještě před spuštěním pozadí vlákna
        _uiContext.Post(_ => ShowBalloon("Prompto – načítám model…",
            "Whisper model se načítá do paměti.\nChvíli strpení, bude to trvat několik sekund.",
            ToolTipIcon.Info, 6000), null);

        Task.Run(() =>
        {
            try
            {
                _transcriber.Initialize();

                // RegisterHotKey MUSI byt volano z UI vlakna (vlastnici okno NativeWindow)
                // jinak Win32 vraci chybu bez ohledu na to, zda je zkratka volna
                _uiContext.Post(_ =>
                {
                    RegisterHotkey();
                    string key = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
                    string runtime = _transcriber.RuntimeInfo;
                    string modeHint = _settings.RecordingMode == RecordingMode.PushToTalk
                        ? $"Zkratka: {key} (drž = nahrávej, pusť = přepiš) [Push-to-Talk]"
                        : $"Zkratka: {key} (1× spustí záznamník, 2× zastaví a spustí přepis)";
                    ShowBalloon("Prompto připraven",
                        $"{modeHint}\n" +
                        $"Vložení přepisu na místo kurzoru chvíli trvá.\n" +
                        $"Runtime: {runtime}", ToolTipIcon.Info, 8000);

                    // Zobraz novinky po aktualizaci
                    ShowWhatsNewIfNeeded();

                    // Ověř aktualizace na pozadí po startu
                    _ = CheckForUpdatesAsync(userInitiated: false);
                }, null);
            }
            catch (FileNotFoundException ex)
            {
                _uiContext.Post(_ => ShowBalloon("Prompto – chyba modelu", ex.Message, ToolTipIcon.Error), null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ => ShowBalloon("Prompto – chyba inicializace", ex.Message, ToolTipIcon.Error), null);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Tray icon build
    // -------------------------------------------------------------------------

    private NotifyIcon BuildTrayIcon()
    {
        _statusItem = new ToolStripMenuItem("Připraven") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Nastavení", null, OpenSettings);
        menu.Items.Add("Historie přepisů", null, (_, _) => new HistoryForm(_history).ShowDialog());
        menu.Items.Add("Zkontrolovat aktualizace", null, (_, _) => _ = CheckForUpdatesAsync(userInitiated: true));
        menu.Items.Add("O aplikaci", null, (_, _) => new AboutForm().ShowDialog());
        menu.Items.Add("Co je nového", null, (_, _) =>
            new WhatsNewForm(GetAppVersion(), null).ShowDialog());
        if (_settings.EnableWebSocket)
        {
            menu.Items.Add(new ToolStripSeparator());
            string wsUrl = $"ws://localhost:{_settings.WebSocketPort}/stt/";
            var wsItem = new ToolStripMenuItem($"WebSocket: {wsUrl}") { Enabled = false };
            menu.Items.Add(wsItem);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ukončit", null, (_, _) => ExitApplication());

        var icon = new NotifyIcon
        {
            Icon = CreateMicIcon(TrayIconState.Idle),
            Text = "Prompto",
            Visible = true,
            ContextMenuStrip = menu
        };

        return icon;
    }

    private static Icon GetTrayIcon(bool idle) => CreateMicIcon(idle ? TrayIconState.Idle : TrayIconState.Recording);

    // Stavy ikony
    private enum TrayIconState { Idle, Recording, Processing }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Vykresli ikonu mikrofonu (16x16) pro dany stav:
    ///   Idle      = modry kruh + bily mikrofon
    ///   Recording = cerveny kruh + bily mikrofon + cervena tecka
    ///   Processing = oranzovy kruh + bily mikrofon + zvlnka
    /// </summary>
    private static Icon CreateMicIcon(TrayIconState state)
    {
        // Kreslime na 32x32 – lepsi renderovani v notifikacnim centru Windows
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Barevny kruh jako pozadi (31x31 v 32x32 bitmape)
        Color bg = state switch
        {
            TrayIconState.Recording  => Color.FromArgb(210, 40, 40),
            TrayIconState.Processing => Color.FromArgb(210, 130, 10),
            _                        => Color.FromArgb(25, 110, 210)
        };
        using (var bgBrush = new SolidBrush(bg))
            g.FillEllipse(bgBrush, 0, 0, 31, 31);

        // Bily mikrofon – telo (zaobleny obdelnik)
        using var whiteBrush = new SolidBrush(Color.White);
        using var whitePen   = new Pen(Color.White, 2.8f);

        var bodyPath = new GraphicsPath();
        AddRoundedRect(bodyPath, new RectangleF(10f, 4f, 10f, 13f), 5f);
        g.FillPath(whiteBrush, bodyPath);

        // Podstavec mikrofonu – polokruznice
        g.DrawArc(whitePen, 7f, 11f, 16f, 10f, 0, 180);

        // Drzak (svislá cara + zakladna)
        g.DrawLine(whitePen, 15f, 21f, 15f, 25f);
        g.DrawLine(whitePen, 10f, 25f, 20f, 25f);

        // Stavovy indikator v praveho hornim rohu
        if (state == TrayIconState.Recording)
        {
            // Cervena tecka = "nahrava"
            g.FillEllipse(Brushes.White, 23f, 1f, 7f, 7f);
            using var redBrush = new SolidBrush(Color.FromArgb(220, 40, 40));
            g.FillEllipse(redBrush, 24f, 2f, 5f, 5f);
        }
        else if (state == TrayIconState.Processing)
        {
            // Tri male bily prouzky = "zpracovava"
            for (int i = 0; i < 3; i++)
            {
                float h = 3f + i * 2f;
                g.FillRectangle(whiteBrush, 23f + i * 3f, 28f - h, 2f, h);
            }
        }

        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    private static void AddRoundedRect(GraphicsPath path, RectangleF r, float radius)
    {
        float d = radius * 2;
        path.AddArc(r.X,          r.Y,          d, d, 180, 90);
        path.AddArc(r.Right - d,  r.Y,          d, d, 270, 90);
        path.AddArc(r.Right - d,  r.Bottom - d, d, d,   0, 90);
        path.AddArc(r.X,          r.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
    }

    // -------------------------------------------------------------------------
    // Hotkey + pipeline
    // -------------------------------------------------------------------------

    private void RegisterHotkey()
    {
        // Odpojit stary handler (pri re-registraci z nastaveni)
        _hotkey.HotkeyPressed  -= OnHotkeyPressed;
        _hotkey.HotkeyPressed  += OnHotkeyPressed;
        _hotkey.HotkeyReleased -= OnHotkeyReleased;
        _hotkey.HotkeyReleased += OnHotkeyReleased;

        // Nastav PTT režim
        _hotkey.PushToTalkEnabled = _settings.RecordingMode == RecordingMode.PushToTalk;

        // Zkus nakonfigurovanou zkratku
        if (_hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey))
            return;

        // Nakonfigurovana zkratka je obsazena – zkus kandidaty v poradi preference
        // Format: (modifiers, virtualKey)
        // MOD_CONTROL|MOD_SHIFT = 0x06, MOD_CONTROL|MOD_ALT = 0x03
        var candidates = new (int mods, int vk)[]
        {
            (0x06, 0x77), // Ctrl+Shift+F8
            (0x06, 0x78), // Ctrl+Shift+F9
            (0x06, 0x79), // Ctrl+Shift+F10
            (0x06, 0x7A), // Ctrl+Shift+F11
            (0x06, 0x7B), // Ctrl+Shift+F12
            (0x03, 0x52), // Ctrl+Alt+R
            (0x03, 0x54), // Ctrl+Alt+T
            (0x03, 0x4D), // Ctrl+Alt+M
            (0x01, 0x78), // Alt+F9
            (0x01, 0x79), // Alt+F10
        };

        foreach (var (mods, vk) in candidates)
        {
            if (_hotkey.Register(mods, vk))
            {
                // Uloz uspesnou zkratku do nastaveni
                _settings.HotkeyModifiers = mods;
                _settings.HotkeyVirtualKey = vk;
                _settings.Save();
                return;
            }
        }

        // Zadna zkratka nefunguje – zobraz chybu
        ShowBalloon("Prompto – zkratka obsazena",
            "Vsechny vychozi zkratky jsou obsazeny.\n" +
            "Klikni pravym na ikonu → Nastaveni → zvol vlastni zkratku.",
            ToolTipIcon.Warning, 8000);
    }

    private static string FormatHotkey(int mods, int vk)
    {
        var parts = new List<string>();
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        parts.Add(((System.Windows.Forms.Keys)vk).ToString());
        return string.Join("+", parts);
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_processing) return;

        if (_settings.RecordingMode == RecordingMode.PushToTalk)
        {
            // PTT: stisk = start nahrávání (pokud ještě neběží)
            if (!_recorder.IsRecording)
            {
                PlayStartSound();
                StartRecording();
            }
            return;
        }

        // Toggle režim: 1× start, 2× stop
        if (_recorder.IsRecording)
        {
            PlayStopSound();
            StopRecordingAndTranscribe();
        }
        else
        {
            PlayStartSound();
            StartRecording();
        }
    }

    private void OnHotkeyReleased(object? sender, EventArgs e)
    {
        // Pouze pro PTT – uvolnění klávesy zastaví nahrávání a spustí přepis
        if (_settings.RecordingMode != RecordingMode.PushToTalk) return;
        if (_processing || !_recorder.IsRecording) return;

        PlayStopSound();
        StopRecordingAndTranscribe();
    }

    private void StartRecording()
    {
        // Zachyť HWND okna, do kterého bude text vložen (aktuální foreground)
        _targetHwnd = GetForegroundWindow();
        // Zachyt take focused child – v Outlooku je to konkretni _WwG compose pane
        // Bez toho by RestoreForeground po 8+ sekundach obnovil focus na reading pane
        _targetChildHwnd = TextInjector.CaptureFocusedEditChild(_targetHwnd);
        _chunkAccum.Clear();
        _liveInsertEnabled = _settings.EnableWebSocket && _settings.ChunkIntervalSeconds > 0;
        _liveInsertAny = false;

        _recorder.Start();
        SetStatus("Nahrávám...", TrayIconState.Recording);

        // Spusť chunk timer (pokud je live mode zapnutý)
        if (_settings.EnableWebSocket && _settings.ChunkIntervalSeconds > 0)
        {
            int intervalMs = _settings.ChunkIntervalSeconds * 1000;
            // První chunk pošli dříve, aby stream běžel i u kratších nahrávek.
            int firstTickMs = Math.Min(1500, intervalMs);
            _chunkTimer = new System.Threading.Timer(
                OnChunkTimerTick, null, firstTickMs, intervalMs);
        }
    }

    private void OnChunkTimerTick(object? state)
    {
        // Extrahuj chunk a přepiš asynchronně; ignoruj pokud předchozí stále běží
        if (!_recorder.IsRecording) return;
        Task.Run(TranscribeChunkAsync);
    }

    private async Task TranscribeChunkAsync()
    {
        // Nepřekrývat – pokud transcriber ještě zpracovává předchozí chunk, přeskoč
        if (!await _chunkSem.WaitAsync(0)) return;
        MemoryStream? chunk = null;
        try
        {
            chunk = _recorder.ExtractChunkAndContinue();
            if (chunk is null || chunk.Length < 8_000) return;

            WhisperTranscriber.AppLog($"  chunk {chunk.Length / 1024} kB → TranscribeAsync start");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
                _settings.ChunkIntervalSeconds * 6 + 30));

            IntPtr hwnd = _targetHwnd;
            IntPtr childHint = _targetChildHwnd;
            var segmentCallback = BuildSegmentStreamingCallback(hwnd, childHint, allowLiveInsert: true);

            var text = await _transcriber.TranscribeAsync(chunk, cts.Token, segmentCallback);
            WhisperTranscriber.AppLog($"  chunk hotovo: \"{text}\"");

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Chunk NEkládáme hned – akumulujeme, paste proběhne najednou na konci.
                // Přímý paste chunků selhává v Outlook/Copilot (UIPI, focus).
                lock (_chunkAccum)
                    _chunkAccum.Append(text.TrimEnd()).Append(' ');
                WhisperTranscriber.AppLog($"  chunk akumulován ({_chunkAccum.Length} znaků)");
            }
        }
        catch (Exception ex)
        {
            WhisperTranscriber.AppLog($"  chunk chyba: {ex.Message}");
        }
        finally
        {
            chunk?.Dispose();
            _chunkSem.Release();
        }
    }

    private void StopRecordingAndTranscribe()
    {
        // Zastav chunk timer
        _chunkTimer?.Dispose();
        _chunkTimer = null;

        _processing = true;
        SetStatus("Přepisuji...", TrayIconState.Processing);

        IntPtr hwnd      = _targetHwnd;
        IntPtr childHint = _targetChildHwnd;

        Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            using var progressTimer = new System.Threading.Timer(_ =>
            {
                _uiContext.Post(_ => ShowBalloon("Prompto – přepisuji...",
                    $"Inference stále běží.\nRuntime: {_transcriber.RuntimeInfo}", ToolTipIcon.Info, 6000), null);
            }, null, 15_000, System.Threading.Timeout.Infinite);

            MemoryStream? wavStream = null;
            try
            {
                // Počkej až doběhne případný posledního chunk
                await _chunkSem.WaitAsync(cts.Token);

                wavStream = await _recorder.StopAndGetWavAsync();
                long wavBytes = wavStream.Length;
                _chunkSem.Release();

                WhisperTranscriber.AppLog($"StopAndGetWavAsync hotovo, WAV: {wavBytes / 1024} kB");

                if (wavBytes < 8_000)
                {
                    // V chunk mode je prázdný finální buffer normální – vše bylo přepsáno průběžně
                    if (_settings.ChunkIntervalSeconds > 0)
                    {
                        _uiContext.Post(_ => SetStatus("Připraven", TrayIconState.Idle), null);
                        return;
                    }
                    _uiContext.Post(_ => ShowBalloon("Prompto – příliš krátký záznam",
                        $"Nahráno pouze {wavBytes} B.", ToolTipIcon.Warning), null);
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var segmentCallback = BuildSegmentStreamingCallback(hwnd, childHint, allowLiveInsert: _liveInsertEnabled);
                var text = await _transcriber.TranscribeAsync(wavStream, cts.Token, segmentCallback);
                sw.Stop();

                // Spoj akumulované chunky + finální přepis do jednoho textu
                string accumulated;
                lock (_chunkAccum)
                {
                    accumulated = _chunkAccum.ToString();
                    _chunkAccum.Clear();
                }

                // Pokud finální buffer prázdný ale chunky existují (krátký zbytek po chunking)
                if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(accumulated))
                {
                    _uiContext.Post(_ => ShowBalloon("Prompto – nic nerozpoznáno",
                        $"WAV: {wavBytes / 1024} kB, čas: {sw.Elapsed.TotalSeconds:F1}s\n" +
                        $"Runtime: {_transcriber.RuntimeInfo}", ToolTipIcon.Warning), null);
                    return;
                }

                string fullText = (accumulated + text).Trim();
                string preview = fullText.Length > 120 ? fullText[..120] + "…" : fullText;

                if (_liveInsertEnabled && _liveInsertAny)
                {
                    WhisperTranscriber.AppLog($"paste skip (live inserted), celkem {fullText.Length} znaků");
                }
                else
                {
                    // Paste před ballooněm – balloon může krátce krást focus
                    if (_settings.CopyToClipboard)
                    {
                        WhisperTranscriber.AppLog($"paste start, hwnd={hwnd:X} childHint={childHint:X}, celkem {fullText.Length} znaků (chunky: {accumulated.Length}, finální: {text.Length})");
                        await TextInjector.PasteViaClipboardAsync(fullText, hwnd, childHint);
                        WhisperTranscriber.AppLog($"paste hotovo");
                    }
                    else
                    {
                        WhisperTranscriber.AppLog($"clipboard přeskočen (vypnuto v nastavení), celkem {fullText.Length} znaků");
                    }
                }

                _history.Add(fullText, sw.Elapsed.TotalSeconds, _settings.Language);

                string balloonSuffix = _settings.CopyToClipboard ? "\n(text je ve schránce – Ctrl+V)" : "";
                _uiContext.Post(_ => ShowBalloon("Prompto – přepsáno", preview + balloonSuffix, ToolTipIcon.Info, 4000), null);
            }
            catch (OperationCanceledException)
            {
                _uiContext.Post(_ => ShowBalloon("Prompto – timeout",
                    "Přepis trval déle než 3 minuty.\n" +
                    $"Runtime: {_transcriber.RuntimeInfo}", ToolTipIcon.Error), null);
            }
            catch (Exception ex)
            {
                _uiContext.Post(_ => ShowBalloon("Prompto – chyba přepisu", ex.Message, ToolTipIcon.Error), null);
            }
            finally
            {
                wavStream?.Dispose();
                _processing = false;
                SetStatus("Připraven", TrayIconState.Idle);
            }
        });
    }

    // -------------------------------------------------------------------------
    // UI helpers
    // -------------------------------------------------------------------------

    private void SetStatus(string text, TrayIconState state)
    {
        _uiContext.Post(_ =>
        {
            _statusItem.Text = text;
            var newIcon = CreateMicIcon(state);
            var oldIcon = _trayIcon.Icon;
            _trayIcon.Icon = newIcon;
            _trayIcon.Text = $"Prompto – {text}";
            oldIcon?.Dispose();
        }, null);
    }

    private Func<string, Task>? BuildSegmentStreamingCallback(IntPtr hwnd, IntPtr childHint, bool allowLiveInsert)
    {
        bool wsEnabled = _settings.EnableWebSocket && _wsServer is not null;
        bool liveEnabled = allowLiveInsert && _liveInsertEnabled;
        if (!wsEnabled && !liveEnabled) return null;

        return async seg =>
        {
            var piece = seg?.Trim();
            if (string.IsNullOrWhiteSpace(piece)) return;
            string chunkText = piece + " ";

            if (wsEnabled && _wsServer is not null)
            {
                try { await _wsServer.BroadcastAsync(piece); }
                catch (Exception ex) { WhisperTranscriber.AppLog($"[WS] segment send chyba: {ex.Message}"); }
            }

            if (liveEnabled)
            {
                await _livePasteSem.WaitAsync();
                try
                {
                    await TextInjector.PasteViaClipboardAsync(chunkText, hwnd, childHint);
                    _liveInsertAny = true;
                }
                catch (Exception ex)
                {
                    WhisperTranscriber.AppLog($"live paste segment chyba: {ex.Message}");
                }
                finally
                {
                    _livePasteSem.Release();
                }
            }
        };
    }

    private void ShowBalloon(string title, string msg,
        ToolTipIcon icon = ToolTipIcon.Info, int ms = 3000)
    {
        _trayIcon.ShowBalloonTip(ms, title, msg, icon);
    }

    private void OpenSettings(object? sender, EventArgs e)
    {
        var form = new SettingsForm(_settings);
        form.ShowDialog();
        // SettingsForm.FormClosing vždy zapíše do _settings; tady persist + přeregistruj
        _settings.Save();
        _hotkey.Unregister();
        RegisterHotkey();
    }

    private void ShowWhatsNewIfNeeded()
    {
        var version = GetAppVersion();
        if (!WhatsNewForm.ShouldShow(version))
            return;

        var lastSeen = WhatsNewForm.GetLastSeenVersion();
        WhatsNewForm.MarkAsSeen(version);

        var form = new WhatsNewForm(version, lastSeen);
        form.ShowDialog();
    }

    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is System.Reflection.AssemblyInformationalVersionAttribute[] { Length: > 0 } attrs
            ? attrs[0].InformationalVersion : null;
        if (info is not null)
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        var ver = asm?.GetName().Version;
        return ver is null ? "?" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    // -------------------------------------------------------------------------
    // Zvukové efekty (NAudio – generovaný sinus, žádné soubory)
    // -------------------------------------------------------------------------

    // Start: dva tóny vzestupně – "připraven nahrávat"
    private static void PlayStartSound() => Task.Run(() =>
    {
        PlayBeep(659f, 80);           // E5 – krátký intro
        System.Threading.Thread.Sleep(60);
        PlayBeep(880f, 160);          // A5 – zřetelně vyšší
    });

    // Stop: dva tóny sestupně – "konec nahrávání"
    private static void PlayStopSound() => Task.Run(() =>
    {
        PlayBeep(784f, 130);          // G5 – výchozí výška
        System.Threading.Thread.Sleep(70);
        PlayBeep(494f, 250);          // B4 – výrazně nižší, delší
    });

    private static void PlayBeep(float frequency, int durationMs)
    {
        try
        {
            using var waveOut = new WaveOutEvent { DesiredLatency = 100 };
            var gen = new SignalGenerator(44100, 1)
            {
                Frequency = frequency,
                Gain      = 0.25,
                Type      = SignalGeneratorType.Sin
            };
            waveOut.Init(gen.Take(TimeSpan.FromMilliseconds(durationMs)));
            waveOut.Play();
            // Počkáme dokud tón dohraje
            while (waveOut.PlaybackState == PlaybackState.Playing)
                System.Threading.Thread.Sleep(10);
        }
        catch { }
    }

    // -------------------------------------------------------------------------
    // Auto-update (Velopack)
    // -------------------------------------------------------------------------

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        try
        {
            if (!_updateManager.IsInstalled)
            {
                if (userInitiated)
                    _uiContext.Post(_ => ShowBalloon("Prompto – aktualizace",
                        "Aplikace nebyla nainstalována přes installer.\nAutomatické aktualizace nejsou dostupné.",
                        ToolTipIcon.Warning, 5000), null);
                return;
            }

            WhisperTranscriber.AppLog($"[Update] IsInstalled=true, CurrentVersion={_updateManager.CurrentVersion}, AppId={_updateManager.AppId}");
            WhisperTranscriber.AppLog($"[Update] ExePath={Environment.ProcessPath}");

            var newVersion = await _updateManager.CheckForUpdatesAsync();
            if (newVersion is null)
            {
                WhisperTranscriber.AppLog("[Update] No update available (CheckForUpdatesAsync returned null)");
                if (userInitiated)
                    _uiContext.Post(_ => ShowBalloon("Prompto – aktualizace",
                        "Máte nejnovější verzi.", ToolTipIcon.Info, 3000), null);
                return;
            }

            WhisperTranscriber.AppLog($"[Update] Available: {newVersion.TargetFullRelease.Version}, current: {_updateManager.CurrentVersion}");

            // Zeptej se uživatele
            var result = MessageBox.Show(
                $"Je dostupná nová verze Prompto {newVersion.TargetFullRelease.Version}.\n\n" +
                $"Chcete ji stáhnout a nainstalovat?\nAplikace se po aktualizaci restartuje.",
                "Prompto – aktualizace",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result != DialogResult.Yes) return;

            _uiContext.Post(_ => ShowBalloon("Prompto – stahuji aktualizaci…",
                $"Verze {newVersion.TargetFullRelease.Version}", ToolTipIcon.Info, 5000), null);

            await _updateManager.DownloadUpdatesAsync(newVersion);

            // Uvolni VŠECHNY prostředky, které drží soubory v adresáři current\.
            // Zejména WhisperTranscriber drží nativní DLL (whisper.dll),
            // a AudioRecorder drží NAudio DLL.
            _uiContext.Post(_ =>
            {
                _trayIcon.Visible = false;
                _hotkey.Unregister();
            }, null);

            if (_wsServer is not null)
                await _wsServer.StopAsync();

            // Uvolni nativní knihovny (whisper.dll, NAudio atd.)
            _transcriber.Dispose();
            _recorder.Dispose();

            // Vynutíme uvolnění nativních handlů
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Postup aktualizace:
            // 1. Vyprázdníme current\ (smažeme soubory a podadresáře uvnitř)
            //    – Windows neumí přejmenovat neprázdný adresář, protože pozadí procesy
            //      (Defender, indexer, shell) drží file-level handly.
            //    – PRÁZDNÝ adresář přejmenovat JDE.
            // 2. Spustíme Update.exe apply -p <nupkg> – Velopack nyní úspěšně
            //    přejmenuje prázdný current\ → záloha, rozbalí nupkg do nového current\
            //    a uklidí zálohu. Tím se zachová veškerá Velopack metadata/shortcuts.
            var installDir = Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath!))!;
            var currentDir = Path.Combine(installDir, "current");
            var updateExe = Path.Combine(installDir, "Update.exe");
            var pkgPath = Path.Combine(installDir, "packages",
                $"Prompto-{newVersion.TargetFullRelease.Version}-full.nupkg");
            var exePath = Path.Combine(currentDir, "Prompto.exe");

            // Přesuneme nupkg do temp, aby VelopackApp ProcessExit hook nemohl
            // spustit Update.exe souběžně s naším PowerShell skriptem.
            var tempPkgDir = Path.Combine(Path.GetTempPath(), "Prompto_update_pkg");
            Directory.CreateDirectory(tempPkgDir);
            var tempPkgPath = Path.Combine(tempPkgDir,
                $"Prompto-{newVersion.TargetFullRelease.Version}-full.nupkg");
            if (File.Exists(tempPkgPath)) File.Delete(tempPkgPath);
            File.Move(pkgPath, tempPkgPath);

            WhisperTranscriber.AppLog($"[Update] Moved pkg to {tempPkgPath}, launching helper");

            var script = $@"
$ErrorActionPreference = 'Stop'
$logFile = Join-Path $env:LOCALAPPDATA 'Prompto\update-helper.log'
function Log($msg) {{ ""$(Get-Date -f 'HH:mm:ss') $msg"" | Out-File $logFile -Append }}

$myPid = {Environment.ProcessId}
$currentDir = '{currentDir.Replace("'", "''")}'
$pkgPath = '{tempPkgPath.Replace("'", "''")}'
$updateExe = '{updateExe.Replace("'", "''")}'
$exePath = '{exePath.Replace("'", "''")}'

Log 'Waiting for process exit...'
try {{ $p = Get-Process -Id $myPid -ErrorAction Stop; $p.WaitForExit() }} catch {{}}

Log 'Process exited, waiting 10s for handle release...'
Start-Sleep -Seconds 10

# 1. Vyprázdni current\ (soubory + podadresáře, ne samotný adresář)
Log 'Emptying current\ directory...'
for ($i = 0; $i -lt 5; $i++) {{
    try {{
        Get-ChildItem $currentDir -Force | Remove-Item -Recurse -Force -ErrorAction Stop
        Log 'current\ emptied successfully'
        break
    }} catch {{
        Log ""Retry $i emptying current\: $_""
        Start-Sleep -Seconds 5
    }}
}}

$remaining = (Get-ChildItem $currentDir -Force | Measure-Object).Count
if ($remaining -gt 0) {{
    Log ""ERROR: could not empty current\ ($remaining items remain)""
    # Fallback: alespoň zkopírujeme soubory přes existující
    Log 'Falling back to overwrite extraction...'
    $zipPath = $pkgPath -replace '\.nupkg$', '.zip'
    Copy-Item $pkgPath $zipPath -Force
    $extractDir = Join-Path $env:TEMP 'Prompto_update_extract'
    if (Test-Path $extractDir) {{ Remove-Item $extractDir -Recurse -Force }}
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    Remove-Item $zipPath -Force
    $srcDir = Join-Path $extractDir 'lib\app'
    if (Test-Path $srcDir) {{ Copy-Item ""$srcDir\*"" $currentDir -Recurse -Force }}
    Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    Log 'Fallback copy done, starting app...'
    Start-Process $exePath
    Log 'Update complete (fallback)!'
    exit 0
}}

# 2. Spusť Update.exe apply – prázdný current\ jde přejmenovat
Log 'Running Update.exe apply...'
try {{
    $proc = Start-Process -FilePath $updateExe -ArgumentList 'apply', '--norestart', '-p', $pkgPath `
        -Wait -PassThru -NoNewWindow -RedirectStandardError (Join-Path $env:TEMP 'prompto_update_stderr.txt')
    Log ""Update.exe exited with code $($proc.ExitCode)""
    if ($proc.ExitCode -ne 0) {{ throw ""Update.exe failed with exit code $($proc.ExitCode)"" }}
}} catch {{
    Log ""Update.exe failed: $_. Falling back to manual extraction...""
    # Fallback: ruční extrakce nupkg
    $zipPath = $pkgPath -replace '\.nupkg$', '.zip'
    Copy-Item $pkgPath $zipPath -Force
    $extractDir = Join-Path $env:TEMP 'Prompto_update_extract'
    if (Test-Path $extractDir) {{ Remove-Item $extractDir -Recurse -Force }}
    Expand-Archive -Path $zipPath -DestinationPath $extractDir -Force
    Remove-Item $zipPath -Force
    $srcDir = Join-Path $extractDir 'lib\app'
    if (Test-Path $srcDir) {{ Copy-Item ""$srcDir\*"" $currentDir -Recurse -Force }}
    Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
}}

# Ukliď temp nupkg
Remove-Item $pkgPath -Force -ErrorAction SilentlyContinue

Log 'Starting new version...'
Start-Process $exePath
Log 'Update complete!'
";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);

            // Ukončíme aplikaci ČISTĚ přes Application.Exit (ne Environment.Exit)
            // aby se provedly všechny WinForms cleanup handlery.
            _uiContext.Post(_ => Application.Exit(), null);
        }
        catch (Exception ex)
        {
            if (userInitiated)
                _uiContext.Post(_ => ShowBalloon("Prompto – chyba aktualizace",
                    ex.Message, ToolTipIcon.Error, 5000), null);
        }
    }

    private void ExitApplication()
    {
        _chunkTimer?.Dispose();
        _chunkTimer = null;
        _hotkey.Unregister();
        _trayIcon.Visible = false;
        Application.Exit();
    }

    // -------------------------------------------------------------------------
    // Dispose
    // -------------------------------------------------------------------------

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chunkTimer?.Dispose();
            _chunkSem.Dispose();
            _livePasteSem.Dispose();
            _hotkey.Dispose();
            _recorder.Dispose();
            _transcriber.Dispose();
            _trayIcon.Dispose();
            _wsServer?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    public async ValueTask DisposeAsync()
    {
        _hotkey.Dispose();
        _recorder.Dispose();
        _transcriber.Dispose();
        _trayIcon.Dispose();
        _livePasteSem.Dispose();
        if (_wsServer is not null)
            await _wsServer.DisposeAsync();
    }
}
