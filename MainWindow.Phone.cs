using System.Windows;

namespace Notify.Desktop;

public partial class MainWindow
{
    private static string PhoneDirectory => Path.Combine(LocalSettings.DirectoryPath, "phone-assistant");
    private bool phoneEnabled;

    private static bool IsPhoneThread(Chat chat)
    {
        var path = Path.Combine(PhoneDirectory, "thread-id");
        return File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), chat.Id, StringComparison.OrdinalIgnoreCase);
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
        Volatile.Write(ref phoneEnabled, preferences.PhoneEnabled && codexAvailable);
        PhoneAssistantStatus.Text = !preferences.PhoneEnabled ? "Помощник отключён."
            : !codexAvailable ? "Codex CLI недоступен."
            : bridgeCancellation is null ? "Подключите чат к Notify."
            : "Телефонный режим включён для текущего агента.";
    }

    private void ShowPhoneCall(CancellationToken ct, string number, bool active) => _ = Dispatcher.BeginInvoke(() =>
    {
        if (ct.IsCancellationRequested) return;
        PhoneAssistantCall.Text = $"Номер {number} · {(active ? "звонок активен" : "нет активного звонка")}";
    });
}
