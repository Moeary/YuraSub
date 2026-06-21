using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using YuraSub.Native.Json;

namespace YuraSub.Native.Server;

/// <summary>
/// Minimal WebSocket server matching Python SubtitleServer behavior.
/// </summary>
internal sealed class WebSocketServer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _host;
    private readonly int _port;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private readonly List<WebSocketClient> _clients = new();
    private readonly object _lock = new();
    private volatile bool _running;

    public string Url => $"ws://{_host}:{_port}";
    public string ErrorString { get; private set; } = "";

    public event Action<int>? OnClientCountChanged;
    public event Action<string>? OnLog;

    public WebSocketServer(Dispatcher dispatcher, string host = "127.0.0.1", int port = 8765)
    {
        _dispatcher = dispatcher;
        _host = host;
        _port = port;
    }

    public bool Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Parse(_host), _port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WS-Accept" };
            _acceptThread.Start();
            OnLog?.Invoke($"WebSocket listening on {Url}");
            return true;
        }
        catch (Exception ex)
        {
            ErrorString = ex.Message;
            OnLog?.Invoke($"WebSocket listen failed: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var client in _clients)
                client.Close();
            _clients.Clear();
        }
        OnClientCountChanged?.Invoke(0);
    }

    public void BroadcastMediaCommand(string command)
    {
        var payload = new JsonObject { ["type"] = "mediaCommand", ["command"] = command };
        string json = JsonSimple.Stringify(payload);
        BroadcastText(json);
        OnLog?.Invoke($"Media command sent: {command}");
    }

    public void BroadcastMediaSeek(double seconds)
    {
        var payload = new JsonObject
        {
            ["type"] = "mediaCommand",
            ["command"] = "seekTo",
            ["time"] = Math.Max(0, seconds),
        };
        string json = JsonSimple.Stringify(payload);
        BroadcastText(json);
        OnLog?.Invoke($"Media seek sent: {seconds:F2}s");
    }

    private void BroadcastText(string text)
    {
        lock (_lock)
        {
            byte[] frame = EncodeTextFrame(text);
            foreach (var client in _clients)
            {
                try { client.SendFrame(frame); } catch { }
            }
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var tcp = _listener!.AcceptTcpClient();
                var client = new WebSocketClient(tcp);
                if (client.DoHandshake())
                {
                    lock (_lock)
                    {
                        _clients.Add(client);
                        OnClientCountChanged?.Invoke(_clients.Count);
                    }
                    OnLog?.Invoke($"Client connected: {_clients.Count}");

                    // Send hello
                    var hello = new JsonObject { ["type"] = "hello", ["app"] = "YuraSub", ["ok"] = true };
                    client.SendFrame(EncodeTextFrame(JsonSimple.Stringify(hello)));

                    // Start receive thread
                    var recvThread = new Thread(() => ReceiveLoop(client)) { IsBackground = true, Name = "WS-Recv" };
                    recvThread.Start();
                }
                else
                {
                    client.Close();
                }
            }
            catch (SocketException) when (!_running) { break; }
            catch (ObjectDisposedException) when (!_running) { break; }
            catch (Exception ex)
            {
                if (_running) OnLog?.Invoke($"Accept error: {ex.Message}");
            }
        }
    }

    private void ReceiveLoop(WebSocketClient client)
    {
        try
        {
            while (_running && client.IsConnected)
            {
                string? message = client.ReadTextMessage();
                if (message == null) break; // Connection closed

                if (message.Length > 65536)
                {
                    var error = new JsonObject { ["type"] = "error", ["error"] = "message_too_large" };
                    client.SendFrame(EncodeTextFrame(JsonSimple.Stringify(error)));
                    continue;
                }

                var payload = ParseMessage(message);
                var response = _dispatcher.Dispatch(payload);
                if (response != null)
                    client.SendFrame(EncodeTextFrame(JsonSimple.Stringify(response)));
            }
        }
        catch { }
        finally
        {
            lock (_lock) { _clients.Remove(client); }
            client.Close();
            OnClientCountChanged?.Invoke(_clients.Count);
            OnLog?.Invoke($"Client disconnected: {_clients.Count}");
        }
    }

    private JsonObject ParseMessage(string message)
    {
        string stripped = message.Trim();
        if (stripped.Length == 0)
            return new JsonObject { ["type"] = "clear" };

        if (stripped[0] == '{')
        {
            try
            {
                var parsed = JsonSimple.Parse(stripped);
                if (parsed is JsonObject obj)
                    return obj;
            }
            catch { }
        }
        return new JsonObject { ["type"] = "subtitle", ["text"] = message };
    }

    private static byte[] EncodeTextFrame(string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        // FIN + TEXT opcode
        ms.WriteByte(0x81);
        // Mask bit not set (server→client), length
        if (payload.Length < 126)
        {
            ms.WriteByte((byte)payload.Length);
        }
        else if (payload.Length < 65536)
        {
            ms.WriteByte(126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)(payload.Length & 0xFF));
        }
        else
        {
            ms.WriteByte(127);
            for (int i = 7; i >= 0; i--)
                ms.WriteByte((byte)((payload.Length >> (8 * i)) & 0xFF));
        }
        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Represents a single WebSocket client connection.
