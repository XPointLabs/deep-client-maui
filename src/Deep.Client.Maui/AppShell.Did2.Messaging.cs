using System.Globalization;
using Deep.Client.Maui.CleanUi;
using Deep.Client.Maui.Core.ViewModels;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui;

public sealed partial class AppShell
{
    private View CreateContactSection()
    {
        var address = new Editor
        {
            AutomationId = "Contacts.Address", Placeholder = "Deep ID контакта",
            HeightRequest = 100, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
            BindingContext = messaging
        };
        address.SetBinding(Editor.TextProperty, nameof(messaging.ContactAddress), BindingMode.TwoWay);
        var start = DeepTheme.SecondaryButton("Добавить контакт", "Contacts.Add");
        start.Command = messaging.StartContactCommand;
        var view = Surface("Page.Contacts", "КОНТАКТЫ", "Добавить контакт",
            Card(new VerticalStackLayout
            {
                Spacing = 12, Children =
                {
                    new Label { Text = "Вставьте адрес. Получатель должен принять запрос, прежде чем начнётся переписка.", TextColor = DeepTheme.Secondary },
                    address, start, CreateMessagingStatus()
                }
            }));
        return view;
    }

    private View CreateChatsSection()
    {
        if (!messaging.HasRuntime) return CreateEmptyChatsSection();
        var refresh = DeepTheme.SecondaryButton("Обновить", "Conversations.Refresh");
        refresh.Command = messaging.RefreshCommand;
        var conversations = new CollectionView
        {
            AutomationId = "Conversations.Items", ItemsSource = messaging.Conversations,
            SelectionMode = SelectionMode.Single,
            EmptyView = new Label { Text = "Нет диалогов. Добавьте контакт или обновите входящие.", Margin = new Thickness(0, 20), TextColor = DeepTheme.Secondary },
            ItemTemplate = new DataTemplate(() =>
            {
                var peer = new Label { FontSize = 16, FontAttributes = FontAttributes.Bold };
                peer.SetBinding(Label.TextProperty, "Conversation.PeerAccountId", converter: new ShortPeerConverter());
                var state = new Label { FontSize = 12, TextColor = DeepTheme.Secondary };
                state.SetBinding(Label.TextProperty, "ContactState", converter: new ContactStateConverter());
                return Card(new VerticalStackLayout { Spacing = 6, Children = { peer, state } });
            })
        };
        conversations.SelectionChanged += async (_, change) =>
        {
            if (change.CurrentSelection.FirstOrDefault() is DeepIdV2ConversationSnapshot item && !messaging.IsBusy)
                await messaging.SelectConversationAsync(item);
        };
        var list = new Grid
        {
            AutomationId = "Did2Workspace.ConversationList", Padding = new Thickness(18, 22), RowSpacing = 14,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children = { new Label { Text = "Диалоги", FontSize = 25, FontAttributes = FontAttributes.Bold }, refresh, CreateMessagingStatus(), conversations }
        };
        Grid.SetRow(refresh, 1); list.SetRow(list.Children[2], 2); Grid.SetRow(conversations, 3);
        var detail = CreateConversationDetail();
        var canvas = new Grid
        {
            AutomationId = "Page.Conversations", BackgroundColor = DeepTheme.Background,
            ColumnDefinitions = { new(GridLength.Star), new(new GridLength(0)) },
            Children = { list, detail }
        };
        refreshConversationLayout = () =>
        {
            var split = canvas.Width >= 650;
            var showDetail = messaging.HasSelection;
            if (!showDetail) conversations.SelectedItem = null;
            list.IsVisible = split || !showDetail;
            detail.IsVisible = split || showDetail;
            canvas.ColumnDefinitions[0].Width = split ? new GridLength(320) : GridLength.Star;
            canvas.ColumnDefinitions[1].Width = split ? GridLength.Star : new GridLength(0);
            Grid.SetColumn(detail, split ? 1 : 0);
        };
        canvas.SizeChanged += (_, _) => refreshConversationLayout?.Invoke();
        refreshConversationLayout();
        return canvas;
    }

