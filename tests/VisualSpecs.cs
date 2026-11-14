using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notify.Desktop;

internal static class VisualSpecs
{
    internal static void Capture(string output)
    {
        _ = new Application();
        Directory.CreateDirectory(output);
        using var transport = new HttpClient(new HistoryFixture());
        var api = new NotifyHttp(new Uri("https://notify.example.invalid"), "synthetic", transport);
        var window = new MainWindow(visualTest: true, historyFixture: api) { Width = 970, Height = 800, ShowInTaskbar = false };
        window.Show();
        try
        {
            foreach (var theme in new[] { "Light", "Dark" })
            {
                window.SetVisualTheme(theme);
                VerifyCaption(window);
                foreach (var page in new[] { "Home", "Connections", "Calls", "Diagnostics", "Settings" })
                {
                    if (page == "Diagnostics")
                        FindNavigation(window, "2").IsChecked = true;
                    else if (page != "Home") ((RadioButton)window.FindName(page + "Nav")).IsChecked = true;
                    else FindNavigation(window, "0").IsChecked = true;
                    if (page == "Calls")
                    {
                        var list = (ListBox)window.FindName("CallsList");
                        PumpUntil(() => list.Items.Count == 2);
                        list.SelectedIndex = 0;
                        var turns = (ItemsControl)window.FindName("CallTurns");
                        PumpUntil(() => turns.Items.Count == 1);
                        var report = (Button)window.FindName("CopyCallReport");
                        if (!report.IsEnabled) throw new InvalidOperationException("Selected trace has no diagnostic export.");
                        ((TabControl)window.FindName("CallDetail")).SelectedIndex = 1;
                    }
                    window.UpdateLayout();
                    var content = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"{page}-{theme}.png"));
                    encoder.Save(stream);
                }
            }
        }
        finally { window.ExitAsync().GetAwaiter().GetResult(); }
    }

    private static void VerifyCaption(MainWindow window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;
        if (window.CaptionThemeResult is not (0, 0))
            throw new InvalidOperationException($"Windows rejected the caption theme: {window.CaptionThemeResult}.");
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!ready())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(3))
                throw new InvalidOperationException("The synthetic call history did not load.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class HistoryFixture : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Get || request.Headers.Authorization?.Parameter != "synthetic")
                throw new InvalidOperationException("Unexpected history request.");
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/api/v1/calls" => """
                    [{"id":"call-synthetic","sourceId":"notify-fixture","sessionId":"phone","direction":"outgoing","target":"asterisk","startedAt":1794610000000,"status":"ended","reason":"ended","durationMs":7400},
                    {"id":"call-old","sourceId":"notify-fixture","direction":"incoming","target":"android","startedAt":1794609900000,"status":"ended","reason":"missed","durationMs":0}]
                    """,
                "/api/v1/calls/call-synthetic/trace" => """
                    {"callId":"call-synthetic","events":[
                    {"sequence":1,"offsetMs":0,"stage":"call_created","status":"completed","source":"api"},
                    {"sequence":2,"offsetMs":1100,"stage":"stt","status":"completed","source":"api","durationMs":450},
                    {"sequence":3,"offsetMs":2800,"stage":"codex_generation","status":"completed","source":"agent","durationMs":1500,"tokens":120},
                    {"sequence":4,"offsetMs":4330,"stage":"rtp_playback","status":"interrupted","source":"asterisk","durationMs":420,"frames":22}],"truncated":false}
                    """,
                "/api/v1/calls/call-synthetic/transcript" => """{"turns":[{"speaker":"user","text":"Синтетическая реплика","status":"complete"}],"partial":false}""",
                _ => null
            };
            return Task.FromResult(json is null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static RadioButton FindNavigation(DependencyObject parent, string tag)
    {
        return Descendants(parent).OfType<RadioButton>().Single(button => Equals(button.Tag, tag));
        static IEnumerable<DependencyObject> Descendants(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    }
}
