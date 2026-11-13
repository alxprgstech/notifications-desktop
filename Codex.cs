using System.Diagnostics;

namespace Notify.Desktop;

internal sealed class Codex(string executable)
{
    private const string ChatInUse = "Этот чат открыт в другом процессе Codex. Освободите чат или выберите другой и нажмите «Подключить».";
    private const string ChatUnavailable = "Этот чат недоступен локальному Codex. Выберите другой сохранённый чат.";

    internal static string? Find()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npm = Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "node_modules");
        var platform = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
            System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";
        var vendor = Path.Combine(npm, "@openai", $"codex-win32-{platform}", "vendor");
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "codex.exe"))
            .Concat(Directory.Exists(vendor) ? Directory.EnumerateFiles(vendor, "codex.exe", SearchOption.AllDirectories) : [])
            .FirstOrDefault(File.Exists);
    }

    internal async Task<List<Chat>> ChatsAsync(CancellationToken ct)
    {
        using var process = Start(["app-server", "--listen", "stdio://"]);
        using var cleanup = ct.Register(() => Kill(process));
        try
        {
            await InitializeAsync(process, ct);
            await WriteAsync(process, new { id = 2, method = "thread/list", @params = new { limit = 50, archived = false, sortKey = "updated_at" } }, ct);
            var result = await ReadResultAsync(process, 2, ct);
            return result.GetProperty("data").EnumerateArray().Select(item =>
                new Chat(item.GetProperty("id").GetString()!, ChatLabel(item),
                    item.String("cwd"))).ToList();
        }
        finally { Kill(process); }
    }

    internal async Task CheckChatAsync(Chat chat, CancellationToken ct)
    {
        RequireChatDirectory(chat);
        using var process = Start(["app-server", "--listen", "stdio://"], chat.Directory);
        using var cleanup = ct.Register(() => Kill(process));
        try
        {
            await InitializeAsync(process, ct);
            await ResumeChatAsync(process, chat, 2, ct);
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(ct);
        }
    }

    internal async Task<Chat?> FindAvailableChatAsync(IEnumerable<Chat> chats, CancellationToken ct)
    {
        using var process = Start(["app-server", "--listen", "stdio://"]);
        using var cleanup = ct.Register(() => Kill(process));
        try
        {
            await InitializeAsync(process, ct);
            var requestId = 2;
            foreach (var chat in chats)
            {
                if (!Directory.Exists(chat.Directory)) continue;
                try { await ResumeChatAsync(process, chat, requestId++, ct); }
                catch (NotifyError error) when (error.Message is ChatInUse or ChatUnavailable) { continue; }
                return chat;
            }
            return null;
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync(ct);
        }
    }

    private static async Task ResumeChatAsync(Process process, Chat chat, int id, CancellationToken ct)
    {
        await WriteAsync(process, new { id, method = "thread/resume",
            @params = new { threadId = chat.Id, cwd = chat.Directory, excludeTurns = true,
                approvalPolicy = "never", sandbox = "read-only" } }, ct);
        var result = await ReadResultAsync(process, id, ct);
        if (result.Field("thread").String("id") != chat.Id)
            throw new NotifyError("Codex не подтвердил выбранный чат.");
    }

    private static string ChatLabel(JsonElement item)
    {
        var title = item.String("name");
        if (!string.IsNullOrWhiteSpace(title)) return title.Trim();
        var preview = item.String("preview");
        if (!string.IsNullOrWhiteSpace(preview)) return preview.Trim();
        return "Чат Codex · " + item.GetProperty("id").GetString();
    }

    internal async Task SetupMcpAsync(CancellationToken ct)
    {
        var current = Environment.ProcessPath ?? throw new NotifyError("Не найден исполняемый файл Notify Desktop.");
        using var existing = Start(["mcp", "get", "alxprgs-notify-desktop", "--json"]);
        using var existingCleanup = ct.Register(() => Kill(existing));
        var output = await existing.StandardOutput.ReadToEndAsync(ct);
        await existing.WaitForExitAsync(ct);
        if (existing.ExitCode == 0)
        {
            using var config = JsonDocument.Parse(output);
            var transport = config.RootElement.Field("transport");
            var command = transport.String("command");
            var args = transport.Field("args");
            if (string.Equals(command, current, StringComparison.OrdinalIgnoreCase) ||
                command is not null && Path.IsPathFullyQualified(command) && File.Exists(command) &&
                string.Equals(Path.GetFileName(command), Path.GetFileName(current), StringComparison.OrdinalIgnoreCase) &&
                args.ValueKind == JsonValueKind.Array && args.GetArrayLength() == 1 && args[0].GetString() == "--mcp")
                return;
            throw new NotifyError("Имя MCP alxprgs-notify-desktop занято другой установкой.");
        }
        using var add = Start(["mcp", "add", "alxprgs-notify-desktop", "--", current, "--mcp"]);
        using var cleanup = ct.Register(() => Kill(add));
        await add.StandardOutput.ReadToEndAsync(ct);
        await add.WaitForExitAsync(ct);
        if (add.ExitCode != 0) throw new NotifyError("Codex не сохранил настройку MCP.");
    }

    internal async Task<string> ReplyAsync(Chat chat, string text, CancellationToken ct)
    {
        RequireChatDirectory(chat);
        string? failure = null;
        using var process = Start(["--ask-for-approval", "never", "--sandbox", "read-only",
            "exec", "resume", "--json", "--skip-git-repo-check", chat.Id, "-"], chat.Directory,
            diagnostic =>
            {
                if (ExplainFailure(diagnostic) is { } known) Interlocked.CompareExchange(ref failure, known, null);
            });
        using var cleanup = ct.Register(() => Kill(process));
        try
        {
            await process.StandardInput.WriteAsync(text.AsMemory(), ct);
            process.StandardInput.Close();
            string? reply = null;
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith('{')) continue;
                using var doc = JsonDocument.Parse(line);
                var entry = doc.RootElement;
                if (entry.String("type") is "error" or "turn.failed")
                {
                    var message = entry.String("message") ?? entry.Field("error").String("message");
                    if (ExplainFailure(message) is { } known) Interlocked.CompareExchange(ref failure, known, null);
                }
                var item = entry.Field("item");
                if (entry.String("type") == "item.completed" && item.String("type") == "agent_message")
                    reply = item.String("text");
            }
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(reply)
                ? reply : throw new NotifyError(failure ?? "Codex не вернул ответ. Проверьте вход и доступность выбранного чата.");
        }
        finally { Kill(process); }
    }

    internal Process Start(string[] arguments, string? directory = null, Action<string>? diagnostic = null, bool phone = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = directory ?? Environment.CurrentDirectory
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (phone)
        {
            foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "OPENAI_BASE_URL",
                "CODEX_INTERNAL_ORIGINATOR_OVERRIDE" })
                start.Environment.Remove(name);
        }
        var process = Process.Start(start) ?? throw new NotifyError("Не удалось запустить Codex.");
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is { } line) diagnostic?.Invoke(line);
        };
        process.BeginErrorReadLine();
        return process;
    }

    private static void RequireChatDirectory(Chat chat)
    {
        if (!Directory.Exists(chat.Directory))
            throw new NotifyError("Рабочий каталог чата Codex недоступен.");
    }

    internal static string? ExplainFailure(string? diagnostic)
    {
        if (diagnostic is null) return null;
        if (diagnostic.Contains("active writer", StringComparison.OrdinalIgnoreCase)) return ChatInUse;
        if (diagnostic.Contains("no rollout found", StringComparison.OrdinalIgnoreCase))
            return ChatUnavailable;
        return null;
    }

    internal static async Task InitializeAsync(Process process, CancellationToken ct)
    {
        await WriteAsync(process, new { id = 1, method = "initialize",
            @params = new { clientInfo = new { name = "notify_desktop", title = "ALXPRGS Notify Desktop", version = "1.0.0" } } }, ct);
        await ReadResultAsync(process, 1, ct);
        await WriteAsync(process, new { method = "initialized", @params = new { } }, ct);
    }

    internal static Task WriteAsync(Process process, object value, CancellationToken ct) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);

    internal static async Task<JsonElement> ReadResultAsync(Process process, int id, CancellationToken ct)
    {
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.Field("id") is { ValueKind: JsonValueKind.Number } value && value.GetInt32() == id)
            {
                if (doc.RootElement.TryGetProperty("result", out var result)) return result.Clone();
                if (ExplainFailure(doc.RootElement.Field("error").String("message")) is { } known)
                    throw new NotifyError(known);
                break;
            }
        }
        throw new NotifyError("Codex App Server не подтвердил запрос.");
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
