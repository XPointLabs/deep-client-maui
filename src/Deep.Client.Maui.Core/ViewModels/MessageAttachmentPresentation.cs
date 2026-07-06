using Deep.Client.Shared.Domain;

namespace Deep.Client.Maui.Core.ViewModels;

internal static class MessageAttachmentPresentation
{
    public const string GenericAttachmentPlaceholder = "[Вложение]";
    public const string VoiceAttachmentPlaceholder = "[Голосовое сообщение]";

    public static bool HasVisibleBody(string body) =>
        !string.IsNullOrWhiteSpace(body)
        && !string.Equals(body, GenericAttachmentPlaceholder, StringComparison.Ordinal)
        && !string.Equals(body, VoiceAttachmentPlaceholder, StringComparison.Ordinal);

    public static bool IsVoiceMessage(IReadOnlyList<AttachmentMetadata> attachments) =>
        attachments.Count == 1
        && attachments[0].ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);

    public static string Summary(IReadOnlyList<AttachmentMetadata> attachments) => attachments.Count switch
    {
        0 => string.Empty,
        1 => attachments[0].FileName,
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
            return "Нажмите, чтобы открыть список файлов";
        }

        var attachment = attachments[0];
        var kind = attachment.ContentType switch
        {
            var value when value.StartsWith("image/", StringComparison.OrdinalIgnoreCase) => "Фото",
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
}
