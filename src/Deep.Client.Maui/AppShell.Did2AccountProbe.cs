#if DEEP_DID2_ACCOUNT_PROBE
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.ViewModels;
using Microsoft.Maui.ApplicationModel.DataTransfer;

namespace Deep.Client.Maui;

public sealed class AppShell : ContentPage
{
    private readonly DeepIdV2AccountViewModel account;

    public AppShell(DeepIdV2AccountViewModel account)
    {
        this.account = account;
        Title = "Deep";
        BackgroundColor = DeepTheme.Background;
        Render();
    }

    private void Render() => Content = account.Account is null
        ? CreateWelcome()
        : CreateAccountSettings();

    private View CreateWelcome()
    {
        var name = new Entry
        {
            Placeholder = "Ваше имя",
            AutomationId = "Welcome.DisplayName",
            Text = account.DisplayName
        };
        name.TextChanged += (_, _) => account.DisplayName = name.Text ?? string.Empty;
        var status = Status("Welcome.Status");
        var create = new Button
        {
            Text = "Создать аккаунт",
            AutomationId = "Welcome.CreateAccount"
        };
        create.Clicked += async (_, _) =>
        {
            create.IsEnabled = false;
            account.DisplayName = name.Text ?? string.Empty;
            await account.CreateAccountAsync();
            if (account.ErrorMessage is null && account.Account is not null)
                Render();
            else
                status.Text = "Не удалось создать DID2-аккаунт. Проверьте имя и локальное хранилище.";
            create.IsEnabled = true;
        };
        return Scroll(new VerticalStackLayout
        {
            Spacing = 14,
            MaximumWidthRequest = 420,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                Brand(),
                new Label
                {
                    Text = "Аккаунт создаётся локально в одно нажатие, без подключения к сети.",
                    TextColor = DeepTheme.Secondary,
                    HorizontalTextAlignment = TextAlignment.Center
                },
                name, create,
                new Label
                {
                    Text = "Фраза восстановления сохранится на устройстве. Позже сохраните её отдельно в безопасном месте.",
                    TextColor = DeepTheme.Secondary
                },
                status
            }
        });
    }

    private View CreateAccountSettings()
    {
        var current = account.Account!;
        var phrase = new Label
        {
            AutomationId = "Settings.RecoveryPhrase",
            LineBreakMode = LineBreakMode.WordWrap
        };
        var status = Status("Settings.Status");
        var reveal = new Button
        {
            Text = "Показать фразу",
            AutomationId = "Settings.RevealPhrase",
            IsEnabled = account.HasRetainedRecoveryPhrase
        };
        var copy = DeepTheme.SecondaryButton("Скопировать фразу", "Settings.CopyPhrase");
        copy.IsEnabled = false;
        reveal.Clicked += async (_, _) =>
        {
            await account.RevealRecoveryPhraseAsync();
            phrase.Text = account.RevealedRecoveryPhrase;
            copy.IsEnabled = account.IsRecoveryPhraseRevealed;
            status.Text = account.ErrorMessage is null
                ? account.IsRecoveryPhraseRevealed
                    ? "Сохраните фразу вне этого устройства."
                    : "Защищённая копия уже удалена."
                : "Фразу не удалось открыть.";
            reveal.IsEnabled = account.HasRetainedRecoveryPhrase &&
                !account.IsRecoveryPhraseRevealed;
        };
        copy.Clicked += async (_, _) =>
        {
            if (!account.IsRecoveryPhraseRevealed) return;
            await Clipboard.Default.SetTextAsync(account.RevealedRecoveryPhrase);
            status.Text = "Фраза скопирована. Очистите буфер обмена после сохранения.";
        };
        var hide = DeepTheme.SecondaryButton("Скрыть фразу", "Settings.HidePhrase");
        hide.Clicked += (_, _) =>
        {
            account.HideRecoveryPhrase();
            phrase.Text = string.Empty;
            copy.IsEnabled = false;
            reveal.IsEnabled = account.HasRetainedRecoveryPhrase;
        };
        var delete = DeepTheme.SecondaryButton(
            "Удалить фразу с устройства", "Settings.DeletePhrase");
        delete.TextColor = DeepTheme.Danger;
        delete.IsEnabled = account.HasRetainedRecoveryPhrase;
        delete.Clicked += async (_, _) =>
        {
            if (!await DisplayAlertAsync("Удалить фразу?",
                    "После удаления её нельзя будет посмотреть на этом устройстве. Сначала сохраните отдельную копию.",
                    "Удалить", "Отмена"))
                return;
            await account.DeleteRecoveryPhraseAsync();
            if (account.ErrorMessage is not null)
            {
                status.Text = "Не удалось удалить защищённую копию.";
                return;
            }
            phrase.Text = string.Empty;
            copy.IsEnabled = false;
            reveal.IsEnabled = false;
            delete.IsEnabled = false;
            status.Text = "Защищённая копия удалена с устройства. DID2-аккаунт сохранён.";
        };
        return Scroll(new VerticalStackLayout
        {
            Spacing = 16,
            MaximumWidthRequest = 560,
            Children =
            {
                Brand(),
                new Label { Text = current.DisplayName, FontSize = 25, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = current.PermanentId.CanonicalText,
                    AutomationId = "Settings.Identity",
                    LineBreakMode = LineBreakMode.CharacterWrap
                },
                new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 },
                new Label { Text = "Фраза восстановления", FontSize = 18, FontAttributes = FontAttributes.Bold },
                reveal, phrase, copy, hide, delete, status,
                new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 },
                new Label
                {
                    Text = "Контакты, сообщения, вложения и группы пока недоступны в DID2 UAT-проверке. Этот экран не подключает V1-транспорт.",
                    TextColor = DeepTheme.Secondary,
                    AutomationId = "Did2Probe.TransportUnavailable"
                }
            }
        });
    }

    private static ScrollView Scroll(View content) => new()
    {
        Content = content,
        BackgroundColor = DeepTheme.Background,
        Padding = new Thickness(16, 24, 16, 12)
    };

    private static View Brand() => new VerticalStackLayout
    {
        Spacing = 8,
        Children =
        {
            new Image { Source = "deep_mark.png", HeightRequest = 90 },
            new Label
            {
                Text = "Deep",
                FontSize = 32,
                FontAttributes = FontAttributes.Bold,
                HorizontalTextAlignment = TextAlignment.Center
            }
        }
    };

    private static Label Status(string automationId) => new()
    {
        AutomationId = automationId,
        TextColor = DeepTheme.Secondary,
        LineBreakMode = LineBreakMode.WordWrap
    };
}
#endif
