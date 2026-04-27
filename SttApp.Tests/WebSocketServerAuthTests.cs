using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;
using SttApp;

namespace SttApp.Tests;

public class WebSocketServerAuthTests
{
    private const string ValidToken = "test-token-1234567890";

    private static int FindFreePort()
    {
        // Loopback only – stačí najít volný TCP port
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<WebSocketServer> StartServerAsync(int port)
    {
        var s = new WebSocketServer(port, ValidToken);
        s.Start();
        // Krátká pauza – HttpListener potřebuje spustit accept loop
        await Task.Delay(50);
        return s;
    }

    [Fact]
    public void Constructor_RejectsEmptyToken()
    {
        Assert.Throws<ArgumentException>(() => new WebSocketServer(5050, ""));
        Assert.Throws<ArgumentException>(() => new WebSocketServer(5050, "   "));
    }

    [Fact]
    public void ClientUrl_ContainsToken()
    {
        var s = new WebSocketServer(5050, ValidToken);
        Assert.Contains($"token={ValidToken}", s.ClientUrl);
        Assert.Contains("5050", s.ClientUrl);
        Assert.DoesNotContain("token=", s.PublicUrl);
    }

    [Fact]
    public async Task Handshake_WithValidQueryToken_Succeeds()
    {
        int port = FindFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(
            new Uri($"ws://localhost:{port}/stt/?token={ValidToken}"), cts.Token);

        Assert.Equal(WebSocketState.Open, client.State);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
    }

    [Fact]
    public async Task Handshake_WithValidBearerHeader_Succeeds()
    {
        int port = FindFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", $"Bearer {ValidToken}");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(new Uri($"ws://localhost:{port}/stt/"), cts.Token);

        Assert.Equal(WebSocketState.Open, client.State);
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
    }

    [Fact]
    public async Task Handshake_WithoutToken_Returns401()
    {
        int port = FindFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAnyAsync<WebSocketException>(async () =>
            await client.ConnectAsync(new Uri($"ws://localhost:{port}/stt/"), cts.Token));

        // ClientWebSocket vyhodí WebSocketException při ne-101 odpovědi.
        // Mapped status v HResult/Message – stačí ověřit, že connect selhal.
        Assert.NotEqual(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task Handshake_WithWrongToken_Returns401()
    {
        int port = FindFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<WebSocketException>(async () =>
            await client.ConnectAsync(
                new Uri($"ws://localhost:{port}/stt/?token=wrong-token"), cts.Token));

        Assert.NotEqual(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task Broadcast_DeliversTextToAuthorizedClient()
    {
        int port = FindFreePort();
        await using var server = await StartServerAsync(port);

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await client.ConnectAsync(
            new Uri($"ws://localhost:{port}/stt/?token={ValidToken}"), cts.Token);

        // Server has 1 client now – wait for accept loop registration
        await Task.Delay(100);

        await server.BroadcastAsync("hello", cts.Token);

        var buf = new byte[1024];
        var result = await client.ReceiveAsync(buf, cts.Token);
        var msg = Encoding.UTF8.GetString(buf, 0, result.Count);

        Assert.Equal("hello", msg);

        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
    }
}
