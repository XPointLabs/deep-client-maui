using Deep.Client.Maui.Core.Navigation;
using Deep.Client.Maui.Core.ViewModels;

namespace Deep.Client.Maui.Pages;

public partial class OnboardingPage : ContentPage
{
    private readonly OnboardingViewModel viewModel;

    public OnboardingPage(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
        RecoveryPhraseEditor.HandlerChanged += OnRecoveryPhraseHandlerChanged;
        HardenRecoveryPhraseInput();
    }

    protected override void OnDisappearing()
    {
        viewModel.ClearRecoveryPhrase();
        base.OnDisappearing();
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync($"//{ShellRouteCatalog.Onboarding}");
    }

    private void OnRecoveryPhraseHandlerChanged(object? sender, EventArgs e) =>
        HardenRecoveryPhraseInput();

    private void HardenRecoveryPhraseInput()
    {
#if ANDROID
        if (RecoveryPhraseEditor.Handler?.PlatformView is Android.Widget.EditText editor)
        {
            editor.ImportantForAutofill = Android.Views.ImportantForAutofill.NoExcludeDescendants;
            editor.SetAutofillHints([]);

            var inputType = editor.InputType;
            inputType &= ~(Android.Text.InputTypes.MaskClass
                | Android.Text.InputTypes.MaskVariation
                | Android.Text.InputTypes.TextFlagAutoComplete
                | Android.Text.InputTypes.TextFlagAutoCorrect);
            inputType |= Android.Text.InputTypes.ClassText
                | Android.Text.InputTypes.TextVariationVisiblePassword
                | Android.Text.InputTypes.TextFlagMultiLine
                | Android.Text.InputTypes.TextFlagNoSuggestions;
            editor.SetRawInputType(inputType);
            editor.ImeOptions = (Android.Views.InputMethods.ImeAction)(
                (int)editor.ImeOptions | (int)Android.Views.InputMethods.ImeFlags.NoPersonalizedLearning);
        }
#elif IOS || MACCATALYST
        if (RecoveryPhraseEditor.Handler?.PlatformView is UIKit.UITextView editor)
        {
            editor.AutocorrectionType = UIKit.UITextAutocorrectionType.No;
            editor.SpellCheckingType = UIKit.UITextSpellCheckingType.No;
            editor.SmartDashesType = UIKit.UITextSmartDashesType.No;
            editor.SmartInsertDeleteType = UIKit.UITextSmartInsertDeleteType.No;
            editor.SmartQuotesType = UIKit.UITextSmartQuotesType.No;
            editor.TextContentType = new Foundation.NSString(string.Empty);

            if (OperatingSystem.IsIOSVersionAtLeast(17)
                || OperatingSystem.IsMacCatalystVersionAtLeast(17))
            {
                editor.InlinePredictionType = UIKit.UITextInlinePredictionType.No;
            }

            if (OperatingSystem.IsIOSVersionAtLeast(18)
                || OperatingSystem.IsMacCatalystVersionAtLeast(18))
            {
                editor.WritingToolsBehavior = UIKit.UIWritingToolsBehavior.None;
            }
        }
#elif WINDOWS
        if (RecoveryPhraseEditor.Handler?.PlatformView is Microsoft.UI.Xaml.Controls.TextBox editor)
        {
            editor.IsSpellCheckEnabled = false;
            editor.IsTextPredictionEnabled = false;
        }
#endif
    }
}
