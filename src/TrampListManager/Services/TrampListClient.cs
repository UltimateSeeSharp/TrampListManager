using System.IO;
using System.Net.Http;
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
    public const string SiteUrl = "https://www.tramplist.pro";

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
