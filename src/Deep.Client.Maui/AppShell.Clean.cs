using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Controls.Shapes;

namespace Deep.Client.Maui;

public sealed class AppShell : ContentPage
{
    private readonly AuthNavigationState auth;
    private readonly IServiceProvider services;
    private readonly ProductionContactPublicationBootstrap contactPublication;

    public AppShell(
        AuthNavigationState auth,
        IServiceProvider services,
        ProductionContactPublicationBootstrap contactPublication)
    {
        this.auth = auth;
        this.services = services;
        this.contactPublication = contactPublication;
        Title = "Deep";
        BackgroundColor = DeepTheme.Background;
        auth.AuthenticationChanged += OnAuthenticationChanged;
        Render();
        StartContactPublicationIfAuthenticated();
    }

    private void OnAuthenticationChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Render();
            StartContactPublicationIfAuthenticated();
        });

    private void StartContactPublicationIfAuthenticated()
    {
        if (!auth.IsAuthenticated)
            return;
        _ = PublishContactInBackgroundAsync();
    }

    private async Task PublishContactInBackgroundAsync()
    {
        try
        {
            _ = await contactPublication.EnsurePublishedAsync();
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("ContactV1.GenesisPublication", exception);
        }
    }

    private void Render() => Content = auth.IsAuthenticated
        ? CreateAuthenticatedView()
        : CreateWelcomeView();

    private View CreateWelcomeView()
    {
        var viewModel = services.GetRequiredService<WelcomeViewModel>();
        var name = new Entry
        {
            Placeholder = "Ваше имя",
            AutomationId = "Welcome.DisplayName"
        };
        var status = StatusLabel("Welcome.Status");
        var create = new Button
        {
            Text = "Создать аккаунт",
            AutomationId = "Welcome.CreateAccount"
        };
        create.Clicked += async (_, _) =>
        {
            create.IsEnabled = false;
            status.Text = "Создаём аккаунт локально…";
            try
            {
                viewModel.DisplayName = name.Text ?? string.Empty;
                await viewModel.CreateAccountAsync();
                status.Text = "Аккаунт создан локально";
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Не удалось создать аккаунт.");
            }
            finally
            {
                create.IsEnabled = true;
            }
        };

        var restore = DeepTheme.SecondaryButton("Восстановить по фразе", "Welcome.Restore");
        restore.Clicked += (_, _) => Content = CreateRestoreView();

        return Scroll(new VerticalStackLayout
        {
            Spacing = 14,
            MaximumWidthRequest = 420,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Image { Source = "deep_mark.png", HeightRequest = 118, WidthRequest = 118 },
                Heading("Deep"),
                new Label
                {
                    Text = "Создание локального аккаунта не требует подключения к сети.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 15,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 24)
                },
                name,
                create,
                new Label
                {
                    Text = "Фраза восстановления сохранится на этом устройстве. Её можно посмотреть и удалить в настройках.",
                    TextColor = DeepTheme.Secondary,
                    FontSize = 13
                },
                restore,
                status
            }
        });
    }

    private View CreateRestoreView()
    {
        var viewModel = services.GetRequiredService<OnboardingViewModel>();
        var name = new Entry { Placeholder = "Ваше имя", AutomationId = "Restore.DisplayName" };
        var phrase = new Editor
        {
            Placeholder = "Фраза восстановления",
            AutoSize = EditorAutoSizeOption.TextChanges,
            AutomationId = "Restore.Phrase"
        };
        var status = StatusLabel("Restore.Status");
        var restore = new Button { Text = "Восстановить", AutomationId = "Restore.Submit" };
        restore.Clicked += async (_, _) =>
        {
            restore.IsEnabled = false;
            try
            {
                viewModel.DisplayName = name.Text ?? string.Empty;
                viewModel.RecoveryPhrase = phrase.Text ?? string.Empty;
                await viewModel.RestoreAsync();
                phrase.Text = string.Empty;
                status.Text = "Аккаунт восстановлен как новое устройство";
            }
            catch (Exception exception)
            {
                phrase.Text = string.Empty;
                status.Text = UserSafeFailure(exception, "Фраза не принята.");
            }
            finally
            {
                restore.IsEnabled = true;
            }
        };
        var back = DeepTheme.SecondaryButton("Назад", "Restore.Back");
        back.Clicked += (_, _) => Render();
        return Scroll(new VerticalStackLayout
        {
            Spacing = 14,
            MaximumWidthRequest = 420,
            Children = { Heading("Восстановление"), name, phrase, restore, back, status }
        });
    }

    private View CreateAuthenticatedView()
    {
        var body = new ContentView
        {
            Content = CreateContactsView(),
            AutomationId = "Main.TabContent"
        };
        var contacts = new Button { Text = "Контакты", AutomationId = "Main.Contacts" };
        var groups = new Button { Text = "Группы", AutomationId = "Main.Groups" };
        var settings = new Button { Text = "Настройки", AutomationId = "Main.Settings" };
        foreach (var tab in new[] { contacts, groups, settings })
        {
            tab.BackgroundColor = DeepTheme.Panel;
            tab.TextColor = DeepTheme.Secondary;
            tab.CornerRadius = 0;
            tab.FontSize = 13;
        }
        contacts.TextColor = DeepTheme.Accent;
        void Select(Button selected, Func<View> content)
        {
            body.Content = content();
            foreach (var tab in new[] { contacts, groups, settings })
                tab.TextColor = ReferenceEquals(tab, selected)
                    ? DeepTheme.Accent
                    : DeepTheme.Secondary;
        }
        contacts.Clicked += (_, _) => Select(contacts, CreateContactsView);
        groups.Clicked += (_, _) => Select(groups, CreateGroupsView);
        settings.Clicked += (_, _) => Select(settings, CreateSettingsView);
        var navigation = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            Children = { contacts, groups, settings }
        };
        Grid.SetColumn(groups, 1);
        Grid.SetColumn(settings, 2);
        var layout = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto)
            },
            Children = { body, navigation },
            AutomationId = "Main.Tabs"
        };
        Grid.SetRow(navigation, 1);
        return layout;
    }

    private View CreateContactsView()
    {
        var viewModel = services.GetRequiredService<NewConversationViewModel>();
        var address = new Editor
        {
            Placeholder = "Постоянный deep1… или deepinvite:DIA1",
            AutoSize = EditorAutoSizeOption.TextChanges,
            AutomationId = "Contacts.Address"
        };
        var status = StatusLabel("Contacts.Status");
        var resolve = new Button { Text = "Добавить контакт", AutomationId = "Contacts.Resolve" };
        resolve.Clicked += async (_, _) =>
        {
            resolve.IsEnabled = false;
            try
            {
                viewModel.AddressInput = address.Text ?? string.Empty;
                await viewModel.ResolveAsync();
                status.Text = $"{viewModel.StatusTitle}\n{viewModel.StatusMessage}".Trim();
                if (viewModel.VerifiedConversation is not null)
                {
                    // A contact import is not a message send. Claiming a
                    // one-time pre-key here would leave an undispatched DPH2
                    // in the local store and strand the remote conversation.
                    viewModel.ReportDirectRuntimeUnavailable();
                    status.Text = $"{viewModel.StatusTitle}\n{viewModel.StatusMessage}".Trim();
                }
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Контакт не добавлен: проверка завершилась отказом.");
            }
            finally
            {
                resolve.IsEnabled = true;
            }
        };
        return Scroll(new VerticalStackLayout
        {
            Spacing = 18,
            Children =
            {
                BrandHeader(),
                new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 },
                new Label
                {
                    Text = "Контакты",
                    FontSize = 22,
                    FontAttributes = FontAttributes.Bold
                },
                Card(new VerticalStackLayout
                {
                    Spacing = 8,
                    Children =
                    {
                        new Label { Text = "Диалоги пока недоступны", FontSize = 17, FontAttributes = FontAttributes.Bold },
                        new Label
                        {
                            Text = "Проверка контакта доступна ниже. Отправка и получение сообщений в этой сборке ещё не подключены.",
                            TextColor = DeepTheme.Secondary,
                            FontSize = 13
                        }
                    }
                }),
                Card(new VerticalStackLayout
                {
                    Spacing = 12,
                    Children =
                    {
                        new Label { Text = "Новый контакт", FontSize = 16, FontAttributes = FontAttributes.Bold },
                        address,
                        resolve,
                        status
                    }
                })
            }
        });
    }

    private View CreateGroupsView()
    {
        var viewModel = services.GetRequiredService<GroupsViewModel>();
        var name = new Entry { Placeholder = "Название группы", AutomationId = "Groups.Name" };
        var member = new Editor
        {
            Placeholder = "deep1… участника",
            AutoSize = EditorAutoSizeOption.TextChanges,
            AutomationId = "Groups.Member"
        };
        var draft = new Label { AutomationId = "Groups.Draft" };
        var list = new Label { AutomationId = "Groups.List" };
        var status = StatusLabel("Groups.Status");
        void RefreshLabels()
        {
            draft.Text = viewModel.DraftMembers.Count == 0
                ? "Участники не добавлены"
                : string.Join(Environment.NewLine, viewModel.DraftMembers.Select(item => $"• {item.DisplayName}"));
            list.Text = viewModel.Groups.Count == 0
                ? "Групп пока нет"
                : string.Join(Environment.NewLine, viewModel.Groups.Select(item =>
                    $"{item.Name}: {item.MemberCount} участников, {item.PendingInvitationCount} ожидают принятия"));
            status.Text = viewModel.OperationStatus ?? viewModel.ComposerStatus;
        }
        var add = new Button { Text = "Проверить и добавить участника", AutomationId = "Groups.AddMember" };
        add.Clicked += async (_, _) =>
        {
            try
            {
                viewModel.MemberAddress = member.Text ?? string.Empty;
                await viewModel.AddDraftMemberAsync();
                member.Text = string.Empty;
                RefreshLabels();
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Участник не прошёл ContactV1-проверку.");
            }
        };
        var create = new Button { Text = "Создать группу", AutomationId = "Groups.Create" };
        create.Clicked += async (_, _) =>
        {
            try
            {
                viewModel.GroupName = name.Text ?? string.Empty;
                _ = await viewModel.CreateGroupFromComposerAsync();
                name.Text = string.Empty;
                RefreshLabels();
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Группа не создана.");
            }
        };
        _ = InitializeGroupsAsync();
        return Scroll(new VerticalStackLayout
        {
            Spacing = 18,
            Children =
            {
                BrandHeader(),
                new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 },
                new Label { Text = "Группы", FontSize = 22, FontAttributes = FontAttributes.Bold },
                Card(new VerticalStackLayout
                {
                    Spacing = 10,
                    Children = { list }
                }),
                Card(new VerticalStackLayout
                {
                    Spacing = 12,
                    Children =
                    {
                        new Label { Text = "Новая группа", FontSize = 16, FontAttributes = FontAttributes.Bold },
                        new Label
                        {
                            Text = "Группа создаётся локально. Доставка приглашений и сообщений пока не подключена.",
                            TextColor = DeepTheme.Secondary,
                            FontSize = 13
                        },
                        name, member, add, draft, create, status
                    }
                })
            }
        });

        async Task InitializeGroupsAsync()
        {
            try
            {
                await viewModel.InitializeAsync();
                await MainThread.InvokeOnMainThreadAsync(RefreshLabels);
            }
            catch (Exception exception)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    status.Text = UserSafeFailure(exception, "GroupV1 недоступна."));
            }
        }
    }

    private View CreateSettingsView()
    {
        var viewModel = services.GetRequiredService<SettingsViewModel>();
        var identity = new Label { AutomationId = "Settings.Identity" };
        var phrase = new Label
        {
            AutomationId = "Settings.RecoveryPhrase",
            LineBreakMode = LineBreakMode.WordWrap
        };
        var status = StatusLabel("Settings.Status");
        var reveal = new Button { Text = "Показать фразу", AutomationId = "Settings.RevealPhrase" };
        reveal.Clicked += async (_, _) =>
        {
            try
            {
                await viewModel.RevealRecoveryPhraseAsync();
                phrase.Text = viewModel.RetainedRecoveryPhrase;
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Фразу не удалось открыть.");
            }
        };
        var hide = DeepTheme.SecondaryButton("Скрыть фразу", "Settings.HidePhrase");
        hide.Clicked += (_, _) =>
        {
            viewModel.ClearRecoveryPhraseFromUi();
            phrase.Text = string.Empty;
        };
        var delete = DeepTheme.SecondaryButton("Удалить фразу с устройства", "Settings.DeletePhrase");
        delete.TextColor = DeepTheme.Danger;
        delete.Clicked += async (_, _) =>
        {
            try
            {
                await viewModel.DeleteRecoveryPhraseAsync();
                phrase.Text = string.Empty;
                status.Text = viewModel.RecoveryPhraseStatus;
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Фразу не удалось удалить.");
            }
        };
        var logout = DeepTheme.SecondaryButton("Удалить локальный аккаунт", "Settings.Logout");
        logout.TextColor = DeepTheme.Danger;
        logout.Clicked += async (_, _) =>
        {
            try
            {
                await viewModel.LogoutAsync();
            }
            catch (Exception exception)
            {
                status.Text = UserSafeFailure(exception, "Локальный аккаунт не удалён.");
            }
        };
        _ = LoadSettingsAsync();
        return Scroll(new VerticalStackLayout
        {
            Spacing = 18,
            Children =
            {
                BrandHeader(),
                new BoxView { Color = DeepTheme.Divider, HeightRequest = 1 },
                new Label { Text = "Настройки", FontSize = 22, FontAttributes = FontAttributes.Bold },
                Card(new VerticalStackLayout
                {
                    Spacing = 12,
                    Children = { new Label { Text = "Аккаунт", FontSize = 16, FontAttributes = FontAttributes.Bold }, identity }
                }),
                Card(new VerticalStackLayout
                {
                    Spacing = 12,
                    Children =
                    {
                        new Label { Text = "Восстановление", FontSize = 16, FontAttributes = FontAttributes.Bold },
                        new Label
                        {
                            Text = "Фраза хранится зашифрованно на устройстве, пока вы её не удалите.",
                            TextColor = DeepTheme.Secondary,
                            FontSize = 13
                        },
                        reveal, phrase, hide, delete
                    }
                }),
                Card(new VerticalStackLayout { Spacing = 12, Children = { logout, status } })
            }
        });

        async Task LoadSettingsAsync()
        {
            viewModel.Activate();
            try
            {
                await viewModel.LoadAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    identity.Text = $"{viewModel.AccountDisplayName}\n{viewModel.DeepId}";
                    status.Text = $"{viewModel.ConnectionStatus}\n{viewModel.RecoveryPhraseStatus}";
                });
            }
            catch (Exception exception)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    status.Text = UserSafeFailure(exception, "Настройки недоступны."));
            }
        }
    }

    private static ScrollView Scroll(View content) => new()
    {
        Content = content,
        BackgroundColor = DeepTheme.Background,
        Padding = new Thickness(16, 24, 16, 12)
    };

    private static View BrandHeader() => new HorizontalStackLayout
    {
        Spacing = 12,
        Children =
        {
            new Border
            {
                WidthRequest = 42,
                HeightRequest = 42,
                Padding = 0,
                BackgroundColor = DeepTheme.AccentMuted,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(21) },
                Content = new Label
                {
                    Text = "X",
                    FontSize = 19,
                    FontAttributes = FontAttributes.Bold,
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center
                }
            },
            new VerticalStackLayout
            {
                Spacing = 0,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label { Text = "Deep", FontSize = 20, FontAttributes = FontAttributes.Bold },
                    new Label { Text = "защищённый транспорт", FontSize = 11, TextColor = DeepTheme.Tertiary }
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

    private static Label Heading(string text) => new()
    {
        Text = text,
        FontSize = 28,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = DeepTheme.Text
    };

    private static Label StatusLabel(string automationId) => new()
    {
        AutomationId = automationId,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = DeepTheme.Secondary,
        FontSize = 13
    };

    private static string UserSafeFailure(Exception exception, string fallback) =>
        exception is ArgumentException or InvalidOperationException
            ? exception.Message
            : fallback;
}
