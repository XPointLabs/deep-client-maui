using Microsoft.Maui.Storage;

#if ANDROID
using Android.Content;
using Android.Content.PM;
using Microsoft.Maui.ApplicationModel;
#endif

namespace Deep.Client.Maui.Services;

public sealed record AppIconOption(
    string Id,
    string Title,
    string Subtitle,
    string PreviewImage);

public interface IAppIconService
{
    IReadOnlyList<AppIconOption> Options { get; }

    string SelectedIconId { get; }

    bool IsSupported { get; }

    Task ApplyFromPreferencesAsync(CancellationToken cancellationToken = default);

    Task SelectAsync(string id, CancellationToken cancellationToken = default);
}

public sealed class AppIconService : IAppIconService
{
    private const string DefaultIconId = "deep";

    private static readonly IReadOnlyList<AppIconOption> IconOptions =
    [
        new(
            DefaultIconId,
            "Deep",
            "Основная иконка мессенджера.",
            "app_icon_deep.png"),
        new(
            "deep-dark",
            "Deep Dark",
            "Тёмная иконка в палитре XPoint.",
            "app_icon_deep_dark.png"),
        new(
            "deep-light",
            "Deep Light",
            "Светлая иконка в палитре XPoint.",
            "app_icon_deep_light.png"),
        new(
            "notes",
            "Notes",
            "Маскировка под приложение для заметок.",
            "app_icon_notes.png"),
        new(
            "calculator",
            "Calculator",
            "Маскировка под калькулятор.",
            "app_icon_calculator.png")
    ];

#if ANDROID
    private static readonly IReadOnlyDictionary<string, string> AndroidAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [DefaultIconId] = "network.xpoint.deep.DeepLauncher",
        ["deep-dark"] = "network.xpoint.deep.DeepDarkLauncher",
        ["deep-light"] = "network.xpoint.deep.DeepLightLauncher",
        ["notes"] = "network.xpoint.deep.NotesLauncher",
        ["calculator"] = "network.xpoint.deep.CalculatorLauncher"
    };
#endif

    public IReadOnlyList<AppIconOption> Options => IconOptions;

    public string SelectedIconId => Normalize(Preferences.Default.Get(ClientSettingKeys.AppearanceAppIcon, DefaultIconId));

#if ANDROID
    public bool IsSupported => true;
#else
    public bool IsSupported => false;
#endif

    public Task ApplyFromPreferencesAsync(CancellationToken cancellationToken = default) =>
        ApplyAsync(SelectedIconId, closeCurrentActivity: false, cancellationToken);

    public async Task SelectAsync(string id, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(id);
        Preferences.Default.Set(ClientSettingKeys.AppearanceAppIcon, normalized);
        await ApplyAsync(normalized, closeCurrentActivity: true, cancellationToken).ConfigureAwait(false);
    }

    private static string Normalize(string? value) =>
        IconOptions.Any(option => string.Equals(option.Id, value, StringComparison.Ordinal))
            ? value!
            : DefaultIconId;

    private static Task ApplyAsync(string id, bool closeCurrentActivity, CancellationToken cancellationToken)
    {
#if ANDROID
        cancellationToken.ThrowIfCancellationRequested();
        var context = Platform.AppContext ?? throw new InvalidOperationException("Android application context is unavailable.");
        var packageManager = context.PackageManager ?? throw new InvalidOperationException("Android package manager is unavailable.");
        var selectedAlias = AndroidAliases[Normalize(id)];

        foreach (var alias in AndroidAliases.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var component = new ComponentName(context, alias);
            var desiredState = string.Equals(alias, selectedAlias, StringComparison.Ordinal)
                ? ComponentEnabledState.Enabled
                : ComponentEnabledState.Disabled;

            if (packageManager.GetComponentEnabledSetting(component) == desiredState)
            {
                continue;
            }

            packageManager.SetComponentEnabledSetting(
                component,
                desiredState,
                ComponentEnableOption.DontKillApp);
        }

        if (closeCurrentActivity)
        {
            MainThread.BeginInvokeOnMainThread(() => Platform.CurrentActivity?.FinishAffinity());
        }
#else
        _ = id;
        _ = closeCurrentActivity;
        _ = cancellationToken;
#endif
        return Task.CompletedTask;
    }
}
