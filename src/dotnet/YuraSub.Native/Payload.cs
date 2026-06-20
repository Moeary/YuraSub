using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using YuraSub.Native.Json;

namespace YuraSub.Native;

/// <summary>
/// Text normalization and payload dispatch matching Python payload.py and server.py behavior.
/// </summary>
internal static class Payload
{
    public const int MaxTextLength = 1200;

    private static readonly HashSet<string> BoolTrue = new(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };
    private static readonly HashSet<string> BoolFalse = new(StringComparer.OrdinalIgnoreCase) { "0", "false", "no", "off" };

    // Text aliases for primary text
    private static readonly string[] TextAliases = { "text", "subtitle", "caption", "lyric", "primary" };
    // Text aliases for translation
    private static readonly string[] TranslationAliases = { "translation", "translated", "secondary", "extra" };

    /// <summary>
    /// Normalize text: CRLF→LF, collapse whitespace per line, remove empty lines, trim, truncate to 1200.
    /// </summary>
    public static string CleanText(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string text = value.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = new List<string>();
        foreach (string line in text.Split('\n'))
        {
            // Collapse whitespace
            var sb = new StringBuilder();
            bool inSpace = false;
            foreach (char c in line)
            {
                if (c == ' ' || c == '\t')
                {
                    if (!inSpace && sb.Length > 0) { sb.Append(' '); inSpace = true; }
                }
                else
                {
                    sb.Append(c);
                    inSpace = false;
                }
            }
            string trimmed = sb.ToString().Trim();
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        string result = string.Join("\n", lines);
        if (result.Length > MaxTextLength) result = result.Substring(0, MaxTextLength);
        return result;
    }

    /// <summary>
    /// Normalize a JsonValue that might be a string.
    /// </summary>
    public static string CleanText(JsonValue? value)
    {
        if (value is null || value is JsonNull) return "";
        return CleanText(value.ToString());
    }

    /// <summary>
    /// Parse a boolean from various forms (bool, number, string).
    /// </summary>
    public static bool? ReadBool(JsonValue? value, bool? defaultValue = null)
    {
        if (value is null || value is JsonNull) return defaultValue;
        if (value is JsonBool b) return b.Value;
        if (value is JsonNumber n) return n.Value != 0;
        string s = value.ToString().Trim().ToLowerInvariant();
        if (BoolTrue.Contains(s)) return true;
        if (BoolFalse.Contains(s)) return false;
        return defaultValue;
    }

    /// <summary>
    /// Pick text and translation from a payload dict, trying all aliases.
    /// </summary>
    public static (string text, string translation) PickText(JsonObject payload)
    {
        string text = "";
        foreach (string alias in TextAliases)
        {
            if (payload.TryGetValue(alias, out var v) && v is not JsonNull)
            {
                text = CleanText(v);
                if (text.Length > 0) break;
            }
        }

        string translation = "";
        foreach (string alias in TranslationAliases)
        {
            if (payload.TryGetValue(alias, out var v) && v is not JsonNull)
            {
                translation = CleanText(v);
                if (translation.Length > 0) break;
            }
        }

        return (text, translation);
    }

    /// <summary>
    /// Check if a subtitle payload looks like noise (short uppercase code with no translation/CJK).
    /// </summary>
    public static bool LooksLikeNoise(JsonObject payload)
    {
        string text = CleanText(payload.TryGetValue("text", out var t) ? t :
                               payload.TryGetValue("subtitle", out var s) ? s :
                               payload.TryGetValue("lyric", out var l) ? l : null);
        string translation = CleanText(payload.TryGetValue("translation", out var tr) ? tr :
                                      payload.TryGetValue("translated", out var tl) ? tl : null);
        if (!string.IsNullOrEmpty(translation) || string.IsNullOrEmpty(text))
            return false;
        if (text == "OK" || text == "NO" || text == "YES")
            return false;
        // Check for [A-Z0-9_-]{3,8}
        if (text.Length >= 3 && text.Length <= 8 && Regex.IsMatch(text, @"^[A-Z0-9_-]+$"))
        {
            // No CJK/kana
            if (!Regex.IsMatch(text, @"[぀-ヿ㐀-鿿]"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extract style fields from a payload if present.
    /// </summary>
    public static JsonObject? ExtractStyle(JsonObject payload)
    {
        if (payload.TryGetValue("style", out var s) && s is JsonObject styleObj)
            return styleObj;
        return null;
    }

    /// <summary>
    /// Extract command fields (clickThrough, interactive, geometry) from a payload.
    /// </summary>
    public static JsonObject? ExtractCommands(JsonObject payload)
    {
        var cmd = new JsonObject();
        foreach (string key in new[] { "clickThrough", "click_through", "interactive", "geometry" })
        {
            if (payload.TryGetValue(key, out var v))
                cmd[key] = v;
        }
        return cmd.Count > 0 ? cmd : null;
    }
}
