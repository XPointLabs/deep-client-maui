using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Deep.Client.Maui.Core.Services;

public interface IIpCountryLookup
{
    Task<string?> LookupCountryAsync(
        string ipAddress,
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default);
}

public sealed class IpCountryLookup : IIpCountryLookup
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Lazy<Task<CountryDatabase>> database;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> cache = new(StringComparer.Ordinal);

    public IpCountryLookup(
        Func<CancellationToken, Task<Stream>> openBlocks,
        Func<CancellationToken, Task<Stream>> openCountries)
    {
        ArgumentNullException.ThrowIfNull(openBlocks);
        ArgumentNullException.ThrowIfNull(openCountries);
        database = new Lazy<Task<CountryDatabase>>(
            () => LoadAsync(openBlocks, openCountries, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<string?> LookupCountryAsync(
        string ipAddress,
        CultureInfo? culture = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = ipAddress.Trim();
        if (!IPAddress.TryParse(normalized, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return Task.FromResult<string?>(null);
        }

        var requestedCulture = culture ?? CultureInfo.CurrentUICulture;
        var key = $"{normalized}\n{requestedCulture.TwoLetterISOLanguageName}";
        return cache.GetOrAdd(
            key,
            _ => new Lazy<Task<string?>>(
                () => LookupCoreAsync(parsed, requestedCulture, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private async Task<string?> LookupCoreAsync(
        IPAddress address,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = await database.Value.ConfigureAwait(false);
        var numericAddress = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        var index = Array.BinarySearch(loaded.StartAddresses, numericAddress);
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index < 0 || !loaded.Countries.TryGetValue(loaded.CountryIds[index], out var country))
        {
            return null;
        }

        if (string.Equals(culture.TwoLetterISOLanguageName, "ru", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(country.Ru))
        {
            return country.Ru;
        }

        try
        {
            var localized = new RegionInfo(country.Code).DisplayName;
            return string.IsNullOrWhiteSpace(localized) ? country.Name : localized;
        }
        catch (ArgumentException)
        {
            return country.Name;
        }
    }

    private static async Task<CountryDatabase> LoadAsync(
        Func<CancellationToken, Task<Stream>> openBlocks,
        Func<CancellationToken, Task<Stream>> openCountries,
        CancellationToken cancellationToken)
    {
        await using var blockStream = await openBlocks(cancellationToken).ConfigureAwait(false);
        using var blockBuffer = new MemoryStream();
        await blockStream.CopyToAsync(blockBuffer, cancellationToken).ConfigureAwait(false);
        var bytes = blockBuffer.ToArray();
        if (bytes.Length == 0 || bytes.Length % 8 != 0)
        {
            throw new InvalidDataException("The IP country block database is invalid.");
        }

        var starts = new uint[bytes.Length / 8];
        var countryIds = new int[starts.Length];
        for (var index = 0; index < starts.Length; index++)
        {
            var offset = index * 8;
            starts[index] = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            countryIds[index] = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset + 4, 4));
        }

        await using var countryStream = await openCountries(cancellationToken).ConfigureAwait(false);
        var countries = await JsonSerializer.DeserializeAsync<Dictionary<int, CountryRecord>>(
            countryStream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false) ?? [];

        return new CountryDatabase(starts, countryIds, countries);
    }

    private sealed record CountryDatabase(
        uint[] StartAddresses,
        int[] CountryIds,
        IReadOnlyDictionary<int, CountryRecord> Countries);

    private sealed record CountryRecord(string Code, string Name, string? Ru);
}
