using System.Windows;

namespace Notify.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? phoneCancellation;
    private Task? phoneBridge;
    private string phoneExtension = "";

    private void StartSavedPhone()
    {
        PhoneAssistantStatus.Text = Codex.Find() is null ? "Codex CLI недоступен." : "Помощник отключён.";
        try
        {
            var origin = LocalSettings.HttpsOrigin(preferences.Origin);
            if (LocalSettings.ReadToken(origin, phone: true) is { } token)
                StartPhone(origin, token);
        }
        catch (Exception error) { PhoneAssistantStatus.Text = Friendly(error); }
    }

    private void EnablePhone(object sender, RoutedEventArgs e)
    {
        if (phoneCancellation is not null || exiting) return;
        try
        {
            var origin = LocalSettings.HttpsOrigin(preferences.Origin);
            var token = PhoneAssistantToken.Password.Trim();
            if (token.Length == 0 || token.Any(char.IsControl))
                throw new NotifyError("Введите токен отдельного источника телефонного помощника.");
            if (token == LocalSettings.ReadToken(origin))
                throw new NotifyError("Для телефонного помощника нужен отдельный источник Notify.");
            LocalSettings.SavePhoneToken(origin, token);
            PhoneAssistantToken.Clear();
            StartPhone(origin, token);
        }
        catch (Exception error) { PhoneAssistantStatus.Text = Friendly(error); }
    }

    private async void DisablePhone(object sender, RoutedEventArgs e)
    {
        try
        {
            LocalSettings.RemovePhoneToken();
            await StopPhoneAsync();
            PhoneAssistantStatus.Text = "Помощник отключён.";
            PhoneAssistantCall.Text = "";
        }
        catch (Exception error) { PhoneAssistantStatus.Text = Friendly(error); }
    }

    private void StartPhone(Uri origin, string token)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        phoneCancellation = lifetime;
        phoneBridge = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var opening = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    opening.CancelAfter(TimeSpan.FromSeconds(60));
                    var api = new NotifyHttp(origin, token);
                    var registration = await api.RegistrationAsync(opening.Token);
                    if (token == LocalSettings.ReadToken(origin))
                        throw new NotifyError("Телефонный помощник и обычное подключение используют одного агента.");
                    using var route = await api.Transport.SendAsync(api.Request(HttpMethod.Get, "/api/v1/telephony/assistant"), opening.Token);
                    if (!route.IsSuccessStatusCode) throw new NotifyError("Сервер Notify не настроил телефонный маршрут для этого агента.");
                    using var data = JsonDocument.Parse(await route.Content.ReadAsByteArrayAsync(opening.Token));
                    var number = data.RootElement.String("extension") ?? throw new NotifyError("На сервере Notify не указан внутренний номер.");
                    ShowState("Проверяем вход в Codex…", number);
                    using var phone = new PhoneCodex(RequireCodex(), Path.Combine(LocalSettings.DirectoryPath, "phone-assistant"));
                    await phone.WarmAsync(opening.Token);
                    await new AgentBridge(origin, token, registration, new Chat(AgentBridge.PhoneSessionId, PhoneCodex.Name),
                        (_, _) => throw new InvalidOperationException(), _ => { }, connectionChanged: state =>
                        ShowState(phone.Ready
                            ? state == BridgeConnection.Connected ? "Codex доступен · помощник на связи." : "Codex доступен · подключение к Notify…"
                            : "Codex недоступен · повторное подключение…", number),
                        enterCall: callActivity.TryEnter, leaveCall: CallFinished, phone: phone, phoneState: (extension, active) =>
                        {
                            _ = Dispatcher.BeginInvoke(() =>
                            {
                                if (phoneCancellation != lifetime) return;
                                phoneExtension = extension;
                                PhoneAssistantCall.Text = $"Номер {phoneExtension} · {(active ? "звонок активен" : "нет активного звонка")}";
                            });
                        }).RunAsync(lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    ShowState(Friendly(error), "");
                    try { await Task.Delay(TimeSpan.FromSeconds(20), lifetime.Token); }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                }
            }
        });

        void ShowState(string status, string extension) => _ = Dispatcher.BeginInvoke(() =>
        {
            if (phoneCancellation != lifetime) return;
            PhoneAssistantStatus.Text = status;
            if (extension.Length != 0) phoneExtension = extension;
            if (phoneExtension.Length != 0 && !callActivity.Active)
                PhoneAssistantCall.Text = $"Номер {phoneExtension} · нет активного звонка";
        });
    }

    private async Task StopPhoneAsync()
    {
        var lifetime = phoneCancellation;
        var task = phoneBridge;
        if (lifetime is null) return;
        await lifetime.CancelAsync();
        if (task is not null) await task;
        phoneCancellation = null;
        phoneBridge = null;
        lifetime.Dispose();
    }
}
