#if DEEP_DID2_ACCOUNT_PROBE
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui;

public sealed class App : Application
{
    private readonly IServiceProvider services;
    private readonly DeepIdV2AccountViewModel account;

    public App(IServiceProvider services, DeepIdV2AccountViewModel account)
    {
        this.services = services;
        this.account = account;
        Resources = DeepTheme.Create();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var loading = new ContentPage
        {
            Content = new Label
            {
                Text = "Проверяем локальный DID2-аккаунт…",
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                AutomationId = "Startup.Status"
            }
        };
        var window = new Window(loading);
        window.Created += async (_, _) =>
        {
            try
            {
                await ApprovedNativeCryptoAssetBootstrap.StageAsync(
                    CancellationToken.None);
                await account.RefreshAsync();
                if (account.ErrorMessage is not null)
                    throw new InvalidOperationException(
                        "DID2 local account verification failed.");
                await MainThread.InvokeOnMainThreadAsync(() =>
                    window.Page = services.GetRequiredService<AppShell>());
            }
            catch (Exception exception)
            {
                CrashDiagnostics.LogException("Did2AccountProbe.Startup", exception,
                    account.ErrorMessage);
                await MainThread.InvokeOnMainThreadAsync(() =>
                    loading.Content = new Label
                    {
                        Padding = 24,
                        Text = "DID2-аккаунт не прошёл локальную проверку. Не удаляйте данные приложения; проверьте диагностику.",
                        AutomationId = "Startup.Error"
                    });
            }
        };
        window.Destroying += async (_, _) =>
        {
            if (services.GetService<IDeepIdV2AccountRuntimeAccessor>() is { } runtime)
                await runtime.DisposeAsync();
        };
        return window;
    }
}
#endif
