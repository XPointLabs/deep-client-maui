using System.Text;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.SmokeTests.Smoke;

public sealed class WnsPushPayloadCodecTests
{
    [Fact]
    public void Decode_AcceptsOnlyCanonicalEncryptedEnvelope()
    {
        var payload = Encoding.UTF8.GetBytes("{\"enc_payload\":\"AQID\",\"spns\":\"1\"}");

        Assert.True(WnsPushPayloadCodec.TryDecode(payload, out var data));
        Assert.Equal("AQID", data["enc_payload"]);
        Assert.Equal("1", data["spns"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"enc_payload\":\"AQID\",\"spns\":\"1\",\"title\":\"plaintext\"}")]
    [InlineData("{\"enc_payload\":\"AQID\",\"spns\":\"2\"}")]
    [InlineData("{\"enc_payload\":\"AQID\",\"enc_payload\":\"BAUG\",\"spns\":\"1\"}")]
    public void Decode_RejectsMalformedOrAdditionalFields(string json)
    {
        Assert.False(WnsPushPayloadCodec.TryDecode(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void Decode_AcceptsExactlyFiveThousandBytesAndRejectsLargerPayloads()
    {
        const string canonical = "{\"enc_payload\":\"AQID\",\"spns\":\"1\"}";
        var exact = Encoding.UTF8.GetBytes(canonical.PadRight(WnsPushPayloadCodec.MaxPayloadBytes));
        var oversized = Encoding.UTF8.GetBytes(canonical.PadRight(WnsPushPayloadCodec.MaxPayloadBytes + 1));

        Assert.True(WnsPushPayloadCodec.TryDecode(exact, out _));
        Assert.False(WnsPushPayloadCodec.TryDecode(oversized, out _));
    }

    [Theory]
    [InlineData("https://wns2-by3p.notify.windows.com/?token=abc")]
    [InlineData("https://notify.windows.com/?token=abc")]
    public void ChannelValidator_AcceptsWnsHosts(string uri)
    {
        Assert.True(WnsChannelUriValidator.IsValid(uri));
    }

    [Theory]
    [InlineData("http://wns2-by3p.notify.windows.com/?token=abc")]
    [InlineData("https://notify.windows.com.evil.example/?token=abc")]
    [InlineData("https://user@notify.windows.com/?token=abc")]
    [InlineData("https://notify.windows.com/")]
    [InlineData("https://notify.windows.com:444/?token=abc")]
    public void ChannelValidator_RejectsUnsafeEndpoints(string uri)
    {
        Assert.False(WnsChannelUriValidator.IsValid(uri));
    }
}
