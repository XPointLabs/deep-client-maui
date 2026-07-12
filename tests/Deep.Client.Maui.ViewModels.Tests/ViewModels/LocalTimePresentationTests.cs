using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.ViewModels.Tests.ViewModels;

public sealed class LocalTimePresentationTests
{
    private static readonly DateTimeOffset UtcInstant =
        DateTimeOffset.Parse("2026-07-11T11:03:00Z");

    [Fact]
    public void ConversationTimestampUsesDeviceTimeZone()
    {
        var item = new ConversationListItem(
            ConversationId.CreateGroupV2(),
            "Group",
            "G",
            ConversationKind.GroupV2,
            UtcInstant,
            IsMuted: false,
            LastMessagePreview: "message",
            UnreadCount: 0,
            IsUnread: false,
            IsMessageRequest: false,
            IsSelected: false);

        AssertLocalInstant(UtcInstant, item.LocalUpdatedAt);
    }

    [Fact]
    public void DirectMessageTimestampUsesDeviceTimeZone()
    {
        var item = new ChatMessageItem(
            MessageId.NewId(),
            "message",
            MessageDirection.Outgoing,
            MessageDeliveryState.Sent,
            UtcInstant,
            [],
            replyTo: null,
            reactions: []);

        AssertLocalInstant(UtcInstant, item.LocalCreatedAt);
    }

    [Fact]
    public void GroupMessageTimestampUsesDeviceTimeZone()
    {
        var item = new GroupChatMessageItem(
            MessageId.NewId(),
            "message",
            MessageDirection.Outgoing,
            MessageDeliveryState.Sent,
            UtcInstant,
            [],
            senderLabel: "",
            replyTo: null,
            reactions: []);

        AssertLocalInstant(UtcInstant, item.LocalCreatedAt);
    }

    private static void AssertLocalInstant(DateTimeOffset expectedUtc, DateTimeOffset actual)
    {
        Assert.Equal(expectedUtc.UtcDateTime, actual.UtcDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(expectedUtc.UtcDateTime), actual.Offset);
    }
}
