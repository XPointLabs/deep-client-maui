using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Pages;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Domain;
using Deep.Client.Shared.Persistence;
using Deep.Client.Shared.Platform;
using Deep.Client.Shared.State;
using Microsoft.Extensions.DependencyInjection;

namespace Deep.Client.Maui;

public partial class AppShell : Shell
{
    private readonly AuthNavigationState authNavigationState;
    private readonly ClientRuntime runtime;
    private readonly IShareExtensionBridge shareBridge;
    private readonly IServiceProvider services;
    private readonly SemaphoreSlim rootNavigationGate = new(1, 1);
    private readonly SemaphoreSlim ingressGate = new(1, 1);

    public AppShell(
        AuthNavigationState authNavigationState,
        ClientRuntime runtime,
        IShareExtensionBridge shareBridge,
        IServiceProvider services)
    {
        InitializeComponent();
        this.authNavigationState = authNavigationState;
        this.runtime = runtime;
        this.shareBridge = shareBridge;
        this.services = services;
#if WINDOWS
        ConversationsTab.ContentTemplate = new DataTemplate(
            () => services.GetRequiredService<DesktopWorkspacePage>());
#endif

        Routing.RegisterRoute(ShellRouteCatalog.Restore, typeof(OnboardingPage));
        Routing.RegisterRoute(ShellRouteCatalog.Chat, typeof(ChatPage));
        Routing.RegisterRoute(ShellRouteCatalog.ContactProfile, typeof(ContactProfilePage));
        Routing.RegisterRoute(ShellRouteCatalog.StartConversation, typeof(StartConversationPage));
        Routing.RegisterRoute(ShellRouteCatalog.NewConversation, typeof(NewConversationPage));
        Routing.RegisterRoute(ShellRouteCatalog.GroupChat, typeof(GroupChatPage));
        Routing.RegisterRoute(ShellRouteCatalog.Groups, typeof(GroupsPage));
        Routing.RegisterRoute(ShellRouteCatalog.Settings, typeof(SettingsPage));
        Routing.RegisterRoute(ShellRouteCatalog.SettingsDetail, typeof(SettingsDetailPage));
        Routing.RegisterRoute(ShellRouteCatalog.Call, typeof(CallPage));

        this.authNavigationState.AuthenticationChanged += OnAuthenticationChanged;
        NotificationActionBridge.Published += OnIngressPublished;
        MauiShareExtensionBridge.PendingSharePublished += OnIngressPublished;
        Navigated += OnNavigated;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        try
        {
            await authNavigationState.InitializeAsync();
            await ApplyAuthenticationStateAsync();
            MauiBackgroundTaskService.TryPublishForegroundCatchUp();
            await ApplyIngressAsync();
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
                await ApplyIngressAsync();
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

        await rootNavigationGate.WaitAsync();
        try
        {
            var authenticated = authNavigationState.IsAuthenticated;
#if WINDOWS
            if (!authenticated)
            {
                services.GetRequiredService<DesktopWorkspaceViewModel>().ResetSession();
            }
#endif
            var targetItem = authenticated ? ConversationsTab : OnboardingTab;
            var targetRoute = authenticated
                ? $"//{ShellRouteCatalog.Conversations}"
                : $"//{ShellRouteCatalog.Onboarding}";

            // Keep both root ShellContent instances in the visual tree. Removing the active
            // item while Android is creating its fragment can terminate the process with
            // "Content not found for active ShellItem" during a cold authenticated launch.
            CurrentItem = targetItem;

            var currentRoute = CurrentState?.Location?.OriginalString;
            if (string.Equals(currentRoute, targetRoute, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await GoToAsync(targetRoute, animate: false);
        }
        finally
        {
            rootNavigationGate.Release();
        }
    }

    private void OnIngressPublished() => QueueIngressApplication();

    private void OnNavigated(object? sender, ShellNavigatedEventArgs e) => QueueIngressApplication();

    private void QueueIngressApplication()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await ApplyIngressAsync();
            }
            catch (Exception ex)
            {
                CrashDiagnostics.LogException("AppShell.Ingress", ex);
            }
        });
    }

    private async Task ApplyIngressAsync()
    {
        if (!authNavigationState.IsInitialized || !authNavigationState.IsAuthenticated)
        {
            return;
        }

        await ingressGate.WaitAsync();
        try
        {
            foreach (var action in NotificationActionBridge.Drain())
            {
                if (await NavigateToConversationAsync(action.ConversationId))
                {
                    NotificationActionBridge.MarkHandled(action);
                }
            }

            var shares = await shareBridge.DrainPendingSharesAsync();
            foreach (var share in shares)
            {
                if (await ApplyShareToComposerAsync(share))
                {
                    MauiShareExtensionBridge.MarkHandled(share);
                    DeleteConsumedShareFiles(share);
                }
            }
        }
        catch (Exception ex)
        {
            CrashDiagnostics.LogException("AppShell.ApplyIngress", ex);
        }
        finally
        {
            ingressGate.Release();
        }
    }

    private async Task<bool> NavigateToConversationAsync(string conversationId)
    {
        ConversationId id;
        try
        {
            id = ConversationId.Parse(conversationId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (runtime.Store is not IConversationRepository conversations)
        {
            return false;
        }

        var conversation = await conversations.GetAsync(id);
        if (conversation is null)
        {
            return false;
        }

        var activationTarget = await ResolveConversationActivationTargetAsync();
        if (activationTarget is not null
            && await activationTarget.ActivateConversationAsync(id))
        {
            return true;
        }

        var route = conversation.Kind switch
        {
            ConversationKind.OneToOne => CreateOneToOneRoute(id, conversation.DisplayName),
            ConversationKind.GroupV2 => $"{ShellRouteCatalog.GroupChat}?groupId={Uri.EscapeDataString(id.Value)}&displayName={Uri.EscapeDataString(conversation.DisplayName)}",
            _ => null
        };
        if (string.IsNullOrWhiteSpace(route))
        {
            return false;
        }

        await GoToAsync(route, animate: false);
        return true;
    }

    private async Task<bool> ApplyShareToComposerAsync(SharePayload share)
    {
        if (string.IsNullOrWhiteSpace(share.Text) && share.FilePaths.Count == 0)
        {
            return true;
        }

        if (!TryGetComposer(out var chat, out var group))
        {
            var latest = (await runtime.Conversations.ListAsync())
                .OrderByDescending(static item => item.UpdatedAt)
                .FirstOrDefault();
            if (latest is null || !await NavigateToConversationAsync(latest.Id.Value))
            {
                return false;
            }

            for (var attempt = 0; attempt < 30 && !TryGetComposer(out chat, out group); attempt++)
            {
                await Task.Delay(50);
            }
        }

        if (chat is null && group is null)
        {
            return false;
        }

        var staged = new List<AttachmentMetadata>(share.FilePaths.Count);
        foreach (var path in share.FilePaths)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var info = new FileInfo(path);
            var fileName = Path.GetFileName(path);
            var contentType = ResolveShareContentType(fileName);
            var attachment = AttachmentMetadata.Local(
                fileName,
                contentType,
                info.Length,
                isDocument: !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    && !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));
            await AttachmentOpenService.CacheLocalCopyAsync(attachment, path);
            staged.Add(attachment);
        }

        var text = share.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (chat is not null)
            {
                chat.Draft = AppendDraft(chat.Draft, text);
            }
            else
            {
                group!.Draft = AppendDraft(group.Draft, text);
            }
        }

        foreach (var attachment in staged)
        {
            if (chat is not null)
            {
                chat.StageAttachment(attachment);
            }
            else
            {
                group!.StagedAttachments.Add(attachment);
            }
        }

        return true;
    }

    private bool TryGetComposer(out ChatViewModel? chat, out GroupChatViewModel? group)
    {
        chat = null;
        group = null;
        if (CurrentPage is IActiveComposerProvider currentProvider
            && currentProvider.TryGetActiveComposer(out chat, out group))
        {
            return true;
        }

        if (ConversationsTab?.Content is IActiveComposerProvider rootProvider
            && ReferenceEquals(CurrentPage, ConversationsTab.Content)
            && rootProvider.TryGetActiveComposer(out chat, out group))
        {
            return true;
        }

        var bindingContext = CurrentPage?.BindingContext;
        if (bindingContext is ChatViewModel chatViewModel && chatViewModel.Conversation is not null)
        {
            chat = chatViewModel;
            return true;
        }

        if (bindingContext is GroupChatViewModel groupViewModel && groupViewModel.GroupTitle.Length > 0)
        {
            group = groupViewModel;
            return true;
        }

        return false;
    }

    private async Task<IConversationActivationTarget?> ResolveConversationActivationTargetAsync()
    {
        if (CurrentPage is IConversationActivationTarget currentTarget)
        {
            return currentTarget;
        }

#if WINDOWS
        await GoToAsync($"//{ShellRouteCatalog.Conversations}", animate: false);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (CurrentPage is IConversationActivationTarget navigatedTarget)
            {
                return navigatedTarget;
            }

            if (ConversationsTab?.Content is IConversationActivationTarget contentTarget)
            {
                return contentTarget;
            }

            await Task.Delay(50);
        }
#else
        if (ConversationsTab?.Content is IConversationActivationTarget rootTarget)
        {
            return rootTarget;
        }
#endif
        return null;
    }

    private static string CreateOneToOneRoute(ConversationId id, string displayName)
    {
        try
        {
            SessionId.Parse(id.Value);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        return $"{ShellRouteCatalog.Chat}?sessionId={Uri.EscapeDataString(id.Value)}&displayName={Uri.EscapeDataString(displayName)}";
    }

    private static string AppendDraft(string current, string incoming) =>
        string.IsNullOrWhiteSpace(current) ? incoming : $"{current}{Environment.NewLine}{incoming}";

    private static string ResolveShareContentType(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };

    private static void DeleteConsumedShareFiles(SharePayload share)
    {
        foreach (var path in share.FilePaths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                CrashDiagnostics.LogException("AppShell.DeleteShareFile", ex);
            }
        }
    }
}
