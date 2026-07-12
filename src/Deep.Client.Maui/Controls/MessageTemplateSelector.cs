using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.Controls;

public sealed class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate PlainTextTemplate { get; set; } = null!;

    public DataTemplate TextTemplate { get; set; } = null!;

    public DataTemplate ImageTemplate { get; set; } = null!;

    public DataTemplate VoiceTemplate { get; set; } = null!;

    public DataTemplate AttachmentTemplate { get; set; } = null!;

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) =>
        item switch
        {
            ChatMessageItem { IsVoiceMessage: true } or GroupChatMessageItem { IsVoiceMessage: true } => VoiceTemplate,
            ChatMessageItem { IsImageMessage: true } or GroupChatMessageItem { IsImageMessage: true } => ImageTemplate,
            ChatMessageItem { HasGenericAttachments: true } or GroupChatMessageItem { HasGenericAttachments: true } => AttachmentTemplate,
            ChatMessageItem { HasReply: false, HasReactions: false }
                or GroupChatMessageItem { HasReply: false, HasReactions: false } => PlainTextTemplate,
            _ => TextTemplate
        };
}
