using System.Text.Json;
using Deep.Client.Maui.Services;
using Deep.Client.Shared.Services;

namespace Deep.Client.Maui.Pages;

public partial class CallPage : ContentPage, IQueryAttributable
{
    private readonly CallSessionCoordinator coordinator;
    private CallDescriptor? call;
    private CancellationTokenSource? lifetime;
    private IDispatcherTimer? receiveTimer;
    private IDispatcherTimer? durationTimer;
    private DateTimeOffset? connectedAt;
    private bool webReady;
    private bool initialized;
    private bool isEnding;
    private bool receivingSignals;
    private bool microphoneEnabled = true;
    private bool cameraEnabled = true;

    public CallPage(CallSessionCoordinator coordinator)
    {
        InitializeComponent();
        this.coordinator = coordinator;
        CallWebView.SetInvokeJavaScriptTarget(this);
        CallWebView.HandlerChanged += OnWebViewHandlerChanged;
    }

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (!query.TryGetValue("call", out var value) || value is not CallDescriptor descriptor)
        {
            return;
        }

        call = descriptor;
        DisplayNameLabel.Text = descriptor.DisplayName;
        CameraButton.IsVisible = descriptor.IsVideo;
        CameraLabel.IsVisible = descriptor.IsVideo;
        SwitchCameraContainer.IsVisible = descriptor.IsVideo;
        cameraEnabled = descriptor.IsVideo;
        StatusLabel.Text = descriptor.IsIncoming ? "Входящий звонок" : "Вызов...";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        lifetime = new CancellationTokenSource();
        CallWebViewPlatform.Configure(CallWebView);
        TryInitialize();
        _ = WaitForWebRuntimeAsync(lifetime.Token);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        receiveTimer?.Stop();
        durationTimer?.Stop();
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        if (!isEnding && call is not null)
        {
            _ = SendByeSafelyAsync(call, "navigation");
        }
    }

    protected override bool OnBackButtonPressed()
    {
        _ = EndAndCloseAsync("back");
        return true;
    }

    private void OnWebViewHandlerChanged(object? sender, EventArgs e) =>
        CallWebViewPlatform.Configure(CallWebView);

    private async void OnRawMessageReceived(object? sender, HybridWebViewRawMessageReceivedEventArgs e) =>
        await HandleWebMessageAsync(e.Message);

    public Task OnWebMessage(string message) =>
        MainThread.InvokeOnMainThreadAsync(() => HandleWebMessageAsync(message));

    private async Task HandleWebMessageAsync(string? rawMessage)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rawMessage))
            {
                return;
            }

            using var document = JsonDocument.Parse(rawMessage);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            switch (type)
            {
                case "ready":
                    webReady = true;
                    TryInitialize();
                    break;
                case "signal":
                    await ForwardSignalAsync(root);
                    break;
                case "state":
                    UpdateConnectionState(root.GetProperty("state").GetString());
                    break;
                case "error":
                    StatusLabel.Text = root.TryGetProperty("message", out var message)
                        ? message.GetString() ?? "Ошибка звонка"
                        : "Ошибка звонка";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            CrashDiagnostics.LogInfo("CallPage.Signaling", exception.Message);
            StatusLabel.Text = "Переподключение...";
        }
        catch (Exception exception) when (IsCallException(exception))
        {
            CrashDiagnostics.LogException("CallPage.RawMessage", exception);
            StatusLabel.Text = "Не удалось установить звонок";
        }
    }

    private void TryInitialize()
    {
        if (!webReady || initialized || call is null || lifetime is null)
        {
            return;
        }

        initialized = true;
        _ = InitializeCallAsync(call, lifetime.Token);
    }

    private async Task WaitForWebRuntimeAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30 && !webReady; attempt++)
        {
            try
            {
                await Task.Delay(100, cancellationToken);
                var state = await CallWebView.EvaluateJavaScriptAsync("document.readyState");
                if (state?.Contains("complete", StringComparison.OrdinalIgnoreCase) == true)
                {
                    webReady = true;
                    TryInitialize();
                    return;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or TaskCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
            }
        }

        if (!webReady && !cancellationToken.IsCancellationRequested)
        {
            StatusLabel.Text = "Не удалось запустить защищенный медиаканал";
        }
    }

    private async Task InitializeCallAsync(CallDescriptor descriptor, CancellationToken cancellationToken)
    {
        try
        {
            var microphone = await Permissions.RequestAsync<Permissions.Microphone>();
            if (microphone != PermissionStatus.Granted)
            {
                await FailAndCloseAsync("Разрешите доступ к микрофону для звонка.");
                return;
            }

            if (descriptor.IsVideo)
            {
                var camera = await Permissions.RequestAsync<Permissions.Camera>();
                if (camera != PermissionStatus.Granted)
                {
                    await FailAndCloseAsync("Разрешите доступ к камере для видеозвонка.");
                    return;
                }
            }

            var ice = await coordinator.GetIceConfigurationAsync(descriptor.LocalParty, cancellationToken);
            var message = JsonSerializer.Serialize(new
            {
                command = "initialize",
                initiator = !descriptor.IsIncoming,
                video = descriptor.IsVideo,
                iceServers = ice.IceServers.Select(static server => new
                {
                    urls = server.Urls,
                    username = server.Username,
                    credential = server.Credential
                })
            });
            await SendToWebAsync(message);
            StartTimers();
            await ReceiveSignalsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsCallException(exception))
        {
            CrashDiagnostics.LogException("CallPage.Initialize", exception);
            StatusLabel.Text = "Не удалось подключиться";
        }
    }

    private async Task ForwardSignalAsync(JsonElement root)
    {
        if (call is null || lifetime is null)
        {
            return;
        }

        var type = root.GetProperty("signalType").GetString() switch
        {
            "offer" => CallSignalType.Offer,
            "answer" => CallSignalType.Answer,
            "ice" => CallSignalType.IceCandidate,
            _ => throw new InvalidOperationException("Unsupported WebRTC signal type.")
        };
        var payload = root.GetProperty("payload").GetRawText();
        await coordinator.SendAsync(call, type, payload, lifetime.Token);
    }

    private void StartTimers()
    {
        receiveTimer ??= Dispatcher.CreateTimer();
        receiveTimer.Interval = TimeSpan.FromMilliseconds(750);
        receiveTimer.Tick -= OnReceiveTimerTick;
        receiveTimer.Tick += OnReceiveTimerTick;
        receiveTimer.Start();

        durationTimer ??= Dispatcher.CreateTimer();
        durationTimer.Interval = TimeSpan.FromSeconds(1);
        durationTimer.Tick -= OnDurationTimerTick;
        durationTimer.Tick += OnDurationTimerTick;
        durationTimer.Start();
    }

    private async void OnReceiveTimerTick(object? sender, EventArgs e)
    {
        if (lifetime is null || lifetime.IsCancellationRequested || receivingSignals)
        {
            return;
        }

        try
        {
            receivingSignals = true;
            await ReceiveSignalsAsync(lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            CrashDiagnostics.LogInfo("CallPage.Signaling", exception.Message);
        }
        finally
        {
            receivingSignals = false;
        }
    }

    private async Task ReceiveSignalsAsync(CancellationToken cancellationToken)
    {
        if (call is null)
        {
            return;
        }

        var signals = await coordinator.ReceiveForCallAsync(call, cancellationToken);
        foreach (var signal in signals)
        {
            if (signal.Type == CallSignalType.Bye)
            {
                isEnding = true;
                StatusLabel.Text = "Звонок завершен";
                await Task.Delay(500, CancellationToken.None);
                await Shell.Current.GoToAsync("..");
                return;
            }

            var signalType = signal.Type switch
            {
                CallSignalType.Offer => "offer",
                CallSignalType.Answer => "answer",
                CallSignalType.IceCandidate => "ice",
                _ => null
            };
            if (signalType is null)
            {
                continue;
            }

            using var payloadDocument = JsonDocument.Parse(signal.Payload);
            await SendToWebAsync(JsonSerializer.Serialize(new
            {
                command = "signal",
                signalType,
                payload = payloadDocument.RootElement
            }));
        }
    }

    private void UpdateConnectionState(string? state)
    {
        StatusLabel.Text = state switch
        {
            "connected" => "Защищенное соединение",
            "connecting" => "Подключение...",
            "disconnected" => "Переподключение...",
            "failed" => "Соединение не установлено",
            "closed" => "Звонок завершен",
            _ => "Установка соединения..."
        };
        if (state == "connected" && connectedAt is null)
        {
            connectedAt = DateTimeOffset.UtcNow;
        }
    }

    private void OnDurationTimerTick(object? sender, EventArgs e)
    {
        if (connectedAt is null)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - connectedAt.Value;
        DurationLabel.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void OnMicrophoneClicked(object? sender, EventArgs e)
    {
        microphoneEnabled = !microphoneEnabled;
        SetControlState(MicrophoneButton, MicrophoneIcon, microphoneEnabled);
        SemanticProperties.SetDescription(MicrophoneButton, microphoneEnabled ? "Выключить микрофон" : "Включить микрофон");
        SendCommand("setMicrophone", microphoneEnabled);
    }

    private void OnCameraClicked(object? sender, EventArgs e)
    {
        cameraEnabled = !cameraEnabled;
        SetControlState(CameraButton, CameraIcon, cameraEnabled);
        SemanticProperties.SetDescription(CameraButton, cameraEnabled ? "Выключить камеру" : "Включить камеру");
        SendCommand("setCamera", cameraEnabled);
    }

    private static void SetControlState(Border control, Microsoft.Maui.Controls.Shapes.Path icon, bool enabled)
    {
        control.Background = new SolidColorBrush(Color.FromArgb(enabled ? "#E8F0F7" : "#33455A"));
        icon.Stroke = new SolidColorBrush(Color.FromArgb(enabled ? "#07111F" : "#FFFFFF"));
    }

    private void OnSwitchCameraClicked(object? sender, EventArgs e) => SendCommand("switchCamera");

    private async void OnHangupClicked(object? sender, EventArgs e) => await EndAndCloseAsync("hangup");

    private async void OnBackClicked(object? sender, EventArgs e) => await EndAndCloseAsync("back");

    private void SendCommand(string command, bool? enabled = null) =>
        _ = SendToWebAsync(JsonSerializer.Serialize(new { command, enabled }));

    private Task<string?> SendToWebAsync(string message)
    {
        var argument = JsonSerializer.Serialize(message);
        return CallWebView.EvaluateJavaScriptAsync($"window.DeepCallReceive({argument})");
    }

    private async Task EndAndCloseAsync(string reason)
    {
        if (isEnding)
        {
            return;
        }

        isEnding = true;
        if (call is not null)
        {
            await SendByeSafelyAsync(call, reason);
        }

        SendCommand("hangup");
        await Shell.Current.GoToAsync("..");
    }

    private async Task SendByeSafelyAsync(CallDescriptor descriptor, string reason)
    {
        try
        {
            await coordinator.SendAsync(descriptor, CallSignalType.Bye, JsonSerializer.Serialize(new { reason }));
        }
        catch (Exception exception) when (IsNetworkException(exception))
        {
            CrashDiagnostics.LogInfo("CallPage.Hangup", exception.Message);
        }
    }

    private async Task FailAndCloseAsync(string message)
    {
        await DisplayAlertAsync("Звонок Deep", message, "Закрыть");
        await EndAndCloseAsync("permission-denied");
    }

    private static bool IsCallException(Exception exception) =>
        exception is JsonException or InvalidOperationException || IsNetworkException(exception);

    private static bool IsNetworkException(Exception exception) =>
        exception is HttpRequestException or System.Net.WebException;
}
