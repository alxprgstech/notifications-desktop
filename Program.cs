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

        using var mutex = new Mutex(true, "Local\\ALXPRGS.Notify.Desktop", out var first);
        if (!first) return 0;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow();
        app.MainWindow = window;
        if (!args.Contains("--background", StringComparer.Ordinal)) window.Show();
        window.StartSaved();
        return app.Run();
    }

    private static async Task RunMcpAsync(NotifyHttp notify)
    {
        await using var relay = new McpRelay(notify);
        await relay.RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), CancellationToken.None);
    }
}
