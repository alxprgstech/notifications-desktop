using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Notify.Desktop;

if (args is ["--visual", var output])
{
    Exception? failure = null;
    var visual = new Thread(() =>
    {
        try { VisualSpecs.Capture(output); }
        catch (Exception error) { failure = error; }
    });
    visual.SetApartmentState(ApartmentState.STA);
    visual.Start();
    visual.Join();
    if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    return;
}

if (args is ["--github-update"])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var manager = new UpdateManager(Path.Combine(Environment.CurrentDirectory, "artifacts", "github-updater-smoke"));
    var update = await manager.CheckAsync("0.0.0", deadline.Token) ?? throw new Exception("No downloadable public release.");
    await manager.DownloadAsync(update, null, deadline.Token);
    Console.WriteLine($"PASS: anonymous GitHub release {update.Version}, HTTPS download, {update.Size} bytes, SHA-256 verified.");
    return;
}

if (args is ["--phone-agent-lab", var labOrigin, var certificateHash, var labAgentId, var assistantDirectory])
{
    var token = Environment.GetEnvironmentVariable("NOTIFY_PHONE_LAB_TOKEN") ?? throw new Exception("Missing synthetic lab credential.");
    using var transport = new HttpMessageInvoker(new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == certificateHash }
    });
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
    await using var assistant = new PhoneCodex(new Codex(Codex.Find()!), Path.GetFullPath(assistantDirectory));
    await assistant.WarmAsync(deadline.Token);
    await new AgentBridge(new Uri(labOrigin), token, new Registration(labAgentId, "", "Phone lab", false, [], []),
        new Chat(AgentBridge.PhoneSessionId, PhoneCodex.Name), (_, _) => throw new InvalidOperationException(),
        Console.WriteLine, transport, phoneReply: assistant.ReplyAsync).RunAsync(deadline.Token);
    return;
}

if (args is ["--chat-instructions", var instructionLab])
{
    using var instructionDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var directory = Path.GetFullPath(instructionLab);
    Directory.CreateDirectory(directory);
    var codex = new Codex(Codex.Find() ?? throw new Exception("Codex CLI was not detected."));
    string id;
    using (var process = codex.Start(["app-server", "--listen", "stdio://"], directory))
    {
        try
        {
            await Codex.InitializeAsync(process, instructionDeadline.Token);
            await Codex.WriteAsync(process, new { id = 2, method = "thread/start", @params = new { cwd = directory } }, instructionDeadline.Token);
            var result = await Codex.ReadResultAsync(process, 2, instructionDeadline.Token);
            id = result.Field("thread").String("id") ?? throw new Exception("No test thread ID.");
            await Codex.WriteAsync(process, new { id = 3, method = "turn/start", @params = new
            {
                threadId = id, input = new[] { new { type = "text", text = "Напиши «готово». Инструменты не нужны." } }
            } }, instructionDeadline.Token);
            var seedTurn = await Codex.ReadResultAsync(process, 3, instructionDeadline.Token);
            var turnId = seedTurn.Field("turn").String("id");
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(instructionDeadline.Token)
                    ?? throw new IOException("Codex exited before persisting the test turn.");
                using var message = JsonDocument.Parse(line);
                if (message.RootElement.String("method") != "turn/completed") continue;
                var turn = message.RootElement.Field("params").Field("turn");
                if (turn.String("id") != turnId) continue;
                if (turn.String("status") != "completed") throw new Exception("The test session could not complete its first turn.");
                break;
            }
        }
        finally
        {
            Codex.Kill(process);
            await process.WaitForExitAsync(instructionDeadline.Token);
        }
    }
    var marker = "NOTIFY-" + Guid.NewGuid().ToString("N");
    var reply = await codex.ReplyAsync(new Chat(id, "Notify instruction test", directory), "Ответь на сообщение «Проверка».",
        instructionDeadline.Token, $"Не используй инструменты.\nОтветь ровно строкой \"{marker}\".");
    if (reply.Trim() != marker) throw new Exception("Codex did not apply the ordinary chat instructions.");
    Console.WriteLine("PASS: live ordinary chat applied multiline instructions.");
    return;
}

