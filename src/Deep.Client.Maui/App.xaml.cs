using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Persistence;

namespace Deep.Client.Maui;

public partial class App : Application
{
    private readonly IServiceProvider services;
    private readonly ClientRuntimeBootstrapper runtimeBootstrapper;
    private readonly IRealityTransportRuntime realityTransportRuntime;
    private readonly SemaphoreSlim startupGate = new(1, 1);
    private readonly object shutdownSync = new();
    private readonly StartupLocalStateResetContext localStateResetContext = new();
    private Task? realityShutdownTask;

    public static IServiceProvider? Services { get; private set; }

    public App(
        IServiceProvider services,
        ClientRuntimeBootstrapper runtimeBootstrapper,
        IRealityTransportRuntime realityTransportRuntime)
    {
        InitializeComponent();
        this.services = services;
        this.runtimeBootstrapper = runtimeBootstrapper;
        this.realityTransportRuntime = realityTransportRuntime;
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
        realityTransportRuntime.SetForeground(true);
        var startupPage = CreateStartupPage();
        var window = new Window(startupPage.Page)
        {
            Title = "Deep"
        };
        window.Destroying += OnWindowDestroying;
        startupPage.RetryButton.Clicked += async (_, _) =>
        {
            if (!startupPage.RetryButton.IsEnabled)
            {
                return;
            }

            await InitializeWindowAsync(window, startupPage).ConfigureAwait(true);
        };
        startupPage.ResetLocalStateButton.Clicked += async (_, _) =>
        {
            if (!startupPage.ResetLocalStateButton.IsEnabled)
            {
                return;
            }

            await ResetLocalStateAndRetryAsync(window, startupPage).ConfigureAwait(true);
        };

        _ = InitializeWindowAsync(window, startupPage);
        return window;
    }

