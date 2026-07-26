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
    public const int MaximumFrameBytes = 1_500_000;

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
            frame = JsonSerializer.SerializeToUtf8Bytes(
                new RequestEnvelope(keyCopy, payload, mac),
                JsonOptions);
            await WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false);
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
        var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        RequestEnvelope? envelope = null;
        var transferredSessionKey = false;
        try
        {
            envelope = JsonSerializer.Deserialize<RequestEnvelope>(frame, JsonOptions)
                ?? throw new InvalidDataException("Invalid worker request.");
            if (envelope.SessionKey is null
                || envelope.SessionKey.Length != SessionKeyBytes
                || envelope.Payload is null
                || envelope.Mac is null
                || envelope.Mac.Length != 32)
            {
                throw new InvalidDataException("Invalid worker request.");
            }

            var expectedMac = HMACSHA256.HashData(envelope.SessionKey, envelope.Payload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, envelope.Mac))
                {
                    throw new InvalidDataException("Invalid worker request.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
            }

            var request = JsonSerializer.Deserialize<ExternalTransportOutboxWorkerRequest>(
                    envelope.Payload,
                    JsonOptions)
                ?? throw new InvalidDataException("Invalid worker request.");
            ValidateRequest(request);
            transferredSessionKey = true;
            return new(request, envelope.SessionKey);
        }
        catch
        {
            throw new InvalidDataException("Invalid worker request.");
        }
        finally
        {
            if (!transferredSessionKey && envelope?.SessionKey is not null)
            {
                CryptographicOperations.ZeroMemory(envelope.SessionKey);
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
            frame = JsonSerializer.SerializeToUtf8Bytes(new ResponseEnvelope(payload, mac), JsonOptions);
            await WriteFrameAsync(stream, frame, cancellationToken).ConfigureAwait(false);
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

        var frame = await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false);
        try
        {
            var envelope = JsonSerializer.Deserialize<ResponseEnvelope>(frame, JsonOptions)
                ?? throw new InvalidDataException("Invalid worker response.");
            if (envelope.Payload is null
                || envelope.Mac is null
                || envelope.Mac.Length != 32)
            {
                throw new InvalidDataException("Invalid worker response.");
            }

            var expectedMac = HMACSHA256.HashData(sessionKey.Span, envelope.Payload);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, envelope.Mac))
                {
                    throw new InvalidDataException("Invalid worker response.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
            }

            var response = JsonSerializer.Deserialize<ExternalTransportOutboxWorkerResponse>(
                    envelope.Payload,
                    JsonOptions)
                ?? throw new InvalidDataException("Invalid worker response.");
            ValidateResponse(response);
            return response;
        }
        catch
        {
            throw new InvalidDataException("Invalid worker response.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame);
        }
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        if (frame.Length is <= 0 or > MaximumFrameBytes)
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
        CancellationToken cancellationToken)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaximumFrameBytes)
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

    private sealed record RequestEnvelope(byte[] SessionKey, byte[] Payload, byte[] Mac);

    private sealed record ResponseEnvelope(byte[] Payload, byte[] Mac);
}
