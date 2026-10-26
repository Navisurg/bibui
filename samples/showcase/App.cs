using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;
using static Showcase.Ui;

namespace Showcase;

internal enum Page { Overview, Targets, Telemetry, Journal }

internal enum Metric { Seeing, Temperature, Throughput }

/// <summary>
/// Zenith: the console of an invented observatory. The app owns every bit of
/// state; the build reads it, asks the boxes it holds what happened, and
/// writes the answers back. Nothing in here has a callback.
/// </summary>
internal sealed partial class App
{
    // ── What the app knows ───────────────────────────────────────────────────

    public Page Page = Page.Overview;
    public bool Live = true;                      // telemetry and the sky move on their own
    public double Clock = 2 * 3600 + 17 * 60;     // seconds into the night (UTC 02:17 at start)
    public string Selected = "lantern";
    public string Pointing = "lantern";            // where the telescope is now
    public Metric Metric = Metric.Seeing;
    public bool Grid = true;
    public bool Labels = true;
    public bool SidebarWanted = true;              // what the person asked for; a narrow window folds it anyway
    public string Sort = "priority";
    public bool SortDescending;
    public readonly BibuiStr Search = new(64);
    public readonly TextEditState SearchEdit = new();
    public bool SearchFocused;
    public string? MenuFor;                         // the target the context menu, row-menu, was opened on
    public float MenuX, MenuY;
    public bool SettingsOpen;
    public int SlewCount;

    // Appearance
    public bool Dark = true;
    public string Accent = "#7C8CFF";
    public string? ThemeFileName;                  // a registered theme file in force, or null for the seed
    public float Radius = 8f;
    public Density Density = Density.Default;
    public bool ReducedMotion;

    public static readonly (string Hex, string Name)[] Accents =
    [
        ("#7C8CFF", "Periwinkle"), ("#38BDF8", "Glacier"), ("#2DD4BF", "Lagoon"), ("#A3E635", "Lime"),
        ("#FBBF24", "Amber"), ("#FB7185", "Coral"), ("#E879F9", "Orchid"), ("#F97316", "Ember"),
    ];

    private readonly Dictionary<string, BibuiStr> _logs = new();
    private string _themeKey = "";
    private readonly List<string> _themeFiles;

    public App(IReadOnlyList<string> themeFiles) => _themeFiles = [.. themeFiles];

    // ── The theme ────────────────────────────────────────────────────────────

    /// <summary>The theme the appearance settings describe.</summary>
    public ThemeTokens Theme()
    {
        ThemeTokens t = ThemeFileName is { } file
            ? ThemeRegistry.Load(file)
            : Bibui.Core.Theming.Theme.FromSeed(Accent, neutral: "#64748B", radius: Radius, density: Density,
                mode: Dark ? ThemeMode.Dark : ThemeMode.Light, name: "zenith");
        t = t with { Elevation = Dark ? ElevationTokens.Soft : LightElevation };
        // Reduced motion: the whole group copied to change one field, as token
        // groups are not records.
        if (ReducedMotion)
            t = t with
            {
                Motion = new MotionTokens
                {
                    Hover = t.Motion.Hover, Follow = t.Motion.Follow, Toggle = t.Motion.Toggle,
                    Default = t.Motion.Default, Easing = MotionEasing.None,
                },
            };
        return t;
    }

    private static readonly ElevationTokens LightElevation = new()
    {
        Level1 = new BoxShadow { OffsetY = 1f, Blur = 3f, Color = "#0F172A14" },
        Level2 = new BoxShadow { OffsetY = 6f, Blur = 16f, Color = "#0F172A1F" },
        Level3 = new BoxShadow { OffsetY = 10f, Blur = 28f, Color = "#0F172A26" },
        Level4 = new BoxShadow { OffsetY = 18f, Blur = 48f, Color = "#0F172A33" },
    };

    private void ApplyTheme(BibuiRuntime rt)
    {
        string key = $"{ThemeFileName}|{Accent}|{Dark}|{Radius}|{Density}|{ReducedMotion}";
        if (key == _themeKey) return;
        _themeKey = key;
        rt.SetTheme(Theme());
    }

    // ── The frame ────────────────────────────────────────────────────────────

    /// <summary>One frame of the app, between the loop's BeginBuild and EndBuild.</summary>
    public void Build(BibuiRuntime rt)
    {
        ApplyTheme(rt);
        if (Live)
        {
            Clock += rt.Dt * 20.0;   // the night runs twenty times faster than the wall clock
            rt.RequestFrame();
        }
        // The context menu takes its own Escape, so it is built before the keys are read.
        BuildContextMenu(rt);
        Keyboard(rt);

        float w = rt.SurfaceWidth, h = rt.SurfaceHeight;
        bool roomForSidebar = w >= 1080f;
        float open = rt.Animate("sidebar", "open", SidebarWanted && roomForSidebar ? 1f : 0f);

        // Overlays ask first: they are on top.
        BuildSettings(rt);

        var frame = rt.BeginParent(rt.Row().Sized(Size.Px(w), Size.Px(h)));
        frame.Style = new Style { BackgroundColor = UiColor.Background };
        Sidebar(rt, open, h);
        Main(rt, w - SidebarWidth(open), h);
        rt.EndParent(frame);
    }

