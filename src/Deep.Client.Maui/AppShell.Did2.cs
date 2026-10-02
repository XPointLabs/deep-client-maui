using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.ViewModels;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;

namespace Deep.Client.Maui;

// The DID2 shell never navigates into pre-clean-break conversations.
// The diagnostic package adds proof controls, but neither package owns a V1 runtime.
public sealed partial class AppShell : ContentPage
{
    private enum WorkspaceSection { Chats, Contacts, Groups, Settings }

    private readonly DeepIdV2AccountViewModel account;
    private readonly DeepIdV2MessagingViewModel messaging;
    private Action? refreshConversationLayout;
    private Action? hideSensitive;
#if DEEP_DID2_ACCOUNT_PROBE
    private WorkspaceSection section = WorkspaceSection.Settings;
#else
    private WorkspaceSection section = WorkspaceSection.Chats;
#endif

    public AppShell(DeepIdV2AccountViewModel account, DeepIdV2MessagingViewModel messaging)
    {
        this.account = account;
        this.messaging = messaging;
        messaging.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(DeepIdV2MessagingViewModel.HasSelection)) refreshConversationLayout?.Invoke();
        };
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
        refreshConversationLayout = null;
        Content = account.Account is null ? CreateWelcome() : CreateWorkspace();
    }

    private View CreateWorkspace()
    {
        var current = account.Account!;
        var sidebar = new Border
        {
            AutomationId = "Did2Workspace.Sidebar",
            BackgroundColor = DeepTheme.Panel,
            StrokeThickness = 0,
            Padding = new Thickness(12, 18),
            Content = new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    BrandHeader(),
                    new BoxView { Color = DeepTheme.Divider, HeightRequest = 1,
                        Margin = new Thickness(0, 8) },
                    WorkspaceNavigationButton("Диалоги", WorkspaceSection.Chats,
                        "Did2Workspace.Chats"),
                    WorkspaceNavigationButton("Контакты", WorkspaceSection.Contacts,
                        "Did2Workspace.Contacts"),
                    WorkspaceNavigationButton("Группы", WorkspaceSection.Groups,
                        "Did2Workspace.Groups"),
                    WorkspaceNavigationButton("Настройки", WorkspaceSection.Settings,
                        "Did2Workspace.Settings"),
                    new Label
                    {
                        Text = current.DisplayName,
                        TextColor = DeepTheme.Secondary,
                        FontSize = 13,
                        Margin = new Thickness(10, 20, 10, 0)
                    }
                }
            }
        };
        var mobileNavigation = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            BackgroundColor = DeepTheme.Panel,
            Content = new HorizontalStackLayout
            {
                Padding = new Thickness(10, 6),
                Spacing = 8,
                Children =
                {
                    WorkspaceNavigationButton("Чаты", WorkspaceSection.Chats,
                        "Did2Workspace.MobileChats"),
                    WorkspaceNavigationButton("Контакты", WorkspaceSection.Contacts,
                        "Did2Workspace.MobileContacts"),
                    WorkspaceNavigationButton("Группы", WorkspaceSection.Groups,
                        "Did2Workspace.MobileGroups"),
                    WorkspaceNavigationButton("Профиль", WorkspaceSection.Settings,
                        "Did2Workspace.MobileSettings")
                }
            }
        };
        var sectionContent = section switch
        {
            WorkspaceSection.Chats => CreateChatsSection(),
            WorkspaceSection.Contacts => CreateContactSection(),
            WorkspaceSection.Groups => CreateUnavailableSection(
                "Page.Groups", "ГРУППЫ", "Ваши группы", "Групп пока нет",
                "Создание группы откроется после проверки доставки и смены состава на DID2-устройствах."),
            _ => CreateAccountSettings()
        };
        var root = new Grid
        {
            AutomationId = "Did2Workspace.Root",
            BackgroundColor = DeepTheme.Background,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(0)),
                new ColumnDefinition(GridLength.Star)
            },
            Children = { sidebar, mobileNavigation, sectionContent }
        };
        Grid.SetRowSpan(sidebar, 2);
        Grid.SetColumn(sectionContent, 1);
        Grid.SetRow(sectionContent, 1);
        Grid.SetColumn(mobileNavigation, 1);
        root.SizeChanged += (_, _) =>
        {
            var desktop = root.Width >= 800;
            root.ColumnDefinitions[0].Width = new GridLength(desktop ? 268 : 0);
            sidebar.IsVisible = desktop;
            mobileNavigation.IsVisible = !desktop;
            Grid.SetRow(sectionContent, desktop ? 0 : 1);
            Grid.SetRowSpan(sectionContent, desktop ? 2 : 1);
        };
        return root;
    }

    private Button WorkspaceNavigationButton(string label,
        WorkspaceSection destination, string automationId)
    {
        var selected = section == destination;
        var button = DeepTheme.SecondaryButton(label, automationId);
        button.HorizontalOptions = LayoutOptions.Fill;
        button.Padding = new Thickness(16, 8);
        button.BackgroundColor = selected ? DeepTheme.AccentMuted : DeepTheme.Panel;
        button.TextColor = selected ? DeepTheme.Text : DeepTheme.Secondary;
        button.Clicked += (_, _) =>
        {
            if (section == destination) return;
            section = destination;
            Render();
        };
        return button;
    }

    private View CreateEmptyChatsSection()
    {
        var list = new VerticalStackLayout
        {
            AutomationId = "Did2Workspace.ConversationList",
            Spacing = 12,
            Padding = new Thickness(22, 28),
            Children =
            {
                new Label { Text = "Диалоги", FontSize = 25,
                    FontAttributes = FontAttributes.Bold },
                new Label { Text = "Нет диалогов", FontSize = 17,
                    FontAttributes = FontAttributes.Bold,
                    Margin = new Thickness(0, 36, 0, 0) },
                new Label
                {
                    Text = "Переписка станет доступна после подключения проверенного DID2-транспорта.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13
                }
            }
        };
        var detail = new VerticalStackLayout
        {
            AutomationId = "Did2Workspace.EmptyConversation",
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Image { Source = "deep_mark.png", WidthRequest = 52,
                    HeightRequest = 52, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = "Deep", FontSize = 24,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = "Выберите диалог", FontSize = 13,
                    TextColor = DeepTheme.Secondary,
                    HorizontalTextAlignment = TextAlignment.Center }
            }
        };
        var canvas = new Grid
        {
            AutomationId = "Page.Conversations",
            BackgroundColor = DeepTheme.Background,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(0))
            },
            Children = { list, detail }
        };
        Grid.SetColumn(detail, 1);
        canvas.SizeChanged += (_, _) =>
        {
            var split = canvas.Width >= 650;
            canvas.ColumnDefinitions[0].Width = split
                ? new GridLength(320) : GridLength.Star;
            canvas.ColumnDefinitions[1].Width = split
                ? GridLength.Star : new GridLength(0);
            detail.IsVisible = split;
        };
        return canvas;
    }

    private static View CreateUnavailableSection(string pageId,
        string eyebrow, string heading, string emptyTitle, string explanation) =>
        Surface(pageId, eyebrow, heading, Card(new VerticalStackLayout
        {
            Spacing = 9,
            Children =
            {
                new Label { Text = emptyTitle, FontSize = 17,
                    FontAttributes = FontAttributes.Bold },
                new Label { Text = explanation, TextColor = DeepTheme.Secondary,
                    FontSize = 13 }
            }
        }));

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
                    Text = "Этот адрес останется тем же после удаления локальной копии фразы. Для добавления контактов требуется проверенная сетевая регистрация.",
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
        var contactDescriptor = new Entry
        {
            Placeholder = "Короткий Deep ID другого аккаунта",
            AutomationId = "Did2Probe.ContactDescriptor"
        };
        var exactContactDid2 = new Editor
        {
            Placeholder = "Точный публичный DID2 credential (hex)",
            AutomationId = "Did2Probe.ContactCredential",
            AutoSize = EditorAutoSizeOption.Disabled,
            HeightRequest = 120,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false
        };
        var contactStatus = Status("Did2Probe.ContactStatus");
        contactStatus.Text = "Контактный proof ещё не проверен.";
        void ContactInputChanged(object? _, TextChangedEventArgs __)
        {
            account.InvalidateContactProof();
            contactStatus.Text = "Данные контакта изменены; требуется новая проверка proof.";
        }
        contactDescriptor.TextChanged += ContactInputChanged;
        exactContactDid2.TextChanged += ContactInputChanged;
