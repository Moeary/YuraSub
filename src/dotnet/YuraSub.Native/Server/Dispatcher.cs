using System;
using YuraSub.Native.Json;

namespace YuraSub.Native.Server;

/// <summary>
/// Subtitle dispatch logic matching Python server.py SubtitleDispatcher.
/// </summary>
internal sealed class Dispatcher
{
    public event Action<JsonObject>? OnSubtitle;
    public event Action<JsonObject>? OnStyle;
    public event Action<JsonObject>? OnCommand;
    public event Action? OnClear;
    public event Action<string>? OnLog;

    /// <summary>
    /// Dispatch a payload and return the response to send back.
    /// </summary>
    public JsonObject? Dispatch(JsonObject payload)
    {
        string messageType = "subtitle";
        if (payload.TryGetValue("type", out var t) && t is not JsonNull)
            messageType = t.ToString().ToLowerInvariant();

        switch (messageType)
        {
            case "ping":
                return new JsonObject { ["type"] = "pong" };

            case "hello":
            case "style":
                EmitStyleAndCommands(payload);
                return new JsonObject { ["type"] = "ack", ["for"] = messageType };

            case "clear":
            case "empty":
                OnClear?.Invoke();
                OnLog?.Invoke("Clear requested");
                return new JsonObject { ["type"] = "ack", ["for"] = messageType };

            case "command":
            case "config":
                EmitStyleAndCommands(payload);
                return new JsonObject { ["type"] = "ack", ["for"] = messageType };

            default:
                // Check for noise
                if (Payload.LooksLikeNoise(payload))
                {
                    OnClear?.Invoke();
                    string noiseText = Payload.CleanText(
                        payload.TryGetValue("text", out var nt) ? nt :
                        payload.TryGetValue("subtitle", out var ns) ? ns :
                        payload.TryGetValue("lyric", out var nl) ? nl : null);
                    OnLog?.Invoke($"Ignored noise subtitle: {noiseText}");
                    return new JsonObject { ["type"] = "ack", ["ignored"] = true };
                }

                EmitStyleAndCommands(payload);
                OnSubtitle?.Invoke(payload);
                string textPreview = "";
                foreach (string alias in new[] { "text", "subtitle", "lyric" })
                {
                    if (payload.TryGetValue(alias, out var v) && v is not JsonNull)
                    {
                        textPreview = v.ToString().Trim();
                        break;
                    }
                }
                string source = payload.TryGetValue("source", out var src) ? src.ToString() : "unknown";
                OnLog?.Invoke($"Subtitle received: {(textPreview.Length > 80 ? textPreview.Substring(0, 80) : textPreview)} [{source}]");
                return new JsonObject { ["type"] = "ack", ["for"] = messageType };
        }
    }

    private void EmitStyleAndCommands(JsonObject payload)
    {
        var style = Payload.ExtractStyle(payload);
        if (style != null) OnStyle?.Invoke(style);

        var commands = Payload.ExtractCommands(payload);
        if (commands != null) OnCommand?.Invoke(commands);
    }
}
