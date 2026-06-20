using System;
using System.Runtime.InteropServices;
using YuraSub.Native;
using YuraSub.Native.Json;
using YuraSub.Native.Interop;
using YuraSub.Native.Overlay;
using YuraSub.Native.Server;

namespace YuraSub.Native;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Parse CLI args
        string? configPath = null;
        string? host = null;
        int? port = null;
        int? httpPort = null;
        bool noHttp = false;
        bool clickThrough = false;
        bool debug = false;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                    if (i + 1 < args.Length) configPath = args[++i];
                    break;
                case "--host":
                    if (i + 1 < args.Length) host = args[++i];
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int p)) { port = p; i++; }
                    break;
                case "--http-port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int hp)) { httpPort = hp; i++; }
                    break;
                case "--no-http":
                    noHttp = true;
                    break;
                case "--click-through":
                    clickThrough = true;
                    break;
                case "--debug":
                    debug = true;
                    break;
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
            }
        }

        if (showHelp)
        {
            Console.WriteLine("YuraSub - Native desktop subtitle overlay");
            Console.WriteLine();
            Console.WriteLine("Usage: YuraSub.Native [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --config <path>     Path to JSON config file");
            Console.WriteLine("  --host <host>       WebSocket bind host (default: 127.0.0.1)");
            Console.WriteLine("  --port <port>       WebSocket bind port (default: 8765)");
            Console.WriteLine("  --http-port <port>  HTTP fallback bind port (default: 8766)");
            Console.WriteLine("  --no-http           Disable HTTP fallback server");
            Console.WriteLine("  --click-through     Start in click-through mode");
            Console.WriteLine("  --debug             Print server events to stdout");
            Console.WriteLine("  --help, -h          Show this help");
            return 0;
        }

        // Load config
        string resolvedPath = Config.ResolvePath(configPath);
        JsonObject config = Config.Load(resolvedPath);

        // CLI overrides
        var serverCfg = config.TryGetValue("server", out var s) && s is JsonObject sObj ? sObj : new JsonObject();
        if (!config.ContainsKey("server")) config["server"] = serverCfg;
        if (host != null) serverCfg["host"] = host;
        if (port != null) serverCfg["websocketPort"] = port;
        if (httpPort != null) serverCfg["httpPort"] = httpPort;

        string bindHost = Config.GetString(config, "server", "host", Config.Defaults.Host);
        int wsPort = Config.GetInt(config, "server", "websocketPort", Config.Defaults.WebSocketPort);
        int httpPortVal = Config.GetInt(config, "server", "httpPort", Config.Defaults.HttpPort);

        // Initialize COM for D2D
        D2DFactory.CoInitializeEx(IntPtr.Zero, 0x2); // COINIT_APARTMENTTHREADED

        // Create overlay window
        var overlay = new OverlayWindow(config);
        if (!overlay.Create())
        {
            Win32.MessageBoxW(IntPtr.Zero, "Failed to create overlay window.", "YuraSub", Win32.MB_OK | Win32.MB_ICONERROR);
            return 1;
        }

        // Create dispatcher
        var dispatcher = new Dispatcher();

        // Create WebSocket server
        var wsServer = new WebSocketServer(dispatcher, bindHost, wsPort);

        // Create HTTP server
        HttpServer? httpServer = null;
        if (!noHttp)
            httpServer = new HttpServer(dispatcher, bindHost, httpPortVal);

        // Wire up events
        dispatcher.OnSubtitle += payload => overlay.ApplyPayload(payload);
        dispatcher.OnStyle += style => overlay.ApplyStyle(style, false);
        dispatcher.OnCommand += cmd => overlay.ApplyCommand(cmd);
        dispatcher.OnClear += () => overlay.ClearSubtitle();
        if (debug)
        {
            Action<string> log = msg => { Console.WriteLine(msg); Console.Out.Flush(); };
            dispatcher.OnLog += log;
            wsServer.OnLog += log;
            overlay.OnLog += log;
            if (httpServer != null) httpServer.OnLog += log;
        }

        overlay.OnMediaCommand += cmd =>
        {
            wsServer.BroadcastMediaCommand(cmd);
            httpServer?.BroadcastMediaCommand(cmd);
        };
        overlay.OnMediaSeek += seconds =>
        {
            wsServer.BroadcastMediaSeek(seconds);
            httpServer?.BroadcastMediaSeek(seconds);
        };
        wsServer.OnClientCountChanged += count => overlay.UpdateTrayTooltip(wsServer.Url, count);

        // Start servers
        bool wsStarted = wsServer.Start();
        bool httpStarted = httpServer?.Start() ?? false;

        if (wsStarted)
        {
            string secondary = httpStarted ? $"{wsServer.Url} | HTTP {httpServer!.Url}" : wsServer.Url;
            overlay.ShowStatus("YuraSub", secondary, 3500);
        }
        else
        {
            overlay.ShowStatus("YuraSub WebSocket failed", wsServer.ErrorString, 0);
        }

        // Create tray icon
        overlay.CreateTrayIcon(wsServer.Url);

        if (clickThrough)
            overlay.SetClickThrough(true);

        overlay.Show();

        // Message loop
        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessageW(ref msg);
        }

        // Save config on exit
        try
        {
            Config.Save(resolvedPath, overlay.SaveState());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save config to {resolvedPath}: {ex.Message}");
        }

        // Cleanup
        overlay.Dispose();
        wsServer.Dispose();
        httpServer?.Dispose();

        return 0;
    }
}
