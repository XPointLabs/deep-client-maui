#if DEEP_DID2_ACCOUNT_PROBE
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.ViewModels;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;

namespace Deep.Client.Maui;

// The probe deliberately has no navigation into pre-clean-break conversations.
// Its visual language is shared with the client, not its retired runtime.
public sealed class AppShell : ContentPage
{
    private readonly DeepIdV2AccountViewModel account;
    private Action? hideSensitive;

    public AppShell(DeepIdV2AccountViewModel account)
    {
        this.account = account;
        Title = "Deep";
        BackgroundColor = DeepTheme.Background;
        Render();
    }

    protected override void OnDisappearing()
    {
        hideSensitive?.Invoke();
        base.OnDisappearing();
    }

    private void Render()
    {
        hideSensitive?.Invoke();
        hideSensitive = null;
        Content = account.Account is null ? CreateWelcome() : CreateAccountSettings();
    }

    private View CreateWelcome()
    {
        var name = new Entry
        {
            Placeholder = "Ваше имя",
            AutomationId = "Welcome.DisplayName",
            Text = account.DisplayName
        };
        var status = Status("Welcome.Status");
        var create = new Button
        {
            Text = "Создать аккаунт",
            AutomationId = "Welcome.CreateAccount",
            IsEnabled = !string.IsNullOrWhiteSpace(account.DisplayName)
        };
        name.TextChanged += (_, _) =>
        {
            account.DisplayName = name.Text ?? string.Empty;
            create.IsEnabled = !string.IsNullOrWhiteSpace(account.DisplayName);
        };
        create.Clicked += async (_, _) =>
        {
            create.IsEnabled = false;
            name.IsEnabled = false;
            status.Text = "Создаём аккаунт на этом устройстве…";
            try
            {
                account.DisplayName = name.Text ?? string.Empty;
                await account.CreateAccountAsync();
                if (account.ErrorMessage is null && account.Account is not null)
                    Render();
                else
                    status.Text = "Не удалось создать локальный аккаунт. Проверьте имя и защищённое хранилище устройства.";
            }
            finally
            {
                name.IsEnabled = true;
                create.IsEnabled = account.Account is null &&
                    !string.IsNullOrWhiteSpace(account.DisplayName);
            }
        };

        var form = new VerticalStackLayout
        {
            Spacing = 16,
            Children =
            {
                new Label
                {
                    Text = "Ваш профиль",
                    FontSize = 18,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = "Для начала достаточно имени. Аккаунт создаётся локально, без подключения к сети.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 14
                },
                name,
                create,
                status
            }
        };
        var recovery = new VerticalStackLayout
        {
            Spacing = 7,
            Children =
            {
                new Label { Text = "Восстановление", FontSize = 16, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "Фраза восстановления сохранится на устройстве. Позже откройте её в настройках и сохраните отдельно в безопасном месте.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13
                }
            }
        };
        return Surface("Page.Welcome", "ДОБРО ПОЖАЛОВАТЬ", "Создайте аккаунт Deep",
            Card(form), Card(recovery));
    }

