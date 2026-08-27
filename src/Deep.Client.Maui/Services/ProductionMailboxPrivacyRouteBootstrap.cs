using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Sodium;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Loads the immutable production privacy-route bootstrap from build-embedded resources.
/// The detached signature covers the exact canonical JSON resource bytes.
/// </summary>
internal static class ProductionMailboxPrivacyRouteBootstrap
{
    internal const int MaximumJsonBytes = 16 * 1024;
    internal const long MaximumClockSkewSeconds = 300;

    private const int SignatureBytes = 64;
    private const int PublicKeyBytes = 32;
    private static readonly string[] RootProperties =
    [
        "schemaVersion", "developmentOnly", "networkId", "notBeforeUnixSeconds",
        "expiresUnixSeconds", "primary", "fallback"
    ];
    private static readonly string[] RouteProperties = ["entryOrigin", "hops"];
    private static readonly string[] HopProperties = ["routerId", "x25519PublicKey"];

    internal static MailboxPrivacyRouteSet Load(
        Assembly assembly,
        string jsonResourceName,
        string signatureResourceName,
        string publicKeyResourceName,
        ProductionMailboxTrustAnchor trustAnchor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        RequireResourceName(jsonResourceName, nameof(jsonResourceName));
        RequireResourceName(signatureResourceName, nameof(signatureResourceName));
        RequireResourceName(publicKeyResourceName, nameof(publicKeyResourceName));
        if (new[] { jsonResourceName, signatureResourceName, publicKeyResourceName }
            .Distinct(StringComparer.Ordinal).Count() != 3)
        {
            throw new ArgumentException(
                "Production privacy-route resources require three distinct exact names.");
        }

        using var json = OpenExactResource(assembly, jsonResourceName);
        using var signature = OpenExactResource(assembly, signatureResourceName);
        using var publicKey = OpenExactResource(assembly, publicKeyResourceName);
        return Load(json, signature, publicKey, trustAnchor, timeProvider);
    }

