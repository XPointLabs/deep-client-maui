namespace Deep.Client.Maui.Services;

public sealed record RuntimeEndpoint(string Name, string Url);

public sealed record RuntimeEnvironmentOptions(
    string? StorageUrl,
    string? TransportUrl,
    string? RouterUrls,
    string? FileUrl,
    string? PushUrl,
    string? CallSignalingUrl,
    string? RegistryUrl,
    string? StakingBackendUrl,
    string? StakingPortalUrl)
{
    public IReadOnlyList<RuntimeEndpoint> ServiceEndpoints =>
        BuildEndpoints(
            ("Хранилище", StorageUrl),
            ("Транспорт", TransportUrl),
            ("Файлы", FileUrl),
            ("Router RPC", RouterUrls),
            ("Push", PushUrl),
            ("Звонки", CallSignalingUrl),
            ("Реестр нод", RegistryUrl),
            ("Бэкенд стейкинга", StakingBackendUrl),
            ("Портал стейкинга", StakingPortalUrl));

    public static RuntimeEnvironmentOptions FromRuntimeSettings(Func<string, string?> resolve) =>
        new(
            resolve(MauiProgram.StorageBaseUrlEnv),
            resolve(MauiProgram.TransportBaseUrlEnv),
            resolve(MauiProgram.RouterBaseUrlsEnv),
            resolve(MauiProgram.FileBaseUrlEnv),
            resolve(MauiProgram.PushBaseUrlEnv),
            resolve(MauiProgram.CallSignalingBaseUrlEnv),
            resolve(MauiProgram.RegistryBaseUrlEnv),
            resolve(MauiProgram.StakingBackendBaseUrlEnv),
            resolve(MauiProgram.StakingPortalBaseUrlEnv));

    private static RuntimeEndpoint[] BuildEndpoints(params (string Name, string? Url)[] endpoints) =>
        endpoints
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.Url))
            .Select(endpoint => new RuntimeEndpoint(endpoint.Name, endpoint.Url!.Trim()))
            .ToArray();
}
