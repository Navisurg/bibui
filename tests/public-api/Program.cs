// public-api: every bibui package's public API against its snapshot.
//
//   dotnet run --project tests/public-api -c Release              # check; fails on any difference
//   dotnet run --project tests/public-api -c Release -- --update  # rewrite api/*.txt after a deliberate change
//
// The snapshots are api/<assembly>.txt beside this file. A change to a package's
// public surface fails CI until its snapshot is updated and checked in, so every
// API change shows up in review as a diff of those files.

using System.Reflection;
using Bibui.PublicApi;

bool update = args.Contains("--update");

// Every assembly nuget-publish.sh packs, by assembly name. A layer that lists
// its packages in its own nuget-projects file checks their API itself.
string[] assemblies =
[
    "bibui",
    "bibui-testing",
    "Bibui.Raster.Skia",
    "Bibui.Platform.Silk",
    "Bibui.Platform.Wayland",
    "Bibui.Raster.Managed",
    "Bibui.Raster.Managed.Backend",
    "Bibui.Text.FreeType",
];

string repo = Path.GetFullPath(Path.Combine(PublicApiSnapshot.RepoDir("tests/public-api"), "..", ".."));
string apiDir = Path.Combine(repo, "tests", "public-api", "api");
int failed = 0;
foreach (string name in assemblies)
{
    if (!PublicApiSnapshot.Check(Assembly.Load(name), Path.Combine(apiDir, name + ".txt"), update))
        failed++;
}

// A package nuget-publish.sh packs but this list leaves out would ship an
// unchecked API. (Layers with their own nuget-projects file check themselves.)
foreach (System.Text.RegularExpressions.Match m in
         System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(repo, "nuget-publish.sh")), "\"([^\"$]+\\.csproj)\""))
{
    string csproj = File.ReadAllText(Path.Combine(repo, m.Groups[1].Value));
    var assemblyName = System.Text.RegularExpressions.Regex.Match(csproj, "<AssemblyName>([^<]+)</AssemblyName>");
    string packed = assemblyName.Success ? assemblyName.Groups[1].Value : Path.GetFileNameWithoutExtension(m.Groups[1].Value);
    if (!assemblies.Contains(packed))
    {
        Console.WriteLine($"[FAIL] nuget-publish.sh packs {m.Groups[1].Value} ({packed}), which has no snapshot here; list it above");
        failed++;
    }
}

// A snapshot whose assembly is no longer listed would go stale unnoticed.
foreach (string file in Directory.EnumerateFiles(apiDir, "*.txt"))
{
    if (!assemblies.Contains(Path.GetFileNameWithoutExtension(file)))
    {
        Console.WriteLine($"[FAIL] {file} belongs to no listed assembly; delete it or list the assembly");
        failed++;
    }
}

if (failed > 0)
{
    Console.WriteLine();
    Console.WriteLine($"{failed} public API snapshot(s) differ. If the change is deliberate, run");
    Console.WriteLine("  dotnet run --project tests/public-api -c Release -- --update");
    Console.WriteLine("and commit tests/public-api/api/ with it.");
    return 1;
}
Console.WriteLine($"{assemblies.Length} public API snapshots match.");
return 0;
