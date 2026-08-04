using System.IO;

namespace TrampListManager.Services;

/// <summary>
/// What the app was asked to do when it started.
///
/// Windows passes either a file path (from the .wbt association) or a URL (from the
/// tramplist:// protocol), so both arrive as argv[0] and have to be told apart.
/// </summary>
public abstract record LaunchRequest
{
    /// <summary>Opened normally, with no file or link.</summary>
    public sealed record Browse : LaunchRequest;

    /// <summary>A .wbt was double-clicked or dropped on the app.</summary>
    public sealed record InstallFile(string Path) : LaunchRequest;

    /// <summary>tramplist://install/&lt;slug&gt; — fetch that design from the site.</summary>
    public sealed record InstallSlug(string Slug) : LaunchRequest;

    /// <summary>Something was passed that we could not make sense of.</summary>
    public sealed record Invalid(string Reason) : LaunchRequest;

    public static LaunchRequest Parse(string[] args)
    {
        if (args.Length == 0) return new Browse();

        var arg = args[0].Trim();

        if (arg.StartsWith("tramplist://", StringComparison.OrdinalIgnoreCase))
            return ParseProtocol(arg);

        if (arg.EndsWith(".wbt", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(arg)
                ? new InstallFile(arg)
                : new Invalid($"File not found: {arg}");
        }

        return new Invalid($"Don't know how to open: {arg}");
    }

    private static LaunchRequest ParseProtocol(string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return new Invalid("That link is malformed.");

        // tramplist://install/<slug> — host is "install", first path segment is the slug.
        if (!uri.Host.Equals("install", StringComparison.OrdinalIgnoreCase))
            return new Invalid($"Unknown action: {uri.Host}");

        var slug = uri.AbsolutePath.Trim('/');

        // The slug goes into a URL we then fetch, so it is constrained to the shape the
        // site actually generates rather than passed through.
        if (slug.Length is 0 or > 120 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return new Invalid("That link doesn't contain a valid design.");

        return new InstallSlug(slug);
    }
}
