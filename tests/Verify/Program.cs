using System.IO;
using TrampListManager.Services;

// Verifies the app against a real SAND install and the live site.
var failures = 0;
void Check(string label, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {label}{(detail is null ? "" : $" -> {detail}")}");
    if (!ok) failures++;
}

Console.WriteLine("=== SlugParser ===");
foreach (var (input, expected) in new (string, string?)[]
{
    ("test-8bed65", "test-8bed65"),
    ("https://www.tramplist.pro/d/test-8bed65", "test-8bed65"),
    ("tramplist://install/test-8bed65", "test-8bed65"),
    ("../../etc/passwd", null), ("<script>", null), ("", null),
})
    Check($"\"{input}\"", SlugParser.Extract(input) == expected);

Console.WriteLine("\n=== LabelStore ===");
var tmp = Path.Combine(Path.GetTempPath(), $"labels_{Guid.NewGuid():N}.json");
var store = new LabelStore(tmp);
var id = Guid.NewGuid();
Check("empty by default", store.Get(id) is null);
store.Set(id, "Iron Hauler", "cargo runs");
Check("stores a label", store.Get(id)?.Name == "Iron Hauler");
Check("stores notes", store.Get(id)?.Notes == "cargo runs");
Check("persists across instances", new LabelStore(tmp).Get(id)?.Name == "Iron Hauler");
store.Set(id, "");
Check("blank name clears it", store.Get(id) is null);
store.Set(id, "Temp");
store.Prune(new[] { Guid.NewGuid() });
Check("prunes vanished files", store.Get(id) is null);
File.Delete(tmp);

Console.WriteLine("\n=== Live metadata resolution ===");
var folder = new WalkerFolder();
if (!folder.Exists) { Console.WriteLine("skip: no Walkers folder"); }
else
{
    var walkers = folder.List();
    Check("lists Tramplers", walkers.Count > 0, $"{walkers.Count}");
    var hashes = walkers.ToDictionary(w => w.Path, w => w.ComputeSha256());
    var client = new TrampListClient();
    var resolved = await client.ResolveAsync(hashes.Values);
    Console.WriteLine($"     resolved {resolved.Count} of {walkers.Count} against tramplist.pro");
    foreach (var (h, d) in resolved)
        Console.WriteLine($"       {d.Name}  |  {d.Summary}  |  {d.Url}");

    var rows = walkers.Select(w => new WalkerRow(w, resolved.GetValueOrDefault(hashes[w.Path]), null)).ToList();
    Check("published rows show a real name",
        rows.Where(r => r.IsPublished).All(r => !r.Title.StartsWith("Untitled")));
    Check("unpublished rows fall back cleanly",
        rows.Where(r => !r.IsPublished).All(r => r.Title.StartsWith("Untitled")));
    Check("offline resolve is harmless", (await client.ResolveAsync(new[] { new string('a', 64) })).Count == 0);
}

Console.WriteLine(failures == 0 ? "\nall checks passed" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;