    private View CreateConversationDetail()
    {
        var title = new Label { FontSize = 22, FontAttributes = FontAttributes.Bold };
        title.SetBinding(Label.TextProperty, nameof(messaging.SelectionTitle));
        var state = new Label { TextColor = DeepTheme.Secondary, FontSize = 13 };
        state.SetBinding(Label.TextProperty, nameof(messaging.SelectionStatus));
        var back = DeepTheme.SecondaryButton("К списку диалогов", "Conversation.Back");
        back.Clicked += (_, _) => messaging.CloseConversation();
        var accept = DeepTheme.SecondaryButton("Принять контакт", "Contacts.Accept");
        accept.Command = messaging.AcceptContactCommand;
        accept.SetBinding(Button.TextProperty, nameof(messaging.AcceptCaption));
        accept.SetBinding(IsVisibleProperty, nameof(messaging.CanAccept));
        var messages = new CollectionView
        {
            AutomationId = "Messages.Items", ItemsSource = messaging.Messages,
            ItemTemplate = new DataTemplate(() =>
            {
                var author = new Label { FontSize = 11, TextColor = DeepTheme.Secondary };
                author.SetBinding(Label.TextProperty, "IsLocalAuthor", converter: new MessageAuthorConverter());
                var text = new Label { FontSize = 15, LineBreakMode = LineBreakMode.WordWrap };
                text.SetBinding(Label.TextProperty, "Text");
                var time = new Label { FontSize = 10, TextColor = DeepTheme.Tertiary };
                time.SetBinding(Label.TextProperty, "CreatedAt", stringFormat: "{0:HH:mm}");
                var bubble = Card(new VerticalStackLayout { Spacing = 5, Children = { author, text, time } });
                bubble.Margin = new Thickness(0, 4); return bubble;
            })
        };
        var input = new Editor
        {
            AutomationId = "Message.Text", Placeholder = "Сообщение", AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 48, MaximumHeightRequest = 130
        };
        input.SetBinding(Editor.TextProperty, nameof(messaging.DraftText), BindingMode.TwoWay);
        input.SetBinding(IsEnabledProperty, nameof(messaging.CanSend));
        var send = new Button { Text = "Отправить", AutomationId = "Message.Send", Command = messaging.SendTextCommand };
        var composer = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10, Children = { input, send } };
        Grid.SetColumn(send, 1);
        var refresh = DeepTheme.SecondaryButton("Обновить сообщения", "Conversation.Refresh");
        refresh.Command = messaging.RefreshCommand;
        var navigation = new HorizontalStackLayout { Spacing = 8, Children = { back, refresh } };
        var root = new Grid
        {
            AutomationId = "Did2Workspace.EmptyConversation", BindingContext = messaging,
            Padding = new Thickness(20, 22), RowSpacing = 12,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            Children = { navigation, title, state, accept, messages, composer,
                new Label { Text = "Вложения и группы ещё не подключены к DID2-транспорту.", FontSize = 12, TextColor = DeepTheme.Tertiary } }
        };
        for (var index = 0; index < root.Children.Count; index++) root.SetRow(root.Children[index], index);
        return root;
    }

    private View CreateMessagingStatus()
    {
        var unavailable = new Label
        {
            Text = messaging.HasRuntime ? "Локальная история доступна без сети. Для отправки проверьте регистрацию в настройках профиля." : "DID2-транспорт не подключён в этой сборке. Добавление контактов недоступно.",
            TextColor = DeepTheme.Secondary
        };
        unavailable.SetBinding(IsVisibleProperty, nameof(messaging.IsReady), converter: new NotConverter());
        var status = Status("Messaging.Status"); status.SetBinding(Label.TextProperty, nameof(messaging.Status));
        var error = Status("Messaging.Error"); error.TextColor = DeepTheme.Danger;
        error.SetBinding(Label.TextProperty, nameof(messaging.ErrorMessage));
        var busy = new ActivityIndicator { AutomationId = "Messaging.Busy", Color = DeepTheme.Accent };
        busy.SetBinding(ActivityIndicator.IsRunningProperty, nameof(messaging.IsBusy));
        busy.SetBinding(IsVisibleProperty, nameof(messaging.IsBusy));
        return new VerticalStackLayout { BindingContext = messaging, Spacing = 6, Children = { unavailable, status, error, busy } };
    }

    private abstract class DisplayConverter : IValueConverter
    {
        public abstract object Convert(object? value, Type targetType, object? parameter, CultureInfo culture);
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private sealed class ShortPeerConverter : DisplayConverter
    { public override object Convert(object? value, Type type, object? parameter, CultureInfo culture) => value is string peer && peer.Length >= 8 ? "Контакт " + peer[..8] : "Контакт"; }
    private sealed class ContactStateConverter : DisplayConverter
    {
        public override object Convert(object? value, Type type, object? parameter, CultureInfo culture) => value switch
        {
            DeepIdV2ContactState.IncomingRequest => "Входящий запрос",
            DeepIdV2ContactState.OutgoingRequest => "Ожидаем принятия",
            DeepIdV2ContactState.LocalAcceptanceRetained or DeepIdV2ContactState.PeerAcceptanceRetained => "Контакт принят",
            _ => "Состояние недоступно"
        };
    }
    private sealed class MessageAuthorConverter : DisplayConverter
    { public override object Convert(object? value, Type type, object? parameter, CultureInfo culture) => value is true ? "Вы" : "Контакт"; }
    private sealed class NotConverter : DisplayConverter
    { public override object Convert(object? value, Type type, object? parameter, CultureInfo culture) => value is not true; }
}
