using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace SttApp;

public sealed class WhisperTranscriber : IAsyncDisposable, IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private readonly AppSettings _settings;
    private bool _initialized;

    // Zpráva o použitém runtime (Vulkan / Cpu) – nastavena při Initialize()
    public string RuntimeInfo { get; private set; } = "nezjištěno";

    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "stt.log");

    internal static void AppLog(string msg) => Log(msg);

    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:HH:mm:ss.fff}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public WhisperTranscriber(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Načte model (volat jednou při startu aplikace – může trvat několik sekund).
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;

        var modelPath = ResolveModelPath(_settings.ModelPath);
        Log($"Initialize – model: {modelPath}");

        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                $"Whisper model nenalezen. Hledáno v:\n" +
                $"  {modelPath}\n" +
                "Zkopírujte model do složky models/ vedle EXE souboru.", modelPath);

        Log($"Model nalezen, velikost: {new FileInfo(modelPath).Length / (1024 * 1024)} MB");

        // Nastav poradi nacitani native runtime
        var order = _settings.GpuDeviceId >= 0
            ? new System.Collections.Generic.List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
            : new System.Collections.Generic.List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx };

        RuntimeInfo = $"preferován {order[0]} (přepnutí na CPU pokud Vulkan chybí)";
        Log($"RuntimeLibraryOrder: {string.Join(", ", order)}");
        RuntimeOptions.Instance.SetRuntimeLibraryOrder(order);

        Log("WhisperFactory.FromPath – start");
        _factory = WhisperFactory.FromPath(modelPath);
        Log("WhisperFactory.FromPath – hotovo");

        _processor = _factory.CreateBuilder()
            .WithLanguage(_settings.Language)
            .Build();

        _initialized = true;
        Log($"Initialize – dokončeno, runtime: {RuntimeInfo}");
    }

    /// <summary>
    /// Přepíše WAV stream do textu. Vrátí celý přepsaný text.
    /// <paramref name="onSegment"/> je volán pro každý segment ihned po jeho zpracování (streaming).
    /// </summary>
    public async Task<string> TranscribeAsync(Stream wavStream, CancellationToken ct = default,
        Func<string, Task>? onSegment = null)
    {
        if (!_initialized || _processor is null)
            throw new InvalidOperationException("Transcriber není inicializován. Zavolejte Initialize().");

        Log($"TranscribeAsync – start, WAV: {wavStream.Length / 1024} kB, runtime: {RuntimeInfo}");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var segments = new System.Text.StringBuilder();
        int segCount = 0;

        await foreach (var segment in _processor.ProcessAsync(wavStream, ct))
        {
            segCount++;
            string segText = segment.Text.Trim();
            Log($"  segment #{segCount}: \"{segText}\"");
            if (!string.IsNullOrWhiteSpace(segText) && !IsHallucination(segText))
            {
                segments.Append(segText).Append(' ');
                if (onSegment is not null)
                {
                    try { await onSegment(segText); }
                    catch (Exception ex) { Log($"  onSegment callback chyba: {ex.Message}"); }
                }
            }
        }

        var result = segments.ToString().Trim();
        Log($"TranscribeAsync – hotovo za {sw.Elapsed.TotalSeconds:F1}s, segmentů: {segCount}, délka textu: {result.Length}");
        return result;
    }

    // Whisper halucination filter – krátké fráze kde vyžadujeme exaktní shodu
    // (aby se neodfiltrovala věta jako "Děkujeme vám za spolupráci na projektu")
    private static readonly string[] _hallucinationExact =
    [
        "děkujeme",
        "děkuji",
        "děkuju",
        "díky",
        "thank you",
        "thanks",
        "na shledanou",
        "ahoj",
    ];

    // Whisper halucination filter – segmenty ktere Whisper casto vklada do vystupu
    // bez ohledu na obsah audia (titulky, podekování, znacky ticha, apod.)
    private static readonly string[] _hallucinationPatterns =
    [
        "titulky vytvořil",
        "titulky vytvoril",
        "subtitles by",
        "subtitle by",
        "translated by",
        "translation by",
        "transcribed by",
        "amara.org",
        "www.zelenka",
        "subscribed to",
        "subscribe to",
        "thanks for watching",
        "thank you for watching",
        "like and subscribe",
        "www.",
        ".com",
        ".cz/",
        "[hudba]",
        "[music]",
        "[smích]",
        "[potlesk]",
        "[applause]",
        "[laughter]",
        "díky za pozornost",
        "děkuji za pozornost",
        "děkujeme za pozornost",
    ];

    private static bool IsHallucination(string text)
    {
        // Krátké segmenty složené jen z interpunkce/mezer
        var trimmed = text.Trim(' ', '.', ',', '!', '?', '…', '-', '–');
        if (trimmed.Length == 0) return true;

        var lower = text.ToLowerInvariant().Trim();

        // Exaktní shoda (celý segment je jen halucinační fráze)
        var stripped = lower.Trim(' ', '.', ',', '!', '?', '…', '-', '–');
        foreach (var exact in _hallucinationExact)
        {
            if (stripped == exact)
            {
                AppLog($"  hallucination filtered (exact): \"{text}\"");
                return true;
            }
        }

        // Obsahová shoda (vzor je součástí segmentu)
        foreach (var pattern in _hallucinationPatterns)
        {
            if (lower.Contains(pattern))
            {
                AppLog($"  hallucination filtered: \"{text}\"");
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _initialized = false;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await ValueTask.CompletedTask;
    }

    /// <summary>
    /// Hleda model v tomto poradi:
    /// 1. Absolutni cesta (pokud je zadana)
    /// 2. Relativne k AppContext.BaseDirectory (vydany EXE)
    /// 3. Relativne k aktualni pracovni slozce (dotnet run / vyvoj)
    /// Vraci prvni nalezenou cestu, jinak cestu z bodu 2.
    /// </summary>
    private static string ResolveModelPath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
            return configuredPath;

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, configuredPath),
            Path.Combine(Directory.GetCurrentDirectory(), configuredPath),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        // Vrat cestu relativne k BaseDirectory (pro chybovou hlasku)
        return candidates[0];
    }
}