    private View CreateAccountSettings()
    {
        var current = account.Account!;
        var phrase = new Editor
        {
            AutomationId = "Settings.RecoveryPhrase",
            IsReadOnly = true,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 110,
            IsVisible = false
        };
        var status = Status("Settings.Status");
        var phraseStatus = new Label
        {
            AutomationId = "Settings.RecoveryPhraseStatus",
            Text = account.HasRetainedRecoveryPhrase
                ? "Зашифрованная копия хранится на этом устройстве."
                : "Копия удалена с устройства. Аккаунт остаётся доступным здесь.",
            TextColor = DeepTheme.Secondary,
            FontSize = 13
        };
        var reveal = DeepTheme.SecondaryButton("Показать фразу", "Settings.RevealPhrase");
        reveal.IsEnabled = account.HasRetainedRecoveryPhrase;
        var copy = new Button
        {
            Text = "Скопировать",
            AutomationId = "Settings.CopyPhrase",
            IsVisible = false
        };
        var hide = DeepTheme.SecondaryButton("Скрыть", "Settings.HidePhrase");
        hide.IsVisible = false;
        void HidePhrase()
        {
            account.HideRecoveryPhrase();
            phrase.Text = string.Empty;
            phrase.IsVisible = false;
            copy.IsVisible = false;
            hide.IsVisible = false;
            reveal.IsEnabled = account.HasRetainedRecoveryPhrase;
        }
        hideSensitive = HidePhrase;
        reveal.Clicked += async (_, _) =>
        {
            reveal.IsEnabled = false;
            await account.RevealRecoveryPhraseAsync();
            if (account.ErrorMessage is not null)
            {
                status.Text = "Фразу не удалось открыть.";
                reveal.IsEnabled = account.HasRetainedRecoveryPhrase;
                return;
            }
            if (!account.IsRecoveryPhraseRevealed)
            {
                phraseStatus.Text = "Защищённая копия уже удалена.";
                return;
            }
            phrase.Text = account.RevealedRecoveryPhrase;
            phrase.IsVisible = true;
            copy.IsVisible = true;
            hide.IsVisible = true;
            status.Text = "Сохраните 24 слова вне этого устройства. Никому их не отправляйте.";
        };
        copy.Clicked += async (_, _) =>
        {
            if (!account.IsRecoveryPhraseRevealed) return;
            try
            {
                await Clipboard.Default.SetTextAsync(account.RevealedRecoveryPhrase);
                status.Text = "Фраза скопирована. Очистите буфер обмена после сохранения.";
            }
            catch
            {
                status.Text = "Не удалось скопировать фразу. Сохраните её другим безопасным способом.";
            }
        };
        hide.Clicked += (_, _) => HidePhrase();
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
            delete.IsEnabled = false;
            HidePhrase();
            reveal.IsEnabled = false;
            await account.DeleteRecoveryPhraseAsync();
            if (account.ErrorMessage is not null)
            {
                status.Text = "Не удалось удалить защищённую копию.";
                delete.IsEnabled = account.HasRetainedRecoveryPhrase;
                reveal.IsEnabled = account.HasRetainedRecoveryPhrase;
                return;
            }
            reveal.IsEnabled = false;
            phraseStatus.Text = "Копия удалена с устройства. Аккаунт остаётся доступным здесь.";
            status.Text = "Фраза удалена с этого устройства. DID2-аккаунт сохранён.";
        };

