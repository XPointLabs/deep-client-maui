#if DEEP_DID2_ACCOUNT_PROBE
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;

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
                    window.Page = CreateRecoveryPage(window));
            }
        };
        window.Destroying += async (_, _) =>
        {
            if (services.GetService<IDeepIdV2AccountRuntimeAccessor>() is { } runtime)
                await runtime.DisposeAsync();
        };
        return window;
    }

    private ContentPage CreateRecoveryPage(Window window)
    {
        var status = new Label
        {
            Text = "Тестовый DID2-аккаунт не прошёл локальную проверку. Возможно, он создан до clean-break. Данные других приложений не затронуты.",
            AutomationId = "Startup.Error"
        };
        var reset = new Button
        {
            Text = "Сбросить тестовый DID2-аккаунт",
            AutomationId = "Startup.ResetIncompatibleDid2"
        };
        var page = new ContentPage
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children = { status, reset }
            }
        };
        reset.Clicked += async (_, _) =>
        {
            if (!await page.DisplayAlertAsync("Удалить тестовый аккаунт?",
                    "Будут удалены только данные этого отдельного DID2 probe. Старая сид-фраза и адрес не восстановятся.",
                    "Удалить", "Отмена"))
                return;
            reset.IsEnabled = false;
            try
            {
                await DeepIdV2AccountRuntimeOwner
                    .ResetIsolatedProbeAfterConfirmationAsync(
                        CancellationToken.None);
                await account.RefreshAsync();
                if (account.ErrorMessage is not null)
                    throw new InvalidOperationException(
                        "DID2 probe verification failed after explicit reset.");
                window.Page = services.GetRequiredService<AppShell>();
            }
            catch (Exception exception)
            {
                CrashDiagnostics.LogException("Did2AccountProbe.Reset", exception);
                status.Text = "Сброс тестового аккаунта не завершён. Проверьте диагностику; другие данные не затронуты.";
                reset.IsEnabled = true;
            }
        };
        return page;
    }
}
#endif
