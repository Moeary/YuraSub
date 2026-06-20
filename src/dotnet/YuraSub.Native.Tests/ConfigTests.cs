using System.IO;
using YuraSub.Native;
using YuraSub.Native.Json;
using Xunit;

namespace YuraSub.Native.Tests;

public class ConfigTests
{
    [Fact]
    public void DefaultConfig_HasRequiredSections()
    {
        var config = Config.MakeDefault();
        Assert.True(config.ContainsKey("schemaVersion"));
        Assert.True(config.ContainsKey("server"));
        Assert.True(config.ContainsKey("window"));
        Assert.True(config.ContainsKey("style"));
    }

    [Fact]
    public void DefaultPorts_MatchSpec()
    {
        var config = Config.MakeDefault();
        Assert.Equal(8765, Config.GetInt(config, "server", "websocketPort", 0));
        Assert.Equal(8766, Config.GetInt(config, "server", "httpPort", 0));
    }

    [Fact]
    public void DefaultWindow_IncludesLocked()
    {
        var config = Config.MakeDefault();
        Assert.False(Config.GetBool(config, "window", "locked", true));
        Assert.False(Config.GetBool(config, "window", "clickThrough", true));
    }

    [Fact]
    public void DefaultStyle_MatchesSpec()
    {
        var config = Config.MakeDefault();
        Assert.Equal("Microsoft YaHei UI", Config.GetString(config, "style", "fontFamily", ""));
        Assert.Equal(34, Config.GetInt(config, "style", "fontSize", 0));
        Assert.Equal(24, Config.GetInt(config, "style", "translationFontSize", 0));
        Assert.Equal("#ffffff", Config.GetString(config, "style", "textColor", ""));
        Assert.Equal(100, Config.GetInt(config, "style", "textOpacity", 0));
        Assert.Equal("#101522", Config.GetString(config, "style", "outlineColor", ""));
        Assert.Equal(4, Config.GetInt(config, "style", "outlineWidth", 0));
        Assert.Equal(100, Config.GetInt(config, "style", "outlineOpacity", 0));
        Assert.Equal("#f5fff8e6", Config.GetString(config, "style", "controlColor", ""));
        Assert.Equal(90, Config.GetInt(config, "style", "controlOpacity", 0));
        Assert.Equal("#00000000", Config.GetString(config, "style", "backgroundColor", ""));
        Assert.Equal(0, Config.GetInt(config, "style", "backgroundOpacity", 0));
    }

    [Fact]
    public void SchemaVersion_IsInt()
    {
        Assert.True(Config.SchemaVersion >= 1);
    }

    [Fact]
    public void DeepMerge_BaseOnly()
    {
        var baseObj = new JsonObject { ["a"] = 1, ["b"] = 2 };
        var result = Config.DeepMerge(baseObj, new JsonObject());
        Assert.Equal(1, ((JsonNumber)result["a"]).ToInt());
        Assert.Equal(2, ((JsonNumber)result["b"]).ToInt());
    }

    [Fact]
    public void DeepMerge_OverrideWins()
    {
        var baseObj = new JsonObject { ["a"] = 1, ["b"] = 2 };
        var over = new JsonObject { ["b"] = 99 };
        var result = Config.DeepMerge(baseObj, over);
        Assert.Equal(1, ((JsonNumber)result["a"]).ToInt());
        Assert.Equal(99, ((JsonNumber)result["b"]).ToInt());
    }

    [Fact]
    public void DeepMerge_NestedDict()
    {
        var baseObj = new JsonObject
        {
            ["section"] = new JsonObject { ["x"] = 1, ["y"] = 2 }
        };
        var over = new JsonObject
        {
            ["section"] = new JsonObject { ["y"] = 99 }
        };
        var result = Config.DeepMerge(baseObj, over);
        var section = (JsonObject)result["section"];
        Assert.Equal(1, ((JsonNumber)section["x"]).ToInt());
        Assert.Equal(99, ((JsonNumber)section["y"]).ToInt());
    }

    [Fact]
    public void DeepMerge_DoesNotMutateBase()
    {
        var baseObj = new JsonObject
        {
            ["a"] = new JsonObject { ["x"] = 1 }
        };
        var over = new JsonObject
        {
            ["a"] = new JsonObject { ["x"] = 2 }
        };
        Config.DeepMerge(baseObj, over);
        var section = (JsonObject)baseObj["a"];
        Assert.Equal(1, ((JsonNumber)section["x"]).ToInt());
    }

    [Fact]
    public void LoadConfig_MissingFile_ReturnsDefaults()
    {
        var result = Config.Load("/nonexistent/path/config.json");
        Assert.Equal(Config.SchemaVersion, ((JsonNumber)result["schemaVersion"]).ToInt());
    }

    [Fact]
    public void LoadConfig_MalformedJson_ReturnsDefaults()
    {
        string tmpFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmpFile, "not json {{{");
            var result = Config.Load(tmpFile);
            Assert.Equal(Config.SchemaVersion, ((JsonNumber)result["schemaVersion"]).ToInt());
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
