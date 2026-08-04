using System.IO;
using System.Windows;
using System.Windows.Media;
using TrampListManager.Services;

// Verifies the app against a real SAND install and the live site.
// Run: dotnet run --project tests/Verify

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
    ("https://www.tramplist.pro/d/test-8bed65/download", "test-8bed65"),
    ("tramplist://install/test-8bed65", "test-8bed65"),
    ("../../etc/passwd", null),
    ("<script>", null),
    ("", null),
})
    Check($"\"{input}\"", SlugParser.Extract(input) == expected);

Console.WriteLine();
Console.WriteLine("=== LabelStore ===");
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

Console.WriteLine();
Console.WriteLine("=== Branding (palette and type come from the site) ===");
// Theme.xaml is a standalone dictionary precisely so it can be loaded here: constructing
// App would create a second Application in this AppDomain, which WPF forbids.
var app = new Application();
app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
    new Uri("/TrampListManager;component/Theme.xaml", UriKind.Relative)));

foreach (var key in new[] { "Primary", "Background", "Card", "Border", "Foreground", "Muted", "Accent" })
    Check($"brush {key}", app.Resources[key] is SolidColorBrush);

var primary = ((SolidColorBrush)app.Resources["Primary"]).Color.ToString();
Check("Primary matches the site rust", primary == "#FFDE6129", primary);

// A wrong pack URI does not throw - WPF silently falls back - so confirm the bundled
// families actually resolve rather than trusting that the resource merely exists.
foreach (var (key, want) in new[] { ("DisplayFont", "Oswald"), ("BodyFont", "Inter") })
{
    var ff = app.Resources[key] as FontFamily;
    var names = ff is null ? "" : string.Join(",", ff.FamilyNames.Values);
    Check($"{want} embedded and resolvable",
        names.Contains(want, StringComparison.OrdinalIgnoreCase),
        names.Length > 0 ? names : "(unresolved)");
}

foreach (var key in new[] { "PrimaryButton", "GhostButton", "Field", "ListStyle", "SectionLabel" })
    Check($"style {key}", app.Resources[key] is Style);

Console.WriteLine();
Console.WriteLine("=== Live metadata resolution ===");
var folder = new WalkerFolder();
if (!folder.Exists)
{
    Console.WriteLine("skip: no Walkers folder");
}
else
{
    var walkers = folder.List();
    Check("lists Tramplers", walkers.Count > 0, $"{walkers.Count}");

    var hashes = walkers.ToDictionary(w => w.Path, w => w.ComputeSha256());
    var client = new TrampListClient();
    var resolved = await client.ResolveAsync(hashes.Values);
    Console.WriteLine($"     resolved {resolved.Count} of {walkers.Count} against tramplist.pro");
    foreach (var (_, d) in resolved)
        Console.WriteLine($"       {d.Name}  |  {d.Summary}");

    var rows = walkers
        .Select(w => new WalkerRow(w, resolved.GetValueOrDefault(hashes[w.Path]), null))
        .ToList();

    Check("published rows show a real name",
        rows.Where(r => r.IsPublished).All(r => !r.Title.StartsWith("Untitled")));
    Check("unpublished rows fall back cleanly",
        rows.Where(r => !r.IsPublished).All(r => r.Title.StartsWith("Untitled")));
    Check("source colour distinguishes the two",
        rows.All(r => r.SourceBrush is SolidColorBrush));
    Check("unknown hashes resolve to nothing",
        (await client.ResolveAsync(new[] { new string('a', 64) })).Count == 0);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} check(s) FAILED");
return failures == 0 ? 0 : 1;
