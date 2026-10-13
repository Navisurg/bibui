// An assembly's public API as text, compared with the checked-in copy.
//
// Used by tests/public-api (the bibui packages), and compiled into any other
// layer's tests that check their own packages the same way. PublicApiGenerator reads the
// assembly's metadata and prints every public and protected member as C#
// declarations, sorted, so the file only moves when the API does.

using System.Reflection;
using PublicApiGenerator;

namespace Bibui.PublicApi;

internal static class PublicApiSnapshot
{
    private static readonly ApiGeneratorOptions Options = new()
    {
        // TargetFramework, InternalsVisibleTo and the like are not API.
        IncludeAssemblyAttributes = false,
    };

    /// <summary>
    /// The directory <paramref name="relative"/> names in the repository, found
    /// by walking up from the build output (bin/&lt;config&gt;/net10.0) and from the
    /// working directory, so the check runs the same wherever it is started.
    /// </summary>
    public static string RepoDir(string relative)
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        throw new DirectoryNotFoundException($"{relative} is not above {AppContext.BaseDirectory} or {Environment.CurrentDirectory}");
    }

    /// <summary>The public API of <paramref name="assembly"/>, as the snapshot file holds it.</summary>
    public static string Generate(Assembly assembly) =>
        assembly.GeneratePublicApi(Options).ReplaceLineEndings("\n").TrimEnd('\n') + "\n";

    /// <summary>
    /// Compares <paramref name="assembly"/> with <paramref name="snapshotPath"/>, or
    /// rewrites the file when <paramref name="update"/> is set. Returns false on a
    /// difference, after printing the lines that were added and removed.
    /// </summary>
    public static bool Check(Assembly assembly, string snapshotPath, bool update)
    {
        string name = assembly.GetName().Name!;
        string actual = Generate(assembly);
        string? expected = File.Exists(snapshotPath) ? File.ReadAllText(snapshotPath).ReplaceLineEndings("\n") : null;

        if (expected == actual)
        {
            Console.WriteLine($"[PASS] {name}");
            return true;
        }

        if (update)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, actual);
            Console.WriteLine($"[UPDATED] {name} -> {snapshotPath}");
            return true;
        }

        Console.WriteLine($"[FAIL] {name}: the public API differs from {snapshotPath}");
        if (expected is null)
        {
            Console.WriteLine("  (no snapshot checked in)");
        }
        else
        {
            foreach (string line in Diff(expected, actual))
                Console.WriteLine("  " + line);
        }
        return false;
    }

    /// <summary>
    /// A line diff by longest common subsequence: "-" for a line only the snapshot
    /// has, "+" for one only the assembly has, each with its line number.
    /// </summary>
    private static IEnumerable<string> Diff(string expected, string actual)
    {
        string[] a = expected.Split('\n'), b = actual.Split('\n');
        // lcs[i, j]: the common subsequence length of a[i..] and b[j..].
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { x++; y++; }
            else if (y < b.Length && (x == a.Length || lcs[x, y + 1] >= lcs[x + 1, y])) { yield return $"+{y + 1,5}: {b[y]}"; y++; }
            else { yield return $"-{x + 1,5}: {a[x]}"; x++; }
        }
    }
}
