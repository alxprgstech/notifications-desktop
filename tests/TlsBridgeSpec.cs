using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Notify.Desktop;

internal static class TlsBridgeSpec
{
    private const string Protocol = "alxprgs.notify.agent.v1";
    private const string AgentId = "a0000000000000000000000000000001";
    private const string CallId = "b0000000000000000000000000000001";
    private const string TurnId = "c0000000000000000000000000000001";
    private const string Token = "synthetic-token-for-loopback-only";

    internal static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.UserKeySet);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        await using var server = builder.Build();
        server.UseWebSockets();

        var registration = new Registration(AgentId, "synthetic-source", "TLS fixture", false, ["realtime_audio"], [AgentBridge.CallProtocol]);
        var count = 0;
        var live = 0;
        var replies = 0;
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MapGet("/api/v1/agents/self", (HttpContext context) =>
            Authorized(context)
                ? Results.Json(registration with { Online = Volatile.Read(ref live) != 0 })
                : Results.Unauthorized());
        server.Map("/api/v1/agents/connect", async context =>
        {
            if (!Authorized(context)) { context.Response.StatusCode = 401; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var connection = Interlocked.Increment(ref count);
            Interlocked.Increment(ref live);
            try
            {
                var hello = await ReadAsync(socket, ct);
                Require(hello.String("type") == "hello" && hello.String("agentId") == AgentId, "Agent hello mismatch");
                var sessionId = hello.GetProperty("sessions")[0].String("id");
                await SendAsync(socket, new { type = "hello_ack", protocolVersion = Protocol,
                    callProtocol = AgentBridge.CallProtocol, heartbeatSeconds = 1, agent = new { id = AgentId } }, ct);
                var first = await ReadAsync(socket, ct);
                Require(first.String("type") == "heartbeat", "Heartbeat missing on WSS");
                if (connection == 1)
                {
                    context.Abort();
                    return;
                }
                await SendAsync(socket, Event("call.invite", new { callId = CallId, sessionId, mode = "conversation" }), ct);
                var accepted = await ReadControlAsync(socket, ct);
                Require(accepted.String("type") == "call.accept" && accepted.String("protocolVersion") == AgentBridge.CallProtocol,
                    "Conversation invite was not accepted with the negotiated protocol");
                await SendAsync(socket, Event("dialog.user", new { callId = CallId, sessionId, turnId = TurnId, text = "Тестовая реплика" }), ct);
                var said = await ReadControlAsync(socket, ct);
                Require(said.String("type") == "call.say" &&
                    said.Field("payload").String("turnId") == TurnId && said.Field("payload").String("text") == "Тестовый ответ",
                    "The resumed connection did not return the bound chat's answer");
                observed.TrySetResult();
                // Keep the socket online for the second diagnostic and release it when the bridge stops.
                while (socket.State == WebSocketState.Open)
                    await ReadAsync(socket, ct);
            }
            catch (Exception error) when (!observed.Task.IsCompleted)
            {
                observed.TrySetException(error);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or WebSocketException) { }
            finally { Interlocked.Decrement(ref live); }
        });

        await server.StartAsync(ct);
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var origin = new Uri(address);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, remote, _, errors) =>
            remote is not null && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0 &&
            CryptographicOperations.FixedTimeEquals(remote.GetCertHash(HashAlgorithmName.SHA256), certificate.GetCertHash(HashAlgorithmName.SHA256));
        using var http = new HttpClient(handler);
        var api = new NotifyHttp(origin, Token, http);
        var initial = await api.RegistrationAsync(ct);
        Require(initial.AgentId == AgentId && !initial.Online, "Unexpected registration before connecting");
        var bridge = new AgentBridge(origin, Token, initial, new Chat("synthetic-existing-chat", "Тестовый чат"), (message, _) =>
        {
            Require(message == "Тестовая реплика", "Unexpected chat input");
            Interlocked.Increment(ref replies);
            return Task.FromResult("Тестовый ответ");
        }, _ => { }, http);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = bridge.RunAsync(lifetime.Token);
        try
        {
            await observed.Task.WaitAsync(ct);
            var diagnostic = await api.RegistrationAsync(ct);
            Require(diagnostic.Online && count == 2 && replies == 1, "Diagnostics changed the live connection or the reply was duplicated");
        }
        finally
        {
            await lifetime.CancelAsync();
            await running.WaitAsync(ct);
            await server.StopAsync(ct);
        }
        Console.WriteLine("PASS: HTTPS registration, authenticated WSS reconnect, heartbeat, conversation and non-disruptive diagnostics.");
    }

    private static bool Authorized(HttpContext context) => context.Request.Headers.Authorization == "Bearer " + Token;
    private static object Event(string type, object payload) => new { type, protocolVersion = Protocol, payload };
    private static ValueTask SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value).AsMemory(), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonElement> ReadAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var count = 0;
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(count), ct);
            if (received.MessageType != WebSocketMessageType.Text)
                throw new IOException("Test WebSocket closed.");
            count += received.Count;
            Require(count < buffer.Length, "Oversized test frame");
        } while (!received.EndOfMessage);
        using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> ReadControlAsync(WebSocket socket, CancellationToken ct)
    {
        JsonElement message;
        do { message = await ReadAsync(socket, ct); } while (message.String("type") == "heartbeat");
        return message;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
