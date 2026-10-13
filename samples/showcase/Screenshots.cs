using Bibui;
using Bibui.Raster;
using Bibui.Runtime;
using Bibui.TextEdit;

namespace Showcase;

/// <summary>
/// The README's pictures, rendered headless: the same app, the same build,
/// driven by the events a person would send, and read back as PNGs. No window.
/// </summary>
internal static class Screenshots
{
    public static void WriteAll(string dir, Func<IRasterBackend> backend, Func<App> newApp, bool all)
    {
        // 1. The overview, dark, with the pointer over the seeing chart.
        Write(dir, "overview-dark.png", backend(), newApp(), 1440, 900, app => { }, (rt, frame) =>
        {
            if (frame == 3) HoverAt(rt, "tel-main-chart", 0.72f, 0.5f);
        });

        // 2. Light mode from another seed, the pointer on a queue row.
        Write(dir, "overview-light.png", backend(), newApp(), 1440, 900, app =>
        {
            app.Dark = false;
            app.Accent = "#F97316";
            app.Selected = "kestrel";
            app.Metric = Metric.Throughput;
        }, (rt, frame) =>
        {
            if (frame == 3) HoverAt(rt, "row-quill", 0.5f, 0.5f);
        });

        // 2b. The Appearance sheet: one seed, the whole theme.
        Write(dir, "appearance.png", backend(), newApp(), 1440, 900, app =>
        {
            app.Accent = "#2DD4BF";
            app.SettingsOpen = true;
        }, (rt, frame) =>
        {
            if (frame == 3) HoverAt(rt, "swatch-orchid", 0.5f, 0.5f);
        });

        // 3. A narrow window: the sidebar folds to a rail and the cards stack.
        Write(dir, "narrow-dark.png", backend(), newApp(), 820, 1180, app =>
        {
            app.Accent = "#2DD4BF";
            app.Selected = "tamsin";
        }, (rt, frame) => { });

        // The four above are the README's. With --all, two more pages.
        if (!all) return;

        // 4. The queue: sorted by altitude, and a context menu open on a row.
        Write(dir, "targets-menu.png", backend(), newApp(), 1440, 900, app =>
        {
            app.Page = Page.Targets;
            app.Accent = "#38BDF8";
            app.Sort = "alt";
            app.Selected = "harrow";
        }, (rt, frame) =>
        {
            if (frame == 3 && rt.FindById("row-harrow") is { } row)
            {
                float x = row.Rect.X + row.Extent.X * 0.4f, y = row.Rect.Y + row.Extent.Y * 0.5f;
                rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });
                rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Right, X = x, Y = y });
                rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Right, X = x, Y = y });
            }
            if (frame == 5 && rt.FindById("menu-slew") is { } item)
                rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = item.Rect.X + 40f, Y = item.Rect.Y + item.Extent.Y / 2f });
        });

        // 5. The journal: Markdown, a checklist and a rating.
        Write(dir, "journal-dark.png", backend(), newApp(), 1440, 900, app =>
        {
            app.Page = Page.Journal;
            app.Accent = "#E879F9";
        }, (rt, frame) => { });
    }

    private static void Write(string dir, string name, IRasterBackend backend, App app, int w, int h,
        Action<App> setup, Action<BibuiRuntime, int> script)
    {
        app.Live = false;
        setup(app);
        var png = Render(backend, app, w, h, script);
        File.WriteAllBytes(Path.Combine(dir, name), png);
        Console.WriteLine($"  wrote {name} ({w}×{h}, {backend.Name})");
    }

    public static byte[] Render(IRasterBackend backend, App app, int w, int h, Action<BibuiRuntime, int> script, int frames = 10)
    {
        var rt = new BibuiRuntime(backend);
        rt.Mount(() => app.Build(rt), app.Theme(), new SurfaceOptions { Width = w, Height = h, ScaleFactor = 1f });
        byte[] png = [];
        for (int f = 0; f < frames; f++)
        {
            script(rt, f);
            var output = rt.Render();
            if (f == frames - 1) png = output.PngBytes;
        }
        return png;
    }

    private static void HoverAt(BibuiRuntime rt, string id, float fx, float fy)
    {
        if (rt.FindById(id) is not { } node) return;
        rt.ProcessEvent(new BibUiEvent
        {
            Kind = BibUiEventKind.MouseMove,
            X = node.Rect.X + node.Extent.X * fx,
            Y = node.Rect.Y + node.Extent.Y * fy,
        });
    }
}
