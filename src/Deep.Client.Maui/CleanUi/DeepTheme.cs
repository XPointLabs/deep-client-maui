namespace Deep.Client.Maui.CleanUi;

internal static class DeepTheme
{
    internal static readonly Color Background = Color.FromArgb("#050A13");
    internal static readonly Color Panel = Color.FromArgb("#0B1422");
    internal static readonly Color PanelAlt = Color.FromArgb("#111E31");
    internal static readonly Color Accent = Color.FromArgb("#18C8F4");
    internal static readonly Color AccentMuted = Color.FromArgb("#176FF2");
    internal static readonly Color Text = Color.FromArgb("#F7FAFF");
    internal static readonly Color Secondary = Color.FromArgb("#91A4BA");
    internal static readonly Color Tertiary = Color.FromArgb("#60748B");
    internal static readonly Color Danger = Color.FromArgb("#FF8B8B");
    internal static readonly Color Divider = Color.FromArgb("#1A2A3E");

    internal static ResourceDictionary Create()
    {
        var resources = new ResourceDictionary();
        resources.Add(new Style(typeof(ContentPage))
        {
            Setters = { new Setter { Property = VisualElement.BackgroundColorProperty, Value = Background } }
        });
        resources.Add(new Style(typeof(Label))
        {
            Setters =
            {
                new Setter { Property = Label.TextColorProperty, Value = Text },
                new Setter { Property = Label.FontAutoScalingEnabledProperty, Value = true }
            }
        });
        resources.Add(new Style(typeof(Entry))
        {
            Setters =
            {
                new Setter { Property = Entry.TextColorProperty, Value = Text },
                new Setter { Property = Entry.PlaceholderColorProperty, Value = Secondary },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = PanelAlt },
                new Setter { Property = Entry.FontSizeProperty, Value = 16.0 },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 44.0 },
                new Setter { Property = Entry.FontAutoScalingEnabledProperty, Value = true }
            }
        });
        resources.Add(new Style(typeof(Editor))
        {
            Setters =
            {
                new Setter { Property = Editor.TextColorProperty, Value = Text },
                new Setter { Property = Editor.PlaceholderColorProperty, Value = Secondary },
                new Setter { Property = VisualElement.BackgroundColorProperty, Value = PanelAlt },
                new Setter { Property = Editor.FontSizeProperty, Value = 15.0 },
                new Setter { Property = Editor.FontAutoScalingEnabledProperty, Value = true }
            }
        });
        resources.Add(new Style(typeof(Button))
        {
            Setters =
            {
                new Setter { Property = Button.BackgroundColorProperty, Value = Accent },
                new Setter { Property = Button.TextColorProperty, Value = Background },
                new Setter { Property = Button.CornerRadiusProperty, Value = 22 },
                new Setter { Property = VisualElement.MinimumHeightRequestProperty, Value = 44.0 },
                new Setter { Property = Button.FontAttributesProperty, Value = FontAttributes.Bold },
                new Setter { Property = Button.FontAutoScalingEnabledProperty, Value = true }
            }
        });
        return resources;
    }

    internal static Button SecondaryButton(string text, string automationId) => new()
    {
        Text = text,
        AutomationId = automationId,
        BackgroundColor = PanelAlt,
        TextColor = Text
    };
}
