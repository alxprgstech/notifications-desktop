using System.IO.Pipes;
using System.Threading.Channels;

namespace Notify.Desktop;

internal sealed class HarnessLink(Action<string> report)
{
    internal static string AddressPath => Path.Combine(LocalSettings.DirectoryPath, "harness-pipe");
    private sealed record Request(string Text, TaskCompletionSource<string> Reply);

    internal async Task RunAsync(Func<Chat, Func<string, CancellationToken, Task<string>>, CancellationToken, Task> serve, CancellationToken ct)
    {
        var name = "alxprgs-notify-" + Guid.NewGuid().ToString("N");
        using var listener = Create(name);
        Directory.CreateDirectory(LocalSettings.DirectoryPath);
        LocalSettings.Write(AddressPath, Encoding.ASCII.GetBytes(name));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                report("Откройте выбранный чат Harness и нажмите «Подключить этот чат» в панели Notify.");
                await listener.WaitForConnectionAsync(ct);
                try { await ServeAsync(listener, serve, ct); }
                catch (Exception error) when (!ct.IsCancellationRequested && error is IOException or JsonException or OperationCanceledException)
                {
                    report($"Harness отключён · {error.GetType().Name}");
                }
                finally { if (listener.IsConnected) listener.Disconnect(); }
            }
        }
        finally { File.Delete(AddressPath); }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe,
        Func<Chat, Func<string, CancellationToken, Task<string>>, CancellationToken, Task> serve, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var open = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        open.CancelAfter(TimeSpan.FromSeconds(5));
        var reader = new BoundedLineReader(pipe, 16 * 1024);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        using var hello = JsonDocument.Parse(await reader.ReadAsync(open.Token) ?? "{}");
        var id = hello.RootElement.String("sessionId");
        var name = hello.RootElement.String("label");
        if (hello.RootElement.String("type") != "bind" || id is not { Length: > 0 and <= 200 } ||
            name is not { Length: > 0 and <= 160 })
            return;

        var requests = Channel.CreateBounded<Request>(1);
        var io = ExchangeAsync(reader, writer, requests.Reader, lifetime.Token);
        var bridge = serve(new Chat("harness:" + id, name), async (text, replyToken) =>
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            await requests.Writer.WriteAsync(new Request(text, completion), replyToken);
            return await completion.Task.WaitAsync(replyToken);
        }, lifetime.Token);
        try { await await Task.WhenAny(io, bridge); }
        finally
        {
            lifetime.Cancel();
            requests.Writer.TryComplete();
            try { await Task.WhenAll(io, bridge); }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
    }

    private static async Task ExchangeAsync(BoundedLineReader reader, StreamWriter writer,
        ChannelReader<Request> requests, CancellationToken ct)
    {
        await writer.WriteLineAsync("{\"type\":\"ready\"}".AsMemory(), ct);
        var incoming = reader.ReadAsync(ct);
        while (true)
        {
            var next = requests.ReadAsync(ct).AsTask();
            if (await Task.WhenAny(incoming, next) == incoming) return;
            var request = await next;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { type = "prompt", text = request.Text }).AsMemory(), ct);
            string? line;
            try { line = await incoming.WaitAsync(TimeSpan.FromMinutes(2), ct); }
            catch (TimeoutException) { throw new IOException("Harness reply timed out."); }
            if (line is null) return;
            using var response = JsonDocument.Parse(line);
            if (response.RootElement.String("type") == "reply" && response.RootElement.String("text") is { } reply)
                request.Reply.TrySetResult(reply);
            else
                request.Reply.TrySetException(new NotifyError("Harness не вернул ответ выбранного чата."));
            incoming = reader.ReadAsync(ct);
        }
    }

    private static NamedPipeServerStream Create(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
