using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YuraSub.Native.Server;
using Xunit;

namespace YuraSub.Native.Tests;

public class WebSocketTests
{
    [Fact]
    public void AcceptKey_MatchesRfcExample()
    {
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", WebSocketClient.ComputeAcceptKey("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [Fact]
    public async Task ClientWebSocket_ConnectsAndReceivesAck()
    {
        int port = GetFreePort();
        var dispatcher = new Dispatcher();
        using var server = new WebSocketServer(dispatcher, "127.0.0.1", port);
        Assert.True(server.Start());

        using var client = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), cts.Token);

        string hello = await ReceiveText(client, cts.Token);
        Assert.Contains("\"type\":\"hello\"", hello);

        byte[] payload = Encoding.UTF8.GetBytes("{\"type\":\"subtitle\",\"text\":\"hello\"}");
        await client.SendAsync(payload, WebSocketMessageType.Text, true, cts.Token);

        string ack = await ReceiveText(client, cts.Token);
        Assert.Contains("\"type\":\"ack\"", ack);
        Assert.Contains("\"for\":\"subtitle\"", ack);
    }

    private static async Task<string> ReceiveText(ClientWebSocket client, CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        var result = await client.ReceiveAsync(buffer, token);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
