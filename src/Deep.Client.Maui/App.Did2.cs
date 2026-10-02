using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui;

// Production and UAT enter the same local DID2 account owner.
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
        var reconnect = services.GetService<DeepIdV2NetworkReconnect>();
        window.Activated += (_,_) => reconnect?.Resume();
        window.Deactivated += (_,_) => reconnect?.Suspend();
        window.Destroying += (_,_) => reconnect?.Suspend();
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
                CrashDiagnostics.LogException("Did2Runtime.Startup", exception,
                    account.ErrorMessage);
                await MainThread.InvokeOnMainThreadAsync(() =>
                    window.Page = CreateRecoveryPage(window));
            }
        };
        // Android can destroy and recreate a Window without ending the app
        // process. The DI singleton remains process-scoped; disposing it here
        // makes the next Window report a false incompatible-account error.
        return window;
    }

    private ContentPage CreateRecoveryPage(Window window)
    {
#if DEEP_DID2_ACCOUNT_PROBE || DEEP_DID2_HTTPS_ADMISSION
        var status = new Label
        {
            Text = "Изолированный тестовый DID2-аккаунт не прошёл локальную проверку. Возможно, он создан до clean-break. Данные других приложений не затронуты.",
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
                    "Будут удалены только данные этого изолированного тестового DID2-клиента. Старая сид-фраза и адрес не восстановятся. Другие приложения и данные нод не затрагиваются.",
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
#else
        return new ContentPage
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = "DID2-аккаунт не прошёл проверку. Не удаляйте данные приложения; проверьте диагностику и повторите запуск.",
                        AutomationId = "Startup.Error"
                    }
                }
            }
        };
#endif
    }
}
