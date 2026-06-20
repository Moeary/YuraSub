using YuraSub.Native;
using YuraSub.Native.Json;
using Xunit;

namespace YuraSub.Native.Tests;

public class PayloadTests
{
    [Fact]
    public void CleanText_NormalizesWhitespaceAndLines()
    {
        Assert.Equal("hello world\ntranslated line", Payload.CleanText("  hello   world \r\n\n translated   line "));
    }

    [Fact]
    public void CleanText_TruncatesTo1200()
    {
        string longText = new string('a', 1500);
        string result = Payload.CleanText(longText);
        Assert.Equal(1200, result.Length);
    }

    [Fact]
    public void CleanText_NullReturnsEmpty()
    {
        Assert.Equal("", Payload.CleanText((string?)null));
    }

    [Fact]
    public void PickText_UsesAliases()
    {
        var payload = new JsonObject
        {
            ["caption"] = "字幕正文",
            ["translated"] = "translated text",
        };
        var (text, translation) = Payload.PickText(payload);
        Assert.Equal("字幕正文", text);
        Assert.Equal("translated text", translation);
    }

    [Fact]
    public void ReadBool_AcceptsCommonForms()
    {
        Assert.True(Payload.ReadBool("yes"));
        Assert.False(Payload.ReadBool("0"));
        Assert.True(Payload.ReadBool(null, true));
        Assert.True(Payload.ReadBool(new JsonBool(true)));
        Assert.False(Payload.ReadBool(new JsonBool(false)));
        Assert.True(Payload.ReadBool(new JsonNumber(1)));
        Assert.False(Payload.ReadBool(new JsonNumber(0)));
    }

    [Fact]
    public void LooksLikeNoise_DetectsNoise()
    {
        var noise = new JsonObject { ["text"] = "UZMR" };
        Assert.True(Payload.LooksLikeNoise(noise));
    }

    [Fact]
    public void LooksLikeNoise_AllowsOK()
    {
        var ok = new JsonObject { ["text"] = "OK" };
        Assert.False(Payload.LooksLikeNoise(ok));
    }

    [Fact]
    public void LooksLikeNoise_AllowsYES()
    {
        var yes = new JsonObject { ["text"] = "YES" };
        Assert.False(Payload.LooksLikeNoise(yes));
    }

    [Fact]
    public void LooksLikeNoise_WithTranslation_NotNoise()
    {
        var payload = new JsonObject { ["text"] = "UZMR", ["translation"] = "some translation" };
        Assert.False(Payload.LooksLikeNoise(payload));
    }

    [Fact]
    public void LooksLikeNoise_WithCJK_NotNoise()
    {
        var payload = new JsonObject { ["text"] = "テスト" };
        Assert.False(Payload.LooksLikeNoise(payload));
    }
}
