using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Notify.Desktop;

internal sealed record Chat(string Id, string Name, string? Directory = null)
{
    public override string ToString() => Name;
}

internal sealed record Preferences(string Origin = "https://notify.alxprgs.tech", string Client = "Codex", Chat? Chat = null);
internal sealed record Registration(string AgentId, string SourceId, string Name, bool Online, string[] Capabilities, string[] SupportedCallProtocols);

internal static class LocalSettings
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ALXPRGS", "Notify Desktop");

    private static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    private static string TokenPath => Path.Combine(DirectoryPath, "source.dpapi");
    private sealed record Credential(string Origin, string Token);

    internal static Preferences Read() => File.Exists(SettingsPath)
        ? JsonSerializer.Deserialize<Preferences>(File.ReadAllBytes(SettingsPath), Json) ?? new()
        : new();

    internal static string? ReadToken(Uri origin)
    {
        if (!File.Exists(TokenPath)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), null, DataProtectionScope.CurrentUser);
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(bytes, Json);
            return credential?.Origin == origin.GetLeftPart(UriPartial.Authority) ? credential.Token : null;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static void Save(Preferences preferences, string? token = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (token != null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Credential(preferences.Origin, token), Json);
            try { Write(TokenPath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        Write(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(preferences, Json));
    }

    internal static void Write(string path, byte[] bytes)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }

    internal static Uri HttpsOrigin(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            url.UserInfo.Length != 0 || url.PathAndQuery != "/" || url.Fragment.Length != 0)
            throw new NotifyError("Укажите HTTPS-адрес Notify без пути и параметров.");
        return url;
    }
}

internal sealed class NotifyError(string message) : Exception(message);

internal sealed class NotifyHttp(Uri origin, string token, HttpClient? transport = null)
{
    internal static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8)
    }) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    internal HttpClient Transport { get; } = transport ?? Client;

    internal HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(origin, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal async Task<Registration> RegistrationAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "/api/v1/agents/self");
        using var response = await Transport.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                code = body.RootElement.String("code");
            }
            catch (JsonException) { }
            throw new NotifyError(code switch
            {
                "agent_not_registered" => "Зарегистрируйте агента для источника в кабинете и подтвердите на Android.",
                "agents_scope_required" => "Токен источника должен иметь право agents:connect.",
                _ => $"Проверка регистрации Notify вернула HTTP {(int)response.StatusCode}."
            });
        }
        return JsonSerializer.Deserialize<Registration>(await response.Content.ReadAsByteArrayAsync(ct), LocalSettings.Json)
            ?? throw new NotifyError("Notify вернул пустую регистрацию.");
    }
}

internal static class JsonFields
{
    internal static JsonElement Field(this JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;
    internal static string? String(this JsonElement value, string key) =>
        value.Field(key) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
}
