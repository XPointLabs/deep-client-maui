using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Shared.Persistence;
using Microsoft.Maui.Storage;

namespace Deep.Client.Maui.Services;

internal static class StartupLocalStateReset
{
    internal const string WipeLocalDataOnNextLaunchKey = "session.wipe-local-on-next-launch";

    internal static bool IsResetRequired(Exception exception) =>
        exception.GetType() == typeof(LocalStateResetRequiredException) ||
        exception.GetType() == typeof(ProtectedIdentityResetRequiredException);

    internal static StartupLocalStateResetPresentation ToUserPresentation(Exception exception) =>
        exception switch
        {
            LocalStateResetRequiredException
                {
                    Reason: LocalStateResetRequiredReason.UnsupportedVersion
                } => new(
                    "local-state-incompatible-version",
                    "Версия локальных данных несовместима",
                    "Эти данные созданы другой версией Deep. Для продолжения требуется явный сброс локальных данных."),
            LocalStateResetRequiredException
                {
                    Reason: LocalStateResetRequiredReason.InvalidCurrentSchema
                } => new(
                    "local-state-damaged",
                    "Локальные данные повреждены",
                    "Проверка структуры локального хранилища не пройдена. Для продолжения требуется явный сброс локальных данных."),
            LocalStateResetRequiredException
                {
                    Reason: LocalStateResetRequiredReason.UnreadableOrWrongKey
                } => new(
                    "local-state-unreadable",
                    "Защищённые локальные данные недоступны",
                    "Deep не смог расшифровать или прочитать локальное хранилище с текущим защищённым ключом. Для продолжения требуется явный сброс."),
            ProtectedIdentityResetRequiredException
                {
                    Reason: ProtectedIdentityResetRequiredReason.Missing
                } => new(
                    "protected-identity-missing",
                    "Защищённая идентичность отсутствует",
                    "У активного аккаунта нет защищённого материала идентичности. Для продолжения требуется явный сброс локальных данных."),
            ProtectedIdentityResetRequiredException
                {
                    Reason: ProtectedIdentityResetRequiredReason.Incompatible
                } => new(
                    "protected-identity-incompatible",
                    "Формат защищённой идентичности несовместим",
                    "Сохранённая идентичность не соответствует текущему формату Deep. Для продолжения требуется явный сброс локальных данных."),
            ProtectedIdentityResetRequiredException
                {
                    Reason: ProtectedIdentityResetRequiredReason.AccountMismatch
                } => new(
                    "protected-identity-account-mismatch",
                    "Защищённая идентичность не соответствует аккаунту",
                    "Сохранённая идентичность принадлежит не активному аккаунту. Для продолжения требуется явный сброс локальных данных."),
            _ => throw new ArgumentException(
                "The exception is not an exact typed reset-required failure.",
                nameof(exception))
        };

    // This method represents an already-confirmed destructive user action.
    // Callers must never invoke it as part of ordinary startup error handling.
    internal static bool TryRequestConfirmedReset(Exception exception)
    {
        if (!IsResetRequired(exception))
        {
            return false;
        }

        Preferences.Default.Set(WipeLocalDataOnNextLaunchKey, true);
        return true;
    }
}

internal sealed class StartupLocalStateResetContext
{
    private Exception? failure;

    internal void Clear() => failure = null;

    internal void Capture(LocalStateResetRequiredException exception) =>
        failure = exception;

    internal void Capture(ProtectedIdentityResetRequiredException exception) =>
        failure = exception;

    internal bool TryGetPresentation(out StartupLocalStateResetPresentation presentation)
    {
        if (failure is not null && StartupLocalStateReset.IsResetRequired(failure))
        {
            presentation = StartupLocalStateReset.ToUserPresentation(failure);
            return true;
        }

        presentation = default;
        return false;
    }

    internal bool TryRequestConfirmedReset() =>
        failure is not null &&
        StartupLocalStateReset.TryRequestConfirmedReset(failure);
}

internal readonly record struct StartupLocalStateResetPresentation(
    string Code,
    string Status,
    string Guidance);
