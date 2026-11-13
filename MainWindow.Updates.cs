using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Notify.Desktop;

public partial class MainWindow
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);
    private readonly UpdateManager updates = new(Path.Combine(LocalSettings.DirectoryPath, "updates"));
    private readonly CallActivity callActivity = new();
    private DesktopUpdate? pendingUpdate;
    private string? pendingInstaller;
    private string? dismissedVersion;
    private CancellationTokenSource? updateCheck;
    private bool installRequested, installing;

    private void StartUpdates()
    {
        _ = RunUpdateChecksAsync(appLifetime.Token);
    }

    private async Task RunUpdateChecksAsync(CancellationToken ct)
    {
        try
        {
            await CheckUpdatesAsync(manual: false);
            using var timer = new PeriodicTimer(UpdateInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await CheckUpdatesAsync(manual: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async void CheckUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(manual: true);

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (exiting || installing || installRequested || updateCheck is not null) return;
        if (manual) dismissedVersion = null;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        updateCheck = check;
        check.CancelAfter(TimeSpan.FromMinutes(10));
        CancelDownload.IsEnabled = true;
        UpdateStatus.Text = "Проверяем GitHub Releases…";
        try
        {
            var installed = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            var release = await updates.CheckAsync(installed, check.Token);
            if (release is null || release.Version == dismissedVersion)
            {
                pendingUpdate = null;
                pendingInstaller = null;
                UpdateStatus.Text = release is null ? "Доступных обновлений нет." : $"Версия {release.Version} отложена.";
                UpdateOffer.Visibility = Visibility.Collapsed;
                return;
            }
            if (release == pendingUpdate && pendingInstaller is not null)
            {
                UpdateStatus.Text = $"Версия {release.Version} готова к установке.";
                return;
            }
            UpdateOffer.Visibility = Visibility.Collapsed;
            pendingInstaller = null;
            var progress = new Progress<int>(percent =>
            {
                if (updateCheck == check) UpdateStatus.Text = $"Скачиваем версию {release.Version} · {percent}%";
            });
            pendingInstaller = await updates.DownloadAsync(release, progress, check.Token);
            pendingUpdate = release;
            UpdateNotes.Text = release.Notes.Length <= 6000 ? release.Notes : release.Notes[..6000] + "…";
            UpdateOffer.Visibility = Visibility.Visible;
            UpdateStatus.Text = $"Версия {release.Version} готова к установке.";
            tray.ShowBalloonTip(6500, $"Notify Desktop {release.Version}", "Обновление скачано. Откройте настройки для установки.",
                Forms.ToolTipIcon.Info);
        }
        catch (OperationCanceledException)
        {
            if (!exiting) UpdateStatus.Text = "Проверка обновлений остановлена.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            UpdateStatus.Text = "Не удалось проверить или скачать обновление. Повторите проверку позже.";
        }
        finally
        {
            updateCheck = null;
            CancelDownload.IsEnabled = false;
        }
    }

    private void CancelUpdate(object sender, RoutedEventArgs e) => updateCheck?.Cancel();

    private void DismissUpdate(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        installRequested = false;
        dismissedVersion = pendingUpdate?.Version;
        UpdateOffer.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = $"Версия {dismissedVersion} отложена.";
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        installRequested = true;
        await InstallWhenIdleAsync();
    }

    private void ShowUpdates()
    {
        ShowWindow();
        SettingsNav.IsChecked = true;
        UpdateStatus.BringIntoView();
    }

    private void CallFinished()
    {
        callActivity.Leave();
        _ = Dispatcher.BeginInvoke(async () => await InstallWhenIdleAsync());
    }

    private async Task InstallWhenIdleAsync()
    {
        if (!installRequested || installing || exiting || pendingUpdate is null || pendingInstaller is null) return;
        if (callActivity.Busy)
        {
            UpdateStatus.Text = "Установка начнётся, когда телефонный помощник освободится.";
            return;
        }
        installing = true;
        try
        {
            // Verify again immediately before launching a previously downloaded installer.
            if (!await UpdateManager.MatchesAsync(pendingInstaller, pendingUpdate, appLifetime.Token))
                throw new InvalidDataException("Installer SHA-256 mismatch.");
            if (!installRequested || exiting) return;
            if (callActivity.Busy)
            {
                UpdateStatus.Text = "Установка начнётся, когда телефонный помощник освободится.";
                return;
            }
            var start = new ProcessStartInfo(pendingInstaller) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOTIFYUPDATE" })
                start.ArgumentList.Add(argument);
            using var installer = Process.Start(start) ?? throw new IOException("Installer did not start.");
            UpdateStatus.Text = $"Устанавливаем версию {pendingUpdate.Version}…";
            await installer.WaitForExitAsync(appLifetime.Token);
            if (!exiting) UpdateStatus.Text = callActivity.Busy ? "Установка начнётся, когда телефонный помощник освободится."
                : "Установка прервана. Повторите попытку после завершения звонков.";
        }
        catch (OperationCanceledException) when (appLifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            UpdateStatus.Text = "Установщик недоступен. Проверьте обновления ещё раз.";
        }
        finally
        {
            installing = false;
            if (!callActivity.Busy) installRequested = false;
        }
    }

    internal Task ExitForInstallerAsync() => callActivity.TryStop() ? ExitAsync() : Task.CompletedTask;
}

internal sealed class CallActivity
{
    private readonly object gate = new();
    private int active;
    private bool stopping;
    private bool maintaining;
    internal bool Busy { get { lock (gate) return active != 0 || maintaining; } }

    internal bool TryEnter()
    {
        lock (gate)
        {
            if (stopping || maintaining) return false;
            active++;
            return true;
        }
    }

    internal void Leave() { lock (gate) active--; }
    internal bool TryMaintain()
    {
        lock (gate)
        {
            if (active != 0 || stopping || maintaining) return false;
            return maintaining = true;
        }
    }
    internal void EndMaintenance() { lock (gate) maintaining = false; }

    internal bool TryStop()
    {
        lock (gate)
        {
            if (active != 0 || maintaining) return false;
            stopping = true;
            return true;
        }
    }
}
