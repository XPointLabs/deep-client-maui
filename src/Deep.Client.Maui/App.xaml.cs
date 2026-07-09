using Deep.Client.Maui.Services;

namespace Deep.Client.Maui;

public partial class App : Application
{
    private readonly IServiceProvider services;

    public static IServiceProvider? Services { get; private set; }

    public App(IServiceProvider services)
    {
        InitializeComponent();
        this.services = services;
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
        try
        {
            var shell = services.GetRequiredService<AppShell>();
            return new(shell);
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("App.CreateWindow", ex, "Falling back to startup-safe page.");

            var fallback = new ContentPage
            {
                Title = "Deep",
                Content = new ScrollView
                {
                    Content = new VerticalStackLayout
                    {
                        Padding = new Thickness(24),
                        Spacing = 12,
                        Children =
                        {
                            new Label { Text = "Startup error", FontSize = 28, FontAttributes = FontAttributes.Bold },
                            new Label { Text = "The app failed to initialize the shell. See crash.log for details." },
                            new Label { Text = ex.Message }
                        }
                    }
                }
            };

            return new Window(fallback);
        }
    }

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
