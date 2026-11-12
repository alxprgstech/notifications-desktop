using System.Diagnostics;

namespace Notify.Desktop;

internal sealed class Codex(string executable)
{
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
            await WriteAsync(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "notify_desktop", title = "ALXPRGS Notify Desktop", version = "1.0.0" } } }, ct);
            await ReadResultAsync(process, 1, ct);
            await WriteAsync(process, new { method = "initialized", @params = new { } }, ct);
            await WriteAsync(process, new { id = 2, method = "thread/list", @params = new { limit = 50, archived = false, sortKey = "updated_at" } }, ct);
            var result = await ReadResultAsync(process, 2, ct);
            return result.GetProperty("data").EnumerateArray().Select(item =>
                new Chat(item.GetProperty("id").GetString()!, item.String("name") ?? item.String("preview") ?? "Чат Codex",
                    item.String("cwd"))).ToList();
        }
        finally { Kill(process); }
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
            if (string.Equals(config.RootElement.Field("transport").String("command"), current, StringComparison.OrdinalIgnoreCase))
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
        if (!Directory.Exists(chat.Directory))
            throw new NotifyError("Рабочий каталог чата Codex недоступен.");
        using var process = Start(["--ask-for-approval", "never", "--sandbox", "read-only",
            "exec", "resume", "--json", "--skip-git-repo-check", chat.Id, "-"], chat.Directory);
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
                var item = entry.Field("item");
                if (entry.String("type") == "item.completed" && item.String("type") == "agent_message")
                    reply = item.String("text");
            }
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(reply)
                ? reply : throw new NotifyError("Codex не вернул ответ. Проверьте вход и доступность выбранного чата.");
        }
        finally { Kill(process); }
    }

    private Process Start(string[] arguments, string? directory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = directory ?? Environment.CurrentDirectory
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new NotifyError("Не удалось запустить Codex.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return process;
    }

    private static Task WriteAsync(Process process, object value, CancellationToken ct) =>
        process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value).AsMemory(), ct);

    private static async Task<JsonElement> ReadResultAsync(Process process, int id, CancellationToken ct)
    {
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.Field("id") is { ValueKind: JsonValueKind.Number } value && value.GetInt32() == id)
            {
                if (doc.RootElement.TryGetProperty("result", out var result)) return result.Clone();
                break;
            }
        }
        throw new NotifyError("Codex App Server не вернул список чатов.");
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
