using System.Text.Json;
using System.Text.Json.Serialization;

namespace SttApp;

public sealed class AppSettings
{
    // Uložiště v %APPDATA%\Prompto – přežije dotnet build/run i reinstalaci exe
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Prompto", "settings.json");

    // Výchozí tovární konfigurace vedle exe (zapsána při prvním spuštění pokud chybí)
    private static readonly string FactoryPath = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    // Hotkey
    public int HotkeyModifiers { get; set; } = 0x0006; // MOD_CONTROL | MOD_SHIFT
    public int HotkeyVirtualKey { get; set; } = 0x77;  // VK_F8 → výchozí Ctrl+Shift+F8

    // Model
    public string ModelPath { get; set; } = "models/ggml-small.bin";
    public string Language { get; set; } = "cs";

    // GPU – 0 = první CUDA GPU (RTX 4060), -1 = CPU fallback
    public int GpuDeviceId { get; set; } = 0;

    // Recording
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RecordingMode RecordingMode { get; set; } = RecordingMode.Toggle;

    // WebSocket (volitelné)
    public bool EnableWebSocket { get; set; } = false;
    public int WebSocketPort { get; set; } = 5050;

    /// <summary>
    /// Auth token vyžadovaný od WebSocket klientů (query ?token=... nebo header Authorization: Bearer ...).
    /// Pokud je prázdný, vygeneruje se při uložení nastavení.
    /// </summary>
    public string WebSocketAuthToken { get; set; } = string.Empty;

    // Clipboard – automaticky kopírovat přepis do schránky
    public bool CopyToClipboard { get; set; } = true;

    // Live streaming – délka audio chunku v sekundách (0 = vypnuto, použij batch mode)
    public int ChunkIntervalSeconds { get; set; } = 8;

    public static AppSettings Load()
    {
        var s = LoadCore();
        // Vždy zajisti existenci WS auth tokenu (pro existující settings.json bez něj)
        if (string.IsNullOrWhiteSpace(s.WebSocketAuthToken))
        {
            s.WebSocketAuthToken = GenerateAuthToken();
            try { s.Save(); } catch { /* ignore – persistence není kritická */ }
        }
        return s;
    }

    private static AppSettings LoadCore()
    {
        // 1. Zkus uložiště v AppData (uživatelská nastavení)
        if (File.Exists(ConfigPath))
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppSettings>(json,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new AppSettings();
            }
            catch { /* korupce – padni zpět na výchozí */ }
        }

        // 2. Zkus tovární config vedle exe
        if (File.Exists(FactoryPath))
        {
            try
            {
                var json = File.ReadAllText(FactoryPath);
                return JsonSerializer.Deserialize<AppSettings>(json,
                           new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                       ?? new AppSettings();
            }
            catch { }
        }

        return new AppSettings();
    }

    public void Save()
    {
        // Auto-generuj WS token, pokud chybí (kryptograficky bezpečný, URL-safe)
        if (string.IsNullOrWhiteSpace(WebSocketAuthToken))
            WebSocketAuthToken = GenerateAuthToken();

        // Zajisti existenci složky %APPDATA%\Prompto
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    private static string GenerateAuthToken()
    {
        var bytes = new byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        // URL-safe base64 (bez +, /, =)
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

public enum RecordingMode
{
    Toggle,     // první stisk = start, druhý = stop
    PushToTalk  // drž = nahrávej, pusť = stop  (zatím toggle, PTT v ext. verzi)
}