if (args is ["--phone-context", var labRoot])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
    var labCancellation = deadline.Token;
    var directory = Path.Combine(Path.GetFullPath(labRoot), Guid.NewGuid().ToString("N"));
    var codex = new Codex(Codex.Find() ?? throw new Exception("Codex CLI was not detected."));
    PhoneUsage? usage = null;
    await using var assistant = new PhoneCodex(codex, directory, value => usage = value,
        () => "Запоминай предпочтения собеседника.");
    await assistant.WarmAsync(labCancellation);
    var threadFile = Path.Combine(directory, "thread-id");
    var original = await File.ReadAllTextAsync(threadFile, labCancellation);
    await Ask("Мой любимый фрукт — манго. Запомни это предпочтение.");
    if (usage is not { Total: > 0, Context: > 0, Window: > 0 })
        throw new Exception("Codex did not supply usable context telemetry.");
    Console.WriteLine("PASS: live context telemetry.");
    await assistant.RolloverAsync(preserveContext: true, labCancellation);
    var replacement = await File.ReadAllTextAsync(threadFile, labCancellation);
    if (original == replacement) throw new Exception("The persistent phone chat did not change.");
    await RequirePreference();
    Console.WriteLine("PASS: rollover preserved the synthetic fact and changed the persistent thread.");
    await assistant.CompactAsync(labCancellation);
    Equal(replacement, await File.ReadAllTextAsync(threadFile, labCancellation));
    await RequirePreference();
    Console.WriteLine("PASS: compaction preserved the synthetic fact and persistent thread ID.");
    var serverExit = assistant.WaitForServerExitAsync(labCancellation);
    if (serverExit.IsCompleted) throw new Exception("The live App Server was not being watched.");
    await assistant.DisposeAsync();
    await serverExit;
    if (assistant.Ready) throw new Exception("The stopped App Server still reports readiness.");
    await using var recovered = new PhoneCodex(codex, directory);
    await recovered.WarmAsync(labCancellation);
    Equal(replacement, await File.ReadAllTextAsync(threadFile, labCancellation));
    var recoveredReply = await recovered.ReplyAsync(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
        "Какой фрукт я назвал любимым?", labCancellation);
    if (!recoveredReply.Contains("манго", StringComparison.OrdinalIgnoreCase))
        throw new Exception("The synthetic preference was lost after App Server restart.");
    Console.WriteLine("PASS: process exit observed and the same persistent phone chat restored.");
    return;

    Task<string> Ask(string prompt) => assistant.ReplyAsync(
        Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), prompt, labCancellation);

    async Task RequirePreference()
    {
        var reply = await Ask("Какой фрукт я назвал любимым?");
        if (!reply.Contains("манго", StringComparison.OrdinalIgnoreCase))
            throw new Exception("The synthetic preference was not retained.");
    }
}

if (args is ["--phone-codex"])
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    var directory = Path.Combine(LocalSettings.DirectoryPath, "phone-assistant");
    var codex = new Codex(Codex.Find() ?? throw new Exception("Codex CLI was not detected."));
    string? threadId = null;
    for (var session = 0; session < 2; session++)
    {
        await using var assistant = new PhoneCodex(codex, directory);
        await assistant.WarmAsync(deadline.Token);
        var saved = File.ReadAllText(Path.Combine(directory, "thread-id"));
        threadId ??= saved;
        Equal(threadId, saved);
        var reply = await assistant.ReplyAsync(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
            session == 0 ? "Тест телефона. Скажи коротко, сколько будет два плюс два."
                : "Тест телефона. Повтори результат предыдущего сложения одним предложением.", deadline.Token);
        if (!reply.Contains('4') && !reply.Contains("четыр", StringComparison.OrdinalIgnoreCase))
            throw new Exception("The persistent assistant did not return the expected answer.");
    }
    Console.WriteLine("PASS: ChatGPT account, App Server reply and dedicated thread restored after process restart.");
    return;
}

