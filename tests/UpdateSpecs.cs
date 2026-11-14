using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Notify.Desktop;

internal static class UpdateSpecs
{
    internal static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var digest = Convert.ToHexStringLower(SHA256.HashData(payload));
        var releases = new[]
        {
            Release("v2.9.0", digest), Release("v2.10.0", digest),
            Release("v2.11.0", digest, state: "starter"), Release("v3.0.0", digest, prerelease: true),
            Release("v4.0.0", digest, draft: true), Release("v5.0.0", "invalid"),
            Release("v6.0.0", digest, size: UpdateManager.MaxInstallerBytes + 1),
            Release("v7.0.0-rc.2", digest), Release("v08.0.0", digest)
        };
        var checks = 0;
        var downloads = 0;
        var invalidDigest = false;
        var invalidRedirect = false;
        var streamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockDownload = false;
        using var http = new HttpClient(new Handler(request =>
        {
            Require(request.Headers.Authorization is null, "Anonymous GitHub request");
            switch (request.RequestUri!.Host)
            {
                case "api.github.com":
                    if (++checks == 1)
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(JsonSerializer.Serialize(releases)),
                            Headers = { ETag = new EntityTagHeaderValue("\"fixture\"") }
                        };
                    Require(request.Headers.IfNoneMatch.Single().Tag == "\"fixture\"", "Conditional request");
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                case "github.com":
                    return new HttpResponseMessage(HttpStatusCode.Found)
                    {
                        Headers = { Location = new Uri(invalidRedirect ? "http://release-assets.githubusercontent.com/file"
                            : "https://release-assets.githubusercontent.com/file") }
                    };
                case "release-assets.githubusercontent.com":
                    downloads++;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = blockDownload ? new StreamContent(new PausedDownload(payload[0], streamed))
                            : new ByteArrayContent(invalidDigest ? new byte[payload.Length] : payload)
                    };
                default: throw new Exception("Untrusted redirect reached the network");
            }
        }));
        var directory = Directory.CreateTempSubdirectory("notify-updater-spec-");
        try
        {
            var manager = new UpdateManager(directory.FullName, http);
            var candidate = await manager.CheckAsync("2.8.9+installed.1", deadline.Token);
            Require(candidate?.Version == "2.10.0", "Highest complete stable release");
            Require(await manager.CheckAsync("2.8.9+installed.1", deadline.Token) == candidate, "304 preserves release");
            Require(await manager.CheckAsync("2.10.0", deadline.Token) is null, "No newer installable release");
            Require(manager.LatestStableVersion == "6.0.0", "Newest stable tag is visible even when its installer is invalid");
            Require(downloads == 0 && !Directory.EnumerateFileSystemEntries(directory.FullName).Any(),
                "Release checks must neither fetch nor cache an installer");
            var progress = new ProgressCapture();
            var path = await manager.DownloadAsync(candidate!, progress, deadline.Token);
            Require(progress.Last == 100 && File.ReadAllBytes(path).SequenceEqual(payload), "Download and progress");
            Require(path == await manager.DownloadAsync(candidate!, null, deadline.Token) && downloads == 1, "Verified cache reuse");
            using (var laterProcess = new HttpClient(new Handler(_ => throw new Exception("Verified file was requested again"))))
            {
                var restarted = new UpdateManager(directory.FullName, laterProcess);
                Require(await restarted.DownloadAsync(candidate!, null, deadline.Token) == path, "Cache is verified after restart");
            }
            File.WriteAllBytes(path, new byte[payload.Length]);
            invalidDigest = true;
            await Reject(() => manager.DownloadAsync(candidate!, null, deadline.Token));
            invalidRedirect = true;
            await Reject(() => manager.DownloadAsync(candidate!, null, deadline.Token));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { await manager.DownloadAsync(candidate!, null, cancelled.Token); throw new Exception("Cancellation was ignored"); }
            catch (OperationCanceledException) { }
            invalidRedirect = false;
            blockDownload = true;
            using var midTransfer = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var transfer = manager.DownloadAsync(candidate!, null, midTransfer.Token);
            await streamed.Task.WaitAsync(deadline.Token);
            Require(Directory.EnumerateFiles(directory.FullName, "*.part").Any(), "Partial download must exist before cancellation");
            await midTransfer.CancelAsync();
            try { await transfer; throw new Exception("In-flight cancellation was ignored"); }
            catch (OperationCanceledException) { }
            Require(!Directory.EnumerateFiles(directory.FullName, "*.part").Any(), "Partial download cleanup");
        }
        finally { directory.Delete(recursive: true); }

        var calls = new CallActivity();
        Require(calls.TryEnter() && !calls.TryStop() && !calls.TryReserveInstall(), "An active call must defer installation");
        calls.Leave();
        Require(calls.TryReserveInstall() && !calls.TryEnter() && !calls.TryMaintain(), "An installer reservation prevents a new call");
        calls.ReleaseInstall();
        Require(calls.TryEnter(), "An installer failure must release the reservation");
        calls.Leave();
        Require(calls.TryReserveInstall(), "A later installer must be allowed to retry");
        Require(calls.TryStop() && !calls.TryEnter(), "No invite can enter after accepting installer shutdown");
        Console.WriteLine("PASS: stable release filtering, SHA-256, HTTPS redirects, cache, cancellation and call-safe shutdown.");

        static object Release(string tag, string sha, string state = "uploaded", bool draft = false,
            bool prerelease = false, long size = 5)
        {
            var name = $"ALXPRGS-Notify-Desktop-Setup-{tag[1..]}-win-x64.exe";
            return new { tag_name = tag, draft, prerelease, body = "Changes", assets = new[]
            {
                new { name, state, size, digest = $"sha256:{sha}",
                    browser_download_url = $"https://github.com/alxprgstech/notifications-desktop/releases/download/{tag}/{name}" }
            }};
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid installer accepted");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }

    private sealed class ProgressCapture : IProgress<int>
    {
        internal int Last { get; private set; }
        public void Report(int value) => Last = value;
    }

    private sealed class PausedDownload(byte firstByte, TaskCompletionSource streamed) : Stream
    {
        private bool sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (sent)
            {
                streamed.TrySetResult();
                return new ValueTask<int>(WaitAsync(ct));
            }
            sent = true;
            buffer.Span[0] = firstByte;
            return ValueTask.FromResult(1);
        }
        private static async Task<int> WaitAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
