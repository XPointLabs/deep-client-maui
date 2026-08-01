using System.Security.Cryptography;
using System.Text;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Deep.Protocol.DeepExtension.CompatibilityEnvelopes;
using Deep.Protocol.DeepExtension.MailboxCapabilities;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

internal static class OpaqueStorageTestDependencies
{
    public static OpaqueSessionStorageDependencies Create() =>
        new(new TestCapabilities(), new TestCompatibilityCrypto(), new TestReplayGuard());

    private sealed class TestCapabilities : IOpaqueMailboxCapabilityProvider
    {
        private long counter;

        public OpaqueMailboxDepositMaterial CreateDeposit(
            OutboundMessageEnvelope envelope,
            ReadOnlyMemory<byte> transportAttemptId,
            uint currentBucket) =>
            new(
                Presentation(
                    new RotatingDepositCapability(Derive("deposit", envelope.Recipient.Value)),
                    transportAttemptId,
                    currentBucket),
                new OpaquePlacementKey(Derive("placement", envelope.Recipient.Value)),
                Derive("recipient-key", envelope.Recipient.Value),
                Convert.FromHexString(envelope.Sender.Value));

        public OpaqueMailboxRetrieveMaterial CreateRetrieve(
            SessionIdentityProvider identity,
            ReadOnlyMemory<byte> transportAttemptId,
            uint currentBucket)
        {
            var generation = Interlocked.Increment(ref counter);
            return new OpaqueMailboxRetrieveMaterial(
                Presentation(
                    new RotatingRetrieveCapability(
                        Derive("retrieve", $"{identity.SessionId.Value}:{generation}")),
                    transportAttemptId,
                    currentBucket),
                new OpaquePlacementKey(Derive("placement", identity.SessionId.Value)));
        }

        public ReadOnlyMemory<byte> GetRecipientKeyMaterial(SessionId recipient) =>
            Derive("recipient-key", recipient.Value);

        private MailboxCapabilityPresentation Presentation(
            MailboxDomainValue value,
            ReadOnlyMemory<byte> transportAttemptId,
            uint currentBucket) =>
            new()
            {
                DomainValue = value,
                Lifecycle = MailboxCapabilityLifecycle.Active,
                MixedVersion = MailboxMixedVersionMarker.StrictV1,
                Generation = 1,
                NotBeforeBucket = currentBucket,
                ExpiresAtBucket = checked(currentBucket + 24),
                OverlapUntilBucket = 0,
                ReplayCounter = checked((ulong)Interlocked.Increment(ref counter)),
                IdempotencyKey = transportAttemptId,
                FreeAdmission = null
            };

        private static byte[] Derive(string domain, string value) =>
            SHA256.HashData(Encoding.UTF8.GetBytes($"deep-test/{domain}/{value}"));
    }

    private sealed class TestCompatibilityCrypto : ICompatibilityEnvelopeCrypto
    {
        private const int SenderLength = 33;
        private const int TagLength = 16;

        public CompatibilityEnvelopeSealedResult Seal(CompatibilityEnvelopeSealRequest request)
        {
            var plaintext = new byte[SenderLength + request.Plaintext.Length];
            request.SenderAuthenticationSecret.Span.CopyTo(plaintext);
            request.Plaintext.Span.CopyTo(plaintext.AsSpan(SenderLength));
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagLength];
            using var aes = new AesGcm(
                DeriveKey(request.RecipientKeyMaterial.Span, request.Domain), TagLength);
            aes.Encrypt(
                DeriveNonce(request.NonceContext.Span, request.Domain),
                plaintext,
                ciphertext,
                tag,
                request.AssociatedData.Span);
            CryptographicOperations.ZeroMemory(plaintext);
            var sealedBytes = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(sealedBytes, 0);
            tag.CopyTo(sealedBytes, ciphertext.Length);
            return new CompatibilityEnvelopeSealedResult(sealedBytes);
        }

        public CompatibilityEnvelopeOpenedResult Open(CompatibilityEnvelopeOpenRequest request)
        {
            if (request.Ciphertext.Length <= TagLength + SenderLength)
                throw new CryptographicException("Test compatibility ciphertext is truncated.");

            var ciphertext = request.Ciphertext.Span[..^TagLength];
            var plaintext = new byte[ciphertext.Length];
            using var aes = new AesGcm(
                DeriveKey(request.RecipientKeyMaterial.Span, request.Domain), TagLength);
            aes.Decrypt(
                DeriveNonce(request.NonceContext.Span, request.Domain),
                ciphertext,
                request.Ciphertext.Span[^TagLength..],
                plaintext,
                request.AssociatedData.Span);
            var sender = plaintext.AsSpan(0, SenderLength).ToArray();
            var content = plaintext.AsSpan(SenderLength).ToArray();
            CryptographicOperations.ZeroMemory(plaintext);
            return new CompatibilityEnvelopeOpenedResult(content, sender);
        }

        private static byte[] DeriveKey(ReadOnlySpan<byte> recipient, string domain) =>
            SHA256.HashData([.. recipient, .. Encoding.UTF8.GetBytes(domain)]);

        private static byte[] DeriveNonce(ReadOnlySpan<byte> nonceContext, string domain) =>
            SHA256.HashData([.. nonceContext, .. Encoding.UTF8.GetBytes(domain)])[..12];
    }

    private sealed class TestReplayGuard : ICompatibilityEnvelopeReplayGuard
    {
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);

        public bool TryAccept(CompatibilityEnvelopeReplayScope scope) =>
            seen.Add(
                Convert.ToHexString(scope.Capability.Span) + ":" +
                scope.ExpiryBucket + ":" +
                Convert.ToHexString(scope.ReplayMaterial.Span));
    }
}
