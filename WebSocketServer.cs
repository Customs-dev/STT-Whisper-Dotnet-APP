using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace SttApp;

/// <summary>
/// Jednoduchý lokální WebSocket server – broadcast přepsaného textu.
/// Slouží pro volitelnou integraci (IDE plugin, webová stránka apod.).
/// Spouštěn pouze pokud EnableWebSocket = true v nastavení.
/// </summary>
public sealed class WebSocketServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private readonly List<WebSocket> _clients = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public bool IsRunning { get; private set; }

    public WebSocketServer(int port)
    {
        _port = port;
        _listener = new HttpListener();
        // Přidáme oba prefixes – klient může použít 127.0.0.1 i localhost
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/stt/");
        _listener.Prefixes.Add($"http://localhost:{port}/stt/");
    }

    /// <summary>URL pro WebSocket klienty (ws://localhost:{port}/stt/)</summary>
    public string ClientUrl => $"ws://localhost:{_port}/stt/";

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            WhisperTranscriber.AppLog($"[WS] Start selhalo (port {_port}): {ex.Message}");
            throw;
        }
        IsRunning = true;
        WhisperTranscriber.AppLog($"[WS] Server spuštěn – {ClientUrl}");
        _acceptTask = AcceptLoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        _listener.Stop();
        if (_acceptTask is not null)
            await _acceptTask.ConfigureAwait(false);
    }

    /// <summary>Pošle text všem připojeným klientům.</summary>
    public async Task BroadcastAsync(string text, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var segment = new ArraySegment<byte>(bytes);

        await _lock.WaitAsync(ct);
        try
        {
            var dead = new List<WebSocket>();
            foreach (var ws in _clients)
            {
                if (ws.State == WebSocketState.Open)
                {
                    try
                    {
                        await ws.SendAsync(segment, WebSocketMessageType.Text, true, ct);
                    }
                    catch
                    {
                        dead.Add(ws);
                    }
                }
                else
                {
                    dead.Add(ws);
                }
            }
            foreach (var ws in dead)
            {
                _clients.Remove(ws);
                ws.Dispose();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch
            {
                break;
            }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            _ = HandleClientAsync(ctx, ct);
        }
    }

    private async Task HandleClientAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(null);
        }
        catch
        {
            ctx.Response.Close();
            return;
        }

        var ws = wsCtx.WebSocket;
        await _lock.WaitAsync(ct);
        _clients.Add(ws);
        int clientCount = _clients.Count;
        _lock.Release();
        WhisperTranscriber.AppLog($"[WS] Klient připojen (celkem: {clientCount})");

        // Drž spojení - čti (ping/pong/close)
        var buf = new byte[256];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct);
            }
        }
        catch { /* klient se odpojil */ }
        finally
        {
            await _lock.WaitAsync(CancellationToken.None);
            _clients.Remove(ws);
            int remaining = _clients.Count;
            _lock.Release();
            ws.Dispose();
            WhisperTranscriber.AppLog($"[WS] Klient odpojen (zbývá: {remaining})");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lock.Dispose();
        _cts?.Dispose();
    }
}
