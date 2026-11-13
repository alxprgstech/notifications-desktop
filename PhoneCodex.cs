using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Notify.Desktop;

internal sealed class PhoneCodex(Codex codex, string directory) : IDisposable
{
    internal const string Name = "Notify Phone Assistant";
    private const string Instructions = """
        Ты отвечаешь голосом на телефонный звонок. Отвечай по-русски, простым текстом,
        одним или двумя короткими предложениями. Пиши иностранные названия кириллицей.
        Отвечай на вопросы. Запросы на запуск команд, открытие файлов, внешние обращения,
        изменение данных и любые действия от имени собеседника в телефонном режиме отклоняй.
        """;
    private readonly SemaphoreSlim gate = new(1);
    private Process? server;
    private string? threadId;
    private int nextId;
    private string ThreadFile => Path.Combine(directory, "thread-id");
    internal bool Ready => server is { HasExited: false } && threadId is not null;

    internal async Task WarmAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { if (!Ready) await OpenAsync(ct); }
        catch { Reset(); throw; }
        finally { gate.Release(); }
    }

    private async Task OpenAsync(CancellationToken ct)
    {
        Reset();
        Directory.CreateDirectory(directory);
        var started = Stopwatch.GetTimestamp();
        var options = new List<string> { "--strict-config",
            "--disable", "shell_tool", "--disable", "shell_snapshot", "--disable", "hooks",
            "--disable", "plugins", "--disable", "apps", "--disable", "multi_agent", "--disable", "multi_agent_v2",
            "--disable", "memories", "--disable", "skill_search", "--disable", "skill_mcp_dependency_install",
            "--disable", "goals", "-c", "features.code_mode.enabled=false", "-c", "web_search=disabled",
            "-c", "approval_policy=never", "-c", "sandbox_mode=read-only",
            "-c", "forced_login_method=chatgpt", "-c", "model_provider=openai",
            "-c", "chatgpt_base_url=https://chatgpt.com/backend-api/" };
        using (var inventory = codex.Start(["mcp", "list", "--json"], directory, phone: true))
        using (ct.Register(() => Codex.Kill(inventory)))
        {
            using var configured = JsonDocument.Parse(await inventory.StandardOutput.ReadToEndAsync(ct));
            await inventory.WaitForExitAsync(ct);
            if (inventory.ExitCode != 0) throw new NotifyError("Не удалось проверить настройки инструментов Codex.");
            foreach (var entry in configured.RootElement.EnumerateArray())
            {
                if (entry.Field("enabled").ValueKind == JsonValueKind.False) continue;
                var name = entry.String("name");
                if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9_-]+\z"))
                    throw new NotifyError("Имя MCP-сервера несовместимо с телефонным режимом Codex.");
                options.Add("-c");
                options.Add($"mcp_servers.{name}.enabled=false");
                // Some desktop registrations appear in CLI inventory without a user config
                // transport. Give the disabled entry a valid, inert transport in that case.
                if (entry.Field("transport").String("type") == "stdio")
                {
                    options.Add("-c");
                    options.Add($"mcp_servers.{name}.command=notify-disabled-mcp");
                }
                else if (entry.Field("transport").String("type") == "streamable_http")
                {
                    options.Add("-c");
                    options.Add($"mcp_servers.{name}.url=http://127.0.0.1:1");
                }
                else throw new NotifyError("Неизвестный транспорт MCP в конфигурации Codex.");
            }
        }
        options.AddRange(["app-server", "--listen", "stdio://"]);
        server = codex.Start(options.ToArray(), directory, phone: true);
        nextId = 1;
        await Codex.InitializeAsync(server, ct);
        await RequireChatGptAsync(ct);
        var mcp = await RequestAsync("mcpServerStatus/list", new { detail = "toolsAndAuthOnly" }, ct);
        if (mcp.Field("data") is not { ValueKind: JsonValueKind.Array } servers || mcp.String("nextCursor") is not null ||
            servers.EnumerateArray().Any(entry =>
                entry.Field("tools") is not { ValueKind: JsonValueKind.Object } tools || tools.EnumerateObject().Any()))
            throw new NotifyError("Телефонный режим Codex требует отключённых MCP-инструментов.");
        var saved = File.Exists(ThreadFile) ? (await File.ReadAllTextAsync(ThreadFile, ct)).Trim() : null;
        if (saved is not null && !Guid.TryParseExact(saved, "D", out _))
            throw new NotifyError("Повреждён идентификатор чата телефонного помощника.");
        var result = saved is null
            ? await RequestAsync("thread/start", new { cwd = directory, sandbox = "read-only",
                approvalPolicy = "never", modelProvider = "openai", baseInstructions = Instructions }, ct)
            : await RequestAsync("thread/resume", new { threadId = saved, cwd = directory, sandbox = "read-only",
                approvalPolicy = "never", modelProvider = "openai", baseInstructions = Instructions, excludeTurns = true }, ct);
        var id = result.Field("thread").String("id");
        if (!Guid.TryParseExact(id, "D", out _) || (saved is not null && saved != id))
            throw new NotifyError("Codex не подтвердил чат телефонного помощника.");
        if (saved is null)
        {
            await RequestAsync("thread/name/set", new { threadId = id, name = Name }, ct);
            LocalSettings.Write(ThreadFile, Encoding.ASCII.GetBytes(id!));
        }
        threadId = id;
        PhoneTiming.Mark(null, null, saved is null ? "codex_thread_start" : "codex_thread_resume", started);
    }

    private async Task RequireChatGptAsync(CancellationToken ct)
    {
        var account = await RequestAsync("account/read", new { refreshToken = false }, ct);
        if (account.Field("account").String("type") != "chatgpt")
            throw new NotifyError("Войдите в Codex CLI через ChatGPT для телефонного помощника.");
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        var id = ++nextId;
        await Codex.WriteAsync(server!, new { id, method, @params = parameters }, ct);
        return await Codex.ReadResultAsync(server!, id, ct);
    }

    internal async Task<string> ReplyAsync(string callId, string userTurnId, string text, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!Ready) await OpenAsync(ct);
            await RequireChatGptAsync(ct);
            var started = Stopwatch.GetTimestamp();
            var result = await RequestAsync("turn/start", new { threadId, effort = "low",
                input = new[] { new { type = "text", text, text_elements = Array.Empty<object>() } } }, ct);
            var turnId = result.Field("turn").String("id") ?? throw new NotifyError("Codex не подтвердил реплику.");
            PhoneTiming.Mark(callId, userTurnId, "codex_turn_start_ack", started);
            return await ReadReplyAsync(callId, userTurnId, turnId, started, ct);
        }
        catch (TurnInterruptedException) { throw new OperationCanceledException(ct); }
        catch { Reset(); throw; }
        finally { gate.Release(); }
    }

    private async Task<string> ReadReplyAsync(string callId, string userTurnId, string turnId, long started, CancellationToken ct)
    {
        string? reply = null;
        var first = true;
        using var drain = new CancellationTokenSource();
        var cancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => cancellation.TrySetResult());
        var interruptSent = false;
        while (true)
        {
            var line = server!.StandardOutput.ReadLineAsync(drain.Token).AsTask();
            if (!interruptSent && await Task.WhenAny(line, cancellation.Task) == cancellation.Task)
            {
                interruptSent = true;
                drain.CancelAfter(TimeSpan.FromSeconds(3));
                await Codex.WriteAsync(server, new { id = ++nextId, method = "turn/interrupt", @params = new { threadId, turnId } }, drain.Token);
            }
            using var doc = JsonDocument.Parse(await line ?? throw new IOException("App Server exited."));
            var message = doc.RootElement;
            var method = message.String("method");
            var payload = message.Field("params");
            if (method is not null && message.TryGetProperty("id", out _))
                throw new NotifyError("Codex запросил действие, недоступное по телефону.");
            if (payload.String("threadId") != threadId) continue;
            if (method == "item/agentMessage/delta" && payload.String("turnId") == turnId && first)
            {
                first = false;
                PhoneTiming.Mark(callId, userTurnId, "codex_first_delta", started);
            }
            if (method == "item/started" && payload.String("turnId") == turnId &&
                payload.Field("item").String("type") is not ("agentMessage" or "userMessage" or "reasoning" or "contextCompaction"))
                throw new NotifyError("Codex попытался использовать действие, недоступное по телефону.");
            if (method == "item/completed" && payload.String("turnId") == turnId &&
                payload.Field("item").String("type") == "agentMessage")
                reply = payload.Field("item").String("text");
            if (method != "turn/completed" || payload.Field("turn").String("id") != turnId) continue;
            if (ct.IsCancellationRequested) throw new TurnInterruptedException();
            if (payload.Field("turn").String("status") != "completed" || string.IsNullOrWhiteSpace(reply))
                throw new NotifyError("Codex не вернул голосовой ответ.");
            PhoneTiming.Mark(callId, userTurnId, "codex_turn_complete", started);
            return reply;
        }
    }

    private void Reset()
    {
        if (server is null) return;
        Codex.Kill(server);
        server.Dispose();
        server = null;
        threadId = null;
    }

    public void Dispose()
    {
        Reset();
        gate.Dispose();
    }

    private sealed class TurnInterruptedException : Exception;
}

internal static class PhoneTiming
{
    internal static void Mark(string? callId, string? turnId, string stage, long? started = null)
    {
        if (Environment.GetEnvironmentVariable("NOTIFY_PHONE_TIMING") != "1") return;
        try
        {
            Directory.CreateDirectory(LocalSettings.DirectoryPath);
            var line = JsonSerializer.Serialize(new { callId, turnId, stage, ticks = Stopwatch.GetTimestamp(),
                frequency = Stopwatch.Frequency, elapsedMs = started is { } timestamp ? Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds : (double?)null });
            var path = Path.Combine(LocalSettings.DirectoryPath, "phone-timing.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= 4 * 1024 * 1024) File.Move(path, path + ".1", true);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
