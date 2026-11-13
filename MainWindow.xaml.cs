using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Notify.Desktop;

public partial class MainWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ALXPRGS Notify Desktop";
    private readonly Forms.NotifyIcon tray;
    private readonly Queue<string> history = new();
    private readonly CancellationTokenSource appLifetime = new();
    private Preferences preferences = new();
    private CancellationTokenSource? bridgeCancellation;
    private Task? bridge;
    private Task? stoppingBridge;
    private bool exiting;
    private bool checking;

    public MainWindow() : this(visualTest: false) { }

    internal MainWindow(bool visualTest)
    {
        InitializeComponent();
        try { if (!visualTest) preferences = LocalSettings.Read(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { Report("Локальные настройки недоступны."); }
        Origin.Text = preferences.Origin;
        Client.SelectedIndex = preferences.Client == "Harness" ? 1 : 0;
        if (preferences.Chat is { } chat) { Chats.Items.Add(chat); Chats.SelectedItem = chat; }
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        AutoStart.IsChecked = run?.GetValue(RunValue) is string;
        InitializePresentation();
        LoadInstructions();
        DetectClients();
        ContentRendered += async (_, _) =>
        {
            if (!visualTest && Client.SelectedIndex == 0 && Chats.Items.Count == 0 && Codex.Find() is not null)
                await WithControlsAsync(LoadChatsAsync);
        };
        Closing += async (_, eventArgs) =>
        {
            if (exiting) return;
            eventArgs.Cancel = true;
            if (preferences.KeepInTray) Hide();
            else await ExitAsync();
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Notify", null, (_, _) => ShowWindow());
        menu.Items.Add("Проверить обновления", null, async (_, _) =>
        {
            ShowUpdates();
            await CheckUpdatesAsync(manual: true);
        });
        menu.Items.Add("Отключить", null, async (_, _) => await StopBridgeAsync());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        tray = new Forms.NotifyIcon
        {
            Icon = visualTest ? null : LoadTrayIcon(), Text = "ALXPRGS Notify Desktop",
            ContextMenuStrip = menu, Visible = !visualTest
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        tray.BalloonTipClicked += (_, _) => ShowUpdates();
    }

    internal void StartSaved()
    {
        Dispatcher.BeginInvoke(async () =>
        {
            StartUpdates();
            PhoneAssistantEnabled.IsChecked = preferences.PhoneEnabled;
            RefreshPhoneStatus();
            try
            {
                if (LocalSettings.ReadToken(LocalSettings.HttpsOrigin(preferences.Origin)) is not null &&
                    (preferences.Client == "Harness" || preferences.Chat is not null))
                    await ConnectAsync(installMcp: false);
            }
            catch (Exception error) { Report(Friendly(error)); }
        });
    }

    private void DetectClients()
    {
        codexInstalled = Codex.Find() is not null;
        harnessInstalled = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Any(path => File.Exists(Path.Combine(path, "dsh.cmd")) ||
                File.Exists(Path.Combine(path, "dsh.exe")));
        UpdateClientStates();
    }

    private void ClientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CodexPicker is not null)
            CodexPicker.Visibility = Client.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshChats(object sender, RoutedEventArgs e) => await WithControlsAsync(LoadChatsAsync);

    private async void FindAvailableChat(object sender, RoutedEventArgs e) => await WithControlsAsync(async ct =>
    {
        if (Chats.Items.Count == 0) await LoadChatsAsync(ct);
        if (Chats.Items.Count == 0) return;
        ChatStatus.Text = "Ищем сохранённый чат, который Codex может открыть…";
        try
        {
            var available = await RequireCodex().FindAvailableChatAsync(Chats.Items.OfType<Chat>(), ct);
            if (available is not null) Chats.SelectedItem = available;
            ChatStatus.Text = available is null ? "В загруженном списке нет доступного чата."
                : "Доступный чат выбран. Нажмите «Подключить».";
        }
        catch (Exception error) { ChatStatus.Text = $"Поиск чата · {Friendly(error)}"; }
    });

    private async Task LoadChatsAsync(CancellationToken ct)
    {
        var current = (Chats.SelectedItem as Chat)?.Id;
        ChatStatus.Text = "Загружаем список чатов Codex…";
        try
        {
            var chats = await RequireCodex().ChatsAsync(ct);
            chats.RemoveAll(chat => IsPhoneThread(chat));
            Chats.Items.Clear();
            foreach (var chat in chats) Chats.Items.Add(chat);
            Chats.SelectedItem = chats.FirstOrDefault(chat => chat.Id == current);
            ChatStatus.Text = chats.Count == 0
                ? "Codex не вернул сохранённых чатов."
                : $"Чатов в списке: {chats.Count}. Выберите чат.";
        }
        catch (Exception error)
        {
            ChatStatus.Text = $"Список чатов · {Friendly(error)}";
        }
    }

    private async void Connect(object sender, RoutedEventArgs e) => await ConnectAsync(installMcp: true);

    private Task ConnectAsync(bool installMcp) => WithControlsAsync(async ct =>
    {
        var origin = LocalSettings.HttpsOrigin(Origin.Text.Trim());
        var token = RequireToken(origin);
        var client = Client.SelectedIndex == 0 ? "Codex" : "Harness";
        var chat = Chats.SelectedItem as Chat;
        Codex? codex = null;
        if (client == "Codex")
        {
            codex = RequireCodex();
            if (chat is null)
            {
                if (Chats.Items.Count == 0) await LoadChatsAsync(ct);
                else ChatStatus.Text = "Выберите чат и нажмите «Подключить».";
                return;
            }
        }

        var api = new NotifyHttp(origin, token);
        var registration = await ReadRegistrationAsync(api, ct);
        ShowRegistration(registration);
        if (!registration.SupportedCallProtocols.Contains(AgentBridge.CallProtocol, StringComparer.Ordinal))
            throw new NotifyError("Сервер Notify должен поддерживать протокол звонков v2.");
        if (codex is not null && chat is not null)
        {
            if (IsPhoneThread(chat)) throw new NotifyError("Выберите обычный чат Codex.");
            ChatStatus.Text = "Проверяем доступность выбранного чата…";
            try { await codex.CheckChatAsync(chat, ct); }
            catch (NotifyError error)
            {
                ChatStatus.Text = error.Message;
                throw;
            }
            ChatStatus.Text = "Чат доступен для подключения.";
        }
        if (codex is not null && installMcp)
        {
            await codex.SetupMcpAsync(ct);
            try
            {
                await using var relay = new McpRelay(api);
                var count = await relay.ProbeAsync(ct);
                Report($"MCP Notify · доступно инструментов: {count}");
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            { Report($"MCP Notify · {Friendly(error)}"); }
        }

        await StopBridgeAsync();
        ct.ThrowIfCancellationRequested();
        var updated = preferences with { Origin = origin.GetLeftPart(UriPartial.Authority), Client = client, Chat = client == "Codex" ? chat : null };
        LocalSettings.Save(updated, Token.Password.Length > 0 ? token : null);
        preferences = updated;
        Token.Clear();
        TokenLabel.Text = "Токен источника сохранён · введите новый для замены";
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        bridgeCancellation = lifetime;
        SocketState.Text = client == "Harness" ? "Ждёт чат Harness" : "Подключается…";
        bridge = Task.Run(async () =>
        {
            try
            {
                if (codex is not null && chat is not null)
                    await RunBridgeAsync(chat, (text, cancellation) => ReplyToCodexAsync(codex, chat, text, lifetime.Token, cancellation), lifetime.Token);
                else
                    await new HarnessLink(Report).RunAsync(RunBridgeAsync, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Report(Friendly(error)); }
            finally
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (bridgeCancellation == lifetime) _ = StopBridgeAsync();
                });
            }
        });
        Stop.IsEnabled = true;
        RefreshPhoneStatus();
        UpdateClientStates();

        async Task RunBridgeAsync(Chat bound, Func<string, CancellationToken, Task<string>> reply, CancellationToken cancellation)
        {
            var executable = Codex.Find();
            await using var phone = executable is null ? null :
                new PhoneCodex(new Codex(executable), PhoneDirectory, usage => ShowPhoneUsage(cancellation, usage),
                    () => Volatile.Read(ref effectivePhoneInstructions));
            Volatile.Write(ref activePhone, phone);
            using var warmupLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var warmup = phone is null ? Task.CompletedTask : WarmPhoneAsync(phone, warmupLifetime.Token);
            try
            {
                await new AgentBridge(origin, token, registration, bound, reply, Report,
                    connectionChanged: state => ShowSocketState(cancellation, state),
                    enterCall: callActivity.TryEnter, leaveCall: CallFinished,
                    phoneReply: phone is null ? null : phone.ReplyAsync,
                    phoneEnabled: () => Volatile.Read(ref phoneEnabled),
                    phoneState: (number, active) => ShowPhoneCall(cancellation, number, active),
                    phoneReady: () => phone?.Ready == true).RunAsync(cancellation);
            }
            finally
            {
                Volatile.Write(ref activePhone, null);
                await warmupLifetime.CancelAsync();
                await warmup;
            }
        }
    });

    private async void Check(object sender, RoutedEventArgs e) => await WithControlsAsync(async ct =>
    {
        var origin = LocalSettings.HttpsOrigin(Origin.Text.Trim());
        var registration = await ReadRegistrationAsync(new NotifyHttp(origin, RequireToken(origin)), ct);
        ShowRegistration(registration);
        Report(registration.Online ? "Notify видит активное соединение агента." : "Агент зарегистрирован · сейчас не в сети.");
    });

    private string RequireToken(Uri origin)
    {
        if (Token.Password.Trim() is { Length: > 0 and <= 512 } entered && !entered.Any(char.IsControl)) return entered;
        if (Token.Password.Length != 0) throw new NotifyError("Некорректный токен источника.");
        if (origin != LocalSettings.HttpsOrigin(preferences.Origin))
            throw new NotifyError("Для нового адреса введите токен источника этого сервера.");
        return LocalSettings.ReadToken(origin) ?? throw new NotifyError("Создайте источник в кабинете Notify и введите его токен.");
    }

    private static Codex RequireCodex() => new(Codex.Find() ?? throw new NotifyError("Установите Codex CLI для Windows."));

    private async Task<Registration> ReadRegistrationAsync(NotifyHttp api, CancellationToken ct)
    {
        NotifyState.Text = "Проверяется…";
        var started = Stopwatch.GetTimestamp();
        try
        {
            var entry = await api.RegistrationAsync(ct);
            NotifyState.Text = "Доступен";
            NotifyLatency.Text = $"Время проверки Notify: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} мс";
            return entry;
        }
        catch (Exception error)
        {
            NotifyState.Text = error is HttpRequestException or OperationCanceledException ? "Недоступен" : "Доступ не подтверждён";
            NotifyLatency.Text = "Время проверки Notify: нет данных";
            AgentId.Clear();
            SourceId.Clear();
            throw;
        }
    }

    private void ShowRegistration(Registration entry)
    {
        AgentId.Text = entry.AgentId;
        SourceId.Text = entry.SourceId;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        using var stream = Application.GetResourceStream(new Uri("Assets/Notify.ico", UriKind.Relative)).Stream;
        using var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        return (System.Drawing.Icon)icon.Clone();
    }

    private async Task WithControlsAsync(Func<CancellationToken, Task> action)
    {
        if (checking || exiting) return;
        checking = true;
        Setup.IsEnabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try { await action(timeout.Token); }
        catch (Exception error) { Report(Friendly(error), "Ошибка"); }
        finally { Setup.IsEnabled = true; checking = false; }
    }

    private async void Disconnect(object sender, RoutedEventArgs e) => await StopBridgeAsync();

    private async Task StopBridgeAsync()
    {
        if (stoppingBridge is not null) await stoppingBridge;
        var cancellation = bridgeCancellation;
        var task = bridge;
        bridgeCancellation = null;
        bridge = null;
        Stop.IsEnabled = false;
        if (cancellation is not null)
        {
            stoppingBridge = FinishAsync();
            await stoppingBridge;
            stoppingBridge = null;
            cancellation.Dispose();
        }
        agentConnected = false;
        codexReplying = false;
        phoneReady = false;
        phoneActive = false;
        phoneFailed = false;
        PhoneHomeContext.Text = "";
        PhoneContextMeter.Visibility = Visibility.Collapsed;
        RefreshPhoneStatus();
        PhoneAssistantCall.Text = "";
        SocketState.Text = "Отключён";
        tray.Text = "ALXPRGS Notify · Отключён";
        UpdateClientStates();
        Report("Отключено");

        async Task FinishAsync()
        {
            await cancellation!.CancelAsync();
            if (task is not null) await task;
        }
    }

    internal async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        await appLifetime.CancelAsync();
        await StopBridgeAsync();
        appLifetime.Dispose();
        tray.Visible = false;
        var icon = tray.Icon;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        icon?.Dispose();
        Application.Current.Shutdown();
    }

    private void Report(string message) => Report(message, "Сведения");

    private void Report(string message, string level)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Report(message, level)); return; }
        Status.Text = message;
        history.Enqueue($"{DateTime.Now:HH:mm:ss} · {level} · {message}");
        while (history.Count > 15) history.Dequeue();
        History.Text = string.Join(Environment.NewLine, history.Reverse());
    }

    private static string Friendly(Exception error) => error switch
    {
        NotifyError => error.Message,
        OperationCanceledException => "Истекло время ожидания.",
        HttpRequestException => "Не удалось связаться с Notify по HTTPS.",
        _ => "Операция недоступна. Повторите попытку."
    };

    private void OpenCabinet(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(LocalSettings.HttpsOrigin(Origin.Text.Trim()).AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { Report(Friendly(error)); }
    }

    private void ChangeStartup(object sender, RoutedEventArgs e)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            if (AutoStart.IsChecked == true)
                run.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
            else run.DeleteValue(RunValue, false);
        }
        catch (Exception error) { Report(Friendly(error)); }
    }

    internal void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void HideWindow(object sender, RoutedEventArgs e) => Hide();
}
