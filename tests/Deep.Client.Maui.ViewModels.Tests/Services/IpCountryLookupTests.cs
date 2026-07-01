using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class IpCountryLookupTests
{
    [Fact]
    public async Task LookupCountryAsync_ReturnsLocalizedCountryFromOfflineDatabase()
    {
        var blocks = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(blocks.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(blocks.AsSpan(4, 4), 2661886);
        const string countries = "{\"2661886\":{\"code\":\"SE\",\"name\":\"Sweden\",\"ru\":\"Швеция\"}}";
        var lookup = new IpCountryLookup(
            _ => Task.FromResult<Stream>(new MemoryStream(blocks, writable: false)),
            _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(countries), writable: false)));

        var result = await lookup.LookupCountryAsync("8.8.8.8", CultureInfo.GetCultureInfo("ru-RU"));

        Assert.Equal("Швеция", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("2001:4860:4860::8888")]
    public async Task LookupCountryAsync_ReturnsNullForUnsupportedAddress(string address)
    {
        var lookup = new IpCountryLookup(
            _ => throw new InvalidOperationException("Database must not be opened."),
            _ => throw new InvalidOperationException("Database must not be opened."));

        Assert.Null(await lookup.LookupCountryAsync(address));
    }
}
