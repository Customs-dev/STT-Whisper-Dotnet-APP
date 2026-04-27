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
    private readonly string _authToken;
    private readonly List<WebSocket> _clients = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    public bool IsRunning { get; private set; }

    public WebSocketServer(int port, string authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
            throw new ArgumentException("Auth token nesmí být prázdný.", nameof(authToken));
        _port = port;
        _authToken = authToken;
        _listener = new HttpListener();
        // Loopback only – HttpListener s konkrétním hostem neváže na veřejná rozhraní
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/stt/");
        _listener.Prefixes.Add($"http://localhost:{port}/stt/");
    }

    /// <summary>URL pro WebSocket klienty včetně auth tokenu.</summary>
    public string ClientUrl => $"ws://localhost:{_port}/stt/?token={_authToken}";

    /// <summary>URL bez tokenu – pro zobrazení v UI bez prozrazení secret.</summary>
    public string PublicUrl => $"ws://localhost:{_port}/stt/";

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

            // Auth: ?token=... nebo Authorization: Bearer ...
            // Loopback-only binding již omezuje přístup, ale token brání lokálním procesům
            // a CSRF z prohlížeče (origin neni při WS handshake spolehlivě kontrolovatelný).
            if (!IsAuthorized(ctx.Request))
            {
                WhisperTranscriber.AppLog($"[WS] Odmítnutý handshake (neplatný token) z {ctx.Request.RemoteEndPoint}");
                ctx.Response.StatusCode = 401;
                ctx.Response.StatusDescription = "Unauthorized";
                ctx.Response.Close();
                continue;
            }

            _ = HandleClientAsync(ctx, ct);
        }
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        // 1) Query string ?token=...
        var qsToken = req.QueryString["token"];
        if (!string.IsNullOrEmpty(qsToken) && FixedTimeEquals(qsToken, _authToken))
            return true;

        // 2) Authorization: Bearer ...
        var auth = req.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = auth.Substring("Bearer ".Length).Trim();
            if (FixedTimeEquals(bearer, _authToken)) return true;
        }

        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        // Constant-time srovnání – brání timing attackům
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
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
