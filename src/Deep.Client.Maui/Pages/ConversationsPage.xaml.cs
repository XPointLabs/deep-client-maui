using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Services;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Pages;

public partial class ConversationsPage : ContentPage
{
    private const string AvatarFileName = "profile-avatar.jpg";
    private IDispatcherTimer? autoSyncTimer;
    private IDispatcherTimer? incomingCallTimer;
    private readonly ConversationsViewModel viewModel;
    private readonly INetworkStatusService networkStatusService;
    private readonly IPushRegistrationCoordinator pushRegistration;
    private readonly CallSessionCoordinator callCoordinator;
    private bool hasLoaded;
    private bool pushRegistrationStarted;
    private bool checkingCalls;

    public ConversationsPage(
        ConversationsViewModel viewModel,
        INetworkStatusService networkStatusService,
        IPushRegistrationCoordinator pushRegistration,
        CallSessionCoordinator callCoordinator)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        this.pushRegistration = pushRegistration;
        this.callCoordinator = callCoordinator;
        BindingContext = viewModel;

        networkStatusService.StatusChanged += OnNetworkStatusChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        BackgroundSyncBridge.SyncScheduled += OnBackgroundSyncScheduled;
        UpdateProfileAvatarUi();
        UpdateNetworkUi();
        if (hasLoaded)
        {
            await viewModel.SyncAsync();
        }
        else
        {
            await viewModel.LoadAsync();
            hasLoaded = true;
        }

        EnsureAutoSync();
        EnsureIncomingCallPolling();
        EnsurePushRegistration();
        await CheckIncomingCallsAsync();
    }

    private void EnsurePushRegistration()
    {
        if (pushRegistrationStarted ||
            !Preferences.Default.Get(ClientSettingKeys.NotificationsFastMode, true))
        {
            return;
        }

        pushRegistrationStarted = true;
        _ = RegisterPushAsync();
    }

    private async Task RegisterPushAsync()
    {
        try
        {
            await pushRegistration.RegisterAsync();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogInfo("Push", $"Automatic registration failed: {ex.Message}");
            pushRegistrationStarted = false;
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        BackgroundSyncBridge.SyncScheduled -= OnBackgroundSyncScheduled;
        autoSyncTimer?.Stop();
        incomingCallTimer?.Stop();
    }

    private void OnBackgroundSyncScheduled()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!viewModel.IsBusy)
            {
                await viewModel.SyncAsync();
            }

        });
    }

    private async void OnConversationTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Element { BindingContext: ConversationListItem selected })
        {
            return;
        }

        viewModel.SelectedConversation = selected;
        await OpenConversationAsync(selected);
    }

    private void OnSearchClicked(object? sender, EventArgs e)
    {
        SearchEntry.IsVisible = !SearchEntry.IsVisible;
        if (SearchEntry.IsVisible)
        {
            SearchEntry.Focus();
        }
        else
        {
            viewModel.SearchQuery = string.Empty;
        }
    }

    private async void OnNewConversationClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.StartConversation);
    }

    private async void OnProfileClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync(ShellRouteCatalog.Settings);
    }

    private void UpdateProfileAvatarUi()
    {
        if (ProfileAvatarImage is null || ProfileInitialLabel is null)
        {
            return;
        }

        var avatarPath = GetAvatarPath();
        var hasAvatar = File.Exists(avatarPath);
        ProfileAvatarImage.IsVisible = hasAvatar;
        ProfileInitialLabel.IsVisible = !hasAvatar;
        ProfileAvatarImage.Source = hasAvatar ? ImageSource.FromFile(avatarPath) : null;
    }

    private static string GetAvatarPath() => Path.Combine(FileSystem.Current.AppDataDirectory, AvatarFileName);

    private static Task OpenConversationAsync(ConversationListItem selected)
    {
        var route = selected.Kind == ConversationKind.OneToOne
            ? $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Title)}"
            : $"{ShellRouteCatalog.GroupChat}?groupId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Title)}";

        return Shell.Current.GoToAsync(route, animate: false);
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateNetworkUi);
    }

    private void UpdateNetworkUi()
    {
        var connected = networkStatusService.IsConnected;
        if (ProfileStatusDot is not null)
        {
            var key = connected ? "PrimaryColor" : "DangerColor";
            var app = Application.Current;
            if (app is not null && app.Resources.TryGetValue(key, out var value) && value is Color color)
            {
                ProfileStatusDot.Background = new SolidColorBrush(color);
            }
        }

        if (NetworkBanner is not null && NetworkBannerText is not null)
        {
            NetworkBanner.IsVisible = !connected;
            NetworkBannerText.Text = networkStatusService.ConnectionLabel;
        }
    }

    private void EnsureAutoSync()
    {
        if (autoSyncTimer is null)
        {
            autoSyncTimer = Dispatcher.CreateTimer();
            autoSyncTimer.Interval = TimeSpan.FromSeconds(5);
            autoSyncTimer.Tick += OnAutoSyncTick;
        }

        autoSyncTimer.Start();
    }

    private void EnsureIncomingCallPolling()
    {
        if (incomingCallTimer is null)
        {
            incomingCallTimer = Dispatcher.CreateTimer();
            incomingCallTimer.Interval = TimeSpan.FromSeconds(1);
            incomingCallTimer.Tick += OnIncomingCallTick;
        }

        incomingCallTimer.Start();
    }

    private async void OnAutoSyncTick(object? sender, EventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        await viewModel.SyncAsync();
    }

    private async void OnIncomingCallTick(object? sender, EventArgs e)
    {
        try
        {
            await CheckIncomingCallsAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task CheckIncomingCallsAsync()
    {
        if (checkingCalls)
        {
            return;
        }

        try
        {
            checkingCalls = true;
            var offers = await callCoordinator.ReceiveIncomingOffersAsync();
            foreach (var offer in offers)
            {
                var known = viewModel.Conversations.FirstOrDefault(item => item.Id.Value == offer.RemoteParty.Value);
                var call = known is null ? offer : offer with { DisplayName = known.Title };
                var kind = call.IsVideo ? "Видеозвонок" : "Аудиозвонок";
                var accepted = await DisplayAlertAsync(
                    kind,
                    $"{call.DisplayName} звонит вам в Deep.",
                    "Ответить",
                    "Отклонить");
                if (!accepted)
                {
                    await callCoordinator.SendAsync(
                        call,
                        CallSignalType.Bye,
                        "{\"reason\":\"declined\"}");
                    continue;
                }

                await Shell.Current.GoToAsync(
                    ShellRouteCatalog.Call,
                    new ShellNavigationQueryParameters { ["call"] = call });
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or System.Net.WebException)
        {
            CrashDiagnostics.LogException("ConversationsPage.IncomingCalls", exception);
        }
        finally
        {
            checkingCalls = false;
        }
    }
}
