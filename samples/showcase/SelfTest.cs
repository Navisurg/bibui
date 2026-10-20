using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;

namespace Showcase;

/// <summary>
/// <c>--selftest</c>: drives the app headless through the events a person would
/// send — clicks, keys, typing — at several window sizes, and checks the app's
/// state answers. No window. Exits non-zero on the first thing that is wrong.
/// </summary>
internal static class SelfTest
{
    public static int Run(Func<App> newApp)
    {
        var failures = new List<string>();
        void Check(bool ok, string what)
        {
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures.Add(what);
        }

        // Every page, both modes, from a phone-narrow window to a wide one, builds and lays out.
        foreach (var (w, h) in new[] { (640, 480), (820, 1180), (1100, 760), (1440, 900), (1920, 1080) })
            foreach (var page in Enum.GetValues<Page>())
                foreach (bool dark in new[] { true, false })
                {
                    var app = newApp();
                    app.Live = false;
                    app.Page = page;
                    app.Dark = dark;
                    var rt = app.Headless(w, h);
                    try { for (int f = 0; f < 3; f++) app.Frame(rt); }
                    catch (Exception e) { Check(false, $"{page} {(dark ? "dark" : "light")} at {w}×{h}: {e.GetType().Name}: {e.Message}"); }
                }
        Check(failures.Count == 0, "every page renders at 640×480 … 1920×1080, dark and light");

        {
            var app = newApp();
            app.Live = false;
            var rt = app.Headless(1440, 900);
            Frames(app, rt, 3);

            Click(app, rt, "nav-targets");
            Check(app.Page == Page.Targets, "a click on the sidebar opens Targets");

            Key(app, rt, Bibui.Core.Key.Number1);
            Check(app.Page == Page.Overview, "the 1 key goes back to the overview");

            string before = app.Selected;
            Key(app, rt, Bibui.Core.Key.Down);
            Check(app.Selected != before, "the down arrow moves the selection");
            Key(app, rt, Bibui.Core.Key.Enter);
            Check(app.Pointing == app.Selected && app.SlewCount == 1, "Enter slews to the selected target");

            Click(app, rt, "col-mag");
            Check(app.Sort == "mag", "a column header sorts by it");
            Click(app, rt, "col-mag");
            Check(app.SortDescending, "a second click reverses it");

            Key(app, rt, Bibui.Core.Key.Slash);
            Check(app.SearchFocused, "/ gives the search field the keyboard");
            Type(app, rt, "kes");
            Check(app.Search.ToString() == "kes", "typing goes into the search field, and the slash does not");
            Check(app.VisibleTargets().Count == 1, "the search filters the queue to one target");
            Key(app, rt, Bibui.Core.Key.L);
            Check(app.Dark, "a letter typed into the field is not a shortcut");
            Key(app, rt, Bibui.Core.Key.Escape);
            Check(!app.SearchFocused, "Escape gives the keyboard back");

            Key(app, rt, Bibui.Core.Key.L);
            Frames(app, rt, 2);
            Check(!app.Dark && rt.Theme.Colors.Mode == Bibui.Core.Theming.ThemeMode.Light, "L switches to light, and the runtime's theme follows");

            Key(app, rt, Bibui.Core.Key.Comma);
            Frames(app, rt, 30);
            Check(app.SettingsOpen, ", opens the Appearance sheet");
            Click(app, rt, "swatch-coral");
            Frames(app, rt, 2);
            Check(app.Accent == "#FB7185", "a swatch sets the seed");
            Check(rt.Theme.Colors.Ramps.Accent[500].Length == 7, "and the runtime's theme is regenerated from it");
            Click(app, rt, "settings-reduce");
            Check(app.ReducedMotion, "the reduce-motion switch turns on");
            Frames(app, rt, 2);
            Check(rt.Theme.Motion.Easing == Bibui.Core.Theming.MotionEasing.None, "and the theme's motion snaps");
            Key(app, rt, Bibui.Core.Key.Escape);
            Frames(app, rt, 2);
            Check(!app.SettingsOpen, "Escape closes the sheet");

            Key(app, rt, Bibui.Core.Key.LeftBracket);
            Frames(app, rt, 2);
            Check(!app.SidebarWanted, "[ folds the sidebar");
        }

        {
            var app = newApp();
            app.Live = false;
            app.Page = Page.Targets;
            var rt = app.Headless(1440, 900);
            Frames(app, rt, 3);
            string row = "row-" + app.Selected;
            string other = Array.Find(Data.Targets, t => t.Id != app.Selected)!.Id;

            RightClick(app, rt, row);
            Check(rt.IsDropDownOpen("row-menu") && app.MenuFor == app.Selected, "a right press on a row opens its menu");
            RightClick(app, rt, "row-" + other, across: 0.02f);   // left of the open menu, which opened mid-row
            Check(rt.IsDropDownOpen("row-menu") && app.MenuFor == other, "a right press on another row moves it there");
            Key(app, rt, Bibui.Core.Key.Escape);
            Check(!rt.IsDropDownOpen("row-menu"), "Escape closes the menu");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine($"{failures.Count} failed.");
            return 1;
        }
        Console.WriteLine("selftest: all good.");
        return 0;
    }

    private static void Frames(App app, BibuiRuntime rt, int n)
    {
        for (int i = 0; i < n; i++) app.Frame(rt);
    }

    private static void Click(App app, BibuiRuntime rt, string id)
    {
        rt.DispatchClick(id);
        Frames(app, rt, 2);
    }

    private static void RightClick(App app, BibuiRuntime rt, string id, float across = 0.5f)
    {
        var box = (Box)rt.FindById(id)!;
        float x = box.Rect.X + box.Extent.X * across, y = box.Rect.Y + box.Extent.Y / 2f;
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Right, X = x, Y = y });
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Right, X = x, Y = y });
        Frames(app, rt, 2);
    }

    private static void Key(App app, BibuiRuntime rt, Key key)
    {
        rt.ProcessEvent(BibUiEvent.FromKey(key, ctrl: false, shift: false));
        // The window sends the character a printable key types as well.
        char? ch = key switch
        {
            Bibui.Core.Key.Slash => '/', Bibui.Core.Key.Comma => ',', Bibui.Core.Key.LeftBracket => '[', Bibui.Core.Key.L => 'l',
            Bibui.Core.Key.Number1 => '1', _ => null,
        };
        if (ch is { } c) rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Text, Key = Bibui.Core.Key.Unknown, Char = c, CodePoint = c });
        Frames(app, rt, 2);
    }

    private static void Type(App app, BibuiRuntime rt, string text)
    {
        foreach (char c in text)
        {
            rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Text, Key = Bibui.Core.Key.Unknown, Char = c, CodePoint = c });
            Frames(app, rt, 1);
        }
    }
}
