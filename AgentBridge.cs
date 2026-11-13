using System.Net.WebSockets;
using System.Threading.Channels;

namespace Notify.Desktop;

internal enum BridgeConnection { Connecting, Connected, Reconnecting }

internal sealed class AgentBridge(Uri origin, string token, Registration registration, Chat chat,
    Func<string, CancellationToken, Task<string>> reply, Action<string> report, HttpMessageInvoker? transport = null,
    Action<BridgeConnection>? connectionChanged = null, Func<bool>? enterCall = null, Action? leaveCall = null,
    Func<string, string, string, CancellationToken, Task<string>>? phoneReply = null,
    Func<bool>? phoneEnabled = null, Action<string, bool>? phoneState = null)
{
    internal const string PhoneSessionId = "notify-phone-assistant";
    private const string Protocol = "alxprgs.notify.agent.v1";
    internal const string CallProtocol = "alxprgs.notify.call.v2";
    private const int MaxMessage = 64 * 1024;
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
        await SendAsync(socket, new
        {
            type = "hello", protocolVersion = Protocol, eventId = Id(), agentId = registration.AgentId,
            name = "ALXPRGS Notify Desktop", capabilities = new[] { "realtime_audio" },
            callProtocols = new[] { CallProtocol }, sessions = Sessions(), client = "alxprgs-notify-desktop"
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
        using var chatDialog = new DialogState(phone: false);
        using var phoneDialog = new DialogState(phone: true);
        var dialogs = new[] { chatDialog, phoneDialog };
        var tasks = new[]
        {
            ReadLoopAsync(socket, buffer, outbound.Writer, chatDialog, phoneDialog, lifetime.Token),
            SendLoopAsync(socket, outbound.Reader, lifetime.Token),
            HeartbeatAsync(seconds, outbound.Writer, lifetime.Token),
            ReplyLoopAsync(chatDialog, outbound.Writer, lifetime.Token),
            ReplyLoopAsync(phoneDialog, outbound.Writer, lifetime.Token)
        };
        try { await await Task.WhenAny(tasks); }
        finally
        {
            lifetime.Cancel();
            socket.Abort();
            try { await Task.WhenAll(tasks); }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            foreach (var dialog in dialogs) EndDialog(dialog);
        }
    }

    private bool PhoneEnabled => phoneReply is not null && (phoneEnabled?.Invoke() ?? true);

    private object[] Sessions() => PhoneEnabled
        ? [new { id = session.Id, name = session.Name }, new { id = PhoneSessionId, name = PhoneCodex.Name }]
        : [new { id = session.Id, name = session.Name }];

    private async Task ReadLoopAsync(WebSocket socket, byte[] buffer, ChannelWriter<object> outbound,
        DialogState chatDialog, DialogState phoneDialog, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await ReceiveAsync(socket, buffer, ct);
            if (frame.String("protocolVersion") != Protocol) throw new NotifyError("Неизвестная версия события Notify.");
            var payload = frame.Field("payload");
            if (payload.String("callId") is not { Length: 32 } callId) continue;
            var type = frame.String("type");
            var sessionId = payload.String("sessionId");
            var dialog = chatDialog.CallId == callId ? chatDialog : phoneDialog.CallId == callId ? phoneDialog : null;
            if (type == "call.invite")
            {
                var assistant = payload.Field("phoneAssistant").ValueKind == JsonValueKind.True;
                var number = payload.String("extension");
                var selected = assistant ? phoneDialog : chatDialog;
                var routeMatches = assistant ? sessionId == PhoneSessionId && (PhoneEnabled || dialog == phoneDialog) &&
                    payload.String("target") == "asterisk" && payload.String("securityLevel") == "standard" &&
                    number is { Length: >= 2 and <= 8 } && number.All(char.IsAsciiDigit)
                    : sessionId is null || sessionId == session.Id;
                var accept = routeMatches && payload.String("mode") == "conversation" &&
                    (dialog == selected || (dialog is null && selected.CallId is null && (enterCall?.Invoke() ?? true)));
                if (accept && dialog is null)
                {
                    selected.CallId = callId;
                    selected.SessionId = sessionId;
                    selected.Extension = number;
                    selected.Seen.Clear();
                    if (assistant) phoneState?.Invoke(number!, true);
                }
                await outbound.WriteAsync(Call(accept ? "call.accept" : "call.reject", new { callId }), ct);
                continue;
            }

            // Legacy lifecycle events can omit sessionId. A turn always carries its bound session.
            if (dialog is null || (sessionId is not null && sessionId != dialog.SessionId)) continue;
            switch (type)
            {
                case "dialog.user" when sessionId == dialog.SessionId &&
                    payload.String("turnId") is { Length: 32 } turnId &&
                    payload.String("text") is { Length: > 0 and <= 4000 } text && dialog.Seen.Add(turnId):
                    if (dialog.Phone) PhoneTiming.Mark(callId, turnId, "agent_received");
                    dialog.CancelTurn();
                    var turn = new Turn(callId, sessionId, turnId, text, dialog.Interruption.Token);
                    Volatile.Write(ref dialog.Latest, turn);
                    dialog.Turns.Writer.TryWrite(turn);
                    break;
                case "call.interrupted" when payload.String("reason") == "user_speech":
                    dialog.CancelTurn();
                    break;
                case "call.ended":
                    EndDialog(dialog);
                    report("Агент на связи · разговор завершён");
                    break;
            }
        }
    }

    private void EndDialog(DialogState dialog)
    {
        if (dialog.CallId is null) return;
        dialog.CallId = null;
        dialog.CancelTurn();
        if (dialog.Phone) phoneState?.Invoke(dialog.Extension!, false);
        leaveCall?.Invoke();
    }

    private async Task ReplyLoopAsync(DialogState dialog, ChannelWriter<object> outbound, CancellationToken ct)
    {
        await foreach (var turn in dialog.Turns.Reader.ReadAllAsync(ct))
        {
            if (!ReferenceEquals(Volatile.Read(ref dialog.Latest), turn)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, turn.Interruption);
            timeout.CancelAfter(dialog.Phone ? TimeSpan.FromSeconds(45) : TimeSpan.FromMinutes(2));
            try
            {
                report("Ожидание ответа чата…");
                var text = (dialog.Phone
                    ? await phoneReply!(turn.CallId, turn.TurnId, turn.Text, timeout.Token)
                    : await reply(turn.Text, timeout.Token)).Trim();
                if (!ReferenceEquals(Volatile.Read(ref dialog.Latest), turn)) continue;
                if (text.Length == 0) throw new NotifyError("Чат вернул пустой ответ.");
                if (text.Length > 1000) text = text[..(char.IsHighSurrogate(text[999]) ? 999 : 1000)];
                await outbound.WriteAsync(new ReplyFrame(dialog, turn, text), ct);
            }
            catch (Exception) when (turn.Interruption.IsCancellationRequested) { }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                report(error is NotifyError ? error.Message : "Чат недоступен. Проверьте, открыт ли доступ к выбранному чату.");
                if (dialog.Phone && ReferenceEquals(Volatile.Read(ref dialog.Latest), turn))
                    await outbound.WriteAsync(new ReplyFrame(dialog, turn, "Не удалось получить ответ. Попробуйте повторить вопрос."), ct);
            }
        }
    }

    private async Task HeartbeatAsync(int seconds, ChannelWriter<object> outbound, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds));
        while (await timer.WaitForNextTickAsync(ct))
            await outbound.WriteAsync(new { type = "heartbeat", protocolVersion = Protocol, eventId = Id(), sessions = Sessions() }, ct);
    }

    private async Task SendLoopAsync(WebSocket socket, ChannelReader<object> outbound, CancellationToken ct)
    {
        await foreach (var value in outbound.ReadAllAsync(ct))
        {
            var reply = value as ReplyFrame;
            if (reply is not null && !ReferenceEquals(Volatile.Read(ref reply.Dialog.Latest), reply.Turn)) continue;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var frame = reply is null ? value
                : Call("call.say", new { reply.Turn.CallId, reply.Turn.SessionId, reply.Turn.TurnId, reply.Text });
            await SendAsync(socket, frame, timeout.Token);
            if (reply?.Dialog.Phone == true) PhoneTiming.Mark(reply.Turn.CallId, reply.Turn.TurnId, "agent_reply_sent");
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
    private sealed class DialogState(bool phone) : IDisposable
    {
        internal bool Phone { get; } = phone;
        internal string? CallId;
        internal string? SessionId;
        internal string? Extension;
        internal Turn? Latest;
        internal HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
        internal CancellationTokenSource Interruption { get; private set; } = new();
        internal Channel<Turn> Turns { get; } = Channel.CreateBounded<Turn>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

        internal void CancelTurn()
        {
            Volatile.Write(ref Latest, null);
            Interruption.Cancel();
            Interruption.Dispose();
            Interruption = new();
        }

        public void Dispose() => Interruption.Dispose();
    }

    private sealed record Turn(string CallId, string? SessionId, string TurnId, string Text, CancellationToken Interruption);
    private sealed record ReplyFrame(DialogState Dialog, Turn Turn, string Text);
}
