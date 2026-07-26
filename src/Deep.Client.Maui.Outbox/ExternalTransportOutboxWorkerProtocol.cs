using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Outbox;

public enum ExternalTransportOutboxWorkerOperation
{
    Probe = 1,
    Dispatch = 2
}

public sealed record ExternalTransportOutboxWorkerRequest(
    int ProtocolVersion,
    ExternalTransportOutboxWorkerOperation Operation,
    byte[] Nonce,
    byte[]? LogicalId,
    byte[]? AttemptId,
    byte[]? DedupMaterial,
    byte[]? CiphertextBundle,
    long? ExpiresAtUnixMilliseconds);

public sealed record ExternalTransportOutboxWorkerResponse(
    int ProtocolVersion,
    byte[] Nonce,
    bool Success,
    TransportOutboxAdapterDisposition? Disposition,
    byte[]? AcceptedEvidence,
    byte[]? DurableEvidence,
    string? ErrorCode);

public sealed record ExternalTransportOutboxWorkerRequestContext(
    ExternalTransportOutboxWorkerRequest Request,
    byte[] SessionKey) : IDisposable
{
    public void Dispose() => CryptographicOperations.ZeroMemory(SessionKey);
}

/// <summary>
/// Bounded authenticated single-request protocol for the private inherited
/// stdin/stdout handles of an attested outbox worker. The session key is
/// transferred only through the inherited stdin handle and is never placed in
/// arguments, environment variables, diagnostics, or persistent storage.
/// </summary>
public static class ExternalTransportOutboxWorkerProtocol
{
    public const int Version = 1;
    public const int NonceBytes = 32;
    public const int SessionKeyBytes = 32;
    public const int MaximumRequestFrameBytes = 1_500_000;
    public const int MaximumResponseFrameBytes = 32_768;

