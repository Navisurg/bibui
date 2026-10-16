using Bibui;
using Bibui.Runtime;
using Bibui.Raster.Skia;

/// <summary>
/// Icons an app registers with <see cref="Icons.Register"/>: drawn by
/// runtime.Icon exactly as a built-in icon with the same path, and refused at
/// registration when they are wrong. The registry is process-wide, so every
/// name here starts with "t-app-" and is used by no other test.
/// </summary>
static class AppIconTests
{
    // Lucide's square, the same path data as the built-in "square".
    const string SquarePath = "M5 3h14a2 2 0 012 2v14a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2z";

    public static void Register(Action<string, Action> run)
    {
        run("Icons.Register: runtime.Icon draws an app icon exactly as the built-in icon with the same path", () =>
        {
            Icons.Register("t-app-square", SquarePath);
            Check(Icon.IsKnown("t-app-square") && Icon.IsKnown("T-App-Square"), "a registered name is known, in any case");
            Check(Icons.Names.Contains("t-app-square") && Icons.Names.Contains("folder"), "Names lists the app's icons beside the built-in ones");
            Check(!Icons.IsBuiltIn("t-app-square") && Icons.IsBuiltIn("square"), "an app icon is not a built-in one");

            byte[] app = Render("t-app-square");
            byte[] builtIn = Render("square");
            byte[] blank = Render(null);
            Check(app.AsSpan().SequenceEqual(builtIn), "the app's icon draws the same pixels as the built-in square");
            Check(!app.AsSpan().SequenceEqual(blank), "and it draws something");
        });

        run("Icons.Register: the same name and path again does nothing; other path data throws", () =>
        {
            Icons.Register("t-app-twice", "M4 12h16");
            Icons.Register("t-app-twice", "M4 12h16");
            Icons.Register("T-APP-TWICE", "M4 12h16");
            Throws<ArgumentException>(() => Icons.Register("t-app-twice", "M12 4v16"), "t-app-twice");
        });

        run("Icons.Register: a built-in name throws, in any case", () =>
        {
            Throws<ArgumentException>(() => Icons.Register("folder", "M4 12h16"), "folder");
            Throws<ArgumentException>(() => Icons.Register("Chevron-Down", "M4 12h16"), "Chevron-Down");
        });

        run("Icons.Register: an empty name throws", () =>
        {
            Throws<ArgumentException>(() => Icons.Register("", "M4 12h16"), "name");
            Throws<ArgumentException>(() => Icons.Register("  ", "M4 12h16"), "name");
        });

        run("Icons.Register: bad path data fails at registration, naming the icon, and is not registered", () =>
        {
            Throws<FormatException>(() => Icons.Register("t-app-bad-cmd", "M4 4 X 5 5"), "t-app-bad-cmd");
            Throws<FormatException>(() => Icons.Register("t-app-bad-number", "M4 4L5"), "t-app-bad-number");
            Throws<FormatException>(() => Icons.Register("t-app-no-move", "L4 4 8 8"), "move");
            Throws<FormatException>(() => Icons.Register("t-app-empty", "   "), "empty");
            Throws<FormatException>(() => Icons.Register("t-app-nothing", "M4 4"), "draws nothing");
            Throws<FormatException>(() => Icons.Register("t-app-infinite", "M4 4L1e99 4"), "finite");
            Check(!Icon.IsKnown("t-app-bad-cmd") && !Icon.IsKnown("t-app-no-move"), "a refused icon is not known");
        });
    }

    // A 40×40 frame with the icon at 24 px in the middle, or nothing.
    static byte[] Render(string? icon)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(
            () =>
            {
                var row = Arrange.Row().Px(40f, 40f);
                using (runtime.PushParent(row))
                {
                    runtime.Gap(8f);
                    if (icon is not null) runtime.Icon(icon, 24f);
                }
                return row;
            },
            new SurfaceOptions { Width = 40, Height = 40 });
        return runtime.Frame().RawBgraBytes.ToArray();
    }

    static void Throws<T>(Action action, string mentions) where T : Exception
    {
        try { action(); }
        catch (T e)
        {
            Check(e.Message.Contains(mentions, StringComparison.Ordinal), $"{typeof(T).Name} should mention '{mentions}': {e.Message}");
            return;
        }
        throw new Exception($"expected {typeof(T).Name} mentioning '{mentions}'");
    }

    static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
