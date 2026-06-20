using YuraSub.Native;
using YuraSub.Native.Json;
using YuraSub.Native.Server;
using Xunit;

namespace YuraSub.Native.Tests;

public class DispatchTests
{
    [Fact]
    public void Ping_ReturnsPong()
    {
        var dispatcher = new Dispatcher();
        var payload = new JsonObject { ["type"] = "ping" };
        var response = dispatcher.Dispatch(payload);
        Assert.NotNull(response);
        Assert.Equal("pong", ((JsonString)response!["type"]).Value);
    }

    [Fact]
    public void Hello_ReturnsAck()
    {
        var dispatcher = new Dispatcher();
        var payload = new JsonObject { ["type"] = "hello" };
        var response = dispatcher.Dispatch(payload);
        Assert.NotNull(response);
        Assert.Equal("ack", ((JsonString)response!["type"]).Value);
        Assert.Equal("hello", ((JsonString)response["for"]).Value);
    }

    [Fact]
    public void Style_ReturnsAck()
    {
        var dispatcher = new Dispatcher();
        var payload = new JsonObject { ["type"] = "style" };
        var response = dispatcher.Dispatch(payload);
        Assert.NotNull(response);
        Assert.Equal("ack", ((JsonString)response!["type"]).Value);
        Assert.Equal("style", ((JsonString)response["for"]).Value);
    }

    [Fact]
    public void Clear_ReturnsAck()
    {
        var dispatcher = new Dispatcher();
        bool clearCalled = false;
        dispatcher.OnClear += () => clearCalled = true;
        var payload = new JsonObject { ["type"] = "clear" };
        var response = dispatcher.Dispatch(payload);
        Assert.True(clearCalled);
        Assert.NotNull(response);
        Assert.Equal("ack", ((JsonString)response!["type"]).Value);
        Assert.Equal("clear", ((JsonString)response["for"]).Value);
    }

    [Fact]
    public void Command_ReturnsAck()
    {
        var dispatcher = new Dispatcher();
        var payload = new JsonObject { ["type"] = "command" };
        var response = dispatcher.Dispatch(payload);
        Assert.NotNull(response);
        Assert.Equal("ack", ((JsonString)response!["type"]).Value);
        Assert.Equal("command", ((JsonString)response["for"]).Value);
    }

    [Fact]
    public void Subtitle_EmitsSubtitle()
    {
        var dispatcher = new Dispatcher();
        JsonObject? received = null;
        dispatcher.OnSubtitle += p => received = p;
        var payload = new JsonObject { ["type"] = "subtitle", ["text"] = "hello" };
        var response = dispatcher.Dispatch(payload);
        Assert.NotNull(received);
        Assert.Equal("hello", ((JsonString)received!["text"]).Value);
        Assert.NotNull(response);
        Assert.Equal("ack", ((JsonString)response!["type"]).Value);
    }

    [Fact]
    public void NoiseSubtitle_IsIgnored()
    {
        var dispatcher = new Dispatcher();
        bool clearCalled = false;
        JsonObject? received = null;
        dispatcher.OnClear += () => clearCalled = true;
        dispatcher.OnSubtitle += p => received = p;
        var payload = new JsonObject { ["type"] = "subtitle", ["text"] = "UZMR" };
        var response = dispatcher.Dispatch(payload);
        Assert.True(clearCalled);
        Assert.Null(received);
        Assert.NotNull(response);
        Assert.Equal("ack", ((JsonString)response!["type"]).Value);
    }

    [Fact]
    public void Style_EmitsStyle()
    {
        var dispatcher = new Dispatcher();
        JsonObject? receivedStyle = null;
        dispatcher.OnStyle += s => receivedStyle = s;
        var payload = new JsonObject
        {
            ["type"] = "style",
            ["style"] = new JsonObject { ["fontSize"] = 48 }
        };
        dispatcher.Dispatch(payload);
        Assert.NotNull(receivedStyle);
        Assert.Equal(48, ((JsonNumber)receivedStyle!["fontSize"]).ToInt());
    }

    [Fact]
    public void JsonCompact_Output()
    {
        var obj = new JsonObject
        {
            ["type"] = "ack",
            ["for"] = "ping",
        };
        string json = JsonSimple.Stringify(obj);
        Assert.DoesNotContain("\n", json);
        Assert.DoesNotContain(": ", json);
        Assert.DoesNotContain(", ", json);
        Assert.Contains("\"type\":\"ack\"", json);
        Assert.Contains("\"for\":\"ping\"", json);
    }

    [Fact]
    public void JsonCompact_NonAsciiUnescaped()
    {
        var obj = new JsonObject { ["text"] = "你好世界" };
        string json = JsonSimple.Stringify(obj);
        Assert.Contains("你好世界", json);
        Assert.DoesNotContain("\\u", json);
    }
}
