using System.Net.WebSockets;
using System.Threading.Channels;

namespace Notify.Desktop;

internal enum BridgeConnection { Connecting, Connected, Reconnecting }

internal sealed class AgentBridge(Uri origin, string token, Registration registration, Chat chat,
    Func<string, CancellationToken, Task<string>> reply, Action<string> report, HttpMessageInvoker? transport = null,
    Action<BridgeConnection>? connectionChanged = null)
{
    private const string Protocol = "alxprgs.notify.agent.v1";
    internal const string CallProtocol = "alxprgs.notify.call.v2";
    private const int MaxMessage = 64 * 1024;
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly Queue<string> seenOrder = new();
    private readonly Chat session = chat with
    {
        Id = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(chat.Id))),
        Name = ShortLabel(chat.Name)
    };

    internal async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                connectionChanged?.Invoke(BridgeConnection.Connecting);
                report("Подключение к Notify…");
                await ConnectAsync(() =>
                {
                    attempt = 0;
                    connectionChanged?.Invoke(BridgeConnection.Connected);
                    report("Агент на связи");
                }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) when (error is WebSocketException or HttpRequestException or IOException or
                OperationCanceledException or JsonException or NotifyError)
            {
                connectionChanged?.Invoke(BridgeConnection.Reconnecting);
                report(error is NotifyError ? error.Message : "Связь с Notify прервалась. Подключаемся снова…");
            }
            var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)) + Random.Shared.NextDouble());
            attempt = Math.Min(attempt + 1, 6);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private async Task ConnectAsync(Action connected, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(25);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opening.CancelAfter(TimeSpan.FromSeconds(15));
        var address = new UriBuilder(origin) { Scheme = "wss", Path = "/api/v1/agents/connect" }.Uri;
        await socket.ConnectAsync(address, transport ?? NotifyHttp.Client, opening.Token);
        await RunSessionAsync(socket, connected, ct);
    }

    internal async Task RunSessionAsync(WebSocket socket, Action connected, CancellationToken ct)
    {
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opening.CancelAfter(TimeSpan.FromSeconds(15));
        var sessions = new[] { new { id = session.Id, name = session.Name } };
        await SendAsync(socket, new
        {
            type = "hello", protocolVersion = Protocol, eventId = Id(), agentId = registration.AgentId,
            name = "ALXPRGS Notify Desktop", capabilities = new[] { "realtime_audio" },
            callProtocols = new[] { CallProtocol }, sessions, client = "alxprgs-notify-desktop"
        }, opening.Token);
        var buffer = new byte[MaxMessage + 1];
        var hello = await ReceiveAsync(socket, buffer, opening.Token);
        if (hello.String("type") != "hello_ack" || hello.String("protocolVersion") != Protocol ||
            hello.String("callProtocol") != CallProtocol || hello.Field("agent").String("id") != registration.AgentId)
            throw new NotifyError("Notify не согласовал протокол агента и звонков v2.");
        var heartbeat = hello.Field("heartbeatSeconds");
        if (heartbeat.ValueKind != JsonValueKind.Number || !heartbeat.TryGetInt32(out var seconds) || seconds is < 1 or > 600)
            throw new NotifyError("Некорректный интервал heartbeat.");
        connected();

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var outbound = Channel.CreateBounded<object>(32);
        var turns = Channel.CreateBounded<Turn>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var dialog = new DialogState();
        var reader = ReadLoopAsync(socket, buffer, outbound.Writer, turns.Writer, dialog, lifetime.Token);
        var writer = SendLoopAsync(socket, outbound.Reader, dialog, lifetime.Token);
        var timer = HeartbeatAsync(seconds, outbound.Writer, lifetime.Token);
        var worker = ReplyLoopAsync(turns.Reader, outbound.Writer, dialog, lifetime.Token);
        try
        {
            var completed = await Task.WhenAny(reader, writer, timer, worker);
            await completed;
        }
        finally
        {
            lifetime.Cancel();
            socket.Abort();
            try { await Task.WhenAll(reader, writer, timer, worker); }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task ReadLoopAsync(WebSocket socket, byte[] buffer, ChannelWriter<object> outbound,
        ChannelWriter<Turn> turns, DialogState dialog, CancellationToken ct)
    {
        string? acceptedCall = null;
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReceiveAsync(socket, buffer, ct);
            if (frame.String("protocolVersion") != Protocol) throw new NotifyError("Неизвестная версия события Notify.");
            var payload = frame.Field("payload");
            var callId = payload.String("callId");
            var type = frame.String("type");
            if (callId is not { Length: 32 }) continue;
            switch (type)
            {
                case "call.invite":
                    var selected = payload.String("sessionId");
                    var accept = (selected is null || selected == session.Id) && payload.String("mode") == "conversation";
                    if (accept) acceptedCall = callId;
                    await outbound.WriteAsync(Call(accept ? "call.accept" : "call.reject", new { callId }), ct);
                    break;
                case "dialog.user" when callId == acceptedCall &&
                    payload.String("turnId") is { Length: 32 } turnId &&
                    payload.String("text") is { Length: > 0 and <= 4000 } text && Remember(turnId):
                    var turn = new Turn(callId, turnId, text);
                    Volatile.Write(ref dialog.Latest, turn);
                    turns.TryWrite(turn);
                    break;
                case "call.interrupted" when callId == acceptedCall && payload.String("reason") == "user_speech":
                    Volatile.Write(ref dialog.Latest, null);
                    break;
                case "call.ended" when callId == acceptedCall:
                    acceptedCall = null;
                    Volatile.Write(ref dialog.Latest, null);
                    report("Агент на связи · разговор завершён");
                    break;
            }
        }
    }

    private bool Remember(string id)
    {
        if (!seen.Add(id)) return false;
        seenOrder.Enqueue(id);
        if (seenOrder.Count > 512) seen.Remove(seenOrder.Dequeue());
        return true;
    }

    private async Task ReplyLoopAsync(ChannelReader<Turn> turns, ChannelWriter<object> outbound,
        DialogState dialog, CancellationToken ct)
    {
        await foreach (var turn in turns.ReadAllAsync(ct))
        {
            if (!ReferenceEquals(Volatile.Read(ref dialog.Latest), turn)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                report("Ожидание ответа чата…");
                var text = (await reply(turn.Text, timeout.Token)).Trim();
                if (!ReferenceEquals(Volatile.Read(ref dialog.Latest), turn)) continue;
                if (text.Length == 0) throw new NotifyError("Чат вернул пустой ответ.");
                if (text.Length > 1000) text = text[..(char.IsHighSurrogate(text[999]) ? 999 : 1000)];
                // Notify uses turnId to deduplicate replies.
                await outbound.WriteAsync(new ReplyFrame(turn, text), ct);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                report(error is NotifyError ? error.Message : "Чат недоступен. Проверьте, открыт ли доступ к выбранному чату.");
            }
        }
    }

    private static async Task HeartbeatAsync(int seconds, ChannelWriter<object> outbound, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(ct))
            await outbound.WriteAsync(new { type = "heartbeat", protocolVersion = Protocol, eventId = Id() }, ct);
    }

    private async Task SendLoopAsync(WebSocket socket, ChannelReader<object> outbound, DialogState dialog, CancellationToken ct)
    {
        await foreach (var value in outbound.ReadAllAsync(ct))
        {
            var reply = value as ReplyFrame;
            if (reply is not null && !ReferenceEquals(Volatile.Read(ref dialog.Latest), reply.Turn)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var frame = reply is null ? value
                : Call("call.say", new { reply.Turn.CallId, reply.Turn.TurnId, reply.Text });
            await SendAsync(socket, frame, timeout.Token);
            if (reply is not null) report("Агент на связи · ответ передан");
        }
    }

    private static ValueTask SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, LocalSettings.Json).AsMemory(),
            WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset <= MaxMessage)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(offset), ct);
            if (result.MessageType != WebSocketMessageType.Text)
                throw new NotifyError("Notify закрыл соединение агента.");
            offset += result.Count;
            if (offset > MaxMessage) break;
            if (result.EndOfMessage)
            {
                using var json = JsonDocument.Parse(buffer.AsMemory(0, offset));
                return json.RootElement.Clone();
            }
        }
        throw new NotifyError("Событие Notify превысило допустимый размер.");
    }

    private static string ShortLabel(string label)
    {
        var clean = string.Concat(label.Where(ch => !char.IsControl(ch))).Trim();
        if (clean.Length == 0) return "Выбранный чат";
        return clean.Length > 160 ? clean[..(char.IsHighSurrogate(clean[159]) ? 159 : 160)] : clean;
    }

    private static string Id() => Guid.NewGuid().ToString("N");
    private static object Call(string type, object payload) => new { type, protocolVersion = CallProtocol, eventId = Id(), payload };
    private sealed class DialogState { public Turn? Latest; }
    private sealed record Turn(string CallId, string TurnId, string Text);
    private sealed record ReplyFrame(Turn Turn, string Text);
}
