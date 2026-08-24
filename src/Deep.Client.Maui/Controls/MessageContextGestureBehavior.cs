namespace Deep.Client.Maui.Controls;

public sealed class MessageContextGestureBehavior : Behavior<VisualElement>
{
    private VisualElement? target;

#if ANDROID
    private Android.Views.View? platformView;
    private bool previousLongClickable;
#elif WINDOWS
    private Microsoft.UI.Xaml.FrameworkElement? platformView;
#endif

    public event EventHandler? Invoked;

    protected override void OnAttachedTo(VisualElement bindable)
    {
        base.OnAttachedTo(bindable);
        target = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        AttachPlatformView();
    }

    protected override void OnDetachingFrom(VisualElement bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        DetachPlatformView();
        target = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs e)
    {
        DetachPlatformView();
        AttachPlatformView();
    }

    private void RaiseInvoked()
    {
        if (target is null)
        {
            return;
        }

        if (target.Dispatcher.IsDispatchRequired)
        {
            target.Dispatcher.Dispatch(RaiseInvoked);
            return;
        }

        Invoked?.Invoke(target, EventArgs.Empty);
    }

    private void AttachPlatformView()
    {
#if ANDROID
        if (target?.Handler?.PlatformView is not Android.Views.View view)
        {
            return;
        }

        platformView = view;
        previousLongClickable = view.LongClickable;
        view.LongClickable = true;
        view.LongClick += OnAndroidLongClick;
#elif WINDOWS
        if (target?.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement view)
        {
            return;
        }

        platformView = view;
        view.ContextRequested += OnWindowsContextRequested;
        view.RightTapped += OnWindowsRightTapped;
#endif
    }

    private void DetachPlatformView()
    {
#if ANDROID
        if (platformView is not null)
        {
            platformView.LongClick -= OnAndroidLongClick;
            platformView.LongClickable = previousLongClickable;
            platformView = null;
        }
#elif WINDOWS
        if (platformView is not null)
        {
            platformView.ContextRequested -= OnWindowsContextRequested;
            platformView.RightTapped -= OnWindowsRightTapped;
            platformView = null;
        }
#endif
    }

#if ANDROID
    private void OnAndroidLongClick(object? sender, Android.Views.View.LongClickEventArgs e)
    {
        platformView?.PerformHapticFeedback(Android.Views.FeedbackConstants.LongPress);
        e.Handled = true;
        RaiseInvoked();
    }
#elif WINDOWS
    private void OnWindowsContextRequested(
        Microsoft.UI.Xaml.UIElement sender,
        Microsoft.UI.Xaml.Input.ContextRequestedEventArgs args)
    {
        args.Handled = true;
        RaiseInvoked();
    }

    private void OnWindowsRightTapped(
        object sender,
        Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs args)
    {
        args.Handled = true;
        RaiseInvoked();
    }
#endif
}
