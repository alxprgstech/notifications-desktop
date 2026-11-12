namespace Notify.Desktop;

internal sealed class McpRelay(NotifyHttp notify) : IAsyncDisposable
{
    private string? session;
    private string? protocol;

    internal async Task<int> ProbeAsync(CancellationToken ct)
    {
        await ForwardAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"ALXPRGS Notify Desktop","version":"1.0.0"}}}""", "initialize", ct);
        if (protocol is null) throw new NotifyError("Сервер MCP не подтвердил версию протокола.");
        await ForwardAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", "notifications/initialized", ct);
        using var response = JsonDocument.Parse(await ForwardAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""", "tools/list", ct) ?? "{}");
        var tools = response.RootElement.Field("result").Field("tools");
        if (tools.ValueKind != JsonValueKind.Array) throw new NotifyError("Сервер MCP не вернул список инструментов.");
        return tools.GetArrayLength();
    }

    internal async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true };
        var reader = new BoundedLineReader(input, 1024 * 1024);
        while (await reader.ReadAsync(ct) is { } line)
        {
            JsonElement id = default;
            try
            {
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                id = message.Field("id");
                var method = message.String("method");
                if (method is not ("initialize" or "notifications/initialized" or "ping" or "tools/list" or "tools/call"))
                {
                    if (id.ValueKind != JsonValueKind.Undefined)
                        await ErrorAsync(writer, id, -32601, "Method not available", ct);
                    continue;
                }
                var reply = await ForwardAsync(line, method, ct);
                if (reply is not null) await writer.WriteLineAsync(reply.AsMemory(), ct);
            }
            catch (Exception error) when (error is JsonException or HttpRequestException or NotifyError ||
                error is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (id.ValueKind != JsonValueKind.Undefined)
                    await ErrorAsync(writer, id, -32000, error is NotifyError ? error.Message : "Notify MCP request failed", ct);
            }
        }
    }

    private async Task<string?> ForwardAsync(string line, string method, CancellationToken ct)
    {
        using var request = notify.Request(HttpMethod.Post, "/mcp");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (protocol is not null) request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", protocol);
        request.Content = new StringContent(line, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        using var response = await notify.Transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
            throw new NotifyError($"Notify MCP HTTP {(int)response.StatusCode}");
        if ((int)response.StatusCode is 202 or 204) return null;
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.FirstOrDefault();

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        string result;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            var reader = new BoundedLineReader(body, 2 * 1024 * 1024);
            var data = new StringBuilder();
            while (await reader.ReadAsync(timeout.Token) is { } eventLine)
            {
                if (eventLine.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.AppendLine(eventLine[5..].TrimStart());
                    if (data.Length > 2 * 1024 * 1024) throw new NotifyError("Notify MCP response too large");
                }
                else if (eventLine.Length == 0 && data.Length > 0)
                {
                    using var candidate = JsonDocument.Parse(data.ToString());
                    if (candidate.RootElement.Field("id").ValueKind != JsonValueKind.Undefined)
                        break;
                    data.Clear();
                }
            }
            result = data.ToString();
        }
        else
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await body.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (bytes.Length + count > 2 * 1024 * 1024) throw new NotifyError("Notify MCP response too large");
                bytes.Write(buffer, 0, count);
            }
            result = Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
        }

        using var parsed = JsonDocument.Parse(result);
        if (method == "initialize") protocol = parsed.RootElement.Field("result").String("protocolVersion");
        return parsed.RootElement.GetRawText().Replace("\r", "").Replace("\n", "");
    }

    private static Task ErrorAsync(StreamWriter writer, JsonElement id, int code, string message, CancellationToken ct) =>
        writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, error = new { code, message } }).AsMemory(), ct);

    public async ValueTask DisposeAsync()
    {
        if (session is null) return;
        using var request = notify.Request(HttpMethod.Delete, "/mcp");
        request.Headers.TryAddWithoutValidation("Mcp-Session-Id", session);
        if (protocol is not null) request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", protocol);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { using var response = await notify.Transport.SendAsync(request, timeout.Token); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException) { }
        session = null;
    }
}

internal sealed class BoundedLineReader(Stream stream, int limit)
{
    private readonly byte[] buffer = new byte[4096];
    private int start;
    private int end;

    internal async Task<string?> ReadAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (start == end)
            {
                end = await stream.ReadAsync(buffer, ct);
                start = 0;
                if (end == 0)
                    return line.Length == 0 ? null : ReadLine(line);
            }
            var newline = Array.IndexOf(buffer, (byte)'\n', start, end - start);
            var count = (newline < 0 ? end : newline) - start;
            if (line.Length + count > limit) throw new IOException("IPC frame exceeds the size limit.");
            line.Write(buffer, start, count);
            start += count;
            if (newline >= 0)
            {
                start++;
                return ReadLine(line);
            }
        }
    }

    private static string ReadLine(MemoryStream line) =>
        Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
}
