using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Pages;

namespace Deep.Client.Maui;

public partial class AppShell : Shell
{
    private readonly AuthNavigationState authNavigationState;

    public AppShell(AuthNavigationState authNavigationState)
    {
        InitializeComponent();
        this.authNavigationState = authNavigationState;

        Routing.RegisterRoute(ShellRouteCatalog.Restore, typeof(OnboardingPage));
        Routing.RegisterRoute(ShellRouteCatalog.Chat, typeof(ChatPage));
        Routing.RegisterRoute(ShellRouteCatalog.StartConversation, typeof(StartConversationPage));
        Routing.RegisterRoute(ShellRouteCatalog.NewConversation, typeof(NewConversationPage));
        Routing.RegisterRoute(ShellRouteCatalog.GroupChat, typeof(GroupChatPage));
        Routing.RegisterRoute(ShellRouteCatalog.Groups, typeof(GroupsPage));
        Routing.RegisterRoute(ShellRouteCatalog.Settings, typeof(SettingsPage));
        Routing.RegisterRoute(ShellRouteCatalog.SettingsDetail, typeof(SettingsDetailPage));

        this.authNavigationState.AuthenticationChanged += OnAuthenticationChanged;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            await authNavigationState.InitializeAsync();
            await ApplyAuthenticationStateAsync();
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("AppShell.OnLoaded", ex);
        }
    }

    private void OnAuthenticationChanged(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await ApplyAuthenticationStateAsync();
            }
            catch (Exception ex)
            {
                CrashDiagnostics.LogException("AppShell.OnAuthenticationChanged", ex);
            }
        });
    }

    private async Task ApplyAuthenticationStateAsync()
    {
        if (OnboardingTab is null || ConversationsTab is null)
        {
            return;
        }

        var authenticated = authNavigationState.IsAuthenticated;
        OnboardingTab.IsVisible = !authenticated;
        ConversationsTab.IsVisible = authenticated;
        CurrentItem = authenticated ? ConversationsTab : OnboardingTab;

        var targetRoute = authenticated
            ? $"//{ShellRouteCatalog.Conversations}"
            : $"//{ShellRouteCatalog.Onboarding}";

        var currentRoute = CurrentState?.Location?.OriginalString;
        if (string.Equals(currentRoute, targetRoute, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await GoToAsync(targetRoute);
    }
}
