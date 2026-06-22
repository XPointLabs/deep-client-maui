using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.Services;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Pages;

public partial class GroupsPage : ContentPage
{
    private IDispatcherTimer? autoRefreshTimer;
    private readonly GroupsViewModel viewModel;
    private readonly INetworkStatusService networkStatusService;

    public GroupsPage(
        GroupsViewModel viewModel,
        INetworkStatusService networkStatusService)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.networkStatusService = networkStatusService;
        BindingContext = viewModel;

        networkStatusService.StatusChanged += OnNetworkStatusChanged;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateNetworkUi();
        await viewModel.RefreshAsync();
        EnsureAutoRefresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        autoRefreshTimer?.Stop();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackToConversationsAsync();
        return true;
    }

    private async void OnGroupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GroupListItem selected)
        {
            return;
        }

        var route = $"{ShellRouteCatalog.GroupChat}?groupId={Uri.EscapeDataString(selected.Id.Value)}&displayName={Uri.EscapeDataString(selected.Name)}";
        await Shell.Current.GoToAsync(route, animate: false);
    }

    private async void OnCreateGroupClicked(object? sender, EventArgs e)
    {
        var created = await viewModel.CreateGroupFromComposerAsync();
        if (created is null)
        {
            await DisplayAlertAsync("Новая группа", viewModel.ErrorMessage ?? "Введите название группы.", "OK");
            return;
        }

        KeyboardDismissal.Dismiss(GroupNameEntry);
        KeyboardDismissal.Dismiss(MemberSessionIdEntry);
        await Task.Delay(150);

        var route = $"{ShellRouteCatalog.GroupChat}?groupId={Uri.EscapeDataString(created.Id.Value)}&displayName={Uri.EscapeDataString(created.Name)}";
        await Shell.Current.GoToAsync(route, animate: false);
    }

    private void OnDraftMemberSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not GroupDraftMemberItem selected)
        {
            return;
        }

        viewModel.RemoveDraftMember(selected);

        if (sender is CollectionView collection)
        {
            collection.SelectedItem = null;
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync($"//{ShellRouteCatalog.Conversations}", animate: false);

    private void OnNetworkStatusChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(UpdateNetworkUi);
    }

    private void UpdateNetworkUi()
    {
        var connected = networkStatusService.IsConnected;
        if (NetworkBanner is not null && NetworkBannerText is not null)
        {
            NetworkBanner.IsVisible = !connected;
            NetworkBannerText.Text = networkStatusService.ConnectionLabel;
        }
    }

    private void EnsureAutoRefresh()
    {
        if (autoRefreshTimer is null)
        {
            autoRefreshTimer = Dispatcher.CreateTimer();
            autoRefreshTimer.Interval = TimeSpan.FromSeconds(6);
            autoRefreshTimer.Tick += OnAutoRefreshTick;
        }

        autoRefreshTimer.Start();
    }

    private async void OnAutoRefreshTick(object? sender, EventArgs e)
    {
        if (viewModel.IsBusy)
        {
            return;
        }

        await viewModel.RefreshAsync();
    }
}
