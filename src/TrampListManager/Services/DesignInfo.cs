namespace TrampListManager.Services;

/// <summary>
/// What TrampList knows about a design, resolved by the file's SHA-256.
///
/// None of this can be read out of the .wbt itself: the format is an undecoded binary
/// blob, and the in-game name is not even stored in the file — it is two indices into
/// the game's own name tables, held on Hologryph's master server. So the site's own
/// database is the only place this information exists in readable form.
/// </summary>
public sealed record DesignInfo(
    string Slug,
    string Name,
    string ChassisName,
    string ChassisTier,
    int? CrewSize,
    string? Role,
    int DownloadCount,
    string GameVersion)
{
    public string Url => $"{TrampListClient.SiteUrl}/d/{Slug}";

    /// <summary>Crew and role are optional at upload, so the summary adapts to what exists.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { ChassisName };
            if (CrewSize is int c) parts.Add(c == 1 ? "solo" : $"{c} crew");
            if (!string.IsNullOrEmpty(Role)) parts.Add(Role);
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// A local label for a design that is not on TrampList.
///
/// Self-built Tramplers have nothing to resolve against, so the user names them once
/// and the app remembers it. Keyed by the file's UUID, which is stable for as long as
/// the file exists.
/// </summary>
public sealed record LocalLabel(Guid Id, string Name, string? Notes);