using (var fixture = JsonDocument.Parse("""{"total":{"totalTokens":250000},"last":{"totalTokens":85000},"modelContextWindow":100000}"""))
{
    var usage = PhoneUsage.Read(fixture.RootElement);
    if (usage.Total != 250000 || usage.Percent != 85)
        throw new Exception("Cumulative usage must never drive the phone context threshold.");
}
using (var fixture = JsonDocument.Parse("""{"total":{"totalTokens":250000},"last":{"totalTokens":85000},"modelContextWindow":null}"""))
    if (PhoneUsage.Read(fixture.RootElement).Percent is not null)
        throw new Exception("Missing model context window must disable automatic rollover.");
var maintenance = new CallActivity();
if (!maintenance.TryEnter() || maintenance.TryMaintain())
    throw new Exception("Phone maintenance must not start during an active call.");
maintenance.Leave();
if (!maintenance.TryMaintain() || !maintenance.Busy || maintenance.TryEnter() || maintenance.TryStop())
    throw new Exception("Calls and installation must wait during phone maintenance.");
maintenance.EndMaintenance();
if (maintenance.Busy) throw new Exception("Installation must resume after phone maintenance.");
if (!maintenance.TryEnter()) throw new Exception("Calls must resume after phone maintenance.");
maintenance.Leave();

