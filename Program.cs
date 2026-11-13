using System.Windows;

namespace Notify.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--mcp"])
        {
            try
            {
                var origin = LocalSettings.HttpsOrigin(LocalSettings.Read().Origin);
                var token = LocalSettings.ReadToken(origin);
                if (token is null) return 2;
                var notify = new NotifyHttp(origin, token);
                RunMcpAsync(notify).GetAwaiter().GetResult();
                return 0;
            }
            catch (Exception) { return 2; }
        }

        const string instance = @"Local\ALXPRGS.Notify.Desktop";
        if (args is ["--shutdown"])
        {
            Signal(instance + ".Shutdown");
            try
            {
                using var running = Mutex.OpenExisting(instance);
                if (!running.WaitOne(TimeSpan.FromSeconds(15))) return 1;
                running.ReleaseMutex();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (AbandonedMutexException) { }
            return 0;
        }

        using var mutex = new Mutex(true, instance, out var first);
        if (!first)
        {
            if (!args.Contains("--background", StringComparer.Ordinal)) Signal(instance + ".Show");
            return 0;
        }

        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Show");
        using var shutdown = new EventWaitHandle(false, EventResetMode.AutoReset, instance + ".Shutdown");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        app.MainWindow = window;
        var showRegistration = ThreadPool.RegisterWaitForSingleObject(show, (_, _) =>
            app.Dispatcher.BeginInvoke(window.ShowWindow), null, Timeout.Infinite, false);
        var shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(shutdown, (_, _) =>
            app.Dispatcher.BeginInvoke(async () => await window.ExitForInstallerAsync()), null, Timeout.Infinite, false);
        try
        {
            if (!args.Contains("--background", StringComparer.Ordinal)) window.Show();
            window.StartSaved();
            return app.Run();
        }
        finally
        {
            showRegistration.Unregister(null);
            shutdownRegistration.Unregister(null);
            mutex.ReleaseMutex();
        }
    }

    private static void Signal(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(name, out var signal)) return;
        using (signal) signal.Set();
    }

    private static async Task RunMcpAsync(NotifyHttp notify)
    {
        await using var relay = new McpRelay(notify);
        await relay.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None);
    }
}
