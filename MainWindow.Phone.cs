using System.Windows;

namespace Notify.Desktop;

public partial class MainWindow
{
    private static string PhoneDirectory => Path.Combine(LocalSettings.DirectoryPath, "phone-assistant");
    private bool phoneEnabled;
    private readonly SemaphoreSlim phoneEnabledSignal = new(0);
    private bool phoneReady;
    private bool phoneFailed;
    private bool phoneActive;
    private bool phoneMaintaining;
    private PhoneCodex? activePhone;
    private int rolloverPercent = 85;
    private string? effectivePhoneInstructions;
    private string? effectiveChatInstructions;

    private static string? Combine(string general, string mode)
    {
        var result = string.Join("\n\n", new[] { general, mode }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return result.Length == 0 ? null : result;
    }

    private void LoadInstructions()
    {
        GeneralInstructions.Text = preferences.GeneralInstructions;
        PhoneInstructions.Text = preferences.PhoneInstructions;
        ChatInstructions.Text = preferences.ChatInstructions;
        RolloverPercent.Text = Math.Clamp(preferences.PhoneRolloverPercent, 70, 100).ToString();
        Volatile.Write(ref rolloverPercent, int.Parse(RolloverPercent.Text));
        Volatile.Write(ref effectivePhoneInstructions, Combine(GeneralInstructions.Text, PhoneInstructions.Text));
        Volatile.Write(ref effectiveChatInstructions, Combine(GeneralInstructions.Text, ChatInstructions.Text));
    }

    private void SaveInstructions(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RolloverPercent.Text, out var percent) || percent is < 70 or > 100)
        {
            Report("Введите порог от 70 до 100 %.");
            return;
        }
        if (SavePresentation(preferences with { GeneralInstructions = GeneralInstructions.Text.Trim(),
            PhoneInstructions = PhoneInstructions.Text.Trim(), ChatInstructions = ChatInstructions.Text.Trim(),
            PhoneRolloverPercent = percent }))
        {
            LoadInstructions();
            Report("Инструкции и порог сохранены.");
        }
    }

    private void ResetInstructions(object sender, RoutedEventArgs e)
    {
        if (SavePresentation(preferences with { GeneralInstructions = "", PhoneInstructions = "", ChatInstructions = "", PhoneRolloverPercent = 85 }))
            LoadInstructions();
    }

    private void PreviewInstructions(object sender, RoutedEventArgs e)
    {
        var preview = $"Телефонный помощник\n\n{PhoneCodex.Instructions}\n\n{Combine(GeneralInstructions.Text, PhoneInstructions.Text)}\n\n" +
            $"Обычный чат · дополнительные инструкции\n" +
            $"Codex получает их вместе с репликой и сохраняет в истории чата.\n\n{Combine(GeneralInstructions.Text, ChatInstructions.Text) ?? "Нет"}";
        var box = new System.Windows.Controls.TextBox { Text = preview, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto, Padding = new Thickness(12) };
        new Window { Owner = this, Title = "Предпросмотр инструкций Codex", Width = 620, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = box }.ShowDialog();
    }

    private async void NewPhoneThread(object sender, RoutedEventArgs e) => await MaintainPhoneAsync(preserveContext: false);
    private async void CompactPhoneThread(object sender, RoutedEventArgs e) => await MaintainPhoneAsync(preserveContext: false, compact: true);

    private async Task MaintainPhoneAsync(bool preserveContext, bool compact = false)
    {
        var phone = Volatile.Read(ref activePhone);
        if (bridgeCancellation is not { } lifetime || phone is null || !preferences.PhoneEnabled)
        {
            if (!preserveContext) Report("Включите телефонного помощника и подключите агента.");
            return;
        }
        if (preserveContext && !(phone.ContextPercent >= Volatile.Read(ref rolloverPercent))) return;
        if (!callActivity.TryMaintain())
        {
            if (!preserveContext) Report("Дождитесь завершения звонка.");
            return;
        }
        phoneMaintaining = true;
        UpdatePhoneHome();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            if (compact) await phone.CompactAsync(timeout.Token);
            else await phone.RolloverAsync(preserveContext, timeout.Token);
            Report(compact ? "Контекст телефонного чата сжат." : "Новый телефонный чат готов.");
        }
        catch (Exception error) { Report($"Телефонный чат · {Friendly(error)}"); }
        finally
        {
            callActivity.EndMaintenance();
            phoneMaintaining = false;
            UpdatePhoneHome();
            if (!lifetime.IsCancellationRequested) await InstallWhenIdleAsync();
        }
    }

    private void UpdatePhoneHome()
    {
        PhoneHomeState.Text = !preferences.PhoneEnabled ? "Отключён"
            : phoneActive ? "Активный звонок"
            : phoneMaintaining ? "Обновляется контекст"
            : !Volatile.Read(ref phoneEnabled) ? "Ошибка: Codex CLI недоступен"
            : phoneFailed ? "Ошибка подготовки Codex"
            : phoneReady && agentConnected ? "Готов к звонку"
            : !agentConnected ? "Нет соединения агента" : "Codex готовится…";
        PhoneAssistantStatus.Text = PhoneHomeState.Text;
    }

    private void ShowPhoneUsage(CancellationToken ct, PhoneUsage? usage) => _ = Dispatcher.BeginInvoke(() =>
    {
        if (ct.IsCancellationRequested) return;
        var percent = usage?.Percent;
        PhoneContextMeter.Visibility = percent is null ? Visibility.Collapsed : Visibility.Visible;
        PhoneContextMeter.Value = Math.Clamp(percent ?? 0, 0, 100);
        var context = percent is null ? "Нет данных" : $"{usage!.Context:N0} / {usage.Window:N0} ({percent:F0} %)";
        var total = usage?.Total is long count ? $" · Всего токенов: {count:N0}" : "";
        PhoneHomeContext.Text = $"Сессия: {PhoneCodex.Name} · Контекст: {context}{total}" +
            (percent >= 70 ? " · Контекст заполнен на 70 % или более" : "");
    });

    private static bool IsPhoneThread(Chat chat)
    {
        var path = Path.Combine(PhoneDirectory, "thread-id");
        return (chat.Directory is { } directory && string.Equals(Path.TrimEndingDirectorySeparator(directory),
                Path.TrimEndingDirectorySeparator(PhoneDirectory), StringComparison.OrdinalIgnoreCase))
            || (File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), chat.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void ChangePhoneMode(object sender, RoutedEventArgs e)
    {
        try
        {
            var updated = preferences with { PhoneEnabled = PhoneAssistantEnabled.IsChecked == true };
            LocalSettings.Save(updated);
            preferences = updated;
            RefreshPhoneStatus();
        }
        catch (Exception error)
        {
            PhoneAssistantEnabled.IsChecked = preferences.PhoneEnabled;
            PhoneAssistantStatus.Text = Friendly(error);
        }
    }

    private void RefreshPhoneStatus()
    {
        var codexAvailable = Codex.Find() is not null;
        var enabled = preferences.PhoneEnabled && codexAvailable;
        if (enabled != Volatile.Read(ref phoneEnabled))
        {
            Volatile.Write(ref phoneEnabled, enabled);
            if (enabled) phoneEnabledSignal.Release();
        }
        UpdatePhoneHome();
    }

    private async Task WarmPhoneAsync(PhoneCodex phone, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!Volatile.Read(ref phoneEnabled))
                {
                    await phoneEnabledSignal.WaitAsync(ct);
                    continue;
                }
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attempt.CancelAfter(TimeSpan.FromSeconds(20));
                    await phone.WarmAsync(attempt.Token);
                    ShowPhoneReadiness(ct, true);
                    await phone.WaitForServerExitAsync(ct);
                    ShowPhoneReadiness(ct, false);
                }
                catch (Exception error) when (!ct.IsCancellationRequested)
                {
                    Report($"Телефонный помощник · {Friendly(error)}");
                    ShowPhoneReadiness(ct, false);
                    await Task.Delay(TimeSpan.FromSeconds(20), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private void ShowPhoneReadiness(CancellationToken ct, bool ready) => _ = Dispatcher.BeginInvoke(() =>
    {
        if (ct.IsCancellationRequested) return;
        phoneReady = ready;
        phoneFailed = !ready;
        UpdatePhoneHome();
        if (ready && PhoneHomeContext.Text.Length == 0) ShowPhoneUsage(ct, null);
    });

    private void ShowPhoneCall(CancellationToken ct, string number, bool active) => _ = Dispatcher.BeginInvoke(() =>
    {
        if (ct.IsCancellationRequested) return;
        phoneActive = active;
        UpdatePhoneHome();
        if (!active) _ = MaintainPhoneAsync(preserveContext: true);
        PhoneAssistantCall.Text = $"Номер {number} · {(active ? "звонок активен" : "нет активного звонка")}";
    });
}
