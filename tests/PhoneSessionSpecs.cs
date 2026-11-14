using System.IO;
using System.Text;
using System.Text.Json;
using Notify.Desktop;

internal static class PhoneSessionSpecs
{
    internal static async Task RunAsync()
    {
        using var workspace = new LabDirectory(Path.GetTempPath());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var launcher = Path.ChangeExtension(typeof(PhoneSessionSpecs).Assembly.Location, ".exe");
        var codex = new Codex(launcher);
        var file = Path.Combine(workspace.Path, "thread-id");
        var starts = Path.Combine(workspace.Path, "rpc-starts");
        string current;
        await using (var first = new PhoneCodex(codex, workspace.Path))
        {
            await first.WarmAsync(ct);
            current = File.ReadAllText(file);
            await first.WarmAsync(ct);
            await using var concurrent = new PhoneCodex(codex, workspace.Path);
            await RequiresError(() => concurrent.WarmAsync(ct));
        }
        await using (var next = new PhoneCodex(codex, workspace.Path))
        {
            await next.WarmAsync(ct);
            Require(File.ReadAllText(file) == current && File.ReadAllLines(starts).Length == 1);
            var rollover = next.RolloverAsync(false, ct);
            await RequiresError(() => next.RolloverAsync(false, ct));
            await rollover;
            Require(File.ReadAllText(file) != current && File.ReadAllLines(starts).Length == 2);
            current = File.ReadAllText(file);
            File.WriteAllText(Path.Combine(workspace.Path, "fail-name"), "");
            await RequiresError(() => next.RolloverAsync(false, ct));
            Require(File.ReadAllText(file) == current && File.ReadAllLines(starts).Length == 3);
        }
        await using (var recovered = new PhoneCodex(codex, workspace.Path))
        {
            await recovered.WarmAsync(ct);
            Require(File.ReadAllText(file) == current && File.ReadAllLines(starts).Length == 3);
            await RequiresError(() => recovered.RolloverAsync(false, ct));
            Require(File.ReadAllLines(Path.Combine(workspace.Path, "rpc-archives")).Length == 1);
        }
        File.Delete(file);
        File.Delete(Path.Combine(workspace.Path, "thread-pending"));
        await using (var missing = new PhoneCodex(codex, workspace.Path))
            await RequiresError(() => missing.WarmAsync(ct));
        var count = File.ReadAllLines(starts).Length;
        File.WriteAllText(file, "broken id");
        await using (var corrupt = new PhoneCodex(codex, workspace.Path))
            await RequiresError(() => corrupt.WarmAsync(ct));
        Require(File.ReadAllLines(starts).Length == count);
        Console.WriteLine("PASS: isolated Codex RPC fixture, first start, restart, lock, rollover, rollback and invalid pointer.");
    }

    internal static async Task FakeServerAsync(string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (arguments[0] == "mcp")
        {
            Console.WriteLine("[]");
            return;
        }
        var starts = Path.GetFullPath("rpc-starts");
        var archives = Path.GetFullPath("rpc-archives");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var parsed = JsonDocument.Parse(line);
            var request = parsed.RootElement;
            if (request.Field("id").ValueKind != JsonValueKind.Number) continue;
            var id = request.Field("id").GetInt32();
            var values = request.Field("params");
            var threadId = values.String("threadId");
            object result = new { };
            switch (request.String("method"))
            {
                case "account/read":
                    result = new { account = new { type = "chatgpt" } };
                    break;
                case "mcpServerStatus/list":
                    result = new { data = Array.Empty<object>() };
                    break;
                case "thread/start":
                    await Task.Delay(120);
                    threadId = Guid.NewGuid().ToString("D");
                    File.AppendAllLines(starts, [threadId]);
                    result = new { thread = new { id = threadId } };
                    break;
                case "thread/resume":
                    result = new { thread = new { id = threadId } };
                    break;
                case "thread/name/set" when File.Exists("fail-name"):
                    Console.WriteLine(JsonSerializer.Serialize(new { id, error = new { message = "Synthetic write failure" } }));
                    continue;
                case "thread/archive":
                    File.AppendAllLines(archives, [threadId!]);
                    break;
                case "thread/list":
                    var listed = File.Exists(starts) ? File.ReadAllLines(starts) : [];
                    var archived = File.Exists(archives) ? File.ReadAllLines(archives) : [];
                    result = new { data = listed.Except(archived).Select(item => new { id = item }).ToArray() };
                    break;
            }
            Console.WriteLine(JsonSerializer.Serialize(new { id, result }));
        }
    }

    private static async Task RequiresError(Func<Task> action)
    {
        try { await action(); }
        catch (NotifyError) { return; }
        throw new Exception("An invalid phone profile operation succeeded.");
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Phone session lifecycle changed unexpectedly.");
    }
}
