using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Notify.Desktop;

public partial class MainWindow
{
    private ResourceDictionary? fluentTheme;
    private bool codexInstalled;
    private bool harnessInstalled;
    private bool presentationReady;
    private bool agentConnected;
    private bool codexReplying;

    private void InitializePresentation()
    {
        Appearance.SelectedIndex = preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        KeepInTray.IsChecked = preferences.KeepInTray;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += WindowsAppearanceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= WindowsAppearanceChanged;
        presentationReady = true;
    }

    private void WindowsAppearanceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility)
            Dispatcher.BeginInvoke(() => { if (!exiting) ApplyTheme(); });
    }

    private void ApplyTheme()
    {
        using var personalization = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dark = preferences.Theme == "Dark" ||
            (preferences.Theme != "Light" && personalization?.GetValue("AppsUseLightTheme") is 0);
        var themeFile = SystemParameters.HighContrast ? "Fluent" : dark ? "Fluent.Dark" : "Fluent.Light";
        var source = new Uri($"/PresentationFramework.Fluent;component/Themes/{themeFile}.xaml", UriKind.Relative);
        if (fluentTheme?.Source != source)
        {
            if (fluentTheme is not null) Resources.MergedDictionaries.Remove(fluentTheme);
            fluentTheme = new ResourceDictionary { Source = source };
            Resources.MergedDictionaries.Add(fluentTheme);
        }

        SetBrush("AppBackground", "#F3F5FA", "#141821", SystemColors.WindowBrush);
        SetBrush("Chrome", "#F9FAFD", "#181D28", SystemColors.WindowBrush);
        SetBrush("Surface", "#FFFFFF", "#202736", SystemColors.WindowBrush);
        SetBrush("Outline", "#DCE2ED", "#343F54", SystemColors.WindowTextBrush);
        SetBrush("SelectedSurface", "#DDEEF1", "#25434B", SystemColors.ControlBrush);
        SetBrush("PrimaryText", "#17243A", "#F2F5FC", SystemColors.WindowTextBrush);
        SetBrush("SecondaryText", "#4A5D74", "#B9C8DB", SystemColors.WindowTextBrush);
        SetBrush("AccentDetail", "#006A70", "#75DED2", SystemColors.HotTrackBrush);
        SetBrush("AccentFill", "#006A70", "#75DED2", SystemColors.HighlightBrush);
        SetBrush("AccentText", "#FFFFFF", "#102A2B", SystemColors.HighlightTextBrush);

        void SetBrush(string key, string light, string night, Brush contrast)
        {
            if (SystemParameters.HighContrast)
            {
                Resources[key] = contrast;
                return;
            }

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
            brush.Freeze();
            Resources[key] = brush;
        }
    }

    private void ChangeAppearance(object sender, RoutedEventArgs e)
    {
        if (!presentationReady || Appearance.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string theme })
            return;
        if (SavePresentation(preferences with { Theme = theme }))
            ApplyTheme();
        else
            Appearance.SelectedIndex = preferences.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
    }

    private void ChangeBackground(object sender, RoutedEventArgs e)
    {
        if (!SavePresentation(preferences with { KeepInTray = KeepInTray.IsChecked == true }))
            KeepInTray.IsChecked = preferences.KeepInTray;
    }

    private bool SavePresentation(Preferences updated)
    {
        if (updated == preferences) return true;
        try
        {
            LocalSettings.Save(updated);
            preferences = updated;
            return true;
        }
        catch (Exception error)
        {
            Report(Friendly(error));
            return false;
        }
    }

    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (PageScroll is null || sender is not FrameworkElement { Tag: string page }) return;
        HomePage.Visibility = page == "0" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionsPage.Visibility = page == "1" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "2" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "3" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
        if (page == "1") DetectClients();
    }

    private void OpenConnections(object sender, RoutedEventArgs e) => ConnectionsNav.IsChecked = true;

    private void CopyId(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string field }) return;
        var value = field == "Agent" ? AgentId.Text : SourceId.Text;
        if (value.Length == 0) return;
        try
        {
            Clipboard.SetText(value);
            Report(field == "Agent" ? "Agent ID скопирован." : "Source ID скопирован.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Report("Буфер обмена занят. Попробуйте ещё раз.");
        }
    }

    private void UpdateClientStates()
    {
        var active = bridgeCancellation is not null && preferences.Client == "Codex" ? preferences.Chat : null;
        CodexState.Text = active is not null ? codexReplying ? "Отвечает" : "Готов"
            : codexInstalled ? "Чат не подключён" : "Недоступен";
        CurrentCodexChat.Text = active is null ? "" : $"Текущий чат: {active.Name}";
        HarnessState.Text = agentConnected && preferences.Client == "Harness"
            ? "Подключён"
            : harnessInstalled ? "Установлен, но неактивен" : "Не найден";
    }

    private async Task<string> ReplyToCodexAsync(Codex codex, Chat chat, string text, CancellationToken lifetime, CancellationToken ct)
    {
        SetReplying(true);
        try
        {
            var extra = Volatile.Read(ref effectiveChatInstructions);
            var prompt = extra is null ? text : $"Мои инструкции для этого ответа:\n{extra}\n\nМой запрос:\n{text}";
            return await codex.ReplyAsync(chat, prompt, ct);
        }
        finally { SetReplying(false); }

        void SetReplying(bool value) => Dispatcher.BeginInvoke(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            codexReplying = value;
            UpdateClientStates();
        });
    }

    private void ShowSocketState(CancellationToken lifetime, BridgeConnection state) => Dispatcher.BeginInvoke(() =>
    {
        if (lifetime.IsCancellationRequested || exiting) return;
        agentConnected = state == BridgeConnection.Connected;
        SocketState.Text = state switch
        {
            BridgeConnection.Connecting => "Подключается…",
            BridgeConnection.Connected => "Подключён",
            BridgeConnection.Reconnecting => "Переподключается…",
            _ => "Отключён"
        };
        tray.Text = "ALXPRGS Notify · " + SocketState.Text;
        UpdateClientStates();
        UpdatePhoneHome();
    });
}
