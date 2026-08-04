using System.IO;
using System.Text.Json;

namespace TrampListManager.Services;

/// <summary>
/// Local names for Tramplers that aren't published on TrampList.
///
/// Stored in the app's own AppData folder rather than alongside the .wbt files: the
/// Walkers folder is the game's, and writing anything into it that the game did not
/// put there risks confusing the game or being wiped by it.
/// </summary>
public sealed class LabelStore
{
    private readonly string _path;
    private Dictionary<string, LocalLabel> _labels = new();

    public LabelStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TrampListManager", "labels.json");
        Load();
    }

    public LocalLabel? Get(Guid id) =>
        _labels.TryGetValue(id.ToString(), out var l) ? l : null;

    public void Set(Guid id, string name, string? notes = null)
    {
        var key = id.ToString();
        if (string.IsNullOrWhiteSpace(name))
            _labels.Remove(key);
        else
            _labels[key] = new LocalLabel(id, name.Trim(), notes);
        Save();
    }

    /// <summary>
    /// Drops labels whose file no longer exists.
    ///
    /// Without this the file grows forever as designs are dismantled in-game, and a
    /// recycled UUID would inherit a stale name.
    /// </summary>
    public void Prune(IEnumerable<Guid> existing)
    {
        var keep = existing.Select(g => g.ToString()).ToHashSet();
        var removed = _labels.Keys.Where(k => !keep.Contains(k)).ToList();
        if (removed.Count == 0) return;
        foreach (var k in removed) _labels.Remove(k);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            _labels = JsonSerializer.Deserialize<Dictionary<string, LocalLabel>>(json) ?? new();
        }
        catch
        {
            // A corrupt or unreadable label file must not stop the app starting —
            // these are conveniences, not data the user cannot rebuild.
            _labels = new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_labels,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Ignored for the same reason as above.
        }
    }
}
