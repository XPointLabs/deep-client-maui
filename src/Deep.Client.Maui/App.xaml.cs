using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui;

public partial class App : Application
{
    private readonly IServiceProvider services;
    private readonly ClientRuntimeBootstrapper runtimeBootstrapper;

    public static IServiceProvider? Services { get; private set; }

    public App(IServiceProvider services, ClientRuntimeBootstrapper runtimeBootstrapper)
    {
        InitializeComponent();
        this.services = services;
        this.runtimeBootstrapper = runtimeBootstrapper;
        Services = services;

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        CrashDiagnostics.LogInfo("Startup", $"Crash log file: {CrashDiagnostics.LogPath}");
        services.GetService<IAppearanceService>()?.ApplyFromPreferences();
        _ = services.GetService<IAppIconService>()?.ApplyFromPreferencesAsync();
        services.GetService<IPrivacyScreenService>()?.ApplyFromPreferences();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var startupPage = CreateStartupPage();
        var window = new Window(startupPage.Page)
        {
            Title = "Deep"
        };
        startupPage.RetryButton.Clicked += async (_, _) =>
        {
            if (!startupPage.RetryButton.IsEnabled)
            {
                return;
            }

            await InitializeWindowAsync(window, startupPage).ConfigureAwait(true);
        };

        _ = InitializeWindowAsync(window, startupPage);
        return window;
    }

    private async Task InitializeWindowAsync(Window window, StartupPage startupPage)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await UpdateStartupPageAsync(
            startupPage,
            "Запуск Deep...",
            string.Empty,
            retryEnabled: false,
            activityRunning: true).ConfigureAwait(false);

        try
        {
            await runtimeBootstrapper.InitializeAsync().ConfigureAwait(false);
            await services.GetRequiredService<AuthNavigationState>()
                .InitializeAsync()
                .ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (window.Page == startupPage.Page)
                {
                    window.Page = services.GetRequiredService<AppShell>();
                    CrashDiagnostics.LogInfo("Perf.Startup", $"RuntimeReady elapsedMs={stopwatch.ElapsedMilliseconds}");
                }
            });
        }
        catch (OperationCanceledException)
        {
            await UpdateStartupPageAsync(
                startupPage,
                "Запуск отменён",
                "Повторите попытку запуска.",
                retryEnabled: true,
                activityRunning: false).ConfigureAwait(false);
            CrashDiagnostics.LogInfo("App.InitializeWindow", "Runtime initialization was cancelled.");
        }
        catch (Exception ex)
        {
            await UpdateStartupPageAsync(
                startupPage,
                "Не удалось запустить Deep",
                "Проверьте подключение и повторите попытку.",
                retryEnabled: true,
                activityRunning: false).ConfigureAwait(false);
            CrashDiagnostics.LogException("App.InitializeWindow", ex, "Startup can be retried from the startup page.");
        }
        finally
        {
            try
            {
                await MainThread.InvokeOnMainThreadAsync(() => startupPage.Activity.IsRunning = false)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CrashDiagnostics.LogException("App.StartupPage", ex, "Could not stop startup activity indicator.");
            }
        }
    }

    private static async Task UpdateStartupPageAsync(
        StartupPage startupPage,
        string status,
        string error,
        bool retryEnabled,
        bool activityRunning)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                startupPage.Status.Text = status;
                startupPage.Error.Text = error;
                startupPage.RetryButton.IsEnabled = retryEnabled;
                startupPage.Activity.IsRunning = activityRunning;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("App.StartupPage", ex, "Could not update startup status page.");
        }
    }

    private static StartupPage CreateStartupPage()
    {
        var status = new Label
        {
            Text = "Подготовка запуска...",
            FontSize = 16,
            HorizontalTextAlignment = TextAlignment.Center
        };
        var error = new Label
        {
            Text = string.Empty,
            LineBreakMode = LineBreakMode.WordWrap
        };
        var activity = new ActivityIndicator
        {
            IsVisible = true,
            IsRunning = false,
            WidthRequest = 28,
            HeightRequest = 28,
            HorizontalOptions = LayoutOptions.Center
        };
        activity.SetDynamicResource(ActivityIndicator.ColorProperty, "PrimaryColor");
        var retryButton = new Button
        {
            Text = "Повторить",
            IsEnabled = false
        };

        var page = new ContentPage
        {
            Title = "Deep",
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(24),
                    Spacing = 18,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new Image
                        {
                            Source = "deep_mark.png",
                            WidthRequest = 112,
                            HeightRequest = 112,
                            Aspect = Aspect.AspectFit
                        },
                        status,
                        error,
                        activity,
                        retryButton
                    }
                }
            }
        };
        page.SetDynamicResource(VisualElement.BackgroundColorProperty, "PageBackground");
        return new StartupPage(page, status, error, activity, retryButton);
    }

    private sealed record StartupPage(
        ContentPage Page,
        Label Status,
        Label Error,
        ActivityIndicator Activity,
        Button RetryButton);

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        CrashDiagnostics.LogException(
            "AppDomain.CurrentDomain.UnhandledException",
            args.ExceptionObject as Exception,
            $"IsTerminating={args.IsTerminating}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        CrashDiagnostics.LogException("TaskScheduler.UnobservedTaskException", args.Exception);
    }
}