    /// <summary>One whole frame on <paramref name="window"/>: BeginBuild with the frame's input, the app, EndBuild.</summary>
    public void Frame(BibuiRuntime rt, Bibui.Core.Runtime.Platform.WindowFrame window, List<Bibui.Core.Runtime.Platform.WindowEvent>? events = null)
    {
        rt.BeginBuild(window, events);
        Build(rt);
        rt.EndBuild();
    }

    /// <summary>One whole frame headless, on the window <see cref="Headless"/> gave the app, with no input.</summary>
    public void Frame(BibuiRuntime rt) => Frame(rt, _headless);

    // The window a headless runtime's frames are built on.
    private Bibui.Core.Runtime.Platform.WindowFrame _headless = new(1440, 900, 1f, 60f);

    /// <summary>A runtime for this app, headless, in its theme; its frames are <paramref name="w"/> × <paramref name="h"/> at 1×, 60 Hz.</summary>
    public BibuiRuntime Headless(int w, int h)
    {
        var rt = BibuiRuntime.Create();
        _headless = new(w, h, 1f, 60f);
        rt.SetTheme(Theme());
        return rt;
    }

    public static float SidebarWidth(float open) => 76f + 172f * open;

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void Keyboard(BibuiRuntime rt)
    {
        bool focusedNow = false;
        foreach (var ev in rt.UnconsumedEvents)
        {
            // The slash that gives the search field the keyboard is not typed into it.
            if (ev is { Kind: BibUiEventKind.Text, Char: '/' } && (focusedNow || !SearchFocused && !NoteFocused))
            {
                ev.Eat();
                continue;
            }
            if (SearchFocused || NoteFocused)
            {
                // A field has the keyboard: only Escape (and Enter, in the one-line search) takes it back.
                if (ev is { IsKeyDown: true, Key: Key.Escape } || SearchFocused && ev.Kind == BibUiEventKind.Submit)
                {
                    SearchFocused = NoteFocused = false;
                    ev.Eat();
                }
                continue;
            }
            // Any key that went down, arrows (Navigate) and Enter (Submit) included.
            if (!ev.IsKeyDown) continue;
            bool used = true;
            switch (ev.Key)
            {
                case Key.Slash:
                    SearchFocused = focusedNow = true;
                    if (Page is Page.Telemetry or Page.Journal) Page = Page.Overview;
                    break;
                case Key.Escape:
                    if (SettingsOpen) SettingsOpen = false;
                    else used = false;
                    break;
                case Key.Down: MoveSelection(+1); break;
                case Key.Up: MoveSelection(-1); break;
                case Key.Enter: Slew(); break;
                case Key.LeftBracket: SidebarWanted = !SidebarWanted; break;
                case Key.Comma: SettingsOpen = !SettingsOpen; break;
                case Key.L: Dark = !Dark; ThemeFileName = null; break;
                case Key.Space: Live = !Live; break;
                case Key.G: Grid = !Grid; break;
                case Key.Number1: Page = Page.Overview; break;
                case Key.Number2: Page = Page.Targets; break;
                case Key.Number3: Page = Page.Telemetry; break;
                case Key.Number4: Page = Page.Journal; break;
                default: used = false; break;
            }
            if (used) ev.Eat();
        }
    }

    private void MoveSelection(int by)
    {
        var rows = VisibleTargets();
        if (rows.Count == 0) return;
        int at = rows.FindIndex(t => t.Id == Selected);
        at = Math.Clamp(at < 0 ? 0 : at + by, 0, rows.Count - 1);
        Selected = rows[at].Id;
    }

    private void Slew()
    {
        if (Pointing == Selected) return;
        Pointing = Selected;
        SlewCount++;
    }

    // ── Data the screens share ───────────────────────────────────────────────

    public Target SelectedTarget => Array.Find(Data.Targets, t => t.Id == Selected) ?? Data.Targets[0];

    public double Minutes => Clock / 60.0;

    public string Utc
    {
        get
        {
            var span = TimeSpan.FromSeconds(Clock);
            return $"{(int)span.TotalHours % 24:00}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }

    public List<Target> VisibleTargets()
    {
        string query = Search.ToString();
        IEnumerable<Target> rows = Data.Targets;
        if (query.Length > 0)
            rows = rows.Where(t => t.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || t.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || Data.KindName(t.Kind).Contains(query, StringComparison.OrdinalIgnoreCase));
        double lst = Data.Lst(Clock);
        Func<Target, double> key = Sort switch
        {
            "name" => t => 0,
            "mag" => t => t.Magnitude,
            "alt" => t => -Data.AltAz(t.RaHours, t.DecDeg, lst).Alt,
            "progress" => t => -(double)t.DoneMin / t.PlannedMin,
            _ => t => t.Priority * 100 + (int)t.Status,
        };
        var list = Sort == "name"
            ? rows.OrderBy(t => t.Name, StringComparer.Ordinal).ToList()
            : rows.OrderBy(key).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        if (SortDescending) list.Reverse();
        return list;
    }

    public BibuiStr LogFor(Target t, bool night = false)
    {
        string key = (night ? "night:" : "") + t.Id;
        if (!_logs.TryGetValue(key, out var log))
            _logs[key] = log = BibuiStr.FromStrLit((night ? Data.Night + "\n" : "") + Data.Log(t, "01:48"));
        return log;
    }

    public IReadOnlyList<string> ThemeFiles => _themeFiles;
}
