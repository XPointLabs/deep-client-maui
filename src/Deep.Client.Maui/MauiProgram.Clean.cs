using Deep.Client.Maui.Services;

namespace Deep.Client.Maui;

public static partial class MauiProgram
{
    public static MauiApp CreateMauiApp() => CreateDid2MauiApp();

    internal static string ResolveAppDataDirectory() => AppDataPath.Resolve();
}
