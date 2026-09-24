#if !DEEP_DID2_ACCOUNT_PROBE
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui;

public sealed class App : Application
{
    private readonly IServiceProvider services;
    private readonly AuthNavigationState authNavigationState;
    private readonly IRealityTransportRuntime realityTransportRuntime;

    public App(
        IServiceProvider services,
        AuthNavigationState authNavigationState,
        IRealityTransportRuntime realityTransportRuntime)
    {
        this.services = services;
        this.authNavigationState = authNavigationState;
        this.realityTransportRuntime = realityTransportRuntime;
        Resources = DeepTheme.Create();
    }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var loading = new ContentPage
        {
            Title = "Deep",
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Image { Source = "deep_mark.png", HeightRequest = 96 },
                    new ActivityIndicator { IsRunning = true },
                    new Label
                    {
                        Text = "Открываем защищённое локальное состояние…",
                        HorizontalTextAlignment = TextAlignment.Center,
                        AutomationId = "Startup.Status"
                    }
                }
            }
        };
        var window = new Window(loading);
        window.Created += async (_, _) =>
        {
            try
            {
                await authNavigationState.InitializeAsync();
                _ = await services.GetRequiredService<DeepAccountRuntimeAccessor>()
                    .TryGetMailboxStoreAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                    window.Page = services.GetRequiredService<AppShell>());
            }
            catch (LocalStateResetRequiredException exception)
            {
                await PresentStartupFailureAsync(exception, resetRequired: true);
            }
            catch (ProtectedIdentityResetRequiredException exception)
            {
                await PresentStartupFailureAsync(exception, resetRequired: true);
            }
            catch (Exception exception)
            {
                await PresentStartupFailureAsync(exception, resetRequired: false);
            }

            async Task PresentStartupFailureAsync(Exception exception, bool resetRequired)
            {
                CrashDiagnostics.LogException("CleanApp.Startup", exception);
                await MainThread.InvokeOnMainThreadAsync(() =>
                    loading.Content = new Label
                    {
                        Padding = 24,
                        Text = resetRequired
                            ? "Защищённое локальное состояние не прошло проверку. Сохраните резервную копию сид-фразы, затем явно сбросьте данные приложения и повторите запуск."
                            : "Не удалось запустить Deep. Не удаляйте данные приложения; повторите запуск и проверьте диагностику.",
                        AutomationId = "Startup.Error"
                    });
            }
        };
        // A Window may be recreated inside the same Android process. These
        // DI singletons belong to the process, not to the Window lifetime.
        return window;
    }
}
#endif
