using System.Text.RegularExpressions;
using Bibui;
using Bibui.Runtime;

/// <summary>
/// Icon names. A name bibui's icon set does not have is drawn as its
/// first letter in a release build, which a baseline blesses as if it were the
/// icon, so every name the repository's own code spells out is checked here,
/// and a debug build throws on one.
/// </summary>
static partial class IconNameTests
{
    public static void Register(Action<string, Action> run)
    {
        run("Icon.IsKnown: a registered name in any case; not an unknown one, an empty one or null", () =>
        {
            Check(Icon.IsKnown("folder"), "folder is registered");
            Check(Icon.IsKnown("Chevron-Down"), "case does not matter");
            Check(!Icon.IsKnown("folder-open"), "folder-open is not");
            Check(!Icon.IsKnown(""), "nor is an empty name");
            Check(!Icon.IsKnown(null), "nor null");
        });

        run("runtime.Icon: an unknown name throws in a debug build and draws a letter in release", () =>
        {
            var runtime = new BibuiRuntime();
            Exception? thrown = null;
            runtime.Mount(
                () =>
                {
                    var row = Arrange.Row().Px(100f, 40f);
                    using (runtime.PushParent(row))
                    {
                        try { runtime.Icon("folder-open", 20f); }
                        catch (ArgumentException e) { thrown = e; }
                    }
                    return row;
                },
                new SurfaceOptions { Width = 100, Height = 40 });
            runtime.Render();
#if DEBUG
            Check(thrown is not null && thrown.Message.Contains("folder-open"), "a debug build names the icon it does not have");
#else
            Check(thrown is null, "a release build draws the fallback and goes on");
#endif
        });

        run("Icon names: the scan finds a name in every form it reads", () =>
        {
            const string source = """
                class Demo
                {
                    static readonly (string Label, string Icon, bool Off)[] Rows =
                        [("Open", "t-tuple", false), ("", "", false)];
                    static Box Helper(BibuiRuntime runtime, string id, string icon) => null!;
                    static void CentredIcon(BibuiRuntime runtime, string name, float size) { }
                    void Build(BibuiRuntime rt)
                    {
                        rt.Icon(open ? "t-icon-a" : "t-icon-b", 16f);
                        Helper(rt, "not-an-icon", "t-helper");
                        CentredIcon(rt, "t-centred", 14f);
                        Menu.Item(rt, "id", Str("Open"), "t-menu");
                        Toggle.Build(rt, "id", false, Str("x"), icon: "t-named");
                        Text("t-u8"u8);
                    }
                }
                """;
            var names = Scan(source).Select(n => n.Name).Order().ToArray();
            string expected = "t-centred t-helper t-icon-a t-icon-b t-menu t-named t-tuple";
            Check(string.Join(' ', names) == expected, $"found [{string.Join(' ', names)}], expected [{expected}]");
        });

        run("Icon names: every icon name the repository's code spells out is registered", () =>
        {
            string root = RepoRoot();
            var unknown = new List<string>();
            int found = 0;
            foreach (string path in ScannedDirs(root))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                {
                    if (IsGenerated(file) || file.EndsWith(nameof(IconNameTests) + ".cs", StringComparison.Ordinal)) continue;
                    string text = File.ReadAllText(file);
                    foreach (var (name, offset) in Scan(text))
                    {
                        found++;
                        if (!Icon.IsKnown(name))
                            unknown.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, offset)} \"{name}\"");
                    }
                }
            }
            Check(found >= 50, $"the scan found only {found} icon names; it has stopped reading something");
            Check(unknown.Count == 0, "icon names bibui does not have: " + string.Join("; ", unknown));
        });
    }

    // Everything in the repository that builds a picture: every top-level
    // directory of this checkout (the components, samples, tests and whatever
    // else it holds), less hidden ones and generated output.
    private static IEnumerable<string> ScannedDirs(string root) =>
        Directory.EnumerateDirectories(root)
            .Where(d => !Path.GetFileName(d).StartsWith('.') && Path.GetFileName(d) != "artifacts")
            .Order(StringComparer.Ordinal);

    private static bool IsGenerated(string file)
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        return file.Contains(sep + "obj" + sep) || file.Contains(sep + "bin" + sep);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
        throw new InvalidOperationException("No Directory.Build.props above " + AppContext.BaseDirectory);
    }

    private static int LineOf(string text, int offset) => 1 + text.AsSpan(0, offset).Count('\n');

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    // ── The scan ────────────────────────────────────────────────────────────
    //
    // Icon names are spelled out in five ways, and each is read:
    //   1. runtime.Icon("name", …): every literal in the first argument;
    //   2. a named argument icon: "name";
    //   3. Menu.Item's and Toggle.Build's icon argument by position;
    //   4. a call of a method declared with a string parameter called icon or
    //      iconName — or name, when the method's own name has Icon in it — at
    //      that parameter's position (a helper such as Entry("Settings", "settings"));
    //   5. a static readonly array of tuples with an element called Icon, at
    //      that element's place in each row.

    internal static IEnumerable<(string Name, int Offset)> Scan(string text)
    {
        var found = new List<(string, int)>();
        var positional = new Dictionary<string, HashSet<int>>
        {
            ["Menu.Item"] = [3],
            ["Toggle.Build"] = [4],
        };

        // Declarations first, so a helper declared below its first call is known.
        foreach (Match call in CallStart().Matches(text))
        {
            string method = call.Groups["method"].Value;
            if (!TryArguments(text, call.Index + call.Length - 1, out var args)) continue;
            for (int i = 0; i < args.Count; i++)
            {
                var p = Parameter().Match(args[i].Text);
                if (!p.Success) continue;
                string name = p.Groups["name"].Value;
                if (name is "icon" or "iconName" || (name == "name" && method.Contains("Icon", StringComparison.Ordinal)))
                {
                    Add(positional, method, i);
                    if (EnclosingType(text, call.Index) is { } type) Add(positional, type + "." + method, i);
                }
            }
        }

        foreach (Match call in CallStart().Matches(text))
        {
            if (!TryArguments(text, call.Index + call.Length - 1, out var args)) continue;
            string method = call.Groups["method"].Value;
            string qualifier = call.Groups["qualifier"].Value;

            if (method == "Icon" && qualifier.Length > 0 && args.Count > 0)
                Literals(args[0], found);

            // Qualified by an object (runtime.Foo) or a type (Menu.Item): a
            // type is looked up by its name, an object's call is rule 1's.
            string key = qualifier.Length > 0 ? qualifier + "." + method : method;
            if (positional.TryGetValue(key, out var places))
                foreach (int i in places)
                    if (i < args.Count && !NamedArgument().IsMatch(args[i].Text))
                        Literals(args[i], found);

            foreach (var arg in args)
                if (IconNamedArgument().IsMatch(arg.Text))
                    Literals(arg, found);
        }

        foreach (Match table in TupleTable().Matches(text))
        {
            var elements = SplitTopLevel(table.Groups["tuple"].Value, 0);
            int icon = elements.FindIndex(e => Regex.IsMatch(e.Text, @"\bIcon\s*$"));
            if (icon < 0) continue;
            int end = text.IndexOf(';', table.Index + table.Length);
            if (end < 0) continue;
            int start = table.Index + table.Length;
            foreach (Match row in InnermostParens().Matches(text[start..end]))
            {
                var cells = SplitTopLevel(row.Groups["inner"].Value, start + row.Groups["inner"].Index);
                if (cells.Count == elements.Count) Literals(cells[icon], found);
            }
        }

        return found.Distinct();
    }

    private static void Add(Dictionary<string, HashSet<int>> map, string key, int index)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(index);
    }

    private static string? EnclosingType(string text, int offset)
    {
        string? type = null;
        foreach (Match m in TypeDeclaration().Matches(text[..offset])) type = m.Groups["type"].Value;
        return type;
    }

    private static void Literals((string Text, int Offset) arg, List<(string, int)> into)
    {
        foreach (Match m in StringLiteral().Matches(arg.Text))
            if (m.Groups["value"].Value.Length > 0)
                into.Add((m.Groups["value"].Value, arg.Offset + m.Index));
    }

    // The argument list of the call whose '(' is at open, split at its
    // top-level commas; false when the parentheses do not close.
    private static bool TryArguments(string text, int open, out List<(string Text, int Offset)> args)
    {
        args = [];
        int close = MatchingClose(text, open);
        if (close < 0) return false;
        args = SplitTopLevel(text[(open + 1)..close], open + 1);
        return true;
    }

    private static int MatchingClose(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { i = SkipString(text, i); continue; }
            if (c == '\'') { i = SkipChar(text, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static List<(string Text, int Offset)> SplitTopLevel(string s, int offset)
    {
        var parts = new List<(string, int)>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { i = SkipString(s, i); continue; }
            if (c == '\'') { i = SkipChar(s, i); continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add((s[start..i], offset + start));
                start = i + 1;
            }
        }
        if (s.Trim().Length > 0) parts.Add((s[start..], offset + start));
        return parts;
    }

    // Index of the closing quote of the string that opens at i ("…", @"…" or """…""").
    private static int SkipString(string s, int i)
    {
        if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
        {
            int end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 2;
        }
        bool verbatim = i > 0 && s[i - 1] == '@';
        for (int j = i + 1; j < s.Length; j++)
        {
            if (!verbatim && s[j] == '\\') { j++; continue; }
            if (s[j] == '"')
            {
                if (verbatim && j + 1 < s.Length && s[j + 1] == '"') { j++; continue; }
                return j;
            }
        }
        return s.Length;
    }

    private static int SkipChar(string s, int i)
    {
        int j = i + 1;
        if (j < s.Length && s[j] == '\\') j++;
        int end = s.IndexOf('\'', j + 1);
        return end < 0 ? i : end;
    }

    [GeneratedRegex(@"(?<!\w)(?:(?<qualifier>[A-Za-z_]\w*)\s*\.\s*)?(?<method>[A-Za-z_]\w*)\s*\(")]
    private static partial Regex CallStart();

    [GeneratedRegex(@"^\s*string\??\s+(?<name>\w+)\s*(?:=.*)?$", RegexOptions.Singleline)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"^\s*\w+\s*:(?!:)")]
    private static partial Regex NamedArgument();

    [GeneratedRegex(@"^\s*(?:icon|iconName)\s*:")]
    private static partial Regex IconNamedArgument();

    [GeneratedRegex(@"(?<![$@\w""])""(?<value>[^""\\\r\n]*)""(?!u8|"")")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"\((?<tuple>[^()]*\bIcon\b[^()]*)\)[^=;{}(]*?=(?![=>])")]
    private static partial Regex TupleTable();

    [GeneratedRegex(@"\((?<inner>[^()]*)\)")]
    private static partial Regex InnermostParens();

    [GeneratedRegex(@"\b(?:class|struct|record|interface)\s+(?<type>\w+)")]
    private static partial Regex TypeDeclaration();
}
