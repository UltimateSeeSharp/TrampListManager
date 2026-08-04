using System.Text.RegularExpressions;

namespace TrampListManager.Services;

/// <summary>
/// Extracts a design slug from whatever a user pastes.
///
/// A friend sharing a design might send any of these, and all of them should work
/// without the recipient having to know which part matters:
///
///   test-8bed65
///   https://www.tramplist.pro/d/test-8bed65
///   tramplist://install/test-8bed65
///   https://tramplist.pro/d/test-8bed65/download?x=1
/// </summary>
public static partial class SlugParser
{
    /// <summary>
    /// Slugs are lowercase words plus a short random suffix, e.g. "iron-hauler-mk3-a4f2".
    /// Anchored and length-capped because the result is interpolated into a request URL.
    /// </summary>
    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    private const int MaxSlugLength = 120;

    /// <summary>Returns the slug, or null if nothing usable was found.</summary>
    public static string? Extract(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.Trim();

        // Strip a URL down to its slug segment, whichever form it takes.
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;

            var segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            text = uri.Scheme.Equals("tramplist", StringComparison.OrdinalIgnoreCase)
                // tramplist://install/<slug> — host is the action, path is the slug.
                ? (uri.Host.Equals("install", StringComparison.OrdinalIgnoreCase)
                    ? segments.FirstOrDefault() ?? ""
                    : "")
                // https://…/d/<slug>[/download] — take the segment after "d".
                : SegmentAfterD(segments);
        }

        text = text.Trim('/').ToLowerInvariant();

        return text.Length is > 0 and <= MaxSlugLength && SlugPattern().IsMatch(text)
            ? text
            : null;
    }

    private static string SegmentAfterD(string[] segments)
    {
        var i = Array.FindIndex(segments, s => s.Equals("d", StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < segments.Length ? segments[i + 1] : "";
    }
}
