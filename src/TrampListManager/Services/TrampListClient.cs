using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrampListManager.Services;

/// <summary>
/// Fetches blueprints from tramplist.pro for the <c>tramplist://install/&lt;slug&gt;</c> path.
///
/// Read-only and unauthenticated: downloads are public, so the app never handles a
/// session or a credential.
/// </summary>
public sealed class TrampListClient : IDisposable
{
    /// <summary>
    /// The live site, unless TRAMPLIST_SITE overrides it.
    ///
    /// The override exists to test against a dev server before a change is deployed —
    /// otherwise the app always talks to production, and a feature that only exists
    /// locally looks broken in a way that is genuinely hard to diagnose.
    /// </summary>
    public static readonly string SiteUrl =
        Environment.GetEnvironmentVariable("TRAMPLIST_SITE")?.TrimEnd('/')
        ?? "https://www.tramplist.pro";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Matches the site's own ceiling, so a hostile response cannot fill the disk.</summary>
    private const long MaxDownloadBytes = 2 * 1024 * 1024;

    static TrampListClient()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("TrampListManager/1.0");
    }

    public static string DesignUrl(string slug) => $"{SiteUrl}/d/{slug}";
    public static string UploadUrl => $"{SiteUrl}/upload";

    /// <summary>Matches the site's own blueprint ceiling; refuse locally rather than round-trip.</summary>
    private const long MaxUploadBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Hands a local .wbt to the site and returns the upload URL to open.
    ///
    /// The app has no account, so it cannot publish a design itself. It parks the file
    /// and receives a short-lived token; the browser — where the user is already signed
    /// in with Steam — claims it and does the actual publishing. That keeps the app free
    /// of credentials, which is most of why it is safe to run.
    /// </summary>
    public async Task<StageResult> StageAsync(string path, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct);
        }
        catch (Exception ex)
        {
            return StageResult.Fail($"Could not read the blueprint: {ex.Message}");
        }

        if (bytes.Length == 0) return StageResult.Fail("That blueprint file is empty.");
        if (bytes.Length > MaxUploadBytes)
            return StageResult.Fail("That file is too large to be a Trampler blueprint.");

        try
        {
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var res = await Http.PostAsync($"{SiteUrl}/upload/stage", content, ct);

            // The site reports an already-published design rather than rejecting it, so
            // the user is told before filling in a form that would be refused at the end.
            if (res.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                var dupe = await res.Content.ReadFromJsonAsync<StageDto>(cancellationToken: ct);
                return dupe?.Slug is { Length: > 0 } slug
                    ? StageResult.Duplicate(slug)
                    : StageResult.Fail("That design is already on TrampList.");
            }

            if (!res.IsSuccessStatusCode)
            {
                return StageResult.Fail(res.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "TrampList is rate-limiting uploads from this connection. Try again shortly.",
                    System.Net.HttpStatusCode.RequestEntityTooLarge =>
                        "That file is too large to be a Trampler blueprint.",
                    System.Net.HttpStatusCode.BadRequest =>
                        "TrampList did not recognise that file as a Trampler blueprint.",
                    // The site predates direct upload, or the deploy has not landed yet.
                    // Worth naming: otherwise this reads as a mysterious failure on a
                    // build that is simply newer than the site.
                    System.Net.HttpStatusCode.NotFound =>
                        "This version of TrampList doesn't support direct upload yet.",
                    _ => $"TrampList returned {(int)res.StatusCode}."
                });
            }

            var payload = await res.Content.ReadFromJsonAsync<StageDto>(cancellationToken: ct);
            if (payload?.Token is not { Length: > 0 } token)
                return StageResult.Fail("TrampList returned an unexpected response.");

            return StageResult.Ok($"{UploadUrl}?staged={Uri.EscapeDataString(token)}");
        }
        catch (TaskCanceledException)
        {
            return StageResult.Fail("The upload timed out.");
        }
        catch (HttpRequestException ex)
        {
            return StageResult.Fail($"Could not reach TrampList: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads a design to a temporary file, which the caller then installs.
    ///
    /// Downloading to temp rather than straight into the Walkers folder means a failed or
    /// truncated transfer never leaves a half-written blueprint where the game will read it.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(string slug, CancellationToken ct = default)
    {
        var url = $"{SiteUrl}/d/{Uri.EscapeDataString(slug)}/download";

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return DownloadResult.Fail(response.StatusCode switch
                {
                    System.Net.HttpStatusCode.NotFound => "That design no longer exists on TrampList.",
                    _ => $"TrampList returned {(int)response.StatusCode}."
                });
            }

            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                return DownloadResult.Fail("That file is unexpectedly large and was not downloaded.");

            var temp = Path.Combine(Path.GetTempPath(), $"tramplist_{Guid.NewGuid():N}.wbt");

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = File.Create(temp))
            {
                // Copy with a cap rather than trusting Content-Length, which is a claim.
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        destination.Close();
                        File.Delete(temp);
                        return DownloadResult.Fail("That file is unexpectedly large and was not downloaded.");
                    }
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            return DownloadResult.Ok(temp);
        }
        catch (TaskCanceledException)
        {
            return DownloadResult.Fail("The download timed out.");
        }
        catch (HttpRequestException ex)
        {
            return DownloadResult.Fail($"Could not reach TrampList: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks TrampList which of these blueprint hashes are published designs.
    ///
    /// Hash rather than filename, because the filename UUID is regenerated on install
    /// and carries no information — the bytes are the only stable identity a local file
    /// has. Returns an empty map on any failure: labels are a convenience, and the app
    /// must stay usable offline.
    /// </summary>
    public async Task<Dictionary<string, DesignInfo>> ResolveAsync(
        IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var list = hashes.Distinct().Take(200).ToList();
        if (list.Count == 0) return new();

        try
        {
            var body = new StringContent(
                JsonSerializer.Serialize(new { hashes = list }),
                System.Text.Encoding.UTF8, "application/json");

            using var res = await Http.PostAsync($"{SiteUrl}/api/designs/by-hash", body, ct);
            if (!res.IsSuccessStatusCode) return new();

            var payload = await res.Content.ReadFromJsonAsync<HashLookupResponse>(cancellationToken: ct);
            if (payload?.Matches is null) return new();

            return payload.Matches.ToDictionary(
                kv => kv.Key,
                kv => new DesignInfo(
                    kv.Value.Slug, kv.Value.Name, kv.Value.ChassisName, kv.Value.ChassisTier,
                    kv.Value.CrewSize, kv.Value.Role, kv.Value.DownloadCount, kv.Value.GameVersion));
        }
        catch
        {
            return new();
        }
    }

    public void Dispose() { }

    private sealed class HashLookupResponse
    {
        [JsonPropertyName("matches")]
        public Dictionary<string, MatchDto>? Matches { get; set; }
    }

    private sealed class StageDto
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
    }

    private sealed class MatchDto
    {
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("chassisName")] public string ChassisName { get; set; } = "";
        [JsonPropertyName("chassisTier")] public string ChassisTier { get; set; } = "";
        [JsonPropertyName("crewSize")] public int? CrewSize { get; set; }
        [JsonPropertyName("role")] public string? Role { get; set; }
        [JsonPropertyName("downloadCount")] public int DownloadCount { get; set; }
        [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
    }
}

public sealed record DownloadResult(bool Success, string? Path, string? Error)
{
    public static DownloadResult Ok(string path) => new(true, path, null);
    public static DownloadResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Outcome of handing a blueprint to the site. "Already published" is a distinct case
/// rather than a failure: the design exists, and the useful response is to show it.
/// </summary>
public sealed record StageResult(bool Success, string? UploadUrl, string? ExistingSlug, string? Error)
{
    public static StageResult Ok(string uploadUrl) => new(true, uploadUrl, null, null);
    public static StageResult Duplicate(string slug) => new(false, null, slug, null);
    public static StageResult Fail(string error) => new(false, null, null, error);
}
