using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using YuraSub.Native;
using YuraSub.Native.Json;
using YuraSub.Native.Server;
using Xunit;

namespace YuraSub.Native.Tests;

public class HttpTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly HttpServer _server;
    private readonly int _port;

    public HttpTests()
    {
        _dispatcher = new Dispatcher();
        // Use port 0 to get a random available port
        _port = 18766 + new Random().Next(1000);
        _server = new HttpServer(_dispatcher, "127.0.0.1", _port);
        _server.Start();
        Thread.Sleep(100); // Give server time to start
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private string SendRequest(string method, string path, string? body = null)
    {
        using var client = new TcpClient();
        client.Connect("127.0.0.1", _port);
        using var stream = client.GetStream();
        stream.ReadTimeout = 3000;

        string request = $"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n";
        if (body != null)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            request += $"Content-Length: {bodyBytes.Length}\r\nContent-Type: application/json\r\n";
        }
        request += "\r\n";
        if (body != null) request += body;

        byte[] requestBytes = Encoding.ASCII.GetBytes(request);
        stream.Write(requestBytes, 0, requestBytes.Length);
        stream.Flush();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Options_ReturnsOk()
    {
        string response = SendRequest("OPTIONS", "/");
        Assert.Contains("200", response);
        Assert.Contains("\"ok\":true", response);
    }

    [Fact]
    public void GetStatus_ReturnsOk()
    {
        string response = SendRequest("GET", "/status");
        Assert.Contains("200", response);
        Assert.Contains("\"ok\":true", response);
        Assert.Contains("\"app\":\"YuraSub\"", response);
        Assert.Contains("\"commands\":", response);
    }

    [Fact]
    public void GetHealth_ReturnsOk()
    {
        string response = SendRequest("GET", "/health");
        Assert.Contains("200", response);
        Assert.Contains("\"ok\":true", response);
    }

    [Fact]
    public void GetRoot_ReturnsOk()
    {
        string response = SendRequest("GET", "/");
        Assert.Contains("200", response);
        Assert.Contains("\"ok\":true", response);
    }

    [Fact]
    public void PostSubtitle_Dispatches()
    {
        string response = SendRequest("POST", "/subtitle", "{\"type\":\"subtitle\",\"text\":\"test\"}");
        Assert.Contains("200", response);
        // Dispatcher returns ack for subtitle type
        Assert.Contains("\"type\":\"ack\"", response);
    }

    [Fact]
    public void UnknownRoute_Returns404()
    {
        string response = SendRequest("GET", "/nonexistent");
        Assert.Contains("404", response);
        Assert.Contains("\"ok\":false", response);
        Assert.Contains("\"error\":\"not_found\"", response);
    }

    [Fact]
    public void BadJson_Returns400()
    {
        string response = SendRequest("POST", "/subtitle", "not json {{{");
        Assert.Contains("400", response);
        Assert.Contains("\"ok\":false", response);
    }

    [Fact]
    public void CorsHeaders_Present()
    {
        string response = SendRequest("OPTIONS", "/");
        Assert.Contains("Access-Control-Allow-Origin: *", response);
        Assert.Contains("Access-Control-Allow-Methods: GET, POST, OPTIONS", response);
        Assert.Contains("Access-Control-Allow-Headers: Content-Type", response);
    }

    [Fact]
    public void Commands_QueueIncrementsId()
    {
        _server.BroadcastMediaCommand("playPause");
        _server.BroadcastMediaCommand("nextTrack");

        string response = SendRequest("GET", "/commands?since=0");
        Assert.Contains("200", response);
        Assert.Contains("\"id\":1", response);
        Assert.Contains("\"id\":2", response);
    }

    [Fact]
    public void Commands_SinceFilters()
    {
        _server.BroadcastMediaCommand("playPause");
        _server.BroadcastMediaCommand("nextTrack");
        _server.BroadcastMediaCommand("previousTrack");

        string response = SendRequest("GET", "/commands?since=2");
        Assert.Contains("200", response);
        // Should only contain id=3 (since > 2)
        Assert.DoesNotContain("\"id\":1", response);
        Assert.DoesNotContain("\"id\":2", response);
        Assert.Contains("\"id\":3", response);
    }

    [Fact]
    public void Commands_RetainsLast200()
    {
        // Queue 210 commands
        for (int i = 0; i < 210; i++)
            _server.BroadcastMediaCommand("playPause");

        string response = SendRequest("GET", "/commands?since=0");
        Assert.Contains("200", response);
        // Should not contain the first 10 (IDs 1-10)
        Assert.DoesNotContain("\"id\":1,", response);
        Assert.Contains("\"id\":11", response);
        Assert.Contains("\"id\":210", response);
    }

    [Fact]
    public void JsonCompact_Utf8Output()
    {
        string response = SendRequest("GET", "/status");
        // Find the JSON body (after the blank line)
        int bodyStart = response.IndexOf("\r\n\r\n");
        if (bodyStart >= 0)
        {
            string body = response.Substring(bodyStart + 4).Trim();
            Assert.DoesNotContain(": ", body);
            Assert.DoesNotContain(", ", body);
            Assert.DoesNotContain("\n", body);
        }
    }
}
