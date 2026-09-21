#if WINDOWS
using Microsoft.UI.Xaml;

namespace Deep.Client.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnWinUiUnhandledException;
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    private static void OnWinUiUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        CrashDiagnostics.LogException(
            "WinUI.UnhandledException",
            args.Exception,
            $"Message={args.Message}; Handled={args.Handled}");
    }
}
#endif
