using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Services;
using Deep.Client.Shared.State;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Storage;
using ContactRecord = Deep.Client.Shared.Domain.Contact;

namespace Deep.Client.Maui.Pages;

public partial class SettingsDetailPage : ContentPage, IQueryAttributable
{
    private static readonly Uri XPointUrl = new("https://xpoint.network/");
    private static readonly Uri XPointSupportUrl = new("https://xpoint.network/");
    private static readonly Uri XPointFaqUrl = new("https://xpoint.network/");
    private readonly ClientRuntime runtime;
    private readonly INetworkStatusService networkStatusService;
    private readonly IPushRegistrationCoordinator pushRegistration;
    private readonly ITransportRouteProvider routeProvider;
    private readonly RuntimeEnvironmentOptions environment;
    private readonly IPrivacyScreenService privacyScreen;
    private readonly IAppLockService appLock;
    private readonly IAppearanceService appearance;
    private readonly IAppIconService appIcons;
    private readonly IIpCountryLookup ipCountryLookup;
    private string section = "help";

    public SettingsDetailPage(
        ClientRuntime runtime,
        INetworkStatusService networkStatusService,
        IPushRegistrationCoordinator pushRegistration,
        ITransportRouteProvider routeProvider,
        RuntimeEnvironmentOptions environment,
        IPrivacyScreenService privacyScreen,
        IAppLockService appLock,
        IAppearanceService appearance,
        IAppIconService appIcons,
        IIpCountryLookup ipCountryLookup)
    {
        InitializeComponent();
        this.runtime = runtime;
        this.networkStatusService = networkStatusService;
        this.pushRegistration = pushRegistration;
        this.routeProvider = routeProvider;
        this.environment = environment;
        this.privacyScreen = privacyScreen;
        this.appLock = appLock;
        this.appearance = appearance;
        this.appIcons = appIcons;
        this.ipCountryLookup = ipCountryLookup;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("section", out var raw) && raw is not null)
        {
            section = raw.ToString()?.Trim().ToLowerInvariant() ?? section;
        }