    private const byte EnvelopeVersion = 1;
    private const byte RequestEnvelopeKind = 1;
    private const byte ResponseEnvelopeKind = 2;
    private const int MacBytes = 32;
    private const int BinaryEnvelopeHeaderBytes = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 8
    };

    public static async Task WriteRequestAsync(
        Stream stream,
        ExternalTransportOutboxWorkerRequest request,
        ReadOnlyMemory<byte> sessionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateRequest(request);
        if (sessionKey.Length != SessionKeyBytes)
        {
            throw new ArgumentException("The worker session key has an invalid length.", nameof(sessionKey));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var mac = HMACSHA256.HashData(sessionKey.Span, payload);
        var keyCopy = sessionKey.ToArray();
        byte[]? frame = null;
        try
        {
            frame = CreateRequestFrame(keyCopy, payload, mac);
            await WriteFrameAsync(
                    stream,
                    frame,
                    MaximumRequestFrameBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (frame is not null)
            {
                CryptographicOperations.ZeroMemory(frame);
            }
            CryptographicOperations.ZeroMemory(keyCopy);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public static async Task<ExternalTransportOutboxWorkerRequestContext> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = await ReadFrameAsync(
                stream,
                MaximumRequestFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);
        byte[]? sessionKey = null;
        byte[]? payload = null;
        byte[]? mac = null;
        var transferredSessionKey = false;
        try
        {
            ParseRequestFrame(frame, out sessionKey, out payload, out mac);
            var expectedMac = HMACSHA256.HashData(sessionKey, payload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
                {
                    throw new InvalidDataException("Invalid worker request.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
            }

            var request = JsonSerializer.Deserialize<ExternalTransportOutboxWorkerRequest>(
                    payload,
                    JsonOptions)
                ?? throw new InvalidDataException("Invalid worker request.");
            ValidateRequest(request);
            transferredSessionKey = true;
            return new(request, sessionKey);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("Invalid worker request.");
        }
        finally
        {
            if (!transferredSessionKey && sessionKey is not null)
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
            if (mac is not null)
            {
                CryptographicOperations.ZeroMemory(mac);
            }
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    public static async Task WriteResponseAsync(
        Stream stream,
        ExternalTransportOutboxWorkerResponse response,
        ReadOnlyMemory<byte> sessionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateResponse(response);
        if (sessionKey.Length != SessionKeyBytes)
        {
            throw new ArgumentException("The worker session key has an invalid length.", nameof(sessionKey));
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
        var mac = HMACSHA256.HashData(sessionKey.Span, payload);
        byte[]? frame = null;
        try
        {
            frame = CreateResponseFrame(payload, mac);
            await WriteFrameAsync(
                    stream,
                    frame,
                    MaximumResponseFrameBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (frame is not null)
            {
                CryptographicOperations.ZeroMemory(frame);
            }
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public static async Task<ExternalTransportOutboxWorkerResponse> ReadResponseAsync(
        Stream stream,
        ReadOnlyMemory<byte> sessionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (sessionKey.Length != SessionKeyBytes)
        {
            throw new ArgumentException("The worker session key has an invalid length.", nameof(sessionKey));
        }

        var frame = await ReadFrameAsync(
                stream,
                MaximumResponseFrameBytes,
                cancellationToken)
            .ConfigureAwait(false);
        byte[]? payload = null;
        byte[]? mac = null;
        try
        {
            ParseResponseFrame(frame, out payload, out mac);
            var expectedMac = HMACSHA256.HashData(sessionKey.Span, payload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
                {
                    throw new InvalidDataException("Invalid worker response.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
            }

            var response = JsonSerializer.Deserialize<ExternalTransportOutboxWorkerResponse>(
                    payload,
                    JsonOptions)
                ?? throw new InvalidDataException("Invalid worker response.");
            ValidateResponse(response);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new InvalidDataException("Invalid worker response.");
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
            if (mac is not null)
            {
                CryptographicOperations.ZeroMemory(mac);
            }
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    private static byte[] CreateRequestFrame(
        ReadOnlySpan<byte> sessionKey,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> mac)
    {
        var length = checked(
            BinaryEnvelopeHeaderBytes
            + SessionKeyBytes
            + payload.Length
            + MacBytes);
        var frame = new byte[length];
        frame[0] = EnvelopeVersion;
        frame[1] = RequestEnvelopeKind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(2, sizeof(int)), payload.Length);
        sessionKey.CopyTo(frame.AsSpan(BinaryEnvelopeHeaderBytes, SessionKeyBytes));
        payload.CopyTo(frame.AsSpan(BinaryEnvelopeHeaderBytes + SessionKeyBytes, payload.Length));
        mac.CopyTo(frame.AsSpan(length - MacBytes, MacBytes));
        return frame;
    }

    private static byte[] CreateResponseFrame(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> mac)
    {
        var length = checked(BinaryEnvelopeHeaderBytes + payload.Length + MacBytes);
        var frame = new byte[length];
        frame[0] = EnvelopeVersion;
        frame[1] = ResponseEnvelopeKind;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(2, sizeof(int)), payload.Length);
        payload.CopyTo(frame.AsSpan(BinaryEnvelopeHeaderBytes, payload.Length));
        mac.CopyTo(frame.AsSpan(length - MacBytes, MacBytes));
        return frame;
    }

    private static void ParseRequestFrame(
        ReadOnlySpan<byte> frame,
        out byte[] sessionKey,
        out byte[] payload,
        out byte[] mac)
    {
        var minimumLength = BinaryEnvelopeHeaderBytes + SessionKeyBytes + MacBytes + 1;
        if (frame.Length < minimumLength
            || frame[0] != EnvelopeVersion
            || frame[1] != RequestEnvelopeKind)
        {
            throw new InvalidDataException("Invalid worker request.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(2, sizeof(int)));
        if (payloadLength <= 0
            || frame.Length != BinaryEnvelopeHeaderBytes
                + SessionKeyBytes
                + payloadLength
                + MacBytes)
        {
            throw new InvalidDataException("Invalid worker request.");
        }

        sessionKey = frame.Slice(BinaryEnvelopeHeaderBytes, SessionKeyBytes).ToArray();
        payload = frame.Slice(
                BinaryEnvelopeHeaderBytes + SessionKeyBytes,
                payloadLength)
            .ToArray();
        mac = frame[^MacBytes..].ToArray();
    }

    private static void ParseResponseFrame(
        ReadOnlySpan<byte> frame,
        out byte[] payload,
        out byte[] mac)
    {
        var minimumLength = BinaryEnvelopeHeaderBytes + MacBytes + 1;
        if (frame.Length < minimumLength
            || frame[0] != EnvelopeVersion
            || frame[1] != ResponseEnvelopeKind)
        {
            throw new InvalidDataException("Invalid worker response.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(frame.Slice(2, sizeof(int)));
        if (payloadLength <= 0
            || frame.Length != BinaryEnvelopeHeaderBytes + payloadLength + MacBytes)
        {
            throw new InvalidDataException("Invalid worker response.");
        }

        payload = frame.Slice(BinaryEnvelopeHeaderBytes, payloadLength).ToArray();
        mac = frame[^MacBytes..].ToArray();
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] frame,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        if (frame.Length is <= 0 || frame.Length > maximumFrameBytes)
        {
            throw new InvalidDataException("Invalid worker frame.");
        }

        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(prefix, frame.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 || length > maximumFrameBytes)
        {
            throw new InvalidDataException("Invalid worker frame.");
        }

        var frame = new byte[length];
        await stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);
        return frame;
    }

    private static void ValidateRequest(ExternalTransportOutboxWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProtocolVersion != Version
            || request.Nonce is null
            || request.Nonce.Length != NonceBytes
            || request.Operation is not (ExternalTransportOutboxWorkerOperation.Probe
                or ExternalTransportOutboxWorkerOperation.Dispatch))
        {
            throw new InvalidDataException("Invalid worker request.");
        }

        if (request.Operation == ExternalTransportOutboxWorkerOperation.Probe)
        {
            if (request.LogicalId is not null
                || request.AttemptId is not null
                || request.DedupMaterial is not null
                || request.CiphertextBundle is not null
                || request.ExpiresAtUnixMilliseconds is not null)
            {
                throw new InvalidDataException("Invalid worker request.");
            }
            return;
        }

        if (request.LogicalId?.Length != TransportOutboxLimits.LogicalIdBytes
            || request.AttemptId?.Length != TransportOutboxLimits.AttemptIdBytes
            || request.DedupMaterial?.Length != TransportOutboxLimits.DedupMaterialBytes
            || request.CiphertextBundle?.Length is not (> 0 and <= TransportOutboxLimits.MaxCiphertextBundleBytes)
            || request.ExpiresAtUnixMilliseconds is null)
        {
            throw new InvalidDataException("Invalid worker request.");
        }
    }

    private static void ValidateResponse(ExternalTransportOutboxWorkerResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.ProtocolVersion != Version
            || response.Nonce is null
            || response.Nonce.Length != NonceBytes)
        {
            throw new InvalidDataException("Invalid worker response.");
        }

        if (!response.Success)
        {
            if (response.Disposition is not null
                || response.AcceptedEvidence is not null
                || response.DurableEvidence is not null
                || string.IsNullOrWhiteSpace(response.ErrorCode)
                || response.ErrorCode.Length > 64
                || response.ErrorCode.Any(static character =>
                    !(character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            {
                throw new InvalidDataException("Invalid worker response.");
            }
            return;
        }

        if (response.Disposition == TransportOutboxAdapterDisposition.Accepted)
        {
            if (response.AcceptedEvidence?.Length is not (> 0 and <= TransportOutboxLimits.MaxEvidenceBytes)
                || response.DurableEvidence is not null
                || response.ErrorCode is not null)
            {
                throw new InvalidDataException("Invalid worker response.");
            }
            return;
        }

        if (response.Disposition != TransportOutboxAdapterDisposition.Durable
            || response.AcceptedEvidence?.Length is not (> 0 and <= TransportOutboxLimits.MaxEvidenceBytes)
            || response.DurableEvidence?.Length is not (> 0 and <= TransportOutboxLimits.MaxEvidenceBytes)
            || response.ErrorCode is not null)
        {
            throw new InvalidDataException("Invalid worker response.");
        }
    }

}
