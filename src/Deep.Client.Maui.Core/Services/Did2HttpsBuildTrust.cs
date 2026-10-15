using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Deep.Client.Maui.Core.Services;

/// <summary>Structural configuration for the isolated HTTPS diagnostic, not
/// directory/network authority. A private endpoint requires an explicit root
/// fingerprint AND its independently checked public certificate.</summary>
public static class Did2HttpsBuildTrust
{
    public const int MaximumCertificateBytes = 16_384;

    public static string RequireOrigin(string origin, ReadOnlySpan<byte> privateRootSha256)
    {
        var privateRoot = !privateRootSha256.IsEmpty;
        if (privateRoot && (privateRootSha256.Length != 32 || privateRootSha256.IndexOfAnyExcept((byte)0) < 0))
            throw new CryptographicException("The explicit private CA fingerprint is invalid.");
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.AbsoluteUri != origin || uri.AbsolutePath != "/" ||
            uri.Port < 1 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
            uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6) ||
            (!privateRoot && (uri.IsLoopback || !uri.IsDefaultPort || uri.HostNameType != UriHostNameType.Dns)))
            throw new InvalidOperationException("The DID2 HTTPS diagnostic origin is outside its explicit trust profile.");
        return origin;
    }

    public static byte[] RequirePublicRoot(ReadOnlySpan<byte> encoded, ReadOnlySpan<byte> expectedSha256)
    {
        if (encoded.Length is < 1 or > MaximumCertificateBytes || expectedSha256.Length != 32 ||
            expectedSha256.IndexOfAnyExcept((byte)0) < 0)
            throw new CryptographicException("The explicit public CA input is outside its bounds.");
        byte[] der;
        if (encoded[0] == 0x30) der = encoded.ToArray();
        else
        {
            string text;
            try { text = new UTF8Encoding(false, true).GetString(encoded); }
            catch (DecoderFallbackException error) { throw new CryptographicException("The public CA encoding is invalid.", error); }
            if (!PemEncoding.TryFind(text, out var fields) || !text.AsSpan(fields.Label).SequenceEqual("CERTIFICATE"))
                throw new CryptographicException("Exactly one public certificate is required.");
            var start = fields.Location.Start.GetOffset(text.Length);
            var end = fields.Location.End.GetOffset(text.Length);
            if (text.AsSpan(0, start).ContainsAnyExceptWhiteSpace() || text.AsSpan(end).ContainsAnyExceptWhiteSpace())
                throw new CryptographicException("Public CA input contains additional material.");
            der = Convert.FromBase64String(text[fields.Base64Data]);
        }
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            _ = reader.ReadEncodedValue(); reader.ThrowIfNotEmpty();
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            if (certificate.HasPrivateKey || constraints?.CertificateAuthority != true || usage is null ||
                (usage.KeyUsages & X509KeyUsageFlags.KeyCertSign) == 0 ||
                !certificate.RawData.AsSpan().SequenceEqual(der) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(der), expectedSha256))
                throw new CryptographicException("The certificate is not the exact configured public CA.");
            return der;
        }
        catch (AsnContentException error) { throw new CryptographicException("The public CA DER is not canonical.", error); }
    }

    private static bool ContainsAnyExceptWhiteSpace(this ReadOnlySpan<char> value)
    {
        foreach (var character in value) if (!char.IsWhiteSpace(character)) return true;
        return false;
    }
}
