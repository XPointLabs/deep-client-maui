namespace Deep.Client.Maui.Controls;

public sealed class DeferredView : ContentView
{
    public static readonly BindableProperty TemplateProperty = BindableProperty.Create(
        nameof(Template),
        typeof(DataTemplate),
        typeof(DeferredView));

    public DataTemplate? Template
    {
        get => (DataTemplate?)GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    public T GetRequiredView<T>(string name)
        where T : Element
    {
        EnsureContent();
        return Content?.FindByName<T>(name)
            ?? throw new InvalidOperationException($"Deferred view '{name}' was not created.");
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == IsVisibleProperty.PropertyName && IsVisible)
        {
            EnsureContent();
        }
    }

    private void EnsureContent()
    {
        if (Content is not null || Template is null)
        {
            return;
        }

        Content = Template.CreateContent() switch
        {
            View view => view,
            var content => throw new InvalidOperationException(
                $"Deferred template must create a View, but created {content?.GetType().FullName ?? "null"}.")
        };
    }
}
