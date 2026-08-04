using System.IO;
using System.Security.Cryptography;

namespace TrampListManager.Services;

/// <summary>
/// Reads and writes SAND's Trampler folder.
///
/// The game stores each design as a gzip-compressed <c>.wbt</c> file named after a UUID.
/// The UUID exists only as the filename — it appears nowhere inside the file — which is
/// what makes file-copy sharing work at all, and lets a design keep one identity
/// everywhere: TrampList serves downloads under the design's own UUID.
/// </summary>
public sealed class WalkerFolder
{
    /// <summary>Real files are 200–280 KB; anything far outside that is not a blueprint.</summary>
    private const long MinPlausibleSize = 1024;
    private const long MaxPlausibleSize = 2 * 1024 * 1024;

    /// <summary>
    /// %USERPROFILE%\AppData\LocalLow\Hologryph\Sand\Data\Walkers
    ///
    /// LocalLow has no <see cref="Environment.SpecialFolder"/> value, so it is built from
    /// LocalApplicationData rather than hardcoding the user's name.
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var lowRoot = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
            return Path.Combine(lowRoot, "Hologryph", "Sand", "Data", "Walkers");
        }
    }

    public string Path_ { get; }

    public WalkerFolder(string? path = null) => Path_ = path ?? DefaultPath;

    public bool Exists => Directory.Exists(Path_);

    /// <summary>Every Trampler currently installed, newest first.</summary>
    public IReadOnlyList<WalkerFile> List()
    {
        if (!Exists) return [];

        return Directory
            .EnumerateFiles(Path_, "*.wbt", SearchOption.TopDirectoryOnly)
            .Select(WalkerFile.FromPath)
            .OfType<WalkerFile>()
            .OrderByDescending(w => w.Modified)
            .ToList();
    }

    /// <summary>
    /// Installs a downloaded blueprint into the Walkers folder.
    ///
    /// Downloads from TrampList arrive named after the design's own UUID, which is both
    /// what the game expects and a global identity — the same design is the same file for
    /// everyone. That name is kept when the source has one, so re-downloading an updated
    /// design replaces the old copy instead of leaving two.
    ///
    /// <paramref name="onDuplicate"/> decides what happens when that file already exists.
    /// Returning null cancels; the caller asks the user rather than guessing, since both
    /// replacing and keeping a second copy are things people legitimately want.
    /// </summary>
    public InstallResult Install(string sourceFile, Func<Guid, DuplicateChoice>? onDuplicate = null)
    {
        if (!File.Exists(sourceFile))
            return InstallResult.Fail("That file no longer exists.");

        var info = new FileInfo(sourceFile);
        if (info.Length is < MinPlausibleSize or > MaxPlausibleSize)
            return InstallResult.Fail(
                $"That file is {FormatSize(info.Length)}. Trampler blueprints are usually 200–280 KB.");

        if (!IsGzip(sourceFile))
            return InstallResult.Fail(
                "That doesn't look like a Trampler blueprint. Expected a gzip-compressed .wbt file.");

        try
        {
            Directory.CreateDirectory(Path_);

            // Keep the source's UUID when it has one; otherwise mint a fresh one.
            var stem = System.IO.Path.GetFileNameWithoutExtension(sourceFile);
            var id = Guid.TryParse(stem, out var parsed) ? parsed : Guid.NewGuid();
            var destination = System.IO.Path.Combine(Path_, $"{id}.wbt");

            if (File.Exists(destination))
            {
                switch (onDuplicate?.Invoke(id) ?? DuplicateChoice.Cancel)
                {
                    case DuplicateChoice.Cancel:
                        return InstallResult.Fail("Already installed.");

                    case DuplicateChoice.AddCopy:
                        // A second copy needs its own identity, since the filename is it.
                        id = Guid.NewGuid();
                        destination = System.IO.Path.Combine(Path_, $"{id}.wbt");
                        break;

                    case DuplicateChoice.Replace:
                        break;
                }
            }

            File.Copy(sourceFile, destination, overwrite: true);

            return InstallResult.Ok(id, destination);
        }
        catch (UnauthorizedAccessException)
        {
            return InstallResult.Fail("Permission denied writing to the Walkers folder.");
        }
        catch (IOException ex)
        {
            return InstallResult.Fail($"Could not install the file: {ex.Message}");
        }
    }

    /// <summary>
    /// Copies the whole folder to a timestamped sibling.
    ///
    /// Offered before the first install because the alternative — a mistake in the folder
    /// holding every design the player has built — is not recoverable.
    /// </summary>
    public string Backup()
    {
        var target = $"{Path_}_backup_{DateTime.Now:yyyyMMdd_HHmmss}";
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(Path_, "*.wbt"))
            File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));

        return target;
    }

    /// <summary>gzip magic: 1f 8b 08. The game writes gzip via Easy Save 3.</summary>
    private static bool IsGzip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[3];
            return stream.Read(header) == 3 && header[0] == 0x1f && header[1] == 0x8b && header[2] == 0x08;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB"
    };
}

/// <summary>One installed Trampler.</summary>
public sealed record WalkerFile(string Path, Guid Id, long Size, DateTime Modified)
{
    public string ShortId => Id.ToString()[..8];
    public string SizeText => WalkerFolder.FormatSize(Size);
    public string ModifiedText => Modified.ToString("d MMM yyyy");
    public string FileName => System.IO.Path.GetFileName(Path);

    public static WalkerFile? FromPath(string path)
    {
        // Files not named after a UUID are not the game's own, so they are skipped rather
        // than shown — listing them would imply the app can do something with them.
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (!Guid.TryParse(stem, out var id)) return null;

        try
        {
            var info = new FileInfo(path);
            return new WalkerFile(path, id, info.Length, info.LastWriteTime);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-256 of the file, matching what TrampList stores. Lets a user tell whether a
    /// local design is the same as one already published.
    /// </summary>
    public string ComputeSha256()
    {
        using var stream = File.OpenRead(Path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>What to do when a design is already installed.</summary>
public enum DuplicateChoice
{
    Cancel,
    /// <summary>Overwrite the existing file, keeping one copy of the design.</summary>
    Replace,
    /// <summary>Install alongside it under a fresh UUID.</summary>
    AddCopy
}

public sealed record InstallResult(bool Success, Guid Id, string? Destination, string? Error)
{
    public static InstallResult Ok(Guid id, string destination) => new(true, id, destination, null);
    public static InstallResult Fail(string error) => new(false, Guid.Empty, null, error);
}
