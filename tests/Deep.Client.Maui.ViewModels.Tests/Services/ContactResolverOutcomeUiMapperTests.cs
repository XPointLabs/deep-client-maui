using Deep.Client.Maui.Core.Services;
using Deep.Client.Shared.Services.ContactV1;

namespace Deep.Client.Maui.ViewModels.Tests.Services;

public sealed class ContactResolverOutcomeUiMapperTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public void Map_ReturnsCompactRussianUiState(
        ContactResolverDisposition disposition,
        ContactResolverRetryClassification retry,
        TimeSpan? retryAfter,
        string expectedTitle,
        string expectedMessage,
        string? expectedRetryAfterText,
        bool expectedVerified,
        bool expectedRetrySameExactRequest,
        bool expectedRequiresFreshOperation,
        bool expectedFailClosed)
    {
        var state = ContactResolverOutcomeUiMapper.Map(disposition, retry, retryAfter);

        Assert.Equal(disposition, state.Disposition);
        Assert.Equal(retry, state.Retry);
        Assert.Equal(retryAfter, state.RetryAfter);
        Assert.Equal(expectedTitle, state.Title);
        Assert.Equal(expectedMessage, state.Message);
        Assert.Equal(expectedRetryAfterText, state.RetryAfterText);
        Assert.Equal(expectedVerified, state.IsVerified);
        Assert.Equal(expectedRetrySameExactRequest, state.CanRetrySameExactRequest);
        Assert.Equal(expectedRequiresFreshOperation, state.RequiresFreshOperation);
        Assert.Equal(expectedFailClosed, state.IsFailClosed);
    }

    [Fact]
    public void RetryAfterFormatting_IsDeterministic()
    {
        var state = ContactResolverOutcomeUiMapper.Map(
            ContactResolverDisposition.RateLimited,
            ContactResolverRetryClassification.RetrySameExactRequest,
            TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(5));

        Assert.Equal("Повторите через 1 мин 5 с.", state.RetryAfterText);
    }

    public static TheoryData<
        ContactResolverDisposition,
        ContactResolverRetryClassification,
        TimeSpan?,
        string,
        string,
        string?,
        bool,
        bool,
        bool,
        bool> Cases => new()
    {
        {
            ContactResolverDisposition.Verified,
            ContactResolverRetryClassification.None,
            null,
            "Контакт подтверждён",
            "Контакт успешно подтверждён и готов к использованию.",
            null,
            true,
            false,
            false,
            false
        },
        {
            ContactResolverDisposition.TemporarilyUnavailable,
            ContactResolverRetryClassification.RetrySameExactRequest,
            TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(30),
            "Служба временно недоступна",
            "Попробуйте ещё раз чуть позже.",
            "Повторите через 1 мин 30 с.",
            false,
            true,
            false,
            false
        },
        {
            ContactResolverDisposition.RateLimited,
            ContactResolverRetryClassification.RetrySameExactRequest,
            TimeSpan.FromSeconds(15),
            "Слишком много запросов",
            "Сделайте паузу перед повторной попыткой.",
            "Повторите через 15 с.",
            false,
            true,
            false,
            false
        },
        {
            ContactResolverDisposition.StaleView,
            ContactResolverRetryClassification.RefreshViewAndCreateNewOperation,
            null,
            "Нужно обновить представление",
            "Данные устарели. Создайте новую операцию после обновления.",
            null,
            false,
            false,
            true,
            false
        },
        {
            ContactResolverDisposition.Expired,
            ContactResolverRetryClassification.Terminal,
            null,
            "Публикация недоступна",
            "Срок приглашения или текущей публикации истёк. Постоянный Deep ID остаётся действительным.",
            null,
            false,
            false,
            false,
            false
        },
        {
            ContactResolverDisposition.AlreadyClaimed,
            ContactResolverRetryClassification.Terminal,
            null,
            "Приглашение уже использовано",
            "Это приглашение уже было заявлено.",
            null,
            false,
            false,
            false,
            false
        },
        {
            ContactResolverDisposition.NotFound,
            ContactResolverRetryClassification.Terminal,
            null,
            "Не найдено",
            "Контакт или приглашение не найдены.",
            null,
            false,
            false,
            false,
            false
        },
        {
            ContactResolverDisposition.OutcomeUnknown,
            ContactResolverRetryClassification.RetrySameExactRequest,
            TimeSpan.FromSeconds(30),
            "Результат неизвестен",
            "Статус операции не подтверждён. Повторите ту же попытку.",
            "Повторите через 30 с.",
            false,
            true,
            false,
            false
        },
        {
            ContactResolverDisposition.TransportUnavailable,
            ContactResolverRetryClassification.Terminal,
            null,
            "Транспорт недоступен",
            "Не удалось связаться с сервисом.",
            null,
            false,
            false,
            false,
            false
        },
        {
            ContactResolverDisposition.Conflict,
            ContactResolverRetryClassification.FailClosed,
            null,
            "Конфликт",
            "Операция отклонена из-за изменения состояния. Повторять ту же попытку нельзя.",
            null,
            false,
            false,
            false,
            true
        },
        {
            ContactResolverDisposition.ProtocolRejected,
            ContactResolverRetryClassification.FailClosed,
            null,
            "Проверка не пройдена",
            "Запрос отклонён по правилам протокола. Повторять нельзя.",
            null,
            false,
            false,
            false,
            true
        }
    };
}
