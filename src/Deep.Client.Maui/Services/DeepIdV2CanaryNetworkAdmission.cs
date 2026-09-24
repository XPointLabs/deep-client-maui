#if DEEP_DID2_CANARY_ADMISSION
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Deep.Protocol.AccountDirectoryV1;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.XPointNetworkV1;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

/// <summary>
/// Debug-only, loopback-tunnel DID2 admission diagnostic. Its bootstrap inputs
/// are public signed artifacts; the compiled pins and full protocol verifiers
/// remain authoritative. This is not a release transport composition.
/// </summary>
internal sealed class DeepIdV2CanaryNetworkAdmission :
    IDeepIdV2NetworkAdmission
{
    private const string OriginKey = "DeepDid2CanaryOrigin";
    private const string XnaPinKey = "DeepDid2CanaryXna1Pin";
    private const string HeadPinKey = "DeepDid2CanaryAdh1Pin";

    public async Task VerifyAsync(DeepIdV2AccountService accounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var origin = Metadata(OriginKey);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" ||
            uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0)
            throw new InvalidOperationException(
                "The DID2 canary must use an explicit loopback tunnel origin.");

        var network = ActiveBuildNetworkId.Load();
        var xnaPin = Pin(XnaPinKey);
        var headPin = Pin(HeadPinKey);
        var exactXna = await ReadAssetAsync("did2_xna1.bin", 65_535,
            cancellationToken);
        var exactDts = await ReadAssetAsync("did2_dts1.bin", 65_535,
            cancellationToken);
        var exactHead = await ReadAssetAsync("did2_adh1.bin", 4096,
            cancellationToken);
        var authority = XPointNetworkAuthorityVerifier.Verify(
            new XPointNetworkGenesisPin(network.Span, xnaPin),
            [(ReadOnlyMemory<byte>)exactXna],
            [(ReadOnlyMemory<byte>)exactDts]);
        _ = DeepIdV2DirectoryBootstrapVerifier.RestoreGenesis(authority,
            exactHead, headPin);
        var protectedFloor = await accounts.OpenDirectoryLkgStoreAsync(
            authority, exactHead, headPin, cancellationToken);

        var factory = new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production);
        using var admission = factory.CreateDeepIdV2GenesisAdmissionClient(
            origin);
        using var verifier = DeepMlDsa65CandidateVerifierFactory
            .OpenForCurrentProcess();
        using var proof = factory.CreateDeepIdV2DirectoryProofClient(origin,
            new CanaryMonotonicClock(), verifier, protectedFloor);
        var verified = await accounts.AdmitAndVerifyGenesisAsync(admission,
            proof, authority, cancellationToken: cancellationToken);
        if (verified.CurrentCheckpoint is null ||
            verified.NextProtectedLkg.TreeSize == 0)
            throw new CryptographicException(
                "The DID2 canary did not prove the current account genesis.");
    }

    private static string Metadata(string key)
    {
        var matches = typeof(DeepIdV2CanaryNetworkAdmission).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(value => string.Equals(value.Key, key,
                StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1 &&
               !string.IsNullOrWhiteSpace(matches[0].Value)
            ? matches[0].Value!
            : throw new InvalidOperationException(
                $"The DID2 canary build input '{key}' is absent.");
    }

    private static byte[] Pin(string key)
    {
        var value = Metadata(key);
        if (value.Length != 64 || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidOperationException(
                $"The DID2 canary build pin '{key}' is malformed.");
        var pin = Convert.FromHexString(value);
        if (pin.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new InvalidOperationException(
                $"The DID2 canary build pin '{key}' is zero.");
        return pin;
    }

    private static async Task<byte[]> ReadAssetAsync(string name, int maximum,
        CancellationToken cancellationToken)
    {
        await using var stream = await FileSystem.Current
            .OpenAppPackageFileAsync(name);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (output.Length + count > maximum)
                throw new InvalidDataException(
                    "The DID2 canary bootstrap asset exceeds its bound.");
            output.Write(buffer, 0, count);
        }
        if (output.Length == 0)
            throw new InvalidDataException(
                "The DID2 canary bootstrap asset is empty.");
        return output.ToArray();
    }

    private sealed class CanaryMonotonicClock : IOnionMonotonicClock
    {
        private readonly byte[] bootId = RandomNumberGenerator.GetBytes(16);
        private readonly long started = Stopwatch.GetTimestamp();

        public ValueTask<OnionMonotonicReading> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = Stopwatch.GetElapsedTime(started);
            return ValueTask.FromResult(new OnionMonotonicReading(bootId,
                checked(1_000UL + (ulong)Math.Floor(elapsed.TotalSeconds))));
        }
    }
}
#endif
