namespace Deep.Client.Maui.Core.Navigation;

public sealed record ShellRoute(string Route, string Title, bool RequiresAccount);

public static class ShellRouteCatalog
{
    public const string Onboarding = "onboarding";
    public const string Restore = "restore";
    public const string Conversations = "conversations";
    public const string Chat = "chat";
    public const string ContactProfile = "contact-profile";
    public const string StartConversation = "start-conversation";
    public const string NewConversation = "new-conversation";
    public const string GroupChat = "group-chat";
    public const string Groups = "groups";
    public const string Settings = "settings";
    public const string SettingsDetail = "settings-detail";
    public const string Call = "call";

    public static IReadOnlyList<ShellRoute> Routes { get; } =
    [
        new(Onboarding, "Onboarding", RequiresAccount: false),
        new(Restore, "Restore", RequiresAccount: false),
        new(Conversations, "Conversations", RequiresAccount: true),
        new(Chat, "Chat", RequiresAccount: true),
        new(ContactProfile, "ContactProfile", RequiresAccount: true),
        new(StartConversation, "StartConversation", RequiresAccount: true),
        new(NewConversation, "NewConversation", RequiresAccount: true),
        new(GroupChat, "GroupChat", RequiresAccount: true),
        new(Groups, "Groups", RequiresAccount: true),
        new(Settings, "Settings", RequiresAccount: true),
        new(SettingsDetail, "SettingsDetail", RequiresAccount: true),
        new(Call, "Call", RequiresAccount: true)
    ];
}
