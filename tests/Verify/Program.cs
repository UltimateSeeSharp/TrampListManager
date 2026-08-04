using System.IO;
using TrampListManager.Services;

// Verifies the parts of the app that touch real data, against a real SAND install.
// Run: dotnet run --project tests/Verify

var failures = 0;

void Check(string label, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {label}{(detail is null ? "" : $" -> {detail}")}");
    if (!ok) failures++;
}

Console.WriteLine("=== SlugParser ===");

// Every form a friend might realistically send.
var slugCases = new (string Input, string? Expected)[]
{
    ("test-8bed65", "test-8bed65"),
    ("  test-8bed65  ", "test-8bed65"),
    ("TEST-8BED65", "test-8bed65"),
    ("https://www.tramplist.pro/d/test-8bed65", "test-8bed65"),
    ("https://tramplist.pro/d/test-8bed65", "test-8bed65"),
    ("https://www.tramplist.pro/d/test-8bed65/download", "test-8bed65"),
    ("tramplist://install/test-8bed65", "test-8bed65"),
    ("iron-hauler-mk3-a4f2", "iron-hauler-mk3-a4f2"),
    // Rejections: empty, wrong path, traversal, injection, overlong.
    ("", null),
    ("   ", null),
    ("https://www.tramplist.pro/upload", null),
    ("tramplist://something/else", null),
    ("../../etc/passwd", null),
    ("test 8bed65", null),
    ("test/8bed65", null),
    ("<script>", null),
    (new string('a', 200), null)
};

foreach (var (input, expected) in slugCases)
{
    var actual = SlugParser.Extract(input);
    var display = input.Length > 40 ? input[..37] + "..." : input;
    Check($"\"{display}\"", actual == expected, actual ?? "(null)");
}

Console.WriteLine("\n=== WalkerFolder ===");

var folder = new WalkerFolder();
Console.WriteLine($"path: {folder.Path_}");
Check("folder exists", folder.Exists);

if (folder.Exists)
{
    var walkers = folder.List();
    Check("lists Tramplers", walkers.Count > 0, $"{walkers.Count} found");

    if (walkers.Count > 0)
    {
        var first = walkers[0];
        Check("parses UUID filename", first.Id != Guid.Empty, first.ShortId);
        Check("reads size", first.Size > 1024, first.SizeText);
        Check("computes sha256", first.ComputeSha256().Length == 64);
        // The game's own files are 200-280 KB; anything wildly off means bad parsing.
        Check("sizes plausible", walkers.All(w => w.Size is > 1024 and < 2 * 1024 * 1024));
        Check("sorted newest first",
            walkers.Zip(walkers.Skip(1)).All(p => p.First.Modified >= p.Second.Modified));
    }

    Console.WriteLine("\n=== Install (into a temp folder, never the real one) ===");

    var temp = Path.Combine(Path.GetTempPath(), $"walkers_test_{Guid.NewGuid():N}");
    var sandbox = new WalkerFolder(temp);
    try
    {
        Directory.CreateDirectory(temp);
        var source = folder.List()[0];
        var result = sandbox.Install(source.Path);
        Check("installs a real .wbt", result.Success, result.Error ?? result.Id.ToString()[..8]);

        if (result.Success)
        {
            var installed = new FileInfo(result.Destination!);
            Check("bytes preserved", installed.Length == source.Size,
                $"{installed.Length} vs {source.Size}");
            Check("renamed to a fresh UUID", result.Id != source.Id);
            Check("appears in listing", sandbox.List().Count == 1);

            // Installing the same design twice must produce two separate designs —
            // the game keys identity off the filename, so this is a supported action.
            var second = sandbox.Install(source.Path);
            Check("installs twice without collision",
                second.Success && second.Id != result.Id, $"{sandbox.List().Count} files");
        }

        var notGzip = Path.Combine(temp, "notgzip.tmp");
        File.WriteAllBytes(notGzip, new byte[4096]);
        Check("rejects non-gzip", !sandbox.Install(notGzip).Success);

        var tiny = Path.Combine(temp, "tiny.tmp");
        File.WriteAllBytes(tiny, new byte[10]);
        Check("rejects tiny file", !sandbox.Install(tiny).Success);

        Check("rejects missing file", !sandbox.Install(Path.Combine(temp, "nope.wbt")).Success);
    }
    finally
    {
        if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
    }
}

Console.WriteLine("\n=== LaunchRequest ===");
Check("no args -> Browse", LaunchRequest.Parse([]) is LaunchRequest.Browse);
Check("protocol -> InstallSlug",
    LaunchRequest.Parse(["tramplist://install/test-8bed65"]) is LaunchRequest.InstallSlug { Slug: "test-8bed65" });
Check("missing file -> Invalid",
    LaunchRequest.Parse([@"C:\nope\missing.wbt"]) is LaunchRequest.Invalid);
Check("junk -> Invalid", LaunchRequest.Parse(["whatever"]) is LaunchRequest.Invalid);
Check("bad protocol action -> Invalid",
    LaunchRequest.Parse(["tramplist://evil/x"]) is LaunchRequest.Invalid);

Console.WriteLine(failures == 0 ? "\nall checks passed" : $"\n{failures} check(s) FAILED");
return failures == 0 ? 0 : 1;
