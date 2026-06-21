using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YuraSub.Native.Json;

namespace YuraSub.Native;

/// <summary>
/// Portable JSON configuration for YuraSub — mirrors the Python frozen/exe config mode.
/// </summary>
internal static class Config
{
    public const int SchemaVersion = 1;
    public const string DefaultFilename = "config.json";

    // Default values matching Python version
    public static class Defaults
    {
        // Server
        public const string Host = "127.0.0.1";
        public const int WebSocketPort = 8765;
        public const int HttpPort = 8766;

        // Window
        public const int WindowWidth = 1100;
        public const int WindowHeight = 180;

        // Style
        public const string FontFamily = "Microsoft YaHei UI";
        public const int FontSize = 34;
        public const int TranslationFontSize = 24;
        public const string TextColor = "#ffffff";
        public const int TextOpacity = 100;
        public const string OutlineColor = "#101522";
        public const int OutlineWidth = 4;
        public const int OutlineOpacity = 100;
        public const string ControlColor = "#f5fff8e6";
        public const int ControlOpacity = 90;
        public const string ControlBackgroundColor = "#0c121e00";
        public const int ControlBackgroundOpacity = 0;
        public const string BackgroundColor = "#00000000";
        public const int BackgroundOpacity = 0;
    }

    /// <summary>
    /// Deep-merge overrides into base (nested dicts merged recursively).
    /// </summary>
    public static JsonObject DeepMerge(JsonObject baseObj, JsonObject overrides)
    {
        var result = new JsonObject();
        foreach (var kv in baseObj)
            result[kv.Key] = kv.Value;
        foreach (var kv in overrides)
        {
            if (kv.Value is JsonObject overrideChild && result.TryGetValue(kv.Key, out var existing) && existing is JsonObject baseChild)
                result[kv.Key] = DeepMerge(baseChild, overrideChild);
            else
                result[kv.Key] = kv.Value;
        }
        return result;
    }

    /// <summary>
    /// Build the default config object.
    /// </summary>
    public static JsonObject MakeDefault()
    {
        var server = new JsonObject { ["host"] = Defaults.Host, ["websocketPort"] = Defaults.WebSocketPort, ["httpPort"] = Defaults.HttpPort };
        var window = new JsonObject
        {
            ["x"] = JsonNull.Instance,
            ["y"] = JsonNull.Instance,
            ["width"] = Defaults.WindowWidth,
            ["height"] = Defaults.WindowHeight,
            ["clickThrough"] = false,
            ["locked"] = false,
        };
        var style = new JsonObject
        {
            ["fontFamily"] = Defaults.FontFamily,
            ["fontSize"] = Defaults.FontSize,
            ["translationFontSize"] = Defaults.TranslationFontSize,
            ["textColor"] = Defaults.TextColor,
            ["textOpacity"] = Defaults.TextOpacity,
            ["outlineColor"] = Defaults.OutlineColor,
            ["outlineWidth"] = Defaults.OutlineWidth,
            ["outlineOpacity"] = Defaults.OutlineOpacity,
            ["controlColor"] = Defaults.ControlColor,
            ["controlOpacity"] = Defaults.ControlOpacity,
            ["controlBackgroundColor"] = Defaults.ControlBackgroundColor,
            ["controlBackgroundOpacity"] = Defaults.ControlBackgroundOpacity,
            ["backgroundColor"] = Defaults.BackgroundColor,
            ["backgroundOpacity"] = Defaults.BackgroundOpacity,
        };
        return new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["server"] = server,
            ["window"] = window,
            ["style"] = style,
        };
    }

    /// <summary>
    /// Load config from path, filling missing keys from defaults.
    /// </summary>
    public static JsonObject Load(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return MakeDefault();

        try
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            var parsed = JsonSimple.Parse(text);
            if (parsed is JsonObject obj)
                return DeepMerge(MakeDefault(), obj);
        }
        catch (Exception)
        {
            // Fall through to defaults
        }
        return MakeDefault();
    }

    /// <summary>
    /// Atomically save config to path.
    /// </summary>
    public static void Save(string path, JsonObject config)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        string tmp = Path.Combine(dir ?? ".", $".config_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, JsonSimple.Stringify(config), new UTF8Encoding(false));
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Resolve config path: CLI override > env var > default.
    /// </summary>
    public static string ResolvePath(string? cliOverride)
    {
        if (!string.IsNullOrEmpty(cliOverride))
            return Path.GetFullPath(cliOverride);

        string? env = Environment.GetEnvironmentVariable("YURASUB_CONFIG");
        if (!string.IsNullOrEmpty(env))
            return Path.GetFullPath(env);

        return Path.Combine(FindDefaultDir(), DefaultFilename);
    }

    private static string FindDefaultDir()
    {
        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// Sanitize window config: fix 0x0, negative, off-screen values.
    /// Call after Load() and before passing config to OverlayWindow.
    /// </summary>
    public static void SanitizeWindow(JsonObject config)
    {
        if (!config.TryGetValue("window", out var w) || w is not JsonObject win)
            return;

        // Fix width/height
        int width = GetInt(config, "window", "width", Defaults.WindowWidth);
        int height = GetInt(config, "window", "height", Defaults.WindowHeight);
        if (width < 280) { width = Defaults.WindowWidth; win["width"] = width; }
        if (height < 80) { height = Defaults.WindowHeight; win["height"] = height; }

        // Fix x/y: null is OK (OverlayWindow will center), but invalid values need fixing
        if (win.TryGetValue("x", out var xVal) && xVal is not JsonNull &&
            win.TryGetValue("y", out var yVal) && yVal is not JsonNull)
        {
            int x = xVal is JsonNumber xn ? xn.ToInt() : int.MinValue;
            int y = yVal is JsonNumber yn ? yn.ToInt() : int.MinValue;
            if (x == int.MinValue || y == int.MinValue)
            {
                // Invalid values — reset to null so OverlayWindow will center
                win["x"] = JsonNull.Instance;
                win["y"] = JsonNull.Instance;
            }
        }

        // Locked + clickThrough is fine, tray can always recover
        // But ensure the flags are boolean
        if (win.TryGetValue("locked", out var lk) && lk is not JsonBool)
            win["locked"] = false;
        if (win.TryGetValue("clickThrough", out var ct) && ct is not JsonBool)
            win["clickThrough"] = false;
    }

    /// <summary>
    /// Get a nested int value from config.
    /// </summary>
    public static int GetInt(JsonObject config, string section, string key, int defaultValue)
    {
        if (config.TryGetValue(section, out var s) && s is JsonObject sectionObj &&
            sectionObj.TryGetValue(key, out var v))
        {
            if (v is JsonNumber n) return (int)n.Value;
            if (v is JsonValue jv && int.TryParse(jv.ToString(), out int parsed)) return parsed;
        }
        return defaultValue;
    }

    public static string GetString(JsonObject config, string section, string key, string defaultValue)
    {
        if (config.TryGetValue(section, out var s) && s is JsonObject sectionObj &&
            sectionObj.TryGetValue(key, out var v))
        {
            if (v is JsonString str) return str.Value;
            if (v is JsonValue jv) return jv.ToString();
        }
        return defaultValue;
    }

    public static bool GetBool(JsonObject config, string section, string key, bool defaultValue)
    {
        if (config.TryGetValue(section, out var s) && s is JsonObject sectionObj &&
            sectionObj.TryGetValue(key, out var v))
        {
            if (v is JsonBool b) return b.Value;
            if (v is JsonValue jv && bool.TryParse(jv.ToString(), out bool parsed)) return parsed;
        }
        return defaultValue;
    }
}
