using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Notify.Desktop;

if (args is ["--codex-discovery"])
{
    var executable = Codex.Find() ?? throw new Exception("Codex CLI was not detected.");
    using var discoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var chats = await new Codex(executable).ChatsAsync(discoveryTimeout.Token);
    Console.WriteLine($"PASS: Codex App Server discovery ({chats.Count} saved chats returned).");
    return;
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
var registration = new Registration(agentId, "fixture", "Agent", false, [], [AgentBridge.CallProtocol]);
var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var invocations = 0;
var bridge = new AgentBridge(new Uri("https://notify.example.test"), "synthetic-token", registration, new Chat("selected", "Выбранный чат"),
    async (text, token) =>
    {
        Interlocked.Increment(ref invocations);
        if (text == "Первая реплика")
        {
            started.SetResult();
            await release.Task.WaitAsync(token);
            return "Устаревший ответ";
        }
        Equal("Вторая реплика", text);
        return "Ответ чата";
    }, _ => { });
var ready = false;
var run = bridge.RunSessionAsync(client, () => ready = true, ct);

var hello = await Receive();
Equal("hello", hello.String("type"));
Equal(agentId, hello.String("agentId"));
var sessionId = hello.GetProperty("sessions")[0].String("id");
await Send(new { type = "hello_ack", protocolVersion = protocol, callProtocol = AgentBridge.CallProtocol,
    heartbeatSeconds = 1, agent = new { id = agentId } });
await Send(Event("call.invite", new { callId, sessionId = "foreign-session", mode = "conversation" }));
Equal("call.reject", (await NextControl()).String("type"));
await Send(Event("call.invite", new { callId, sessionId, mode = "conversation" }));
Equal("call.accept", (await NextControl()).String("type"));
Equal(true, ready);
await Send(Event("dialog.user", new { callId, sessionId, turnId = firstTurn, text = "Первая реплика" }));
await started.Task.WaitAsync(ct);
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
Equal("heartbeat", (await Receive()).String("type"));
await timeout.CancelAsync();
try { await run; }
catch (OperationCanceledException) { }
catch (WebSocketException) when (ct.IsCancellationRequested) { }
catch (IOException) when (ct.IsCancellationRequested) { }
catch (NotifyError) when (ct.IsCancellationRequested) { }
Console.WriteLine("PASS: WebSocket hello, heartbeat, session routing, duplicate turn and stale reply.");

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
