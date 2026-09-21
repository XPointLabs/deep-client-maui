using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Services;

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
            catch (Exception exception)
            {
                CrashDiagnostics.LogException("CleanApp.Startup", exception);
                await MainThread.InvokeOnMainThreadAsync(() =>
                    loading.Content = new Label
                    {
                        Padding = 24,
                        Text = "Защищённое локальное состояние не прошло проверку. Сбросьте данные приложения и повторите запуск.",
                        AutomationId = "Startup.Error"
                    });
            }
        };
        window.Destroying += async (_, _) =>
        {
            await realityTransportRuntime.DisposeAsync();
            if (services.GetService<Core.Services.IDeepAccountRuntimeAccessor>() is { } accountRuntime)
            {
                await accountRuntime.DisposeAsync();
            }
        };
        return window;
    }
}
