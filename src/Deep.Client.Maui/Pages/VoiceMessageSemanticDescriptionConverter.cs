using System.Globalization;

namespace Deep.Client.Maui.Pages;

public sealed class VoiceMessageSemanticDescriptionConverter : IValueConverter
{
    internal const string AccessibleDescription =
        "Воспроизвести голосовое сообщение";

    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        _ = targetType;
        _ = parameter;
        _ = culture;
#if DEBUG && DEEP_PHYSICAL_E2E
        return value as string ?? string.Empty;
#else
        return AccessibleDescription;
#endif
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