#if DEEP_DID2_PEER_FIXTURE
        var loadPeerFixture = DeepTheme.SecondaryButton(
            "Загрузить публичный тестовый контакт", "Did2Probe.LoadPeerFixture");
        loadPeerFixture.Clicked += async (_, _) =>
        {
            try
            {
                await using var fixture = await FileSystem.Current
                    .OpenAppPackageFileAsync("did2_peer_contact.txt");
                using var reader = new StreamReader(fixture);
                var descriptor = await reader.ReadLineAsync();
                var credential = await reader.ReadLineAsync();
                if (descriptor is null || credential is null ||
                    descriptor.Length is < 1 or > 256 ||
                    credential.Length != 4104 ||
                    await reader.ReadLineAsync() is not null)
                    throw new InvalidDataException(
                        "The public DID2 peer fixture is malformed.");
                contactDescriptor.Text = descriptor;
                exactContactDid2.Text = credential;
                contactStatus.Text = "Публичный тестовый контакт загружен; proof ещё не проверен.";
            }
            catch (Exception exception)
            {
                contactStatus.Text = $"Тестовый контакт отклонён: {exception.Message}";
            }
        };
#endif
        var verifyContact = DeepTheme.SecondaryButton(
            "Проверить DID2 контакта", "Did2Probe.VerifyContact");
        verifyContact.IsEnabled = false;
        var verifyNetwork = DeepTheme.SecondaryButton(
            "Проверить регистрацию DID2", "Did2Probe.VerifyNetwork");
        verifyNetwork.Clicked += async (_, _) =>
        {
            verifyNetwork.IsEnabled = false;
            networkStatus.Text = "Проверяем подписанный каталог и регистрацию…";
            account.InvalidateContactProof();
            contactStatus.Text = "После проверки сети требуется новая проверка контакта.";
            try
            {
                await account.VerifyNetworkAsync();
                networkStatus.Text = account.ErrorMessage is null &&
                                     account.IsNetworkVerified
                    ? "DID2-аккаунт зарегистрирован; текущий подписанный proof проверен и защищённое состояние сохранено."
                    : $"Не удалось проверить регистрацию: {account.ErrorMessage ?? "неизвестная ошибка"}. Локальный аккаунт сохранён.";
                verifyContact.IsEnabled = account.IsNetworkVerified;
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
#if DEEP_DID2_HTTPS_ADMISSION
                    Text = "Изолированный HTTPS-клиент: после проверки регистрации доступны контакты и текстовые сообщения. Физический E2E ещё проверяется; это не релизная сборка.",
#else
                    Text = "Изолированная диагностическая проверка через локальный туннель. Она не включает сообщения и не является релизной сборкой.",
#endif
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13
                },
                verifyNetwork,
                networkStatus
            }
        });
        network.IsVisible = account.HasNetworkAdmission;
        verifyContact.Clicked += async (_, _) =>
        {
            verifyContact.IsEnabled = false;
            contactStatus.Text = "Проверяем текущий подписанный DID2 proof контакта…";
            try
            {
                await account.VerifyContactProofAsync(
                    contactDescriptor.Text ?? string.Empty,
                    exactContactDid2.Text ?? string.Empty);
                contactStatus.Text = account.ErrorMessage is null &&
                                     account.IsContactProofVerified
                    ? "Точный DID2 контакта подтверждён текущим подписанным каталогом. Контакт ещё не добавлен и сообщения недоступны."
                    : $"Контактный proof отклонён: {account.ErrorMessage ?? "неизвестная ошибка"}.";
            }
            finally
            {
                verifyContact.IsEnabled = account.IsNetworkVerified;
            }
        };
        var contact = Card(new VerticalStackLayout
        {
            Spacing = 11,
            Children =
            {
                new Label
                {
                    Text = "Диагностика DID2 контакта",
                    FontSize = 16,
                    FontAttributes = FontAttributes.Bold
                },
                new Label
                {
                    Text = "Проверяет только привязку адреса и публичного credential к подписанному каталогу. Не принимает контакт и не открывает чат.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13
                },
                contactDescriptor,
                exactContactDid2,
#if DEEP_DID2_PEER_FIXTURE
                loadPeerFixture,
#endif
                verifyContact,
                contactStatus
            }
        });
        contact.IsVisible = account.HasContactDiscovery;
        var unavailable = new VerticalStackLayout
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = "Пока только локальный аккаунт", FontSize = 15, FontAttributes = FontAttributes.Bold },
                new Label
                {
                    Text = "Контакты, сообщения, вложения и группы недоступны до подключения проверенного DID2-транспорта. Прежний транспорт не используется.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13,
                    AutomationId = "Did2Probe.TransportUnavailable"
                }
            }
        };
        unavailable.IsVisible = !messaging.HasRuntime;