    private void OnWindowDestroying(object? sender, EventArgs args)
    {
        Task shutdown;
        lock (shutdownSync)
        {
            shutdown = realityShutdownTask ??= realityTransportRuntime.DisposeAsync().AsTask();
        }

        _ = shutdown.ContinueWith(
            static completed => CrashDiagnostics.LogException(
                "App.RealityTransportShutdown",
                completed.Exception?.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task InitializeWindowAsync(Window window, StartupPage startupPage)
    {
        await startupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await InitializeWindowCoreAsync(window, startupPage).ConfigureAwait(false);
        }
        finally
        {
            startupGate.Release();
        }
    }

    private async Task InitializeWindowCoreAsync(Window window, StartupPage startupPage)
    {
        localStateResetContext.Clear();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await UpdateStartupPageAsync(
            startupPage,
            "Запуск Deep...",
            string.Empty,
            retryEnabled: false,
            resetEnabled: false,
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
                resetEnabled: false,
                activityRunning: false).ConfigureAwait(false);
            CrashDiagnostics.LogInfo("App.InitializeWindow", "Runtime initialization was cancelled.");
        }
        catch (MailboxRuntimeValidationException exception)
        {
            var presentation = exception.ToUserPresentation();
            await UpdateStartupPageAsync(
                startupPage,
                presentation.Status,
                presentation.Guidance,
                retryEnabled: true,
                resetEnabled: false,
                activityRunning: false).ConfigureAwait(false);
            await MainThread.InvokeOnMainThreadAsync(
                () => startupPage.RuntimeFailureCode.Text = exception.Code)
                .ConfigureAwait(false);
            CrashDiagnostics.LogInfo(
                "App.InitializeWindow",
                $"Mailbox runtime rejected code={exception.Code}.");
        }
        catch (LocalStateResetRequiredException exception)
        {
            localStateResetContext.Capture(exception);
            await UpdateStartupPageAsync(
                startupPage,
                "Требуется сброс локальных данных",
                "Сброс локальных данных удалит локальные сообщения и состояние. Deep создаст новое защищённое хранилище.",
                retryEnabled: false,
                resetEnabled: true,
                activityRunning: false).ConfigureAwait(false);
            CrashDiagnostics.LogInfo(
                "App.InitializeWindow",
                "Local state reset is required before startup can continue.");
        }
        catch (Exception ex)
        {
            await UpdateStartupPageAsync(
                startupPage,
                "Не удалось запустить Deep",
                "Проверьте подключение и повторите попытку.",
                retryEnabled: true,
                resetEnabled: false,
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

    private async Task ResetLocalStateAndRetryAsync(Window window, StartupPage startupPage)
    {
        await startupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var resetEnabled = await MainThread
                .InvokeOnMainThreadAsync(() => startupPage.ResetLocalStateButton.IsEnabled)
                .ConfigureAwait(false);
            if (!resetEnabled)
            {
                return;
            }

            await UpdateStartupPageAsync(
                startupPage,
                "Подтвердите сброс локальных данных",
                "Локальные сообщения и состояние будут удалены безвозвратно.",
                retryEnabled: false,
                resetEnabled: false,
                activityRunning: false).ConfigureAwait(false);
            var confirmed = await MainThread.InvokeOnMainThreadAsync(
                    () => startupPage.Page.DisplayAlertAsync(
                        "Сбросить локальные данные?",
                        "Локальные сообщения и состояние будут удалены. Это действие нельзя отменить.",
                        "Сбросить",
                        "Отмена"))
                .ConfigureAwait(false);
            if (!confirmed)
            {
                await UpdateStartupPageAsync(
                    startupPage,
                    "Требуется сброс локальных данных",
                    "Сброс локальных данных удалит локальные сообщения и состояние. Deep создаст новое защищённое хранилище.",
                    retryEnabled: false,
                    resetEnabled: true,
                    activityRunning: false).ConfigureAwait(false);
                return;
            }

            if (!localStateResetContext.TryRequestConfirmedReset())
            {
                await UpdateStartupPageAsync(
                    startupPage,
                    "Не удалось подтвердить сброс",
                    "Повторите запуск или запросите сброс локальных данных снова.",
                    retryEnabled: true,
                    resetEnabled: false,
                    activityRunning: false).ConfigureAwait(false);
                return;
            }

            await InitializeWindowCoreAsync(window, startupPage).ConfigureAwait(false);
        }
        finally
        {
            startupGate.Release();
        }
    }

    private static async Task UpdateStartupPageAsync(
        StartupPage startupPage,
        string status,
        string error,
        bool retryEnabled,
        bool resetEnabled,
        bool activityRunning)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                startupPage.Status.Text = status;
                startupPage.Error.Text = error;
                startupPage.RuntimeFailureCode.Text = string.Empty;
                startupPage.RetryButton.IsEnabled = retryEnabled;
                startupPage.ResetLocalStateButton.IsVisible = resetEnabled;
                startupPage.ResetLocalStateButton.IsEnabled = resetEnabled;
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
            AutomationId = "Startup.Status",
            FontSize = 16,
            HorizontalTextAlignment = TextAlignment.Center
        };
        var error = new Label
        {
            Text = string.Empty,
            AutomationId = "Startup.Error",
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
        var runtimeFailureCode = new Label
        {
            Text = string.Empty,
            AutomationId = "Startup.RuntimeFailureCode",
            IsVisible = true
        };
        var retryButton = new Button
        {
            Text = "Повторить",
            AutomationId = "Startup.Retry",
            IsEnabled = false
        };

        var resetLocalStateButton = new Button
        {
            Text = "Сбросить локальные данные",
            AutomationId = "StartupResetLocalStateButton",
            IsVisible = false,
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
                        runtimeFailureCode,
                        activity,
                        retryButton,
                        resetLocalStateButton
                    }
                }
            }
        };
        page.SetDynamicResource(VisualElement.BackgroundColorProperty, "PageBackground");
        return new StartupPage(
            page,
            status,
            error,
            runtimeFailureCode,
            activity,
            retryButton,
            resetLocalStateButton);
    }

    private sealed record StartupPage(
        ContentPage Page,
        Label Status,
        Label Error,
        Label RuntimeFailureCode,
        ActivityIndicator Activity,
        Button RetryButton,
        Button ResetLocalStateButton);

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
