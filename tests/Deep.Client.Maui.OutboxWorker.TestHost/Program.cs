using System.Buffers.Binary;
using Deep.Client.Maui.Outbox;
using Deep.Client.Shared.Services;

var mode = args
    .FirstOrDefault(static argument => argument.StartsWith("--mode=", StringComparison.Ordinal))
    ?["--mode=".Length..] ?? "durable";
var pidFile = args
    .FirstOrDefault(static argument => argument.StartsWith("--pid-file=", StringComparison.Ordinal))
    ?["--pid-file=".Length..];
if (!string.IsNullOrWhiteSpace(pidFile))
{
    await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString());
}

using var context = await ExternalTransportOutboxWorkerProtocol.ReadRequestAsync(
    Console.OpenStandardInput());
if (context.Request.Operation == ExternalTransportOutboxWorkerOperation.Probe)
{
    await ReplyAsync(context, durable: false);
    return;
}

switch (mode)
{
    case "hang":
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return;
    case "crash":
        Environment.Exit(23);
        return;
    case "malformed":
    {
        var output = Console.OpenStandardOutput();
        var bytes = "{}"u8.ToArray();
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await output.WriteAsync(prefix);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return;
    }
    case "rejected":
        await ExternalTransportOutboxWorkerProtocol.WriteResponseAsync(
            Console.OpenStandardOutput(),
            new(
                ExternalTransportOutboxWorkerProtocol.Version,
                context.Request.Nonce,
                Success: false,
                Disposition: null,
                AcceptedEvidence: null,
                DurableEvidence: null,
                ErrorCode: "ADAPTER_UNAVAILABLE"),
            context.SessionKey);
        return;
    case "accepted":
        await ReplyAsync(context, durable: false);
        return;
    default:
        await ReplyAsync(context, durable: true);
        return;
}

static Task ReplyAsync(ExternalTransportOutboxWorkerRequestContext context, bool durable) =>
    ExternalTransportOutboxWorkerProtocol.WriteResponseAsync(
        Console.OpenStandardOutput(),
        new(
            ExternalTransportOutboxWorkerProtocol.Version,
            context.Request.Nonce,
            Success: true,
            durable
                ? TransportOutboxAdapterDisposition.Durable
                : TransportOutboxAdapterDisposition.Accepted,
            AcceptedEvidence: [0xA1, 0xA2],
            DurableEvidence: durable ? [0xD1, 0xD2] : null,
            ErrorCode: null),
        context.SessionKey);
