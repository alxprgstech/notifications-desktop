using System.Diagnostics;
using System.Reflection;
using System.Windows;

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
    private CancellationTokenSource? updateDownload;
    private bool installRequested, installing;
    private static string InstalledVersion => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    private void StartUpdates()
    {
        InstalledUpdateVersion.Text = $"Установлена версия {InstalledVersion}";
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
        if (exiting || installing || installRequested || updateCheck is not null || updateDownload is not null) return;
        if (manual) dismissedVersion = null;
        using var check = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        updateCheck = check;
        check.CancelAfter(TimeSpan.FromSeconds(45));
        UpdateControls();
        UpdateStatus.Text = "Проверяем GitHub Releases…";
        try
        {
            var release = await updates.CheckAsync(InstalledVersion, check.Token);
            LatestUpdateVersion.Text = $"Последняя стабильная версия · {updates.LatestStableVersion ?? "Нет данных"}";
            if (release is null || release.Version == dismissedVersion)
            {
                pendingUpdate = null;
                pendingInstaller = null;
                UpdateStatus.Text = release is null ? "Доступных обновлений нет." : $"Версия {release.Version} отложена.";
                UpdateIntegrity.Text = "Установщик не загружен.";
                return;
            }
            if (release == pendingUpdate && pendingInstaller is not null)
            {
                UpdateStatus.Text = $"Версия {release.Version} готова к установке.";
                return;
            }
            pendingInstaller = null;
            pendingUpdate = release;
            UpdateNotes.Text = release.Notes.Length <= 6000 ? release.Notes : release.Notes[..6000] + "…";
            UpdateStatus.Text = $"Доступна версия {release.Version}.";
            UpdateIntegrity.Text = "Установщик не загружен.";
        }
        catch (OperationCanceledException)
        {
            if (!exiting) UpdateStatus.Text = "Проверка обновлений остановлена.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            UpdateStatus.Text = "Не удалось проверить обновления. Повторите проверку позже.";
        }
        finally
        {
            updateCheck = null;
            UpdateCheckedAt.Text = $"Последняя проверка · {DateTimeOffset.Now:g}";
            UpdateControls();
        }
    }

    private async void DownloadUpdate(object sender, RoutedEventArgs e)
    {
        if (pendingUpdate is not { } release || updateCheck is not null || updateDownload is not null || exiting) return;
        using var download = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        updateDownload = download;
        download.CancelAfter(TimeSpan.FromMinutes(10));
        UpdateStatus.Text = $"Скачиваем версию {release.Version}…";
        UpdateIntegrity.Text = "Проверка SHA-256 ожидает завершения загрузки.";
        UpdateControls();
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (updateDownload == download) UpdateStatus.Text = $"Скачиваем версию {release.Version} · {percent}%";
            });
            pendingInstaller = await updates.DownloadAsync(release, progress, download.Token);
            UpdateStatus.Text = $"Версия {release.Version} готова к установке.";
            UpdateIntegrity.Text = "SHA-256 подтверждён.";
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "Загрузка остановлена.";
            UpdateIntegrity.Text = "Установщик не загружен.";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            UpdateStatus.Text = "Загрузка не завершена. Повторите попытку.";
            UpdateIntegrity.Text = error is InvalidDataException ? "Проверка файла не пройдена." : "SHA-256 не проверен.";
        }
        finally
        {
            updateDownload = null;
            UpdateControls();
        }
    }

    private void UpdateControls()
    {
        var idle = updateCheck is null && updateDownload is null && !installing && !installRequested;
        CheckForUpdates.IsEnabled = idle;
        CancelDownload.IsEnabled = updateDownload is not null;
        UpdateOffer.Visibility = pendingUpdate is not null && pendingUpdate.Version != dismissedVersion
            ? Visibility.Visible : Visibility.Collapsed;
        DownloadInstaller.Visibility = pendingInstaller is null ? Visibility.Visible : Visibility.Collapsed;
        DownloadInstaller.IsEnabled = idle;
        InstallInstaller.IsEnabled = idle && pendingInstaller is not null;
        DeferUpdate.IsEnabled = updateCheck is null && updateDownload is null && !installing;
    }

    private void CancelUpdate(object sender, RoutedEventArgs e) => updateDownload?.Cancel();

    private void DismissUpdate(object sender, RoutedEventArgs e)
    {
        if (installing) return;
        installRequested = false;
        dismissedVersion = pendingUpdate?.Version;
        UpdateStatus.Text = $"Версия {dismissedVersion} отложена.";
        UpdateControls();
    }

    private async void InstallUpdate(object sender, RoutedEventArgs e)
    {
        if (pendingInstaller is null || installing || installRequested || updateCheck is not null || updateDownload is not null) return;
        installRequested = true;
        UpdateControls();
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
        _ = Dispatcher.BeginInvoke(async () =>
        {
            if (!phoneActive) await MaintainPhoneAsync(preserveContext: true);
            await InstallWhenIdleAsync();
        });
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
            {
                UpdateIntegrity.Text = "SHA-256 не совпал. Скачайте установщик снова.";
                pendingInstaller = null;
                return;
            }
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
            UpdateStatus.Text = "Установщик недоступен. Скачайте его снова.";
            UpdateIntegrity.Text = "Повторная проверка файла не завершена.";
            pendingInstaller = null;
        }
        finally
        {
            installing = false;
            if (!callActivity.Busy || pendingInstaller is null) installRequested = false;
            UpdateControls();
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
