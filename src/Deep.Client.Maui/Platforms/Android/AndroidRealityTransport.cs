#if ANDROID
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui;

internal static class AndroidRealityTransport
{
    private const string BootstrapResource = "deep.bootstrap.json";
    private static readonly object Sync = new();
    private static global::LibXray.IDialerController? dialerController;
    private static IReadOnlyList<string>? routerBaseUrls;

    public static IReadOnlyList<string> Start()
    {
        lock (Sync)
        {
            if (routerBaseUrls is not null)
            {
                return routerBaseUrls;
            }

            var bootstrap = LoadBootstrap();
            Validate(bootstrap);
            var urls = bootstrap.Seeds
                .Select(seed => $"http://127.0.0.1:{seed.LocalPort}")
                .ToArray();

            RegisterDialerController();

            if (!global::LibXray.LibXray.XrayState)
            {
                var dataDirectory = Path.Combine(FileSystem.AppDataDirectory, "xray");
                Directory.CreateDirectory(dataDirectory);
                var config = BuildXrayConfig(bootstrap.Seeds);
                var request = global::LibXray.LibXray.NewXrayRunFromJSONRequest(dataDirectory, string.Empty, config)
                    ?? throw new InvalidOperationException("libXray did not create a startup request.");
                var response = global::LibXray.LibXray.RunXrayFromJSON(request)
                    ?? throw new InvalidOperationException("libXray did not return a startup response.");
                EnsureSuccess(response);
            }

            WaitForListenersAsync(bootstrap.Seeds).GetAwaiter().GetResult();
            routerBaseUrls = urls;
            return routerBaseUrls;
        }
    }

    private static void RegisterDialerController()
    {
        dialerController ??= new AndroidXrayDialerController();
        global::LibXray.LibXray.RegisterDialerController(dialerController);
    }

    private static RealityBootstrap LoadBootstrap()
    {
        using var stream = typeof(AndroidRealityTransport).Assembly.GetManifestResourceStream(BootstrapResource)
            ?? throw new InvalidOperationException($"Embedded Reality bootstrap '{BootstrapResource}' was not found.");
        return JsonSerializer.Deserialize<RealityBootstrap>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Embedded Reality bootstrap is empty.");
    }

    private static void Validate(RealityBootstrap bootstrap)
    {
        if (bootstrap.Version != 1 || bootstrap.Seeds.Count < 3)
        {
            throw new InvalidOperationException("Production Reality bootstrap must contain at least three version-1 seeds.");
        }
        if (bootstrap.Seeds.Select(seed => seed.RouterId).Distinct(StringComparer.Ordinal).Count() != bootstrap.Seeds.Count
            || bootstrap.Seeds.Select(seed => seed.LocalPort).Distinct().Count() != bootstrap.Seeds.Count)
        {
            throw new InvalidOperationException("Production Reality bootstrap contains duplicate node identities or local ports.");
        }
    }

    private static string BuildXrayConfig(IReadOnlyList<RealitySeed> seeds)
    {
        var inbounds = seeds.Select((seed, index) => new
        {
            tag = $"xpoint-seed-{index + 1}-in",
            listen = "127.0.0.1",
            port = seed.LocalPort,
            protocol = "dokodemo-door",
            settings = new { address = "127.0.0.1", port = 8080, network = "tcp" }
        }).ToArray();

        var outbounds = seeds.Select((seed, index) => new
        {
            tag = $"xpoint-seed-{index + 1}-out",
            protocol = "vless",
            settings = new
            {
                vnext = new[]
                {
                    new
                    {
                        address = seed.OriginIp,
                        port = seed.Port,
                        users = new[] { new { id = seed.ClientId, encryption = "none", flow = seed.Flow } }
                    }
                }
            },
            streamSettings = new
            {
                network = "tcp",
                security = "reality",
                realitySettings = new
                {
                    serverName = seed.ServerName,
                    fingerprint = seed.Fingerprint,
                    password = seed.PublicKey,
                    shortId = seed.ShortId,
                    spiderX = seed.SpiderX
                }
            }
        }).ToArray();

        var rules = seeds.Select((_, index) => new
        {
            type = "field",
            inboundTag = new[] { $"xpoint-seed-{index + 1}-in" },
            outboundTag = $"xpoint-seed-{index + 1}-out"
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            log = new { loglevel = "warning" },
            inbounds,
            outbounds,
            routing = new { domainStrategy = "AsIs", rules }
        }, JsonOptions);
    }

    private static void EnsureSuccess(string encodedResponse)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encodedResponse);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("libXray returned an invalid startup response.", exception);
        }

        using var response = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        if (!response.RootElement.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var error = response.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
            throw new InvalidOperationException($"Could not start embedded Xray: {error ?? "unknown error"}");
        }
    }

    private static async Task WaitForListenersAsync(IReadOnlyList<RealitySeed> seeds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Task.WhenAll(seeds.Select(seed => WaitForListenerAsync(seed, timeout.Token))).ConfigureAwait(false);
    }

    private static async Task WaitForListenerAsync(RealitySeed seed, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new TcpClient();
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
            try
            {
                await socket.ConnectAsync("127.0.0.1", seed.LocalPort, attemptTimeout.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException ex)
            {
                lastError = ex;
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (SocketException ex)
            {
                lastError = ex;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                lastError = ex;
                break;
            }
        }

        throw new InvalidOperationException($"Embedded Xray did not open local seed port {seed.LocalPort}.", lastError);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record RealityBootstrap(int Version, IReadOnlyList<RealitySeed> Seeds);

    private sealed record RealitySeed(
        string RouterId,
        string OriginIp,
        int Port,
        string ClientId,
        string Flow,
        string ServerName,
        string PublicKey,
        string ShortId,
        string Fingerprint,
        string SpiderX,
        int LocalPort);
}
#endif
