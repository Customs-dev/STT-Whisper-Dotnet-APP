using NAudio.Wave;

namespace SttApp;

/// <summary>
/// Nahrává audio z výchozího mikrofonu do MemoryStream (WAV, 16 kHz, 16 bit, mono).
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private static readonly WaveFormat RecordFormat = new(sampleRate: 16000, channels: 1);

    private WaveInEvent? _waveIn;
    private MemoryStream? _buffer;
    private WaveFileWriter? _writer;
    private bool _recording;

    public bool IsRecording => _recording;

    /// <summary>
    /// Vyšší-úrovňová výjimka pro problémy se vstupním zařízením –
    /// nese uživatelsky srozumitelnú zprávu (caller jí může přímo zobrazit).
    /// </summary>
    public sealed class AudioDeviceException : Exception
    {
        public AudioDeviceException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// Počet dostupných vstupních (mikrofonních) zařízení.
    /// </summary>
    public static int GetInputDeviceCount() => WaveInEvent.DeviceCount;

    /// <summary>
    /// Název výchozího vstupního zařízení (device 0), nebo null pokud žádné není.
    /// </summary>
    public static string? GetDefaultInputDeviceName()
    {
        try
        {
            if (WaveInEvent.DeviceCount <= 0) return null;
            return WaveInEvent.GetCapabilities(0).ProductName;
        }
        catch { return null; }
    }

    public void Start()
    {
        if (_recording) return;

        // 1) Kontrola dostupnosti zařízení – nez sáhneme na NAudio
        int deviceCount;
        try { deviceCount = WaveInEvent.DeviceCount; }
        catch (Exception ex)
        {
            throw new AudioDeviceException(
                "Nepodařilo se zjistit počet zvukových vstupních zařízení. " +
                "Zkontroluj, zda běží služba Windows Audio.", ex);
        }

        if (deviceCount <= 0)
        {
            throw new AudioDeviceException(
                "Nebyl nalezen žádný mikrofon.\n" +
                "Připoj mikrofon a v Nastavení Windows → Soukromí → Mikrofon " +
                "povol přístup pro desktopové aplikace.");
        }

        _buffer = new MemoryStream();
        _waveIn = new WaveInEvent
        {
            WaveFormat = RecordFormat,
            BufferMilliseconds = 50
        };

        _writer = new WaveFileWriter(_buffer, RecordFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            // Cleanup rozčasovaných zdrojů, aby objekt zůstal v "not recording" stavu
            try { _waveIn.DataAvailable -= OnDataAvailable; } catch { }
            try { _waveIn.Dispose(); } catch { }
            try { _writer.Dispose(); } catch { }
            try { _buffer.Dispose(); } catch { }
            _waveIn = null; _writer = null; _buffer = null;

            throw new AudioDeviceException(
                "Mikrofon nelze otevřít. Může být využíván jinou aplikací, zakázán " +
                "v nastavení Windows nebo odpojen.\nDetail: " + ex.Message, ex);
        }
        _recording = true;
    }

    /// <summary>
    /// Zahájí zastavení nahrávání. Výsledný WAV stream je dostupný přes vrácený Task.
    /// NEPOUŽÍ z UI vlákna jako blokující volání – vždy await nebo Task.Run.
    /// </summary>
    public Task<MemoryStream> StopAndGetWavAsync()
    {
        if (!_recording || _waveIn is null || _writer is null || _buffer is null)
            return Task.FromResult(new MemoryStream());

        var tcs = new TaskCompletionSource<MemoryStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        // RecordingStopped je volán z NAudio interního vlákna (ne z UI vlákna)
        _waveIn.RecordingStopped += (_, _) =>
        {
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();
            _waveIn = null;
            _recording = false;

            if (_writer is not null)
            {
                _writer.Flush();
                // ToArray() musi byt pred Dispose() – WaveFileWriter zavre _buffer pri Dispose
                var bytes = _buffer?.ToArray();
                _writer.Dispose();
                _writer = null;
                _buffer = null;

                tcs.TrySetResult(bytes is { Length: > 0 }
                    ? new MemoryStream(bytes)
                    : new MemoryStream());
            }
            else
            {
                tcs.TrySetResult(new MemoryStream());
            }
        };

        // StopRecording je neblokující – spustí async zastavení, RecordingStopped dobehne pozdeji
        _waveIn.StopRecording();
        return tcs.Task;
    }

    // Synchronni verze pro Dispose
    private MemoryStream StopAndGetWavSync()
    {
        if (!_recording || _waveIn is null || _writer is null || _buffer is null)
            return new MemoryStream();

        _waveIn.StopRecording();
        System.Threading.Thread.Sleep(200); // kratke cekani pro Dispose scenar
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _waveIn = null;
        _recording = false;
        _writer.Flush();
        _writer.Dispose();
        _writer = null;
        var result = _buffer;
        _buffer = null;
        result.Position = 0;
        return result;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_writeLock)
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    /// <summary>
    /// Extrahuje aktuální audio buffer jako WAV a okamžitě začne nový buffer.
    /// Nahrávání POKRAČUJE bez přerušení – používá se pro live chunked přepis.
    /// Volat pouze z background vlákna, ne z UI vlákna.
    /// </summary>
    public MemoryStream? ExtractChunkAndContinue()
    {
        if (!_recording || _waveIn is null) return null;

        // Atomicky vyměníme writer+buffer za nový, DataAvailable je synchronizován lockem
        MemoryStream? oldBuffer;
        WaveFileWriter? oldWriter;

        lock (_writeLock)
        {
            oldWriter = _writer;
            oldBuffer = _buffer;

            _buffer = new MemoryStream();
            _writer = new WaveFileWriter(_buffer, RecordFormat);
        }

        if (oldWriter is null || oldBuffer is null) return null;

        oldWriter.Flush();
        var bytes = oldBuffer.ToArray();
        oldWriter.Dispose(); // zavře oldBuffer
        // bytes obsahují kompletní WAV včetně headeru

        return bytes.Length > 44 ? new MemoryStream(bytes) : null;
    }

    private readonly object _writeLock = new();

    public void Dispose()
    {
        if (_recording)
            StopAndGetWavSync().Dispose();

        _writer?.Dispose();
        _waveIn?.Dispose();
        _buffer?.Dispose();
    }
}
