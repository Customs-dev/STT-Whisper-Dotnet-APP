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

    public void Start()
    {
        if (_recording) return;

        _buffer = new MemoryStream();
        _waveIn = new WaveInEvent
        {
            WaveFormat = RecordFormat,
            BufferMilliseconds = 50
        };

        _writer = new WaveFileWriter(_buffer, RecordFormat);

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
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
