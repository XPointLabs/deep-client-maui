using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

internal static class MessageAttachmentPresentation
{
    private const double DefaultImagePreviewWidth = 228;
    private const double DefaultImagePreviewHeight = 174;
    private const double MaxImagePreviewWidth = 246;
    private const double MaxImagePreviewHeight = 320;
    private const double MinImagePreviewWidth = 150;
    private const double MinImagePreviewHeight = 118;

    public const string GenericAttachmentPlaceholder = "[Вложение]";
    public const string VoiceAttachmentPlaceholder = "[Голосовое сообщение]";

    public static bool HasVisibleBody(string body) =>
        !string.IsNullOrWhiteSpace(body)
        && !string.Equals(body, GenericAttachmentPlaceholder, StringComparison.Ordinal)
        && !string.Equals(body, VoiceAttachmentPlaceholder, StringComparison.Ordinal);

    public static bool IsVoiceMessage(IReadOnlyList<AttachmentMetadata> attachments) =>
        attachments.Count == 1
        && attachments[0].ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    public static bool IsInlineImage(IReadOnlyList<AttachmentMetadata> attachments) =>
        attachments.Count > 0 && attachments.All(IsInlineImage);

    public static AttachmentMetadata? PrimaryInlineImage(IReadOnlyList<AttachmentMetadata> attachments) =>
        attachments.FirstOrDefault(IsInlineImage);

    public static string ImageCountLabel(IReadOnlyList<AttachmentMetadata> attachments) =>
        attachments.Count > 1 ? $"{attachments.Count} фото" : string.Empty;

    public static (double Width, double Height) ImagePreviewSize(AttachmentMetadata? attachment)
    {
        if (attachment is null || attachment.Width is not > 0 || attachment.Height is not > 0)
        {
            return (DefaultImagePreviewWidth, DefaultImagePreviewHeight);
        }

        var width = attachment.Width.Value;
        var height = attachment.Height.Value;
        var scale = Math.Min(MaxImagePreviewWidth / width, MaxImagePreviewHeight / height);
        if (scale <= 0)
        {
            return (DefaultImagePreviewWidth, DefaultImagePreviewHeight);
        }

        return (
            Math.Clamp(Math.Round(width * scale), MinImagePreviewWidth, MaxImagePreviewWidth),
            Math.Clamp(Math.Round(height * scale), MinImagePreviewHeight, MaxImagePreviewHeight));
    }

    public static string Summary(IReadOnlyList<AttachmentMetadata> attachments) => attachments.Count switch
    {
        0 => string.Empty,
        1 when IsVoiceMessage(attachments) => "Голосовое",
        1 when IsInlineImage(attachments) => "Фото",
        1 => attachments[0].FileName,
        _ when IsInlineImage(attachments) => $"{attachments.Count} фото",
        _ => $"{attachments.Count} влож."
    };

    public static string Title(IReadOnlyList<AttachmentMetadata> attachments)
    {
        if (attachments.Count == 0)
        {
            return string.Empty;
        }

        if (IsVoiceMessage(attachments))
        {
            return "Голосовое сообщение";
        }

        if (IsInlineImage(attachments))
        {
            return attachments.Count == 1 ? "Фото" : $"{attachments.Count} фото";
        }

        return attachments.Count == 1
            ? attachments[0].FileName
            : $"{attachments.Count} вложения";
    }

    public static string Subtitle(IReadOnlyList<AttachmentMetadata> attachments)
    {
        if (attachments.Count == 0)
        {
            return string.Empty;
        }

        if (attachments.Count > 1)
        {
            return IsInlineImage(attachments)
                ? "Нажмите, чтобы открыть фото"
                : "Нажмите, чтобы открыть список файлов";
        }

        var attachment = attachments[0];
        var kind = attachment.ContentType switch
        {
            var value when !attachment.IsDocument && value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "Фото",
            var value when value.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => "Видео",
            var value when value.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) => "Аудио",
            "application/pdf" => "PDF",
            _ => "Файл"
        };

        return $"{kind} · {FormatSize(attachment.SizeBytes)}";
    }

    public static string VoiceDuration(IReadOnlyList<AttachmentMetadata> attachments)
    {
        if (!IsVoiceMessage(attachments))
        {
            return string.Empty;
        }

        var duration = attachments[0].Duration ?? TimeSpan.Zero;
        var seconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} Б";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.#} КБ";
        }

        return $"{bytes / 1024d / 1024d:0.#} МБ";
    }

    private static bool IsInlineImage(AttachmentMetadata attachment) =>
        !attachment.IsDocument
        && attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}
