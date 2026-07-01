using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

internal static class MessageStatusPresentation
{
    public static bool IsVisible(MessageDirection direction, MessageDeliveryState state) =>
        direction == MessageDirection.Outgoing && state != MessageDeliveryState.Draft;

    public static string Glyph(MessageDeliveryState state) => state switch
    {
        MessageDeliveryState.Sending => "◷",
        MessageDeliveryState.Sent => "✓",
        MessageDeliveryState.Delivered or MessageDeliveryState.Read => "✓✓",
        MessageDeliveryState.Failed => "!",
        _ => string.Empty
    };

    public static string Description(MessageDeliveryState state) => state switch
    {
        MessageDeliveryState.Sending => "Отправляется",
        MessageDeliveryState.Sent => "Отправлено",
        MessageDeliveryState.Delivered => "Доставлено",
        MessageDeliveryState.Read => "Прочитано",
        MessageDeliveryState.Failed => "Не удалось отправить",
        _ => string.Empty
    };
}