using (var storage = new System.IO.FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
{
    var directory = storage.Name + ".settings";
    Directory.CreateDirectory(directory);
    try
    {
        const string origin = "https://notify.example.invalid";
        const string synthetic = "synthetic-settings-spec-token";
        var uri = new Uri(origin);
        LocalSettings.Save(new Preferences(Origin: origin), synthetic, directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"origin":"https://notify.example.invalid","theme":"Dark"}""");
        var original = LocalSettings.Read(directory);
        Equal("Dark", original.Theme);
        Equal(85, original.PhoneRolloverPercent);
        var credential = Path.Combine(directory, "source.dpapi");
        var protectedBefore = File.ReadAllBytes(credential);
        var updated = original with { PhoneEnabled = true, PhoneRolloverPercent = 90,
            GeneralInstructions = "Общие", PhoneInstructions = "Телефон", ChatInstructions = "Чат" };
        LocalSettings.Save(updated, directory: directory);
        Equal(updated, LocalSettings.Read(directory));
        if (!protectedBefore.SequenceEqual(File.ReadAllBytes(credential)) ||
            LocalSettings.ReadToken(uri, directory) != synthetic ||
            LocalSettings.ReadToken(new Uri("https://other.example.invalid"), directory) is not null)
            throw new Exception("Saving instructions must preserve the origin-bound DPAPI credential.");
        Console.WriteLine("PASS: legacy settings, instruction round trip and DPAPI preservation.");
    }
    finally
    {
        File.Delete(Path.Combine(directory, "settings.json"));
        File.Delete(Path.Combine(directory, "source.dpapi"));
        Directory.Delete(directory);
    }
}

await UpdateSpecs.RunAsync();

if (args is ["--wss-soak"])
{
    await TlsBridgeSpec.RunAsync(reconnects: 40);
    return;
}

if (args is ["--codex-discovery"])
{
    var executable = Codex.Find() ?? throw new Exception("Codex CLI was not detected.");
    using var discoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var chats = await new Codex(executable).ChatsAsync(discoveryTimeout.Token);
    if (chats.Count == 0 || chats.Any(chat => string.IsNullOrWhiteSpace(chat.Name)))
        throw new Exception("Codex did not return a usable chat list.");
    await VerifyChatLabelAsync(chats[0]);
    Console.WriteLine($"PASS: Codex App Server discovery ({chats.Count} saved chats returned).");
    return;
}

if (args is ["--codex-preflight"])
{
    var executable = Codex.Find() ?? throw new Exception("Codex CLI was not detected.");
    var codex = new Codex(executable);
    using var preflightTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var chat = LocalSettings.Read().Chat ?? throw new Exception("No selected Codex chat.");
    try
    {
        await codex.CheckChatAsync(chat, preflightTimeout.Token);
        Console.WriteLine("Selected Codex chat can be resumed.");
    }
    catch (NotifyError error) when (error.Message.Contains("открыт в другом процессе Codex"))
    {
        Console.WriteLine("PASS: App Server reports the active-writer conflict before the call.");
    }
    var candidates = (await codex.ChatsAsync(preflightTimeout.Token)).Where(candidate => candidate.Id != chat.Id);
    var available = await codex.FindAvailableChatAsync(candidates, preflightTimeout.Token)
        ?? throw new Exception("The saved chats did not expose an available writer.");
    await codex.CheckChatAsync(available, preflightTimeout.Token);
    Console.WriteLine("PASS: A separate saved Codex chat was found without sending a prompt and its writer was released.");
    return;
}

static Task VerifyChatLabelAsync(Chat chat)
{
    var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            var window = new MainWindow();
            var picker = (System.Windows.Controls.ComboBox)window.FindName("Chats");
            var label = (System.Windows.Controls.TextBlock)picker.ItemTemplate.LoadContent();
            label.DataContext = chat;
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (label.Text != chat.Name)
                throw new Exception("Chat title was not rendered by the dropdown template.");
            var foreground = (System.Windows.Media.SolidColorBrush)label.Foreground;
            Equal(System.Windows.SystemColors.WindowTextColor, foreground.Color);
            if (foreground.Color == System.Windows.SystemColors.WindowColor)
                throw new Exception("Chat title is invisible against the dropdown background.");
            result.SetResult();
        }
        catch (Exception error) { result.SetException(error); }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return result.Task;
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var ct = timeout.Token;
var pipeName = "notify-specs-" + Guid.NewGuid().ToString("N");
using var serverPipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
await Task.WhenAll(serverPipe.WaitForConnectionAsync(ct), clientPipe.ConnectAsync(ct));

using var server = WebSocket.CreateFromStream(serverPipe, true, null, Timeout.InfiniteTimeSpan);
using var client = WebSocket.CreateFromStream(clientPipe, false, null, Timeout.InfiniteTimeSpan);
const string protocol = "alxprgs.notify.agent.v1";
const string callId = "10000000000000000000000000000000";
const string firstTurn = "20000000000000000000000000000000";
const string secondTurn = "30000000000000000000000000000000";
const string agentId = "40000000000000000000000000000000";
const string interruptedTurn = "50000000000000000000000000000000";
var registration = new Registration(agentId, "fixture", "Agent", false, [], [AgentBridge.CallProtocol]);
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var interruptedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var interruptedReply = new TaskCompletionSource<string>();
var invocations = 0;
const string phoneCallId = "60000000000000000000000000000000";
const string phoneTurnId = "70000000000000000000000000000000";
const string phoneSession = AgentBridge.PhoneSessionId;
var phoneStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var phoneInterrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var phoneEnabled = true;
var activeCalls = new CallActivity();
var bridge = new AgentBridge(new Uri("https://notify.example.test"), "synthetic-token", registration, new Chat("selected", "Выбранный чат"),
    async (text, token) =>
    {
        Interlocked.Increment(ref invocations);
        if (text == "Первая реплика")
        {
            started.SetResult();
            await release.Task;
            return "Устаревший ответ";
        }
        if (text == "Прерываемая реплика")
        {
            interruptedStarted.SetResult();
            return await interruptedReply.Task;
        }
        Equal("Вторая реплика", text);
        return "Ответ чата";
    }, _ => { }, enterCall: activeCalls.TryEnter, leaveCall: activeCalls.Leave,
    phoneEnabled: () => Volatile.Read(ref phoneEnabled), phoneReply: async (id, turn, text, token) =>
    {
        Equal(phoneCallId, id);
        if (turn == phoneTurnId)
        {
            Equal("Реплика по телефону", text);
            return "Ответ по телефону";
        }
        phoneStarted.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException) { phoneInterrupted.TrySetResult(); throw; }
        throw new Exception("Expected the phone turn to be canceled.");
    });
var ready = false;
var run = bridge.RunSessionAsync(client, () => ready = true, ct);

var hello = await Receive();
Equal("hello", hello.String("type"));
Equal(agentId, hello.String("agentId"));
var sessionId = hello.GetProperty("sessions")[0].String("id");
Equal(phoneSession, hello.GetProperty("sessions")[1].String("id"));
await Send(new { type = "hello_ack", protocolVersion = protocol, callProtocol = AgentBridge.CallProtocol,
    heartbeatSeconds = 1, agent = new { id = agentId } });
await Send(Event("call.invite", new { callId, sessionId = "foreign-session", mode = "conversation" }));
Equal("call.reject", (await NextControl()).String("type"));
await Send(Event("call.invite", new { callId, sessionId, mode = "conversation" }));
Equal("call.accept", (await NextControl()).String("type"));
Equal(true, ready);
await Send(Event("dialog.user", new { callId, sessionId, turnId = firstTurn, text = "Первая реплика" }));
await started.Task.WaitAsync(ct);
var phoneInvite = Event("call.invite", new { callId = phoneCallId, sessionId = phoneSession,
    phoneAssistant = true, target = "asterisk", securityLevel = "standard", extension = "7820", mode = "conversation" });
await Send(phoneInvite);
Equal("call.accept", (await NextControl()).String("type"));
await Send(Event("dialog.user", new { callId = phoneCallId, sessionId, turnId = phoneTurnId, text = "Чужая сессия" }));
await Send(Event("dialog.user", new { callId = phoneCallId, sessionId = phoneSession, turnId = phoneTurnId, text = "Реплика по телефону" }));
var phoneSay = await NextControl();
Equal("call.say", phoneSay.String("type"));
Equal(phoneCallId, phoneSay.Field("payload").String("callId"));
Equal("Ответ по телефону", phoneSay.Field("payload").String("text"));
await Send(Event("dialog.user", new { callId = phoneCallId, sessionId = phoneSession, turnId = firstTurn, text = "Перебивание" }));
await phoneStarted.Task.WaitAsync(ct);
await Send(Event("call.interrupted", new { callId = phoneCallId, sessionId = phoneSession, reason = "user_speech" }));
await phoneInterrupted.Task.WaitAsync(ct);
// End only the SIP call.
await Send(Event("dialog.user", new { callId = phoneCallId, sessionId = phoneSession, turnId = secondTurn, text = "Завершение" }));
await Send(Event("call.ended", new { callId = phoneCallId, sessionId = phoneSession }));
Volatile.Write(ref phoneEnabled, false);
await Send(phoneInvite);
Equal("call.reject", (await NextControl()).String("type"));
Equal(true, activeCalls.Busy);

await Send(Event("dialog.user", new { callId, sessionId, turnId = secondTurn, text = "Вторая реплика" }));
await Send(Event("dialog.user", new { callId, sessionId, turnId = secondTurn, text = "Вторая реплика" }));
// A subsequent control reply proves the socket reader has processed the duplicate before the first worker resumes.
await Send(Event("call.invite", new { callId = new string('f', 32), sessionId = "foreign-session", mode = "conversation" }));
Equal("call.reject", (await NextControl()).String("type"));
release.SetResult();
var say = await NextControl();
Equal("call.say", say.String("type"));
Equal(AgentBridge.CallProtocol, say.String("protocolVersion"));
Equal(secondTurn, say.Field("payload").String("turnId"));
Equal("Ответ чата", say.Field("payload").String("text"));
Equal(2, invocations);
await Send(Event("dialog.user", new { callId, sessionId, turnId = interruptedTurn, text = "Прерываемая реплика" }));
await interruptedStarted.Task.WaitAsync(ct);
await Send(Event("call.interrupted", new { callId, reason = "user_speech" }));
await Send(Event("call.invite", new { callId = new string('f', 32), sessionId = "foreign-session", mode = "conversation" }));
Equal("call.reject", (await NextControl()).String("type"));
interruptedReply.SetResult("Этот ответ уже опоздал");
await Send(Event("call.invite", new { callId = new string('f', 32), sessionId = "foreign-session", mode = "conversation" }));
Equal("call.reject", (await NextControl()).String("type"));
var heartbeat = await Receive();
Equal("heartbeat", heartbeat.String("type"));
Equal(1, heartbeat.GetProperty("sessions").GetArrayLength());
await timeout.CancelAsync();
try { await run; }
catch (OperationCanceledException) { }
catch (WebSocketException) when (ct.IsCancellationRequested) { }
catch (IOException) when (ct.IsCancellationRequested) { }
catch (NotifyError) when (ct.IsCancellationRequested) { }
Console.WriteLine("PASS: WebSocket hello, heartbeat, session routing, duplicate turn, stale reply and interruption.");

var reader = new BoundedLineReader(new MemoryStream("one\r\ntwo\nthree"u8.ToArray()), 5);
Equal("one", await reader.ReadAsync(CancellationToken.None));
Equal("two", await reader.ReadAsync(CancellationToken.None));
Equal("three", await reader.ReadAsync(CancellationToken.None));
Equal<string?>(null, await reader.ReadAsync(CancellationToken.None));
try
{
    await new BoundedLineReader(new MemoryStream("overflow"u8.ToArray()), 4).ReadAsync(CancellationToken.None);
    throw new Exception("Oversized IPC frame was accepted.");
}
catch (IOException) { }
Console.WriteLine("PASS: bounded IPC framing.");

using var handler = new McpFixture();
using var http = new HttpClient(handler);
await using (var relay = new McpRelay(new NotifyHttp(new Uri("https://notify.example.test"), "synthetic-token", http)))
    Equal(1, await relay.ProbeAsync(CancellationToken.None));
Equal(4, handler.Requests);
Console.WriteLine("PASS: MCP initialize, tools/list over SSE and session cleanup.");
if (Codex.ExplainFailure("thread-store conflict: thread synthetic already has an active writer") is not { } conflict ||
    !conflict.Contains("открыт в другом процессе Codex") || conflict.Contains("synthetic"))
    throw new Exception("Codex writer conflicts were not classified safely.");
Equal<string?>(null, Codex.ExplainFailure("synthetic unrecognized failure"));
await TlsBridgeSpec.RunAsync();

object Event(string type, object payload) => new { type, protocolVersion = protocol, payload };
ValueTask Send(object value) => server.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, ct);

async Task<JsonElement> Receive()
{
    var buffer = new byte[8192];
    var count = 0;
    ValueWebSocketReceiveResult result;
    do
    {
        result = await server.ReceiveAsync(buffer.AsMemory(count), ct);
        count += result.Count;
    } while (!result.EndOfMessage);
    using var doc = JsonDocument.Parse(buffer.AsMemory(0, count));
    return doc.RootElement.Clone();
}

async Task<JsonElement> NextControl()
{
    JsonElement frame;
    do { frame = await Receive(); } while (frame.String("type") == "heartbeat");
    return frame;
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

internal sealed class McpFixture : HttpMessageHandler
{
    internal int Requests { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        if (request.RequestUri?.AbsolutePath != "/mcp" || request.Headers.Authorization?.Parameter != "synthetic-token")
            throw new InvalidOperationException("MCP route or authorization mismatch.");
        if (Requests > 1 && (!request.Headers.TryGetValues("Mcp-Session-Id", out var session) || session.Single() != "synthetic-session"))
            throw new InvalidOperationException("MCP session was not propagated.");
        if (request.Method == HttpMethod.Delete)
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        switch (document.RootElement.String("method"))
        {
            case "initialize":
                response.Headers.Add("Mcp-Session-Id", "synthetic-session");
                response.Content = new StringContent("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25"}}""",
                    Encoding.UTF8, "application/json");
                return response;
            case "notifications/initialized":
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            case "tools/list":
                response.Content = new StringContent("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":2,\"result\":{\"tools\":[{\"name\":\"send_notification\"}]}}\n\n",
                    Encoding.UTF8, "text/event-stream");
                return response;
            default: throw new InvalidOperationException("Unexpected MCP method.");
        }
    }
}
