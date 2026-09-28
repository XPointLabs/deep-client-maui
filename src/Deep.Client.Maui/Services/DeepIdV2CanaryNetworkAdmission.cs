#if DEEP_DID2_CANARY_ADMISSION || DEEP_DID2_HTTPS_ADMISSION
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.Services.ContactV2;
using Deep.Protocol.AccountDirectoryV1;
using Deep.Protocol.ApplicationCore;
using Deep.Protocol.DeepExtension.PrivacyRouting;
using Deep.Protocol.Identity;
using Deep.Protocol.XPointNetworkV1;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

/// <summary>
/// DID2 physical diagnostic. The loopback probe and HTTPS UAT package use the
/// same pinned public authority and proof verifier, but neither is a release
/// transport composition.
/// </summary>
internal sealed class DeepIdV2CanaryNetworkAdmission :
    IDeepIdV2NetworkAdmission, IDeepIdV2ContactDiscovery
{
    private const string OriginKey = "DeepDid2CanaryOrigin";
    private const string XnaPinKey = "DeepDid2CanaryXna1Pin";
    private const string HeadPinKey = "DeepDid2CanaryAdh1Pin";
#if DEEP_DID2_HTTPS_ADMISSION
    private const string HttpsOriginKey = "DeepDid2HttpsOrigin";
#endif

    public async Task VerifyAsync(DeepIdV2AccountService accounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var origin = ValidatedOrigin();
        var (genesisPin, authority, exactHead, headPin) = await ReadBootstrapAsync(
            cancellationToken);
        var protectedFloor = await accounts.OpenDirectoryLkgStoreAsync(
            authority, exactHead, headPin, cancellationToken);

        var factory = new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production);
        var publicClientOptions = DiagnosticHttpOptions();
        using var admission = factory.CreateDeepIdV2GenesisAdmissionClient(
            origin, clientOptions: publicClientOptions);
        using var verifier = DeepMlDsa65CandidateVerifierFactory
            .OpenForCurrentProcess();
        var clock = new CanaryMonotonicClock();
        using var proof = factory.CreateDeepIdV2DirectoryProofClient(origin,
            clock, verifier, protectedFloor, clientOptions: publicClientOptions);
        var verified = await HttpStageAsync("AccountProof", () =>
            accounts.AdmitAndVerifyGenesisAsync(admission,
                proof, authority, cancellationToken: cancellationToken), cancellationToken);
        if (verified.CurrentCheckpoint is null ||
            verified.NextProtectedLkg.TreeSize == 0)
            throw new CryptographicException(
                "The DID2 canary did not prove the current account genesis.");
#if DEEP_DID2_HTTPS_ADMISSION
        // Public distribution is untrusted. Only the account-owned source
        // verifies the complete signed closure, fresh proof and durable floor.
        // The HTTP loopback probe remains admission-only, never a TLS claim.
        using var closure = factory.CreateDeepIdV2NetworkClosureArtifactSource(origin,
            clientOptions: publicClientOptions);
        var networkFloor = await accounts.OpenNetworkLkgStoreAsync(genesisPin,
            cancellationToken);
        var networkSource = new DeepIdV2ContactPathAuthoritySource(genesisPin,
            accounts, proof, closure, networkFloor, clock);
        var network = await HttpStageAsync("NetworkVerification", () =>
            networkSource.VerifyCurrentNetworkAsync(
                genesisPin.NetworkId, cancellationToken).AsTask(), cancellationToken);
        network.EnsureCurrent();
        // Seal all local pre-key capabilities before publication.
        // An exact staged retry is historical state, not a delivery authority.
        _ = await HttpStageAsync("PreKeyStaging", () =>
            accounts.EnsureOwnInitialPreKeyInventoryAsync(networkSource,
                cancellationToken), cancellationToken);
        var custody = await accounts.OpenOwnOnionClientCustodyAsync(cancellationToken);
        // Success requires authenticated replies from both selected exits,
        // independently verified XIC1 signatures and durable pair recording.
        // A Registry proof or local staging alone cannot complete this action.
        _ = await HttpStageAsync("PreKeyPublication", () =>
            accounts.PublishOwnStagedPreKeyInventoryAsync(networkSource,
                custody, cancellationToken), cancellationToken);
#endif
    }

    private static HttpServiceClientOptions? DiagnosticHttpOptions()
    {
#if DEEP_DID2_HTTPS_ADMISSION
        // The real path aborts a second response on a reused connection, also
        // with plain HttpClient. Isolate this diagnostic's public requests on
        // fresh connections while preserving TLS, deadlines and signatures.
        // This is not a retry, downgrade, or change to selected-entry transport.
        return new(PooledConnectionLifetime: TimeSpan.FromTicks(1));
#else
        return null;
#endif
    }

    // Only fixed stage labels and exception classifications enter the UI.
    // URLs, credentials, request bytes and private exception messages do not.
    private static async Task<T> HttpStageAsync<T>(string stage, Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        try { return await action(); }
        catch (HttpRequestException exception)
        {
            var tls = string.Empty;
            if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
            {
                var chain = Did2TlsFailureClassifier.Classify(exception);
#if DEEP_DID2_HTTPS_ADMISSION
                if (chain == "Unknown")
                    chain = await Did2TlsFailureClassifier.ObserveRejectedHandshakeAsync(
                        new Uri(ValidatedOrigin()), cancellationToken).ConfigureAwait(false);
#endif
                tls = $"; Chain {chain}";
            }
            throw new InvalidOperationException(
                $"DID2 {stage} failed ({exception.HttpRequestError}; " +
                $"{exception.InnerException?.GetType().Name ?? "None"}{tls}).",
                exception);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"DID2 {stage} failed (Timeout).", exception);
        }
        catch (OnionBoundaryException exception)
        {
            var code = exception.Code is "network-stale-or-fork" or "network-history-mismatch" or
                "network-genesis-required" or "network-rehydration-mismatch" or "network-fork"
                ? exception.Code : "network-verification-rejected";
            throw new InvalidOperationException($"DID2 {stage} failed ({code}).", exception);
        }
    }

    public async Task VerifyAsync(DeepIdV2AccountService accounts,
        string compactDescriptor, string exactDid2Hex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(compactDescriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactDid2Hex);
        var descriptor = DeepPermanentIdV2.ParseCanonical(
            compactDescriptor.Trim());
        var hex = exactDid2Hex.Trim();
        if (hex.Length != 2 * DeepIdV2Codec.Did2Length)
            throw new FormatException(
                "The exact DID2 contact credential has the wrong length.");
        var exactDid2 = DeepIdV2Codec.DecodeDid2(Convert.FromHexString(hex));
        if (!descriptor.MatchesExactCredential(exactDid2))
            throw new CryptographicException(
                "The contact descriptor does not bind the exact DID2 credential.");

        var origin = ValidatedOrigin();
        var (_, authority, exactHead, headPin) = await ReadBootstrapAsync(
            cancellationToken);
        var protectedFloor = await accounts.OpenDirectoryLkgStoreAsync(
            authority, exactHead, headPin, cancellationToken);
        var factory = new HttpServiceTransportFactory(
            HttpServiceEndpointPolicy.Production);
        using var verifier = DeepMlDsa65CandidateVerifierFactory
            .OpenForCurrentProcess();
        using var proof = factory.CreateDeepIdV2DirectoryProofClient(origin,
            new CanaryMonotonicClock(), verifier, protectedFloor);
        _ = await proof.FetchByContactDescriptorAsync(descriptor,
            exactDid2, authority, deploymentProfileId: 1,
            supportedReader: 2, cancellationToken: cancellationToken);
    }

    private static string ValidatedOrigin()
    {
#if DEEP_DID2_HTTPS_ADMISSION
        var origin = Metadata(HttpsOriginKey);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback ||
            uri.IsDefaultPort is false || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns)
            throw new InvalidOperationException(
                "The DID2 physical UAT build requires a canonical HTTPS authority origin.");
        return origin;