#if DEEP_DID2_ACCOUNT_PROBE || DEEP_DID2_HTTPS_ADMISSION
        // Only the isolated diagnostic package exposes this destructive
        // control. It uses the account-owned STORE-V2 reset, never OS data wipe.
        var reset = DeepTheme.SecondaryButton(
            "Сбросить тестовый аккаунт", "Did2Probe.ResetAccount");
        var resetStatus = Status("Did2Probe.ResetStatus");
        reset.Clicked += async (_, _) =>
        {
            if (account.IsBusy) return;
            hideSensitive?.Invoke();
            if (!await DisplayAlertAsync("Сброс тестового аккаунта",
                    "Будут удалены только локальный аккаунт этого диагностического приложения, его ключи и сохранённая фраза. Другие приложения и данные нод не затрагиваются. Продолжить?",
                    "Удалить тестовый аккаунт", "Отмена"))
                return;
            if (account.IsBusy) return;
            reset.IsEnabled = false;
            try
            {
                await account.ResetAccountAfterConfirmationAsync();
                if (account.ErrorMessage is null && account.Account is null)
                    Render();
                else
                    resetStatus.Text = "Сброс не завершён; новый аккаунт не создан.";
            }
            finally { reset.IsEnabled = true; }
        };
        return Surface("Page.Settings", "ПРОФИЛЬ", "Настройки аккаунта",
            profile, Card(identity), Card(recovery), network, contact,
            Card(new VerticalStackLayout
            {
                Spacing = 11,
                Children = { reset, resetStatus }
            }), Card(unavailable));
#else
        return Surface("Page.Settings", "ПРОФИЛЬ", "Настройки аккаунта",
            profile, Card(identity), Card(recovery), network, contact,
            Card(unavailable));
#endif
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
            AutomationId = pageId,
            Content = content,
            Padding = new Thickness(18, 24, 18, 24),
            BackgroundColor = DeepTheme.Background
        };
        return scroll;
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
                    new Label { Text = "DID2 · защищённый аккаунт", FontSize = 11, TextColor = DeepTheme.Tertiary }
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
