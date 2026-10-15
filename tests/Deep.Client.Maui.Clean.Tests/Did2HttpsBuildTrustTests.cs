using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Deep.Client.Maui.Core.Services;

namespace Deep.Client.Maui.Clean.Tests;

public sealed class Did2HttpsBuildTrustTests
{
    private static readonly byte[] Pin = Enumerable.Repeat((byte)0x41, 32).ToArray();

    [Fact]
    public void PublicProfileKeepsCanonicalSystemCaOrigin()
        => Assert.Equal("https://registry.example/", Did2HttpsBuildTrust.RequireOrigin("https://registry.example/", []));

    [Theory]
    [InlineData("http://registry.example/")]
    [InlineData("https://localhost/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://registry.example:28543/")]
    [InlineData("https://registry.example/?x=1")]
    [InlineData("https://user@registry.example/")]
    [InlineData("https://registry.example/#fragment")]
    [InlineData("https://registry.example/path")]
    [InlineData("https://REGISTRY.example/")]
    [InlineData("https://registry.example")]
    public void PublicProfileRejectsPrivateOrNoncanonicalOrigins(string origin)
        => Assert.Throws<InvalidOperationException>(() => Did2HttpsBuildTrust.RequireOrigin(origin, []));

    [Theory]
    [InlineData("https://127.0.0.1:28543/")]
    [InlineData("https://localhost:28543/")]
    [InlineData("https://[::1]:28543/")]
    [InlineData("https://registry.example:28543/")]
    public void ExplicitPrivateProfileAllowsOnlyCanonicalHttpsOrigin(string origin)
        => Assert.Equal(origin, Did2HttpsBuildTrust.RequireOrigin(origin, Pin));

    [Theory]
    [InlineData("http://127.0.0.1:28543/")]
    [InlineData("https://user@localhost:28543/")]
    [InlineData("https://localhost:28543/?x=1")]
    [InlineData("https://localhost:28543/#fragment")]
    [InlineData("https://localhost:28543/path")]
    [InlineData("https://LOCALHOST:28543/")]
    [InlineData("https://localhost:99999/")]
    [InlineData("https://localhost:0/")]
    public void PrivateProfileDoesNotRelaxHttpsCanonicality(string origin)
        => Assert.Throws<InvalidOperationException>(() => Did2HttpsBuildTrust.RequireOrigin(origin, Pin));

    [Fact]
    public void OriginRejectsInvalidPrivateFingerprint()
    {
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequireOrigin("https://localhost/", new byte[32]));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequireOrigin("https://localhost/", new byte[31]));
    }

    [Fact]
    public void PublicCaRequiresExactDerOrSinglePemAndItsDerFingerprint()
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateCertificate(key, true, true);
        var der = certificate.Export(X509ContentType.Cert);
        var hash = SHA256.HashData(der);
        Assert.Equal(der, Did2HttpsBuildTrust.RequirePublicRoot(der, hash));
        Assert.Equal(der, Did2HttpsBuildTrust.RequirePublicRoot(Encoding.UTF8.GetBytes("\n" + certificate.ExportCertificatePem() + "\n"), hash));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(der, Pin));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(der, new byte[32]));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot([.. der, 0], hash));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(Encoding.UTF8.GetBytes(certificate.ExportCertificatePem() + certificate.ExportCertificatePem()), hash));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(Encoding.UTF8.GetBytes(certificate.ExportCertificatePem() + key.ExportPkcs8PrivateKeyPem()), hash));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(certificate.Export(X509ContentType.Pfx, "test-only"), hash));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem()), hash));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void LeafOrNonSigningCertificateCannotBecomePrivateRoot(bool ca, bool signing)
    {
        using var key = RSA.Create(2048);
        using var certificate = CreateCertificate(key, ca, signing);
        var der = certificate.Export(X509ContentType.Cert);
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(der, SHA256.HashData(der)));
    }

    [Fact]
    public void CertificateInputBoundsRejectBeforeParsing()
    {
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot([], Pin));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot(new byte[Did2HttpsBuildTrust.MaximumCertificateBytes + 1], Pin));
        Assert.Throws<CryptographicException>(() => Did2HttpsBuildTrust.RequirePublicRoot([0x30, 0xff], Pin));
    }

    private static X509Certificate2 CreateCertificate(RSA key, bool ca, bool signing)
    {
        var request = new CertificateRequest("CN=Isolated unit test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(signing ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
    }
}
