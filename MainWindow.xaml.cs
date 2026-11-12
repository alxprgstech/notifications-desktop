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
    private bool exiting;
    private bool checking;

    public MainWindow()
    {
        InitializeComponent();
        try { preferences = LocalSettings.Read(); }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { Report("Локальные настройки недоступны."); }
        Origin.Text = preferences.Origin;
        Client.SelectedIndex = preferences.Client == "Harness" ? 1 : 0;
        if (preferences.Chat is { } chat) { Chats.Items.Add(chat); Chats.SelectedItem = chat; }
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        AutoStart.IsChecked = run?.GetValue(RunValue) is string;
        DetectClients();
        ContentRendered += async (_, _) =>
        {
            if (Client.SelectedIndex == 0 && Chats.Items.Count == 0 && Codex.Find() is not null)
                await WithControlsAsync(LoadChatsAsync);
        };
        Closing += (_, eventArgs) => { if (!exiting) { eventArgs.Cancel = true; Hide(); } };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Notify", null, (_, _) => ShowWindow());
        menu.Items.Add("Отключить", null, async (_, _) => await StopBridgeAsync());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Information, Text = "ALXPRGS Notify Desktop",
            ContextMenuStrip = menu, Visible = true
        };
        tray.DoubleClick += (_, _) => ShowWindow();
    }

    internal void StartSaved()
    {
        Dispatcher.BeginInvoke(async () =>
        {
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
        var codex = Codex.Find() is not null;
        var harness = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Any(path => File.Exists(Path.Combine(path, "dsh.cmd")) ||
                File.Exists(Path.Combine(path, "dsh.exe")));
        Detection.Text = $"Codex · {(codex ? "найден" : "не найден")}     Harness · {(harness ? "найден" : "не найден")}";
    }

    private void ClientChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CodexPicker is not null)
            CodexPicker.Visibility = Client.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshChats(object sender, RoutedEventArgs e) => await WithControlsAsync(LoadChatsAsync);

    private async Task LoadChatsAsync(CancellationToken ct)
    {
        var current = (Chats.SelectedItem as Chat)?.Id;
        ChatStatus.Text = "Загружаем список чатов Codex…";
        try
        {
            var chats = await RequireCodex().ChatsAsync(ct);
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
        var registration = await api.RegistrationAsync(ct);
        ShowRegistration(registration);
        if (!registration.SupportedCallProtocols.Contains(AgentBridge.CallProtocol, StringComparer.Ordinal))
            throw new NotifyError("Сервер Notify должен поддерживать протокол звонков v2.");
        if (codex is not null && chat is not null)
        {
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
        preferences = new Preferences(origin.GetLeftPart(UriPartial.Authority), client, client == "Codex" ? chat : null);
        LocalSettings.Save(preferences, Token.Password.Length > 0 ? token : null);
        Token.Clear();
        TokenLabel.Text = "Токен источника сохранён · введите новый для замены";
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        bridgeCancellation = lifetime;
        bridge = Task.Run(async () =>
        {
            try
            {
                if (codex is not null && chat is not null)
                    await RunBridgeAsync(chat, (text, cancellation) => codex.ReplyAsync(chat, text, cancellation), lifetime.Token);
                else
                    await new HarnessLink(Report).RunAsync(RunBridgeAsync, lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Report(Friendly(error)); }
        });
        Stop.IsEnabled = true;

        Task RunBridgeAsync(Chat bound, Func<string, CancellationToken, Task<string>> reply, CancellationToken cancellation) =>
            new AgentBridge(origin, token, registration, bound, reply, Report).RunAsync(cancellation);
    });

    private async void Check(object sender, RoutedEventArgs e) => await WithControlsAsync(async ct =>
    {
        var origin = LocalSettings.HttpsOrigin(Origin.Text.Trim());
        var registration = await new NotifyHttp(origin, RequireToken(origin)).RegistrationAsync(ct);
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

    private void ShowRegistration(Registration entry) =>
        Registration.Text = $"Source ID  {entry.SourceId}\nAgent ID  {entry.AgentId}";

    private async Task WithControlsAsync(Func<CancellationToken, Task> action)
    {
        if (checking || exiting) return;
        checking = true;
        Setup.IsEnabled = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try { await action(timeout.Token); }
        catch (Exception error) { Report(Friendly(error)); }
        finally { Setup.IsEnabled = true; checking = false; }
    }

    private async void Disconnect(object sender, RoutedEventArgs e) => await StopBridgeAsync();

    private async Task StopBridgeAsync()
    {
        var cancellation = bridgeCancellation;
        var task = bridge;
        bridgeCancellation = null;
        bridge = null;
        Stop.IsEnabled = false;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync();
            if (task is not null) await task;
            cancellation.Dispose();
        }
        Report("Отключено");
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        await appLifetime.CancelAsync();
        await StopBridgeAsync();
        appLifetime.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    private void Report(string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Report(message)); return; }
        Status.Text = message;
        history.Enqueue($"{DateTime.Now:HH:mm:ss}   {message}");
        while (history.Count > 5) history.Dequeue();
        History.Text = string.Join(Environment.NewLine, history.Reverse());
    }

    private static string Friendly(Exception error) => error switch
    {
        NotifyError => error.Message,
        OperationCanceledException => "Истекло время ожидания.",
        HttpRequestException => "Не удалось связаться с Notify по HTTPS.",
        _ => $"Операция недоступна · {error.GetType().Name}"
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

    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void HideWindow(object sender, RoutedEventArgs e) => Hide();
}
