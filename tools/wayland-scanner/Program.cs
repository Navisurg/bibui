// wayland-scanner: C# bindings for the Wayland protocols bibui's host speaks.
//
//   dotnet run --project tools/wayland-scanner -- generate   # write bibui-platform-wayland/Protocol/Generated
//   dotnet run --project tools/wayland-scanner -- check      # regenerate in memory, exit 1 on any diff
//   dotnet run --project tools/wayland-scanner -- dump       # the tables as check_tables.py prints them, from the XML
//
// Inputs are the installed XML (Scanner.DefaultSources): /usr/share/wayland
// and /usr/share/wayland-protocols. The output is checked in.

using Bibui.WaylandScanner;

string command = args.Length > 0 ? args[0] : "generate";
string repo = FindRepoRoot();
string outDir = Path.Combine(repo, "bibui-platform-wayland", "Protocol", "Generated");

switch (command)
{
    case "generate":
    {
        var files = Scanner.Generate(Scanner.DefaultSources);
        Directory.CreateDirectory(outDir);
        foreach (var stale in Directory.GetFiles(outDir, "*.g.cs").Where(f => !files.ContainsKey(Path.GetFileName(f))))
        {
            File.Delete(stale);
            Console.WriteLine($"removed {Path.GetRelativePath(repo, stale)}");
        }
        foreach (var (name, text) in files)
        {
            File.WriteAllText(Path.Combine(outDir, name), text);
            Console.WriteLine($"wrote {Path.GetRelativePath(repo, Path.Combine(outDir, name))} ({text.Count(c => c == '\n')} lines)");
        }
        return 0;
    }
    case "check":
    {
        var diffs = Diff(Scanner.Generate(Scanner.DefaultSources), outDir);
        foreach (var d in diffs) Console.WriteLine(d);
        Console.WriteLine(diffs.Count == 0 ? "ok: generated bindings are up to date" : $"FAILED: {diffs.Count} file(s) differ; run `generate`");
        return diffs.Count == 0 ? 0 : 1;
    }
    case "dump":
        foreach (var source in Scanner.DefaultSources)
            foreach (var i in Scanner.Parse(source).Interfaces)
                foreach (var line in Scanner.ExpectedDump(i))
                    Console.WriteLine(line);
        return 0;
    default:
        Console.Error.WriteLine("usage: wayland-scanner [generate|check|dump]");
        return 2;
}

static List<string> Diff(IReadOnlyDictionary<string, string> files, string dir)
{
    var diffs = new List<string>();
    foreach (var (name, text) in files)
    {
        var path = Path.Combine(dir, name);
        if (!File.Exists(path)) diffs.Add($"missing: {name}");
        else if (File.ReadAllText(path) != text) diffs.Add($"differs: {name}");
    }
    if (Directory.Exists(dir))
        foreach (var f in Directory.GetFiles(dir, "*.g.cs"))
            if (!files.ContainsKey(Path.GetFileName(f))) diffs.Add($"stale: {Path.GetFileName(f)}");
    return diffs;
}

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) return d.FullName;
    throw new InvalidOperationException("run from inside the bibui repository");
}
