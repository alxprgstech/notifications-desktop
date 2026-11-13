using System.Net;
using System.Net.Http.Headers;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Notify.Desktop;

internal sealed record DesktopUpdate(string Version, string Notes, Uri Download, long Size, string Sha256);

internal sealed partial class UpdateManager(string cache, HttpClient? transport = null)
{
    internal const long MaxInstallerBytes = 200L * 1024 * 1024;
    private const string Repository = "alxprgstech/notifications-desktop";
    private static readonly Uri Releases = new($"https://api.github.com/repos/{Repository}/releases?per_page=30");
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private HttpClient Http => transport ?? Client;
    private EntityTagHeaderValue? etag;
    private DesktopUpdate? latest;

    // Only stable versions are eligible. Build metadata does not affect SemVer precedence.
    [GeneratedRegex(@"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z")]
    private static partial Regex StableVersion();

    private static (BigInteger Major, BigInteger Minor, BigInteger Patch)? ParseVersion(string? value)
    {
        if (value is not { Length: > 0 and <= 128 }) return null;
        var match = StableVersion().Match(value);
        return match.Success ? (BigInteger.Parse(match.Groups[1].Value), BigInteger.Parse(match.Groups[2].Value),
            BigInteger.Parse(match.Groups[3].Value)) : null;
    }

    internal async Task<DesktopUpdate?> CheckAsync(string installed, CancellationToken ct)
    {
        var current = ParseVersion(installed) ?? throw new InvalidDataException("Invalid installed version.");
        using var request = Request(Releases);
        if (etag != null) request.Headers.IfNoneMatch.Add(etag);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotModified) return latest;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var highest = current;
        DesktopUpdate? candidate = null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.Field("draft").ValueKind != JsonValueKind.False ||
                release.Field("prerelease").ValueKind != JsonValueKind.False) continue;
            var tag = release.String("tag_name");
            if (ParseVersion(tag) is not { } version || version.CompareTo(highest) <= 0 ||
                release.Field("assets") is not { ValueKind: JsonValueKind.Array } assets) continue;
            var label = tag!.TrimStart('v');
            var name = $"ALXPRGS-Notify-Desktop-Setup-{label}-win-x64.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                var digest = asset.String("digest");
                var expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
                if (asset.String("name") != name || asset.String("state") != "uploaded" ||
                    asset.Field("size") is not { ValueKind: JsonValueKind.Number } length ||
                    !length.TryGetInt64(out var size) || size is <= 0 or > MaxInstallerBytes ||
                    digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                    !IsHex(digest.AsSpan(7)) ||
                    !Uri.TryCreate(asset.String("browser_download_url"), UriKind.Absolute, out var download) ||
                    download.AbsoluteUri != expected) continue;
                highest = version;
                candidate = new DesktopUpdate(label, release.String("body") ?? "", download, size, digest[7..]);
                break;
            }
        }
        etag = response.Headers.ETag;
        return latest = candidate;
    }

    internal async Task<string> DownloadAsync(DesktopUpdate update, IProgress<int>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(cache);
        var ready = Path.Combine(cache, $"Notify-{update.Sha256.ToLowerInvariant()}.exe");
        if (File.Exists(ready) && await MatchesAsync(ready, update, ct))
        {
            progress?.Report(100);
            return ready;
        }
        var partial = ready + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response = await OpenDownloadAsync(update.Download, ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new InvalidDataException("Installer Content-Length mismatch.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var percent = -1;
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = await stream.ReadAsync(buffer, ct)) != 0)
                {
                    received += count;
                    if (received > update.Size || received > MaxInstallerBytes)
                        throw new InvalidDataException("Installer is larger than declared.");
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                    var next = (int)(received * 100 / update.Size);
                    if (next != percent) { progress?.Report(next); percent = next; }
                }
            }
            if (received != update.Size || !DigestMatches(hash.GetHashAndReset(), update))
                throw new InvalidDataException("Installer size or SHA-256 mismatch.");
            ct.ThrowIfCancellationRequested();
            File.Move(partial, ready, overwrite: true);
            return ready;
        }
        finally { File.Delete(partial); }
    }

    internal static async Task<bool> MatchesAsync(string path, DesktopUpdate update, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return file.Length == update.Size && DigestMatches(await SHA256.HashDataAsync(file, ct), update);
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri address, CancellationToken ct)
    {
        for (var redirects = 0; redirects <= 3; redirects++)
        {
            if (address.Scheme != "https" || !address.IsDefaultPort || address.UserInfo.Length != 0 ||
                address.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Unexpected installer download host.");
            using var request = Request(address);
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            using (response)
            {
                var next = response.Headers.Location ?? throw new InvalidDataException("Missing installer redirect.");
                address = new Uri(address, next);
            }
        }
        throw new InvalidDataException("Too many installer redirects.");
    }

    private static bool DigestMatches(byte[] hash, DesktopUpdate update) =>
        CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(update.Sha256));

    private static HttpRequestMessage Request(Uri address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("ALXPRGS-Notify-Desktop-Updater");
        if (address.Host == Releases.Host)
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }
    private static bool IsHex(ReadOnlySpan<char> input)
    {
        foreach (var digit in input)
            if (!char.IsAsciiHexDigit(digit)) return false;
        return true;
    }
}