        BuildSection();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        BuildSection();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackAsync();
        return true;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackAsync();
    }

    private static Task NavigateBackAsync() => Shell.Current.GoToAsync("..");

    private void BuildSection()
    {
        if (ContentStack is null)
        {
            return;
        }

        ContentStack.Children.Clear();
        StatusLabel.Text = string.Empty;

        switch (section)
        {
            case "path":
                BuildPathSection();
                break;
            case "offline-update":
                BuildOfflineUpdateSection();
                break;
            case "privacy":
                BuildPrivacySection();
                break;
            case "notifications":
                BuildNotificationsSection();
                break;
            case "conversations":
                BuildConversationsSection();
                break;
            case "appearance":
                BuildAppearanceSection();
                break;
            case "message-requests":
                BuildMessageRequestsSection();
                break;
            default:
                BuildHelpSection();
                break;
        }
    }

    private void BuildPathSection()
    {
        TitleLabel.Text = "Транспорты";
        BuildTransportSection(
            "Подключение проверяется",
            "Получаем текущий маршрут клиента.",
        [
            new("Вы", null, true),
            new("Маршрут строится", "Получаем текущий маршрут клиента.", false),
            new("Назначение", null, true)
        ]);

        _ = LoadPathAsync();
    }

    private void BuildOfflineUpdateSection()
    {
        TitleLabel.Text = "Офлайн-обновление";
        ContentStack.Children.Add(CreateCategory(
            "Проверка пакета",
            CreateRow(
                "Функция выключена",
                "В этой сборке не задан отдельный доверенный корень обновлений. " +
                "Пакет нельзя подтвердить или передать установщику без полной проверки метаданных, " +
                "SHA-256, длины и сертификата подписи Android."),
            CreateRow(
                "Безопасный режим",
                "Ошибка подписи, устаревшие или смешанные метаданные блокируют продолжение. " +
                "Кнопки обхода проверки нет."),
            CreateRow(
                "Android",
                "Передача системному установщику пока не включена в рабочую конфигурацию. " +
                "Она потребует отдельного проверенного FileProvider-контракта с одноразовым " +
                "разрешением только на чтение; автоматической установки и запроса расширенных прав нет."),
            CreateRow(
                "Windows",
                "Установка Android APK в Windows явно не поддерживается. Отдельный установщик " +
                "Windows можно включать только после самостоятельной проверки его формата, подписи и пакета."),
            CreateRow(
                "iPhone и iPad",
                "Этот поток относится только к Android APK. На устройствах Apple действуют " +
                "подпись, provisioning и поддерживаемые Apple каналы распространения; " +
                "офлайн-метаданные не обходят эти ограничения.")));
    }

    private async Task LoadPathAsync()
    {
        try
        {
            var account = await runtime.Accounts.GetActiveAccountAsync();
            var targetKey = account?.SessionId.Value ?? "local-client";
            var snapshot = routeProvider.CurrentRoute ?? await routeProvider.RefreshRouteAsync(targetKey);
            if (!string.Equals(section, "path", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var routeNodes = await BuildRouteNodesAsync(snapshot);
            BuildTransportSection(
                snapshot is null ? "Готов к подключению" : "Активен",
                snapshot is null
                    ? "Маршрут появится после первой сетевой операции."
                    : networkStatusService.ConnectionLabel,
                routeNodes);
        }
        catch (Exception)
        {
            if (!string.Equals(section, "path", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            BuildTransportSection(
                "Временно недоступен",
                "Не удалось обновить текущий маршрут.",
            [
                new("Вы", null, true),
                new("Маршрут временно недоступен", "Не удалось обновить текущий путь. Повторите попытку позже.", false),
                new("Назначение", null, true)
            ]);
        }
    }

    private void BuildPrivacySection()
    {
        TitleLabel.Text = "Конфиденциальность";
        var screenSecurityRows = new List<View>
        {
            CreateSwitchRow(
                "Защита снимков экрана",
                "Не показывать содержимое Deep в системном списке приложений и на снимках экрана.",
                ClientSettingKeys.PrivacyScreenSecurity,
                true,
                OnScreenSecurityToggledAsync)
        };

        if (appLock.IsSupported)
        {
            var appLockSubtitle = appLock.IsDeviceSecure
                ? "Требовать биометрию или PIN-код устройства при возвращении в Deep."
                : appLock.UnavailableReason ?? "Сначала настройте системную блокировку устройства.";
            screenSecurityRows.Add(CreateSwitchRow(
                "Блокировка Deep",
                appLockSubtitle,
                ClientSettingKeys.PrivacyAppLock,
                false,
                OnAppLockToggledAsync,
                isEnabled: true));
        }

        screenSecurityRows.Add(
            CreateSwitchRow(
                "Инкогнито-клавиатура",
                "Попросить клавиатуру не сохранять ввод и не обучаться на тексте сообщений.",
                ClientSettingKeys.PrivacyIncognitoKeyboard,
                true,
                _ => SetStatusAsync("Настройка применится к новым полям ввода.")));

        ContentStack.Children.Add(CreateCategory(
            "Защита экрана и клавиатуры",
            screenSecurityRows.ToArray()));

        ContentStack.Children.Add(CreateCategory(
            "Контакты",
            CreateRow("Запросы сообщений", "Открыть список входящих запросов от новых контактов.", () => NavigateToSectionAsync("message-requests")),
            CreateRow("Заблокированные контакты", "Показать контакты, которые вы заблокировали на этом устройстве.", ShowBlockedContactsAsync)));
    }

    private void BuildNotificationsSection()
    {
        TitleLabel.Text = "Уведомления";
        ContentStack.Children.Add(CreateCategory(
            "Стратегия уведомлений",
            CreateSwitchRow(
                "Push-уведомления",
                "Получать уведомления о новых сообщениях и звонках, когда Deep работает в фоне.",
                ClientSettingKeys.NotificationsFastMode,
                true,
                OnFastModeToggledAsync),
            CreateSwitchRow(
                "Текст уведомлений",
                "Показывать имя отправителя и текст сообщения в системных уведомлениях. По умолчанию Deep скрывает содержимое.",
                ClientSettingKeys.NotificationsShowPreviews,
                false)));

        ContentStack.Children.Add(CreateCategory(
            "Системные каналы",
            CreateRow("Диалоги", "Настройки системных уведомлений для личных сообщений.", () => OpenNotificationSettingsAsync("conversations")),
            CreateRow("Группы", "Настройки системных уведомлений для закрытых групп.", () => OpenNotificationSettingsAsync("groups")),
            CreateRow("Звонки", "Настройки системных уведомлений для входящих звонков.", () => OpenNotificationSettingsAsync("calls"))));
    }

    private void BuildConversationsSection()
    {
        TitleLabel.Text = "Диалоги";
        ContentStack.Children.Add(CreateCategory(
            null,
            CreateRow("Заблокированные контакты", "Показать контакты, которые вы заблокировали на этом устройстве.", ShowBlockedContactsAsync)));
    }

    private void BuildAppearanceSection()
    {
        TitleLabel.Text = "Оформление";
        ContentStack.Children.Add(CreateCategory(
            "Тема",
            CreateSwitchRow(
                "Следовать настройкам системы",
                "Автоматически переключать светлую и темную тему вместе с устройством.",
                ClientSettingKeys.AppearanceFollowSystem,
                false,
                OnAppearanceToggledAsync),
            CreateOptionRow(
                "Тема",
                "Палитра интерфейса Deep.",
                ClientSettingKeys.AppearanceTheme,
                "Тёмная",
                "Тёмная",
                "Светлая")));

        ContentStack.Children.Add(CreateCategory(
            "Акцент",
            CreateAccentRow("Голубой", "#18C8FF", ClientSettingKeys.AppearanceAccent, "Голубой"),
            CreateAccentRow("Синий", "#126DFF", ClientSettingKeys.AppearanceAccent, "Синий"),
            CreateAccentRow("Фиолетовый", "#8A5AFF", ClientSettingKeys.AppearanceAccent, "Фиолетовый")));

        if (appIcons.IsSupported)
        {
            ContentStack.Children.Add(CreateCategory(
                "Иконка приложения",
                appIcons.Options
                    .Select(CreateAppIconRow)
                    .ToArray()));
            ContentStack.Children.Add(CreateDescription("При смене иконки Android может закрыть Deep и на несколько секунд обновить ярлык на рабочем столе."));
        }
    }

    private void BuildMessageRequestsSection()
    {
        TitleLabel.Text = "Запросы сообщений";
        ContentStack.Children.Add(CreateDescription("Новые контакты попадают сюда, пока вы не примете или не удалите запрос."));
        ContentStack.Children.Add(CreateCategory(
            null,
            CreateRow("Ожидающие запросы", "Загружаем локальный список запросов...")));

        _ = LoadMessageRequestsAsync();
    }

    private async Task LoadMessageRequestsAsync()
    {
        var pending = new List<ContactRecord>();
        await foreach (var contact in ((IContactRepository)runtime.Store).ListAsync())
        {
            if (!contact.IsApproved && !contact.IsBlocked)
            {
                pending.Add(contact);
            }
        }

        if (!string.Equals(section, "message-requests", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ContentStack.Children.Clear();
        ContentStack.Children.Add(CreateDescription("Новые контакты попадают сюда, пока вы не примете или не удалите запрос."));

        if (pending.Count == 0)
        {
            ContentStack.Children.Add(CreateCategory(
                null,
                CreateRow("Нет запросов", "Когда новый контакт напишет вам впервые, запрос появится на этом экране.")));
            return;
        }

        ContentStack.Children.Add(CreateCategory(
            null,
            pending
                .OrderByDescending(contact => contact.UpdatedAt)
                .Select(contact => CreateRow(
                    contact.DisplayName ?? ShortId(contact.Id.Value),
                    contact.Id.Value,
                    () => ShowMessageRequestActionsAsync(contact)))
                .ToArray()));
    }

    private void BuildHelpSection()
    {
        TitleLabel.Text = "Помощь";
        ContentStack.Children.Add(CreateCategory(
            null,
            CreateRow("Сообщить об ошибке", "Скопировать диагностические данные приложения.", CopyDiagnosticsAsync),
            CreateRow("Помогите перевести Deep", "Открыть сайт XPoint Network.", () => OpenAsync(XPointUrl)),
            CreateRow("Поделиться отзывом", "Открыть сайт XPoint Network.", () => OpenAsync(XPointUrl)),
            CreateRow("FAQ", "Открыть справочный раздел.", () => OpenAsync(XPointFaqUrl)),
            CreateRow("Поддержка", "Открыть страницу поддержки.", () => OpenAsync(XPointSupportUrl))));

        ContentStack.Children.Add(CreateCategory(
            null,
            CreateRow("Версия приложения", $"Deep {AppInfo.Current.VersionString}"),
            CreateRow("Сеть", "XPoint Network")));
    }

    private Border CreateCategory(string? title, params View[] rows)
    {
        var outer = new VerticalStackLayout { Spacing = 7 };
        if (!string.IsNullOrWhiteSpace(title))
        {
            outer.Children.Add(new Label
            {
                Text = title.ToUpperInvariant(),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = ColorResource("TextSecondary", Colors.Gray),
                Margin = new Thickness(14, 0, 14, -1)
            });
        }

        outer.Children.Add(CreatePanel(rows));
        return new Border
        {
            Background = Colors.Transparent,
            StrokeThickness = 0,
            Padding = 0,
            Content = outer
        };
    }

    private Border CreateDescription(string text) =>
        new()
        {
            Background = new SolidColorBrush(ColorResource("PageBackground", Colors.Black)),
            StrokeThickness = 0,
            Padding = new Thickness(4, 0, 4, 2),
            Content = new Label
            {
                Text = text,
                FontSize = 14,
                TextColor = ColorResource("TextSecondary", Colors.Gray),
                LineBreakMode = LineBreakMode.WordWrap
            }
        };

    private void BuildTransportSection(
        string xpointStatus,
        string xpointDetail,
        IReadOnlyList<PathNodeDisplay> routeNodes)
    {
        ContentStack.Children.Clear();
        ContentStack.Children.Add(CreatePathIntro());
        ContentStack.Children.Add(CreateTransportCard(
            "XPoint Network",
            xpointStatus,
            xpointDetail,
            "Многоскачковый маршрут через проверенные сервисные ноды.",
            true));
        ContentStack.Children.Add(CreateCategory(
            "Маршрут XPoint Network",
            CreatePathGraph(routeNodes)));
        ContentStack.Children.Add(CreateCategory(
            "Параметры XPoint Network",
            CreateSwitchRow(
                "Обход системного VPN",
                "Разрешить встроенному Xray подключаться к нодам напрямую, если внешний VPN мешает работе Deep.",
                ClientSettingKeys.NetworkBypassSystemVpn,
                false)));
        ContentStack.Children.Add(CreateTransportCard(
            "Direct P2P",
            "Не включён",
            "Wi-Fi и Bluetooth пока не активированы в production-пути.",
            "Deep не будет имитировать прямое соединение: транспорт появится здесь только после полной проверки радио, криптографии и физического E2E.",
            false));
    }

    private Border CreatePathIntro() =>
        new()
        {
            Background = Colors.Transparent,
            StrokeThickness = 0,
            Padding = new Thickness(18, 14, 18, 18),
            Content = new Label
            {
                Text = "Deep может использовать несколько транспортов. Здесь показаны их честное состояние и фактический путь текущего соединения.",
                FontSize = 14,
                TextColor = ColorResource("TextSecondary", Colors.Gray),
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap
            }
        };

    private Border CreateTransportCard(
        string title,
        string status,
        string detail,
        string description,
        bool isAvailable)
    {
        var header = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            ],
            ColumnSpacing = 12
        };
        header.Children.Add(new Label
        {
            Text = title,
            FontSize = 17,
            FontAttributes = FontAttributes.Bold,
            VerticalTextAlignment = TextAlignment.Center
        });

        var badge = new Border
        {
            Padding = new Thickness(10, 4),
            StrokeThickness = 0,
            Background = new SolidColorBrush(isAvailable
                ? ColorResource("PrimaryColor", Colors.Cyan).WithAlpha(0.16f)
                : ColorResource("TextSecondary", Colors.Gray).WithAlpha(0.12f)),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            Content = new Label
            {
                Text = status,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                TextColor = isAvailable
                    ? ColorResource("PrimaryColor", Colors.Cyan)
                    : ColorResource("TextSecondary", Colors.Gray)
            }
        };
        Grid.SetColumn(badge, 1);
        header.Children.Add(badge);

        return new Border
        {
            Background = new SolidColorBrush(ColorResource("PanelBackground", Colors.Black)),
            Stroke = Brush.Transparent,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            Padding = new Thickness(16, 14),
            Content = new VerticalStackLayout
            {
                Spacing = 7,
                Children =
                {
                    header,
                    new Label
                    {
                        Text = detail,
                        FontSize = 14,
                        LineBreakMode = LineBreakMode.WordWrap
                    },
                    new Label
                    {
                        Text = description,
                        FontSize = 13,
                        TextColor = ColorResource("TextSecondary", Colors.Gray),
                        LineBreakMode = LineBreakMode.WordWrap
                    }
                }
            }
        };
    }

    private Border CreatePanel(params View[] rows)
    {
        var stack = new VerticalStackLayout { Spacing = 0 };
        for (var index = 0; index < rows.Length; index++)
        {
            stack.Children.Add(rows[index]);
            if (index < rows.Length - 1)
            {
                stack.Children.Add(new BoxView
                {
                    HeightRequest = 1,
                    Color = ColorResource("DividerColor", Colors.DimGray),
                    Margin = new Thickness(14, 0)
                });
            }
        }

        return new Border
        {
            Background = new SolidColorBrush(ColorResource("PanelBackground", Colors.Black)),
            Stroke = Brush.Transparent,
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            Padding = 0,
            Content = stack
        };
    }

    private View CreateRow(string title, string? subtitle = null, Func<Task>? tapped = null, string? trailingText = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            ],
            Padding = new Thickness(16, 9),
            ColumnSpacing = 12,
            MinimumHeightRequest = 50
        };

        var labels = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        labels.Children.Add(new Label
        {
            Text = title,
            FontSize = 16,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            labels.Children.Add(new Label
            {
                Text = subtitle,
                FontSize = 13,
                TextColor = ColorResource("TextSecondary", Colors.Gray),
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 2
            });
        }

        grid.Children.Add(labels);

        if (!string.IsNullOrWhiteSpace(trailingText) || tapped is not null)
        {
            var trailing = new Label
            {
                Text = trailingText ?? "›",
                FontSize = trailingText is null ? 22 : 14,
                TextColor = ColorResource("TextSecondary", Colors.Gray),
                VerticalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.End,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 1
            };
            Grid.SetColumn(trailing, 1);
            grid.Children.Add(trailing);
        }

        if (tapped is not null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) => await tapped();
            grid.GestureRecognizers.Add(tap);
        }

        return grid;
    }

    private View CreateSwitchRow(
        string title,
        string subtitle,
        string key,
        bool defaultValue,
        Func<bool, Task>? toggled = null,
        bool isEnabled = true)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            ],
            Padding = new Thickness(16, 9),
            ColumnSpacing = 12,
            MinimumHeightRequest = 56
        };

        var labels = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        labels.Children.Add(new Label
        {
            Text = title,
            FontSize = 16,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        });
        labels.Children.Add(new Label
        {
            Text = subtitle,
            FontSize = 13,
            TextColor = ColorResource("TextSecondary", Colors.Gray),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2
        });
        grid.Children.Add(labels);

        var toggle = new Switch
        {
            IsToggled = Preferences.Default.Get(key, defaultValue),
            IsEnabled = isEnabled,
            VerticalOptions = LayoutOptions.Center
        };
        grid.Opacity = isEnabled ? 1 : 0.55;
        Grid.SetColumn(toggle, 1);
        toggle.Toggled += async (_, args) =>
        {
            Preferences.Default.Set(key, args.Value);
            ApplySettingSideEffects(key);
            if (toggled is not null)
            {
                await toggled(args.Value);
            }
        };
        grid.Children.Add(toggle);
        return grid;
    }

    private View CreateOptionRow(string title, string subtitle, string key, string defaultValue, params string[] options)
    {
        var current = Preferences.Default.Get(key, defaultValue);
        return CreateRow(title, subtitle, async () =>
        {
            var selected = await DisplayActionSheetAsync(title, "Отмена", null, options);
            if (!string.IsNullOrWhiteSpace(selected) && !string.Equals(selected, "Отмена", StringComparison.Ordinal))
            {
                Preferences.Default.Set(key, selected);
                ApplySettingSideEffects(key);
                BuildSection();
            }
        }, current);
    }

    private View CreateAccentRow(string title, string hex, string key, string value)
    {
        var selected = string.Equals(Preferences.Default.Get(key, "Голубой"), value, StringComparison.Ordinal);
        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            ],
            Padding = new Thickness(14, 10),
            ColumnSpacing = 12,
            MinimumHeightRequest = 52
        };

        grid.Children.Add(new Border
        {
            WidthRequest = 24,
            HeightRequest = 24,
            StrokeThickness = selected ? 2 : 0,
            Stroke = new SolidColorBrush(ColorResource("TextPrimary", Colors.White)),
            Background = new SolidColorBrush(Color.FromArgb(hex)),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            VerticalOptions = LayoutOptions.Center
        });

        var label = new Label
        {
            Text = title,
            FontSize = 16,
            VerticalOptions = LayoutOptions.Center
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (selected)
        {
            var check = new Label
            {
                Text = "✓",
                FontSize = 17,
                TextColor = ColorResource("PrimaryColor", Colors.Cyan),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            Preferences.Default.Set(key, value);
            ApplySettingSideEffects(key);
            BuildSection();
        };
        grid.GestureRecognizers.Add(tap);
        return grid;
    }

    private View CreateAppIconRow(AppIconOption option)
    {
        var selected = string.Equals(appIcons.SelectedIconId, option.Id, StringComparison.Ordinal);
        var grid = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Star },
                new ColumnDefinition { Width = GridLength.Auto }
            ],
            Padding = new Thickness(14, 10),
            ColumnSpacing = 12,
            MinimumHeightRequest = 64
        };

        grid.Children.Add(new Border
        {
            WidthRequest = 42,
            HeightRequest = 42,
            StrokeThickness = selected ? 2 : 0,
            Stroke = new SolidColorBrush(ColorResource("PrimaryColor", Colors.Cyan)),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
            Background = Colors.Transparent,
            Padding = selected ? 2 : 0,
            VerticalOptions = LayoutOptions.Center,
            Content = new Image
            {
                Source = option.PreviewImage,
                Aspect = Aspect.AspectFit
            }
        });

        var labels = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };
        labels.Children.Add(new Label
        {
            Text = option.Title,
            FontSize = 16,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        });
        labels.Children.Add(new Label
        {
            Text = option.Subtitle,
            FontSize = 13,
            TextColor = ColorResource("TextSecondary", Colors.Gray),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2
        });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        if (selected)
        {
            var check = new Label
            {
                Text = "✓",
                FontSize = 17,
                TextColor = ColorResource("PrimaryColor", Colors.Cyan),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await SelectAppIconAsync(option);
        grid.GestureRecognizers.Add(tap);
        return grid;
    }

    private void ApplySettingSideEffects(string key)
    {
        switch (key)
        {
            case ClientSettingKeys.AppearanceFollowSystem:
            case ClientSettingKeys.AppearanceTheme:
            case ClientSettingKeys.AppearanceAccent:
                appearance.ApplyFromPreferences();
                StatusLabel.Text = "Оформление применено.";
                break;
            case ClientSettingKeys.AppearanceAppIcon:
                StatusLabel.Text = "Иконка приложения обновлена.";
                break;
            case ClientSettingKeys.PrivacyAppLock:
                StatusLabel.Text = "Блокировка Deep обновлена.";
                break;
        }
    }

    private Border CreatePathGraph(IReadOnlyList<PathNodeDisplay> nodes)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 0,
            Padding = new Thickness(44, 0, 28, 10),
            HorizontalOptions = LayoutOptions.Fill
        };

        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var grid = new Grid
            {
                ColumnDefinitions =
                [
                    new ColumnDefinition { Width = 72 },
                    new ColumnDefinition { Width = GridLength.Star }
                ],
                ColumnSpacing = 8,
                Padding = new Thickness(0, 0, 0, index == nodes.Count - 1 ? 0 : 12),
                MinimumHeightRequest = index == 0 ? 56 : 76
            };

            var marker = new Grid
            {
                RowDefinitions =
                [
                    new RowDefinition { Height = 22 },
                    new RowDefinition { Height = GridLength.Star }
                ],
                WidthRequest = 72
            };
            marker.Children.Add(new Border
            {
                WidthRequest = index == 0 ? 18 : 10,
                HeightRequest = index == 0 ? 18 : 10,
                StrokeThickness = 0,
                Background = new SolidColorBrush(node.IsHealthy
                    ? ColorResource("PrimaryColor", Colors.Cyan)
                    : ColorResource("TextSecondary", Colors.Gray)),
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(index == 0 ? 9 : 5) },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            });

            if (index < nodes.Count - 1)
            {
                var line = new BoxView
                {
                    WidthRequest = 1,
                    Color = ColorResource("TextSecondary", Colors.Gray).WithAlpha(0.65f),
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Fill
                };
                Grid.SetRow(line, 1);
                marker.Children.Add(line);
            }

            grid.Children.Add(marker);

            var labels = new VerticalStackLayout { Spacing = 5, VerticalOptions = LayoutOptions.Start };
            labels.Children.Add(new Label
            {
                Text = node.Title,
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.WordWrap
            });
            if (!string.IsNullOrWhiteSpace(node.Subtitle))
            {
                labels.Children.Add(new Label
                {
                    Text = node.Subtitle,
                    FontSize = 13,
                    TextColor = ColorResource("TextSecondary", Colors.Gray),
                    LineBreakMode = LineBreakMode.WordWrap
                });
            }

            Grid.SetColumn(labels, 1);
            grid.Children.Add(labels);
            stack.Children.Add(grid);
        }

        return new Border
        {
            Background = Colors.Transparent,
            StrokeThickness = 0,
            Padding = 0,
            Content = stack
        };
    }

    private async Task<IReadOnlyList<PathNodeDisplay>> BuildRouteNodesAsync(TransportRouteSnapshot? snapshot)
    {
        var nodes = new List<PathNodeDisplay>
        {
            new("Вы", null, true)
        };

        if (snapshot is null || snapshot.Nodes.Count == 0)
        {
            nodes.Add(new PathNodeDisplay(
                "Маршрут пока не использовался",
                "Отправьте или получите сообщение, чтобы увидеть фактические сервисные ноды.",
                false));
        }
        else
        {
            foreach (var routeNode in snapshot.Nodes.OrderBy(static node => node.Index))
            {
                var role = routeNode.Index == 0 ? "Узел входа" : "Сервисная нода";
                var ip = ResolveRouteIp(routeNode);
                var country = ip is null
                    ? null
                    : await ipCountryLookup.LookupCountryAsync(ip);
                nodes.Add(new PathNodeDisplay(role, country ?? "Страна не определена", routeNode.IsReachable));
            }
        }

        nodes.Add(new PathNodeDisplay("Назначение", null, true));
        return nodes;
    }

    private static string? ResolveRouteIp(TransportRouteNode node)
    {
        if (System.Net.IPAddress.TryParse(node.PublicIp, out var publicIp))
        {
            return publicIp.ToString();
        }

        if (System.Net.IPAddress.TryParse(node.PublicHost, out var publicHost))
        {
            return publicHost.ToString();
        }

        return Uri.TryCreate(node.Endpoint, UriKind.Absolute, out var endpoint) &&
               System.Net.IPAddress.TryParse(endpoint.Host, out var endpointIp)
            ? endpointIp.ToString()
            : null;
    }

    private async Task OnFastModeToggledAsync(bool enabled)
    {
        if (enabled)
        {
            var registration = await pushRegistration.RegisterAsync();
            if (registration is null)
            {
                Preferences.Default.Set(ClientSettingKeys.NotificationsFastMode, false);
                StatusLabel.Text = "Push-сервис не вернул токен устройства.";
                BuildSection();
                return;
            }

            StatusLabel.Text = $"Push-уведомления {registration.Provider} включены.";
            return;
        }

        await pushRegistration.UnregisterAsync();
        StatusLabel.Text = "Push-уведомления отключены.";
    }

    private Task OnScreenSecurityToggledAsync(bool enabled)
    {
        privacyScreen.SetScreenSecurity(enabled);
        StatusLabel.Text = enabled
            ? "Защита снимков экрана включена."
            : "Защита снимков экрана отключена.";
        return Task.CompletedTask;
    }

    private async Task OnAppLockToggledAsync(bool enabled)
    {
        if (!enabled)
        {
            appLock.SetEnabled(false);
            StatusLabel.Text = "Блокировка Deep отключена.";
            return;
        }

        var authenticated = await appLock.AuthenticateNowAsync();
        if (!authenticated)
        {
            appLock.SetEnabled(false);
            Preferences.Default.Set(ClientSettingKeys.PrivacyAppLock, false);
            StatusLabel.Text = "Блокировка Deep не включена: подтверждение отменено.";
            BuildSection();
            return;
        }

        appLock.SetEnabled(true);
        StatusLabel.Text = "Блокировка Deep включена.";
    }

    private Task OnAppearanceToggledAsync(bool enabled)
    {
        appearance.ApplyFromPreferences();
        StatusLabel.Text = "Оформление применено.";
        return Task.CompletedTask;
    }

    private async Task SelectAppIconAsync(AppIconOption option)
    {
        if (string.Equals(appIcons.SelectedIconId, option.Id, StringComparison.Ordinal))
        {
            return;
        }

        var confirmed = await DisplayAlertAsync(
            "Сменить иконку?",
            "Android обновит ярлык Deep. Приложение может закрыться на несколько секунд при смене иконки.",
            "Сменить и закрыть",
            "Отмена");
        if (!confirmed)
        {
            return;
        }

        await appIcons.SelectAsync(option.Id);
        ApplySettingSideEffects(ClientSettingKeys.AppearanceAppIcon);
        BuildSection();
    }

    private Task SetStatusAsync(string message)
    {
        StatusLabel.Text = message;
        return Task.CompletedTask;
    }

    private Task NavigateToSectionAsync(string targetSection) =>
        Shell.Current.GoToAsync($"{ShellRouteCatalog.SettingsDetail}?section={Uri.EscapeDataString(targetSection)}");

    private async Task ShowMessageRequestActionsAsync(ContactRecord contact)
    {
        var title = contact.DisplayName ?? ShortId(contact.Id.Value);
        var selected = await DisplayActionSheetAsync(title, "Отмена", null, "Открыть диалог", "Принять запрос", "Заблокировать");
        switch (selected)
        {
            case "Открыть диалог":
                await OpenChatAsync(contact);
                break;
            case "Принять запрос":
                await runtime.Conversations.ApproveContactAsync(contact.Id);
                BuildSection();
                break;
            case "Заблокировать":
                await runtime.Conversations.SetContactBlockedAsync(contact.Id, true);
                BuildSection();
                break;
        }
    }

    private async Task ShowBlockedContactsAsync()
    {
        var blocked = new List<ContactRecord>();
        await foreach (var contact in ((IContactRepository)runtime.Store).ListAsync())
        {
            if (contact.IsBlocked)
            {
                blocked.Add(contact);
            }
        }

        ContentStack.Children.Clear();
        TitleLabel.Text = "Заблокированные";
        if (blocked.Count == 0)
        {
            ContentStack.Children.Add(CreateCategory(
                null,
                CreateRow("Нет заблокированных контактов", "Заблокированные контакты будут отображаться на этом экране.")));
            return;
        }

        ContentStack.Children.Add(CreateCategory(
            null,
            blocked
                .OrderByDescending(contact => contact.UpdatedAt)
                .Select(contact => CreateRow(
                    contact.DisplayName ?? ShortId(contact.Id.Value),
                    contact.Id.Value,
                    () => UnblockContactAsync(contact)))
                .ToArray()));
    }

    private async Task UnblockContactAsync(ContactRecord contact)
    {
        var selected = await DisplayActionSheetAsync(contact.DisplayName ?? ShortId(contact.Id.Value), "Отмена", null, "Разблокировать");
        if (!string.Equals(selected, "Разблокировать", StringComparison.Ordinal))
        {
            return;
        }

        await runtime.Conversations.SetContactBlockedAsync(contact.Id, false);
        await ShowBlockedContactsAsync();
    }

    private static Task OpenNotificationSettingsAsync(string channel)
    {
#if ANDROID
        var intent = new Android.Content.Intent(Android.Provider.Settings.ActionAppNotificationSettings);
        intent.PutExtra(Android.Provider.Settings.ExtraAppPackage, AppInfo.Current.PackageName);
        intent.AddFlags(Android.Content.ActivityFlags.NewTask);
        Android.App.Application.Context.StartActivity(intent);
#else
        AppInfo.Current.ShowSettingsUI();
#endif
        return Task.CompletedTask;
    }

    private static Task OpenChatAsync(ContactRecord contact)
    {
        var displayName = contact.DisplayName ?? contact.Id.Value;
        var route = $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(contact.Id.Value)}&displayName={Uri.EscapeDataString(displayName)}";
        return Shell.Current.GoToAsync(route);
    }

    private async Task CopyDiagnosticsAsync()
    {
        var account = await runtime.Accounts.GetActiveAccountAsync();
        var diagnostics = string.Join(Environment.NewLine,
        [
            $"Deep {AppInfo.Current.VersionString}",
            $"Аккаунт: {account?.SessionId.Value ?? "-"}",
            $"Сеть: {networkStatusService.ConnectionLabel}",
            $"Storage: {environment.StorageUrl ?? "-"}",
            $"Transport: {environment.TransportUrl ?? "-"}",
            $"Router RPC: {environment.RouterUrls ?? "-"}",
            $"Registry: {environment.RegistryUrl ?? "-"}"
        ]);

        await Clipboard.Default.SetTextAsync(diagnostics);
        StatusLabel.Text = "Диагностика скопирована.";
    }

    private async Task CopyAsync(string value)
    {
        await Clipboard.Default.SetTextAsync(value);
        StatusLabel.Text = "Скопировано.";
    }

    private async Task OpenAsync(Uri uri)
    {
        try
        {
            await Browser.Default.OpenAsync(uri, BrowserLaunchMode.SystemPreferred);
        }
        catch (Exception ex)
        {
            StatusLabel.Text = ex.Message;
        }
    }

    private static string ShortId(string value)
    {
        if (value.Length <= 12)
        {
            return value;
        }

        return $"{value[..6]}...{value[^4..]}";
    }

    private static bool TryGetUri(string? value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!);

    private static Color ColorResource(string key, Color fallback)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color)
        {
            return color;
        }

        return fallback;
    }

    private sealed record PathNodeDisplay(string Title, string? Subtitle, bool IsHealthy);

}
