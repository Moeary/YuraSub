using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using YuraSub.Native.Json;

namespace YuraSub.Native.Server;

/// <summary>
/// HTTP fallback server matching Python SubtitleHttpServer behavior.
/// </summary>
internal sealed class HttpServer : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly string _host;
    private readonly int _port;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _running;

    private readonly List<JsonObject> _commands = new();
    private int _nextCommandId = 1;
    private readonly object _cmdLock = new();

    public string Url => $"http://{_host}:{_port}";
    public string ErrorString { get; private set; } = "";

    public event Action<string>? OnLog;

    public HttpServer(Dispatcher dispatcher, string host = "127.0.0.1", int port = 8766)
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
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "HTTP-Accept" };
            _acceptThread.Start();
            OnLog?.Invoke($"HTTP fallback listening on {Url}");
            return true;
        }
        catch (Exception ex)
        {
            ErrorString = ex.Message;
            OnLog?.Invoke($"HTTP fallback listen failed: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    public void BroadcastMediaCommand(string command)
    {
        QueueCommand(new JsonObject { ["type"] = "mediaCommand", ["command"] = command });
        OnLog?.Invoke($"HTTP media command queued: {command}");
    }

    public void BroadcastMediaSeek(double seconds)
    {
        QueueCommand(new JsonObject
        {
            ["type"] = "mediaCommand",
            ["command"] = "seekTo",
            ["time"] = Math.Max(0, seconds),
        });
        OnLog?.Invoke($"HTTP media seek queued: {seconds:F2}s");
    }

    private void QueueCommand(JsonObject payload)
    {
        lock (_cmdLock)
        {
            var cmd = new JsonObject();
            foreach (var kv in payload) cmd[kv.Key] = kv.Value;
            cmd["id"] = _nextCommandId++;
            _commands.Add(cmd);
            // Keep last 200
            if (_commands.Count > 200)
                _commands.RemoveRange(0, _commands.Count - 200);
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var tcp = _listener!.AcceptTcpClient();
                var thread = new Thread(() => HandleConnection(tcp)) { IsBackground = true, Name = "HTTP-Conn" };
                thread.Start();
            }
            catch (SocketException) when (!_running) { break; }
            catch (ObjectDisposedException) when (!_running) { break; }
            catch { }
        }
    }

    private void HandleConnection(TcpClient tcp)
    {
        try
        {
            using (tcp)
            using (var stream = tcp.GetStream())
            {
                stream.ReadTimeout = 5000;
                // Read HTTP request
                using var ms = new MemoryStream();
                byte[] buf = new byte[4096];
                int headerEnd = -1;
                int totalRead = 0;

                while (true)
                {
                    int read = stream.Read(buf, 0, buf.Length);
                    if (read <= 0) return;
                    ms.Write(buf, 0, read);
                    totalRead += read;

                    // Check for \r\n\r\n
                    byte[] data = ms.ToArray();
                    for (int i = 0; i < totalRead - 3; i++)
                    {
                        if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                        {
                            headerEnd = i;
                            break;
                        }
                    }
                    if (headerEnd >= 0) break;
                    if (totalRead > 8192) return; // Too large
                }

                string headerStr = Encoding.ASCII.GetString(ms.ToArray(), 0, headerEnd);
                string[] headerLines = headerStr.Split("\r\n");
                if (headerLines.Length == 0) return;

                string[] requestLine = headerLines[0].Split(' ', 3);
                if (requestLine.Length < 2) return;
                string method = requestLine[0].ToUpperInvariant();
                string target = requestLine[1];

                // Parse headers
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 1; i < headerLines.Length; i++)
                {
                    int colon = headerLines[i].IndexOf(':');
                    if (colon > 0)
                        headers[headerLines[i].Substring(0, colon).Trim()] = headerLines[i].Substring(colon + 1).Trim();
                }

                // Read body if Content-Length present
                int contentLength = 0;
                if (headers.TryGetValue("Content-Length", out string? clStr))
                    int.TryParse(clStr, out contentLength);

                byte[] body = Array.Empty<byte>();
                if (contentLength > 0)
                {
                    int bodyStart = headerEnd + 4;
                    byte[] allData = ms.ToArray();
                    int available = totalRead - bodyStart;
                    if (available < contentLength)
                    {
                        body = new byte[contentLength];
                        Array.Copy(allData, bodyStart, body, 0, available);
                        int remaining = contentLength - available;
                        while (remaining > 0)
                        {
                            int read = stream.Read(body, contentLength - remaining, remaining);
                            if (read <= 0) return;
                            remaining -= read;
                        }
                    }
                    else
                    {
                        body = new byte[contentLength];
                        Array.Copy(allData, bodyStart, body, 0, contentLength);
                    }
                }

                // Parse URL path and query
                string path = "/";
                string query = "";
                int qMark = target.IndexOf('?');
                if (qMark >= 0)
                {
                    path = target.Substring(0, qMark);
                    query = target.Substring(qMark + 1);
                }
                else
                {
                    path = target;
                }

                // Handle request
                int status = 200;
                JsonObject response;

                try
                {
                    if (method == "OPTIONS")
                    {
                        response = new JsonObject { ["ok"] = true };
                    }
                    else if (method == "GET" && (path == "/" || path == "/health" || path == "/status"))
                    {
                        lock (_cmdLock)
                        {
                            response = new JsonObject
                            {
                                ["ok"] = true,
                                ["app"] = "YuraSub",
                                ["commands"] = _commands.Count,
                            };
                        }
                    }
                    else if (method == "GET" && path == "/commands")
                    {
                        int since = 0;
                        foreach (string param in query.Split('&'))
                        {
                            string[] kv = param.Split('=', 2);
                            if (kv.Length == 2 && kv[0] == "since")
                                int.TryParse(kv[1], out since);
                        }
                        lock (_cmdLock)
                        {
                            var cmds = new JsonArray();
                            foreach (var cmd in _commands)
                            {
                                if (cmd.TryGetValue("id", out var id) && id is JsonNumber n && n.ToInt() > since)
                                    cmds.Add(cmd);
                            }
                            response = new JsonObject { ["ok"] = true, ["commands"] = cmds };
                        }
                    }
                    else if (method == "POST" && (path == "/subtitle" || path == "/push" || path == "/"))
                    {
                        string bodyText = Encoding.UTF8.GetString(body);
                        if (string.IsNullOrEmpty(bodyText)) bodyText = "{}";
                        var payload = JsonSimple.Parse(bodyText);
                        if (payload is JsonObject obj)
                        {
                            var dispatched = _dispatcher.Dispatch(obj);
                            response = dispatched ?? new JsonObject { ["ok"] = true };
                        }
                        else
                        {
                            throw new Exception("JSON payload must be an object");
                        }
                    }
                    else
                    {
                        status = 404;
                        response = new JsonObject { ["ok"] = false, ["error"] = "not_found" };
                    }
                }
                catch (Exception ex)
                {
                    status = 400;
                    response = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
                    OnLog?.Invoke($"HTTP fallback request failed: {ex.Message}");
                }

                WriteJsonResponse(stream, status, response);
            }
        }
        catch { }
    }

    private static void WriteJsonResponse(NetworkStream stream, int status, JsonObject payload)
    {
        string body = JsonSimple.Stringify(payload);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string reason = status < 400 ? "OK" : "Error";
        string headers = $"HTTP/1.1 {status} {reason}\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                        "Access-Control-Allow-Headers: Content-Type\r\n" +
                        "Connection: close\r\n" +
                        "\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    public void Dispose() => Stop();
}
