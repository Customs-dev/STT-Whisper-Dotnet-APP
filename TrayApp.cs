using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
    // Akumulátor chunků – text se vkládá NAJEDNOU na konci (chunk paste by selhával pro Outlook/Copilot)
    private readonly System.Text.StringBuilder _chunkAccum = new();

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
                    ShowBalloon("Prompto připraven",
                        $"Zkratka: {key} (1× spustí záznamník, 2× zastaví a spustí přepis)\n" +
                        $"Vložení přepisu na místo kurzoru chvíli trvá.\n" +
                        $"Runtime: {runtime}", ToolTipIcon.Info, 8000);
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
        menu.Items.Add("O aplikaci", null, (_, _) => new AboutForm().ShowDialog());
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
        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _hotkey.HotkeyPressed += OnHotkeyPressed;

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

    private void StartRecording()
    {
        // Zachyť HWND okna, do kterého bude text vložen (aktuální foreground)
        _targetHwnd = GetForegroundWindow();
        // Zachyt take focused child – v Outlooku je to konkretni _WwG compose pane
        // Bez toho by RestoreForeground po 8+ sekundach obnovil focus na reading pane
        _targetChildHwnd = TextInjector.CaptureFocusedEditChild(_targetHwnd);
        _chunkAccum.Clear();

        _recorder.Start();
        SetStatus("Nahrávám...", TrayIconState.Recording);

        // Spusť chunk timer (pokud je live mode zapnutý)
        if (_settings.ChunkIntervalSeconds > 0)
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

            // Streaming: každý segment ihned pošleme WebSocket klientům
            Func<string, Task>? wsCallback = (_settings.EnableWebSocket && _wsServer is not null)
                ? seg => _wsServer.BroadcastAsync(seg)
                : null;

            var text = await _transcriber.TranscribeAsync(chunk, cts.Token, wsCallback);
            WhisperTranscriber.AppLog($"  chunk hotovo: \"{text}\"");

            if (!string.IsNullOrWhiteSpace(text))
            {
                // Chunk NEkládáme hned – akumulujeme, paste proběhne najednou na konci.
                // Přímý paste chunků selhává v Outlook/Copilot (UIPI, focus).
                lock (_chunkAccum)
                    _chunkAccum.Append(text.TrimEnd()).Append(' ');
                WhisperTranscriber.AppLog($"  chunk akumulován ({_chunkAccum.Length} znaků)");
                // WebSocket segmenty již odeslány průběžně přes wsCallback výše
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
                // Streaming: každý segment ihned pošleme WebSocket klientům
                Func<string, Task>? wsCallback = (_settings.EnableWebSocket && _wsServer is not null)
                    ? seg => _wsServer.BroadcastAsync(seg)
                    : null;
                var text = await _transcriber.TranscribeAsync(wavStream, cts.Token, wsCallback);
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

                // Paste před ballooněm – balloon může krátce krást focus
                WhisperTranscriber.AppLog($"paste start, hwnd={hwnd:X} childHint={childHint:X}, celkem {fullText.Length} znaků (chunky: {accumulated.Length}, finální: {text.Length})");
                await TextInjector.PasteViaClipboardAsync(fullText, hwnd, childHint);
                WhisperTranscriber.AppLog($"paste hotovo");

                _uiContext.Post(_ => ShowBalloon("Prompto – přepsáno", preview, ToolTipIcon.Info, 4000), null);
                // WebSocket segmenty již odeslány průběžně přes wsCallback výše
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
        if (_wsServer is not null)
            await _wsServer.DisposeAsync();
    }
}
