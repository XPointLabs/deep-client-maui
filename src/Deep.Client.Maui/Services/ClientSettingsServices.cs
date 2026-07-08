using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Storage;

#if ANDROID
using Android.Views;
using Microsoft.Maui.ApplicationModel;
#endif

namespace Deep.Client.Maui.Services;

public interface IPrivacyScreenService
{
    void ApplyFromPreferences();

    void SetScreenSecurity(bool enabled);
}

public interface IAppearanceService
{
    void ApplyFromPreferences();
}

public sealed class MauiPrivacyScreenService : IPrivacyScreenService
{
    public void ApplyFromPreferences()
    {
        SetScreenSecurity(Preferences.Default.Get(ClientSettingKeys.PrivacyScreenSecurity, true));
    }

    public void SetScreenSecurity(bool enabled)
    {
#if ANDROID
        var window = Platform.CurrentActivity?.Window;
        if (window is null)
        {
            return;
        }

#if DEBUG
        var screenSecurityEnabled = false;
#else
        var screenSecurityEnabled = enabled;
#endif
        if (screenSecurityEnabled)
        {
            window.AddFlags(WindowManagerFlags.Secure);
        }
        else
        {
            window.ClearFlags(WindowManagerFlags.Secure);
        }
#endif
    }
}

public sealed class MauiAppearanceService : IAppearanceService
{
    public void ApplyFromPreferences()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var followSystem = Preferences.Default.Get(ClientSettingKeys.AppearanceFollowSystem, false);
        var theme = Preferences.Default.Get(ClientSettingKeys.AppearanceTheme, "Тёмная");
        app.UserAppTheme = followSystem
            ? AppTheme.Unspecified
            : IsLightTheme(theme)
                ? AppTheme.Light
                : AppTheme.Dark;

        var palette = IsLightTheme(theme)
            ? Palette.XPointLight
            : Palette.XPointDark;
        var accent = AccentFor(Preferences.Default.Get(ClientSettingKeys.AppearanceAccent, "Голубой"));

        SetColor(app, "PageBackground", palette.PageBackground);
        SetColor(app, "PanelBackground", palette.PanelBackground);
        SetColor(app, "PanelBackgroundAlt", palette.PanelBackgroundAlt);
        SetColor(app, "TextPrimary", palette.TextPrimary);
        SetColor(app, "TextSecondary", palette.TextSecondary);
        SetColor(app, "IncomingBubbleColor", palette.IncomingBubbleColor);
        SetColor(app, "OutgoingBubbleColor", palette.OutgoingBubbleColor);
        SetColor(app, "DividerColor", palette.DividerColor);
        SetColor(app, "PrimaryColor", accent.Primary);
        SetColor(app, "PrimaryColorMuted", accent.Muted);
        SetColor(app, "AvatarAccentColor", accent.Avatar);
    }

    private static void SetColor(Application app, string key, string value)
    {
        app.Resources[key] = Color.FromArgb(value);
    }

    private static Accent AccentFor(string value) =>
        value switch
        {
            "Синий" => new("#126DFF", "#18C8FF", "#8A5AFF"),
            "XPoint Blue" => new("#126DFF", "#18C8FF", "#8A5AFF"),
            "Фиолетовый" => new("#8A5AFF", "#126DFF", "#18C8FF"),
            "XPoint Violet" => new("#8A5AFF", "#126DFF", "#18C8FF"),
            _ => new("#18C8FF", "#126DFF", "#8A5AFF")
        };

    private static bool IsLightTheme(string value) =>
        value.Contains("Light", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Светл", StringComparison.OrdinalIgnoreCase);

    private sealed record Accent(string Primary, string Muted, string Avatar);

    private sealed record Palette(
        string PageBackground,
        string PanelBackground,
        string PanelBackgroundAlt,
        string TextPrimary,
        string TextSecondary,
        string IncomingBubbleColor,
        string OutgoingBubbleColor,
        string DividerColor)
    {
        public static readonly Palette XPointDark = new(
            "#020711",
            "#07111F",
            "#10244B",
            "#F5F9FF",
            "#9DB1C7",
            "#07111F",
            "#10244B",
            "#1E3356");

        public static readonly Palette XPointLight = new(
            "#F7FBFF",
            "#FFFFFF",
            "#E8F3FF",
            "#07111F",
            "#52657A",
            "#FFFFFF",
            "#DDF3FF",
            "#C9DBEC");
    }
}