/// </summary>
internal sealed class WebSocketClient
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;

    public bool IsConnected => _tcp.Connected;

    public WebSocketClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public bool DoHandshake()
    {
        // Read HTTP request
        using var ms = new MemoryStream();
        byte[] buf = new byte[1];
        int headerEnd = -1;
        while (true)
        {
            int read = _stream.Read(buf, 0, 1);
            if (read <= 0) return false;
            ms.WriteByte(buf[0]);
            if (ms.Length >= 4)
            {
                byte[] data = ms.ToArray();
                int len = data.Length;
                if (data[len - 4] == '\r' && data[len - 3] == '\n' && data[len - 2] == '\r' && data[len - 1] == '\n')
                {
                    headerEnd = (int)ms.Length - 4;
                    break;
                }
                if (ms.Length > 8192) return false; // Too large
            }
        }

        string request = Encoding.ASCII.GetString(ms.ToArray(), 0, headerEnd);
        // Extract Sec-WebSocket-Key
        string? key = null;
        foreach (string line in request.Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            {
                key = line.Substring("Sec-WebSocket-Key:".Length).Trim();
                break;
            }
        }
        if (key == null) return false;

        string acceptKey = ComputeAcceptKey(key);

        // Send handshake response
        string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                         "Upgrade: websocket\r\n" +
                         "Connection: Upgrade\r\n" +
                         $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
                         "\r\n";
        byte[] responseBytes = Encoding.ASCII.GetBytes(response);
        _stream.Write(responseBytes, 0, responseBytes.Length);
        _stream.Flush();
        return true;
    }

    internal static string ComputeAcceptKey(string key)
    {
        using var sha1 = SHA1.Create();
        const string magicGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + magicGuid));
        return Convert.ToBase64String(hash);
    }

    public string? ReadTextMessage()
    {
        // Read WebSocket frame header
        byte[] header = new byte[2];
        if (!ReadExact(header, 0, 2)) return null;

        bool fin = (header[0] & 0x80) != 0;
        int opcode = header[0] & 0x0F;
        bool masked = (header[1] & 0x80) != 0;
        long payloadLength = header[1] & 0x7F;

        if (payloadLength == 126)
        {
            byte[] ext = new byte[2];
            if (!ReadExact(ext, 0, 2)) return null;
            payloadLength = (ext[0] << 8) | ext[1];
        }
        else if (payloadLength == 127)
        {
            byte[] ext = new byte[8];
            if (!ReadExact(ext, 0, 8)) return null;
            payloadLength = 0;
            for (int i = 0; i < 8; i++)
                payloadLength = (payloadLength << 8) | ext[i];
        }

        byte[] maskKey = new byte[4];
        if (masked)
        {
            if (!ReadExact(maskKey, 0, 4)) return null;
        }

        // Read payload
        if (payloadLength > 10 * 1024 * 1024) return null; // 10MB limit
        byte[] payload = new byte[payloadLength];
        if (!ReadExact(payload, 0, (int)payloadLength)) return null;

        if (masked)
        {
            for (int i = 0; i < payload.Length; i++)
                payload[i] ^= maskKey[i % 4];
        }

        // Handle opcodes
        switch (opcode)
        {
            case 0x1: // Text
                return Encoding.UTF8.GetString(payload);
            case 0x8: // Close
                SendFrame(new byte[] { 0x88, 0x02, 0x03, 0xE8 }); // Close frame
                return null;
            case 0x9: // Ping
                SendFrame(CreatePongFrame(payload));
                return ReadTextMessage(); // Read next message
            case 0xA: // Pong
                return ReadTextMessage(); // Ignore pong, read next
            default:
                return null;
        }
    }

    private bool ReadExact(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = _stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read <= 0) return false;
            totalRead += read;
        }
        return true;
    }

    public void SendFrame(byte[] frame)
    {
        try
        {
            _stream.Write(frame, 0, frame.Length);
            _stream.Flush();
        }
        catch { }
    }

    private static byte[] CreatePongFrame(byte[] payload)
    {
        byte[] frame = new byte[2 + payload.Length];
        frame[0] = 0x8A; // FIN + Pong
        frame[1] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 2, payload.Length);
        return frame;
    }

    public void Close()
    {
        try { _stream.Close(); } catch { }
        try { _tcp.Close(); } catch { }
    }
}