        var profile = new VerticalStackLayout
        {
            Spacing = 13,
            Children =
            {
                new Border
                {
                    AutomationId = "Settings.Avatar",
                    WidthRequest = 72,
                    HeightRequest = 72,
                    Padding = 0,
                    StrokeThickness = 0,
                    BackgroundColor = DeepTheme.AccentMuted,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(36) },
                    HorizontalOptions = LayoutOptions.Center,
                    Content = new Label
                    {
                        Text = current.DisplayName.Trim().Length == 0
                            ? "D" : current.DisplayName.Trim()[0].ToString().ToUpperInvariant(),
                        FontSize = 28,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center,
                        VerticalTextAlignment = TextAlignment.Center
                    }
                },
                new Label
                {
                    AutomationId = "Settings.DisplayName",
                    Text = current.DisplayName,
                    FontSize = 23,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalTextAlignment = TextAlignment.Center
                }
            }
        };
        var identity = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "Постоянный Deep ID", FontSize = 16, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = current.PermanentId.CanonicalText,
                    AutomationId = "Settings.Identity",
                    LineBreakMode = LineBreakMode.CharacterWrap,
                    FontSize = 13
                },
                new Label
                {
                    Text = "Этот адрес останется тем же после удаления локальной копии фразы. Обмен контактами в этой DID2-проверке ещё не включён.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 12
                }
            }
        };
        var actions = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 10 };
        actions.Children.Add(copy);
        actions.Children.Add(hide);
        Grid.SetColumn(hide, 1);
        var recovery = new VerticalStackLayout
        {
            Spacing = 11,
            Children =
            {
                new Label { Text = "Фраза восстановления", FontSize = 16, FontAttributes = FontAttributes.Bold },
                phraseStatus,
                reveal,
                phrase,
                actions,
                delete,
                status
            }
        };
        var networkStatus = Status("Did2Probe.NetworkStatus");
        networkStatus.Text = "Сетевая DID2-регистрация ещё не проверена на этом устройстве.";
        var verifyNetwork = DeepTheme.SecondaryButton(
            "Проверить регистрацию DID2", "Did2Probe.VerifyNetwork");
        verifyNetwork.Clicked += async (_, _) =>
        {
            verifyNetwork.IsEnabled = false;
            networkStatus.Text = "Проверяем подписанный каталог и регистрацию…";
            try
            {
                await account.VerifyNetworkAsync();
                networkStatus.Text = account.ErrorMessage is null &&
                                     account.IsNetworkVerified
                    ? "DID2-аккаунт зарегистрирован; текущий подписанный proof проверен и защищённое состояние сохранено."
                    : $"Не удалось проверить регистрацию: {account.ErrorMessage ?? "неизвестная ошибка"}. Локальный аккаунт сохранён.";
            }
            finally
            {
                verifyNetwork.IsEnabled = true;
            }
        };
        var network = Card(new VerticalStackLayout
        {
            Spacing = 11,
            Children =
            {
                new Label
                {
                    Text = "Проверка сети DID2",
                    FontSize = 16,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = "Изолированная диагностическая проверка через локальный туннель. Она не включает сообщения и не является релизной сборкой.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13
                },
                verifyNetwork,
                networkStatus
            }
        });
        network.IsVisible = account.HasNetworkAdmission;
        var unavailable = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = "Пока только локальный аккаунт", FontSize = 15, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "Контакты, сообщения, вложения и группы недоступны в этой DID2-проверке. Здесь не используется прежний транспорт.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13,
                    AutomationId = "Did2Probe.TransportUnavailable"
                }
            }
        };
        return Surface("Page.Settings", "ПРОФИЛЬ", "Настройки аккаунта",
            profile, Card(identity), Card(recovery), network,
            Card(unavailable));
    }

    private static View Surface(string pageId, string eyebrow, string heading,
        params View[] sections)
    {
        var content = new VerticalStackLayout
        {
            Spacing = 18,
            MaximumWidthRequest = 610,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                BrandHeader(),
                new Label
                {
                    Text = eyebrow,
                    TextColor = DeepTheme.Accent,
                    FontSize = 11,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = heading,
                    FontSize = 27,
                    FontAttributes = FontAttributes.Bold
                },
                new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 }
            }
        };
        foreach (var section in sections)
            content.Children.Add(section);
        var scroll = new ScrollView
        {
            Content = content,
            Padding = new Thickness(18, 24, 18, 24),
            BackgroundColor = DeepTheme.Background
        };
        var sidebar = new Border
        {
            AutomationId = "Did2Probe.DesktopBrandPanel",
            BackgroundColor = DeepTheme.Panel,
            StrokeThickness = 0,
            Padding = new Thickness(28, 36),
            IsVisible = false,
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Children =
                {
                    new Image { Source = "deep_mark.png", WidthRequest = 72, HeightRequest = 72, HorizontalOptions = LayoutOptions.Start },
                    new Label { Text = "Deep", FontSize = 30, FontAttributes = FontAttributes.Bold },
                    new Label
                    {
                        Text = "Локальная проверка нового Deep ID",
                        TextColor = DeepTheme.Secondary,
                        FontSize = 15
                    },
                    new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 },
                    new Label
                    {
                        Text = "Ваш профиль и фраза восстановления принадлежат этому устройству. Сетевые возможности появятся только после завершения DID2 clean-break.",
                        TextColor = DeepTheme.Secondary,
                        FontSize = 13
                    }
                }
            }
        };
        var root = new Grid
        {
            AutomationId = pageId,
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(0)),
                new ColumnDefinition(GridLength.Star)
            },
            Children = { sidebar, scroll }
        };
        Grid.SetColumn(scroll, 1);
        root.SizeChanged += (_, _) =>
        {
            var desktop = root.Width >= 900;
            root.ColumnDefinitions[0].Width = new GridLength(desktop ? 300 : 0);
            sidebar.IsVisible = desktop;
        };
        return root;
    }

    private static View BrandHeader() => new HorizontalStackLayout
    {
        Spacing = 10,
        Children =
        {
            new Image { Source = "deep_mark.png", WidthRequest = 36, HeightRequest = 36 },
            new VerticalStackLayout
            {
                Spacing = 0,
                Children =
                {
                    new Label { Text = "Deep", FontSize = 18, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "DID2 · локальный аккаунт", FontSize = 11, TextColor = DeepTheme.Tertiary }
                }
            }
        }
    };

    private static Border Card(View content) => new()
    {
        Content = content,
        Padding = new Thickness(16),
        BackgroundColor = DeepTheme.Panel,
        Stroke = new SolidColorBrush(DeepTheme.Divider),
        StrokeThickness = 1,
        StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) }
    };

    private static Label Status(string automationId) => new()
    {
        AutomationId = automationId,
        TextColor = DeepTheme.Secondary,
        FontSize = 13,
        LineBreakMode = LineBreakMode.WordWrap
    };
}
#endif