    internal static MailboxPrivacyRouteSet Load(
        Stream jsonStream,
        Stream signatureStream,
        Stream publicKeyStream,
        ProductionMailboxTrustAnchor trustAnchor,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(jsonStream);
        ArgumentNullException.ThrowIfNull(signatureStream);
        ArgumentNullException.ThrowIfNull(publicKeyStream);
        ArgumentNullException.ThrowIfNull(trustAnchor);

        ValidateAnchor(trustAnchor);
        var encoded = ReadBounded(jsonStream, MaximumJsonBytes, "JSON");
        var signature = ReadExact(signatureStream, SignatureBytes, "signature");
        var publicKey = ReadExact(publicKeyStream, PublicKeyBytes, "public key");
        try
        {
            var actualPin = SHA256.HashData(publicKey);
            try
            {
                Require(CryptographicOperations.FixedTimeEquals(
                        actualPin, trustAnchor.MrXPublicKeySha256.Span),
                    "Production privacy-route signing key differs from the build trust anchor.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actualPin);
            }

            Require(PublicKeyAuth.VerifyDetached(signature, encoded, publicKey),
                "Production privacy-route bootstrap signature is invalid.");
            return ParseCanonical(encoded, trustAnchor, timeProvider ?? TimeProvider.System);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    private static MailboxPrivacyRouteSet ParseCanonical(
        byte[] encoded,
        ProductionMailboxTrustAnchor trustAnchor,
        TimeProvider timeProvider)
    {
        try
        {
            using var document = JsonDocument.Parse(encoded, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 6
            });
            var root = document.RootElement;
            RequireExactProperties(root, RootProperties, "privacy-route bootstrap");
            Require(root.GetProperty("schemaVersion").GetInt32() == 1,
                "Production privacy-route bootstrap schema is unsupported.");
            Require(!root.GetProperty("developmentOnly").GetBoolean(),
                "Development privacy routes are forbidden in production.");

            var networkId = LowerHex(root, "networkId", 16);
            try
            {
                Require(CryptographicOperations.FixedTimeEquals(
                        networkId, trustAnchor.NetworkId.Span),
                    "Production privacy routes target a different network.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(networkId);
            }

            var notBefore = root.GetProperty("notBeforeUnixSeconds").GetInt64();
            var expires = root.GetProperty("expiresUnixSeconds").GetInt64();
            Require(notBefore >= 0 && expires > notBefore,
                "Production privacy-route validity window is invalid.");
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            Require(now <= long.MaxValue - MaximumClockSkewSeconds &&
                    now >= long.MinValue + MaximumClockSkewSeconds &&
                    now + MaximumClockSkewSeconds >= notBefore &&
                    now - MaximumClockSkewSeconds <= expires,
                "Production privacy-route bootstrap is outside its validity window.");

            var primary = ParseRoute(root.GetProperty("primary"), "primary route");
            var fallback = ParseRoute(root.GetProperty("fallback"), "fallback route");
            Require(encoded.AsSpan().SequenceEqual(EncodeCanonical(
                    root.GetProperty("networkId").GetString()!, notBefore, expires,
                    primary, fallback)),
                "Production privacy-route JSON is not the canonical schema-v1 encoding.");

            try
            {
                return new MailboxPrivacyRouteSet(primary, fallback);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            {
                throw new InvalidDataException(
                    "Production privacy routes are not fully disjoint.", exception);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException(
                "Production privacy-route bootstrap is malformed.", exception);
        }
    }

    private static PrivacyMailboxRoute ParseRoute(JsonElement value, string label)
    {
        RequireExactProperties(value, RouteProperties, label);
        var originText = value.GetProperty("entryOrigin").GetString();
        Uri? origin = null;
        Require(originText is not null &&
                Uri.TryCreate(originText, UriKind.Absolute, out origin) &&
                string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
                string.IsNullOrEmpty(origin.UserInfo) &&
                string.IsNullOrEmpty(origin.Query) &&
                string.IsNullOrEmpty(origin.Fragment) &&
                origin.AbsolutePath == "/" &&
                string.Equals(origin.AbsoluteUri, originText, StringComparison.Ordinal),
            $"Production {label} entry is not one canonical HTTPS root origin.");

        var hopsValue = value.GetProperty("hops");
        Require(hopsValue.ValueKind == JsonValueKind.Array &&
                hopsValue.GetArrayLength() == PrivacyRoutingLimits.RouteHopCount,
            $"Production {label} must contain exactly three hops.");
        var hops = new List<PrivacyRoutingHop>(PrivacyRoutingLimits.RouteHopCount);
        foreach (var hop in hopsValue.EnumerateArray())
        {
            RequireExactProperties(hop, HopProperties, $"{label} hop");
            hops.Add(new PrivacyRoutingHop(
                LowerHex(hop, "routerId", PrivacyRoutingLimits.RouterIdBytes),
                LowerHex(hop, "x25519PublicKey", PrivacyRoutingLimits.X25519KeyBytes)));
        }

        try
        {
            return new PrivacyMailboxRoute(origin!, hops);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Production {label} is invalid.", exception);
        }
    }

    private static byte[] EncodeCanonical(
        string networkId,
        long notBefore,
        long expires,
        PrivacyMailboxRoute primary,
        PrivacyMailboxRoute fallback)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteBoolean("developmentOnly", false);
            writer.WriteString("networkId", networkId);
            writer.WriteNumber("notBeforeUnixSeconds", notBefore);
            writer.WriteNumber("expiresUnixSeconds", expires);
            WriteRoute(writer, "primary", primary);
            WriteRoute(writer, "fallback", fallback);
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static void WriteRoute(
        Utf8JsonWriter writer,
        string propertyName,
        PrivacyMailboxRoute route)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("entryOrigin", route.EntryOrigin.AbsoluteUri);
        writer.WriteStartArray("hops");
        foreach (var hop in route.Hops)
        {
            writer.WriteStartObject();
            writer.WriteString("routerId", Convert.ToHexStringLower(hop.RouterId.Span));
            writer.WriteString("x25519PublicKey",
                Convert.ToHexStringLower(hop.X25519PublicKey.Span));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static Stream OpenExactResource(Assembly assembly, string name) =>
        assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException(
            "A required build-embedded production privacy-route resource is missing.");

    private static byte[] ReadBounded(Stream stream, int maximumBytes, string label)
    {
        Require(stream.CanRead, $"Production privacy-route {label} stream is unreadable.");
        using var output = new MemoryStream(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length,
                maximumBytes + 1 - checked((int)output.Length)));
            if (read == 0) break;
            output.Write(buffer, 0, read);
            Require(output.Length <= maximumBytes,
                $"Production privacy-route {label} exceeds its size bound.");
        }
        Require(output.Length > 0,
            $"Production privacy-route {label} is empty.");
        return output.ToArray();
    }

    private static byte[] ReadExact(Stream stream, int bytes, string label)
    {
        var value = ReadBounded(stream, bytes, label);
        Require(value.Length == bytes,
            $"Production privacy-route {label} has an invalid length.");
        return value;
    }

    private static byte[] LowerHex(JsonElement value, string property, int bytes)
    {
        var text = value.GetProperty(property).GetString();
        Require(text is not null && text.Length == bytes * 2 &&
                text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            $"{property} is not canonical lowercase hexadecimal.");
        var decoded = Convert.FromHexString(text!);
        Require(decoded.AsSpan().IndexOfAnyExcept((byte)0) >= 0,
            $"{property} must be nonzero.");
        return decoded;
    }

    private static void RequireExactProperties(
        JsonElement value,
        IReadOnlyCollection<string> expected,
        string label)
    {
        Require(value.ValueKind == JsonValueKind.Object, $"{label} must be an object.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(actual.Length == expected.Count &&
                actual.ToHashSet(StringComparer.Ordinal).SetEquals(expected),
            $"{label} contains missing, duplicate, or unknown fields.");
    }

    private static void ValidateAnchor(ProductionMailboxTrustAnchor trustAnchor)
    {
        Require(trustAnchor.MrXPublicKeySha256.Length == 32 &&
                trustAnchor.MrXPublicKeySha256.Span.IndexOfAnyExcept((byte)0) >= 0 &&
                trustAnchor.NetworkId.Length == 16 &&
                trustAnchor.NetworkId.Span.IndexOfAnyExcept((byte)0) >= 0,
            "Production mailbox trust anchor is invalid.");
    }

    private static void RequireResourceName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(),
                StringComparison.Ordinal))
            throw new ArgumentException("An exact assembly resource name is required.", parameterName);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
