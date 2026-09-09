using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Maui.Services;

namespace Deep.Client.Maui.Pages;

public partial class NewConversationPage : ContentPage
{
    private readonly NewConversationViewModel viewModel;
    private CancellationTokenSource? pageLifetime;

    public NewConversationPage(NewConversationViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        BindingContext = viewModel;
    }

    protected override void OnAppearing()
    {
        pageLifetime?.Dispose();
        pageLifetime = new CancellationTokenSource();
        base.OnAppearing();
    }

    protected override void OnDisappearing()
    {
        pageLifetime?.Cancel();
        pageLifetime?.Dispose();
        pageLifetime = null;
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = NavigateBackToConversationsAsync();
        return true;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await NavigateBackToConversationsAsync();
    }

    private static Task NavigateBackToConversationsAsync() =>
        Shell.Current.GoToAsync("..", animate: false);

    private async void OnResolveClicked(object? sender, EventArgs e)
    {
        try
        {
            var cancellationToken = pageLifetime?.Token ?? CancellationToken.None;
            await viewModel.ResolveAsync(cancellationToken);
            KeyboardDismissal.Dismiss(DeepIdEntry);
            if (viewModel.VerifiedConversation is not { } verified)
            {
                return;
            }

            if (Shell.Current is IVerifiedDirectConversationActivationTarget activationTarget &&
                await activationTarget.ActivateVerifiedDirectConversationAsync(
                    verified,
                    cancellationToken))
            {
                return;
            }

            viewModel.ReportDirectRuntimeUnavailable();
        }
        catch (OperationCanceledException)
        {
            // If ContactV1 already wrote the address, its durable pending state
            // survives cancellation and remains available for an exact retry.
        }
        catch (Exception exception)
        {
            CrashDiagnostics.LogException("ContactV1.EntryFlow", exception);
        }
    }
}
