using Deep.Client.Shared.Services.ContactV1;

namespace Deep.Client.Maui.Core.Services;

public sealed record ContactResolverUiState(
    ContactResolverDisposition Disposition,
    ContactResolverRetryClassification Retry,
    TimeSpan? RetryAfter,
    string Title,
    string Message,
    string? RetryAfterText)
{
    public bool IsVerified => Disposition == ContactResolverDisposition.Verified;

    public bool CanRetrySameExactRequest =>
        Retry == ContactResolverRetryClassification.RetrySameExactRequest;

    public bool RequiresFreshOperation =>
        Retry == ContactResolverRetryClassification.RefreshViewAndCreateNewOperation;

    public bool IsFailClosed =>
        Retry == ContactResolverRetryClassification.FailClosed ||
        Disposition is ContactResolverDisposition.Conflict or
            ContactResolverDisposition.ProtocolRejected;
}

public static class ContactResolverOutcomeUiMapper
{
    public static ContactResolverUiState Map(
        ContactResolverDisposition disposition,
        ContactResolverRetryClassification retry,
        TimeSpan? retryAfter = null)
    {
        var retryAfterText = retry == ContactResolverRetryClassification.RetrySameExactRequest
            ? FormatRetryAfter(retryAfter)
            : null;

        return disposition switch
        {
            ContactResolverDisposition.Verified => new(
                disposition,
                retry,
                retryAfter,
                "Контакт подтверждён",
                "Контакт успешно подтверждён и готов к использованию.",
                null),
            ContactResolverDisposition.TemporarilyUnavailable => new(
                disposition,
                retry,
                retryAfter,
                "Служба временно недоступна",
                "Попробуйте ещё раз чуть позже.",
                retryAfterText),
            ContactResolverDisposition.RateLimited => new(
                disposition,
                retry,
                retryAfter,
                "Слишком много запросов",
                "Сделайте паузу перед повторной попыткой.",
                retryAfterText),
            ContactResolverDisposition.StaleView => new(
                disposition,
                retry,
                retryAfter,
                "Нужно обновить представление",
                "Данные устарели. Создайте новую операцию после обновления.",
                null),
            ContactResolverDisposition.Expired => new(
                disposition,
                retry,
                retryAfter,
                "Публикация недоступна",
                "Срок приглашения или текущей публикации истёк. Постоянный Deep ID остаётся действительным.",
                null),
            ContactResolverDisposition.AlreadyClaimed => new(
                disposition,
                retry,
                retryAfter,
                "Приглашение уже использовано",
                "Это приглашение уже было заявлено.",
                null),
            ContactResolverDisposition.NotFound => new(
                disposition,
                retry,
                retryAfter,
                "Не найдено",
                "Контакт или приглашение не найдены.",
                null),
            ContactResolverDisposition.OutcomeUnknown => new(
                disposition,
                retry,
                retryAfter,
                "Результат неизвестен",
                "Статус операции не подтверждён. Повторите ту же попытку.",
                retryAfterText),
            ContactResolverDisposition.TransportUnavailable => new(
                disposition,
                retry,
                retryAfter,
                "Транспорт недоступен",
                "Не удалось связаться с сервисом.",
                retryAfterText),
            ContactResolverDisposition.Conflict => new(
                disposition,
                retry,
                retryAfter,
                "Конфликт",
                "Операция отклонена из-за изменения состояния. Повторять ту же попытку нельзя.",
                null),
            ContactResolverDisposition.ProtocolRejected => new(
                disposition,
                retry,
                retryAfter,
                "Проверка не пройдена",
                "Запрос отклонён по правилам протокола. Повторять нельзя.",
                null),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition))
        };
    }

    private static string? FormatRetryAfter(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } value || value <= TimeSpan.Zero)
            return null;

        var totalSeconds = checked((long)Math.Ceiling(value.TotalSeconds));
        var remaining = totalSeconds;
        var parts = new List<string>(3);

        var days = remaining / 86_400;
        if (days > 0)
        {
            parts.Add($"{days} д");
            remaining -= days * 86_400;
        }

        var hours = remaining / 3_600;
        if (hours > 0)
        {
            parts.Add($"{hours} ч");
            remaining -= hours * 3_600;
        }

        var minutes = remaining / 60;
        if (minutes > 0)
        {
            parts.Add($"{minutes} мин");
            remaining -= minutes * 60;
        }

        if (remaining > 0 || parts.Count == 0)
            parts.Add($"{remaining} с");

        return "Повторите через " + string.Join(" ", parts) + ".";
    }
}