#else
        var origin = Metadata(OriginKey);
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" ||
            uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Length != 0)
            throw new InvalidOperationException(
                "The DID2 canary must use an explicit loopback tunnel origin.");
        return origin;
#endif
    }

    private static async Task<(XPointNetworkGenesisPin GenesisPin,
        VerifiedXPointNetworkAuthority Authority,
        byte[] ExactHead, byte[] HeadPin)> ReadBootstrapAsync(
        CancellationToken cancellationToken)
    {
        var network = ActiveBuildNetworkId.Load();
        var xnaPin = Pin(XnaPinKey);
        var headPin = Pin(HeadPinKey);
        var exactXna = await ReadAssetAsync("did2_xna1.bin", 65_535,
            cancellationToken);
        var exactDts = await ReadAssetAsync("did2_dts1.bin", 65_535,
            cancellationToken);
        var exactHead = await ReadAssetAsync("did2_adh1.bin", 4096,
            cancellationToken);
        var genesisPin = new XPointNetworkGenesisPin(network.Span, xnaPin);
        var authority = XPointNetworkAuthorityVerifier.Verify(
            genesisPin,
            [(ReadOnlyMemory<byte>)exactXna],
            [(ReadOnlyMemory<byte>)exactDts]);
        _ = DeepIdV2DirectoryBootstrapVerifier.RestoreGenesis(authority,
            exactHead, headPin);
        return (genesisPin, authority, exactHead, headPin);
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
