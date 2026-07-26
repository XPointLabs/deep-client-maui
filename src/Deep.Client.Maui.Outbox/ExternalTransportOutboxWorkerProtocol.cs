using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(SessionKey);
        CryptographicOperations.ZeroMemory(Request.Nonce);
        Zero(Request.LogicalId);
        Zero(Request.AttemptId);
        Zero(Request.DedupMaterial);
        Zero(Request.CiphertextBundle);
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
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

    private const byte EnvelopeVersion = 1;
    private const byte RequestEnvelopeKind = 1;
    private const byte ResponseEnvelopeKind = 2;
    private const int MacBytes = 32;
    private const int BinaryEnvelopeHeaderBytes = 6;
    private const int CommonRequestPayloadBytes = sizeof(int) + sizeof(byte) + NonceBytes;
    private const int DispatchRequestFixedPayloadBytes =
        CommonRequestPayloadBytes
        + TransportOutboxLimits.LogicalIdBytes
        + TransportOutboxLimits.AttemptIdBytes
        + TransportOutboxLimits.DedupMaterialBytes
        + sizeof(long)
        + sizeof(int);
    private const int ResponseFixedPayloadBytes =
        sizeof(int)
        + NonceBytes
        + sizeof(byte)
        + sizeof(byte)
        + sizeof(int) * 3;
    private const int MaximumErrorCodeBytes = 64;
    public const int MaximumRequestPayloadBytes =
        DispatchRequestFixedPayloadBytes + TransportOutboxLimits.MaxCiphertextBundleBytes;
    public const int MaximumResponsePayloadBytes =
        ResponseFixedPayloadBytes
        + MaximumErrorCodeBytes
        + TransportOutboxLimits.MaxEvidenceBytes * 2;
    public const int MaximumRequestFrameBytes =
        BinaryEnvelopeHeaderBytes
        + SessionKeyBytes
        + MaximumRequestPayloadBytes
        + MacBytes;
    public const int MaximumResponseFrameBytes =
        BinaryEnvelopeHeaderBytes + MaximumResponsePayloadBytes + MacBytes;

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

        var payload = EncodeRequest(request);
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

            var request = DecodeRequest(payload);
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

        var payload = EncodeResponse(response);
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

            var response = DecodeResponse(payload);
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

    private static byte[] EncodeRequest(ExternalTransportOutboxWorkerRequest request)
    {
        var dispatch = request.Operation == ExternalTransportOutboxWorkerOperation.Dispatch;
        var payload = new byte[dispatch
            ? checked(DispatchRequestFixedPayloadBytes + request.CiphertextBundle!.Length)
            : CommonRequestPayloadBytes];
        var offset = 0;
        WriteInt32(payload, ref offset, request.ProtocolVersion);
        payload[offset++] = (byte)request.Operation;
        WriteBytes(payload, ref offset, request.Nonce);
        if (!dispatch)
        {
            return payload;
        }

        WriteBytes(payload, ref offset, request.LogicalId!);
        WriteBytes(payload, ref offset, request.AttemptId!);
        WriteBytes(payload, ref offset, request.DedupMaterial!);
        WriteInt64(payload, ref offset, request.ExpiresAtUnixMilliseconds!.Value);
        WriteInt32(payload, ref offset, request.CiphertextBundle!.Length);
        WriteBytes(payload, ref offset, request.CiphertextBundle);
        return payload;
    }

    private static ExternalTransportOutboxWorkerRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < CommonRequestPayloadBytes
            || payload.Length > MaximumRequestPayloadBytes)
        {
            throw new InvalidDataException("Invalid worker request.");
        }

        var offset = 0;
        var version = ReadInt32(payload, ref offset);
        var operation = (ExternalTransportOutboxWorkerOperation)payload[offset++];
        var nonce = ReadBytes(payload, ref offset, NonceBytes);
        if (operation == ExternalTransportOutboxWorkerOperation.Probe)
        {
            if (payload.Length != CommonRequestPayloadBytes)
            {
                CryptographicOperations.ZeroMemory(nonce);
                throw new InvalidDataException("Invalid worker request.");
            }
            var probe = new ExternalTransportOutboxWorkerRequest(
                version,
                operation,
                nonce,
                null,
                null,
                null,
                null,
                null);
            try
            {
                ValidateRequest(probe);
                return probe;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(nonce);
                throw;
            }
        }
        if (operation != ExternalTransportOutboxWorkerOperation.Dispatch
            || payload.Length < DispatchRequestFixedPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(nonce);
            throw new InvalidDataException("Invalid worker request.");
        }

        byte[]? logicalId = null;
        byte[]? attemptId = null;
        byte[]? dedupMaterial = null;
        byte[]? ciphertext = null;
        try
        {
            logicalId = ReadBytes(payload, ref offset, TransportOutboxLimits.LogicalIdBytes);
            attemptId = ReadBytes(payload, ref offset, TransportOutboxLimits.AttemptIdBytes);
            dedupMaterial = ReadBytes(payload, ref offset, TransportOutboxLimits.DedupMaterialBytes);
            var expiresAt = ReadInt64(payload, ref offset);
            var ciphertextLength = ReadInt32(payload, ref offset);
            if (ciphertextLength is <= 0 or > TransportOutboxLimits.MaxCiphertextBundleBytes
                || payload.Length - offset != ciphertextLength)
            {
                throw new InvalidDataException("Invalid worker request.");
            }
            ciphertext = ReadBytes(payload, ref offset, ciphertextLength);
            var request = new ExternalTransportOutboxWorkerRequest(
                version,
                operation,
                nonce,
                logicalId,
                attemptId,
                dedupMaterial,
                ciphertext,
                expiresAt);
            ValidateRequest(request);
            return request;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(nonce);
            Zero(logicalId);
            Zero(attemptId);
            Zero(dedupMaterial);
            Zero(ciphertext);
            throw;
        }
    }

    private static byte[] EncodeResponse(ExternalTransportOutboxWorkerResponse response)
    {
        var error = response.ErrorCode is null
            ? null
            : Encoding.ASCII.GetBytes(response.ErrorCode);
        var payloadLength = checked(
            ResponseFixedPayloadBytes
            + (error?.Length ?? 0)
            + (response.AcceptedEvidence?.Length ?? 0)
            + (response.DurableEvidence?.Length ?? 0));
        var payload = new byte[payloadLength];
        var offset = 0;
        WriteInt32(payload, ref offset, response.ProtocolVersion);
        WriteBytes(payload, ref offset, response.Nonce);
        payload[offset++] = response.Success ? (byte)1 : (byte)0;
        payload[offset++] = response.Disposition is null ? (byte)0 : (byte)response.Disposition.Value;
        WriteNullableBytes(payload, ref offset, error);
        WriteNullableBytes(payload, ref offset, response.AcceptedEvidence);
        WriteNullableBytes(payload, ref offset, response.DurableEvidence);
        Zero(error);
        return payload;
    }

    private static ExternalTransportOutboxWorkerResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ResponseFixedPayloadBytes
            || payload.Length > MaximumResponsePayloadBytes)
        {
            throw new InvalidDataException("Invalid worker response.");
        }

        var offset = 0;
        var version = ReadInt32(payload, ref offset);
        var nonce = ReadBytes(payload, ref offset, NonceBytes);
        byte[]? error = null;
        byte[]? accepted = null;
        byte[]? durable = null;
        try
        {
            var successByte = payload[offset++];
            if (successByte > 1)
            {
                throw new InvalidDataException("Invalid worker response.");
            }
            var dispositionByte = payload[offset++];
            var disposition = dispositionByte == 0
                ? null
                : (TransportOutboxAdapterDisposition?)dispositionByte;
            error = ReadNullableBytes(payload, ref offset, MaximumErrorCodeBytes);
            accepted = ReadNullableBytes(
                payload,
                ref offset,
                TransportOutboxLimits.MaxEvidenceBytes);
            durable = ReadNullableBytes(
                payload,
                ref offset,
                TransportOutboxLimits.MaxEvidenceBytes);
            if (offset != payload.Length)
            {
                throw new InvalidDataException("Invalid worker response.");
            }

            var response = new ExternalTransportOutboxWorkerResponse(
                version,
                nonce,
                successByte == 1,
                disposition,
                accepted,
                durable,
                error is null ? null : Encoding.ASCII.GetString(error));
            ValidateResponse(response);
            Zero(error);
            return response;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(nonce);
            Zero(error);
            Zero(accepted);
            Zero(durable);
            throw;
        }
    }

    private static void WriteInt32(Span<byte> target, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(target.Slice(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    private static void WriteInt64(Span<byte> target, ref int offset, long value)
    {
        BinaryPrimitives.WriteInt64BigEndian(target.Slice(offset, sizeof(long)), value);
        offset += sizeof(long);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureRemaining(source, offset, sizeof(int));
        var value = BinaryPrimitives.ReadInt32BigEndian(source.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureRemaining(source, offset, sizeof(long));
        var value = BinaryPrimitives.ReadInt64BigEndian(source.Slice(offset, sizeof(long)));
        offset += sizeof(long);
        return value;
    }

    private static void WriteBytes(Span<byte> target, ref int offset, ReadOnlySpan<byte> value)
    {
        value.CopyTo(target[offset..]);
        offset += value.Length;
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> source, ref int offset, int length)
    {
        EnsureRemaining(source, offset, length);
        var value = source.Slice(offset, length).ToArray();
        offset += length;
        return value;
    }

    private static void WriteNullableBytes(Span<byte> target, ref int offset, byte[]? value)
    {
        WriteInt32(target, ref offset, value?.Length ?? -1);
        if (value is not null)
        {
            WriteBytes(target, ref offset, value);
        }
    }

    private static byte[]? ReadNullableBytes(
        ReadOnlySpan<byte> source,
        ref int offset,
        int maximumLength)
    {
        var length = ReadInt32(source, ref offset);
        if (length == -1)
        {
            return null;
        }
        if (length < 0 || length > maximumLength)
        {
            throw new InvalidDataException("Invalid worker response.");
        }
        return ReadBytes(source, ref offset, length);
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> source, int offset, int length)
    {
        if (offset < 0 || length < 0 || source.Length - offset < length)
        {
            throw new InvalidDataException("Invalid worker binary payload.");
        }
    }

    private static void Zero(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
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
