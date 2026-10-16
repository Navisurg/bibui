using Bibui;
using Bibui.Imaging;
using Bibui.Draw;
using Bibui.Runtime;
using Bibui.TextEdit;

namespace Showcase;

/// <summary>
/// The README's pictures, rendered headless: the same app, the same build,
/// driven by the events a person would send, and read back as PNGs. No window.
/// </summary>
internal static class Screenshots
{
    public static void WriteAll(string dir, Func<DrawToPixels> draw, string rasterName, Func<App> newApp, bool all)
    {
        // 1. The overview, dark, with the pointer over the seeing chart.
        Write(dir, rasterName, "overview-dark.png", draw(), newApp(), 1440, 900, app => { }, (rt, frame) =>
        {
            if (frame == 3) HoverAt(rt, "tel-main-chart", 0.72f, 0.5f);
        });

        // 2. Light mode from another seed, the pointer on a queue row.
        Write(dir, rasterName, "overview-light.png", draw(), newApp(), 1440, 900, app =>
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
        Write(dir, rasterName, "appearance.png", draw(), newApp(), 1440, 900, app =>
        {
            app.Accent = "#2DD4BF";
            app.SettingsOpen = true;
        }, (rt, frame) =>
        {
            if (frame == 3) HoverAt(rt, "swatch-orchid", 0.5f, 0.5f);
        });

        // 3. A narrow window: the sidebar folds to a rail and the cards stack.
        Write(dir, rasterName, "narrow-dark.png", draw(), newApp(), 820, 1180, app =>
        {
            app.Accent = "#2DD4BF";
            app.Selected = "tamsin";
        }, (rt, frame) => { });

        // The four above are the README's. With --all, two more pages.
        if (!all) return;

        // 4. The queue: sorted by altitude, and a context menu open on a row.
        Write(dir, rasterName, "targets-menu.png", draw(), newApp(), 1440, 900, app =>
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
        Write(dir, rasterName, "journal-dark.png", draw(), newApp(), 1440, 900, app =>
        {
            app.Page = Page.Journal;
            app.Accent = "#E879F9";
        }, (rt, frame) => { });
    }

    private static void Write(string dir, string rasterName, string name, DrawToPixels draw, App app, int w, int h,
        Action<App> setup, Action<BibuiRuntime, int> script)
    {
        app.Live = false;
        setup(app);
        var png = Render(draw, app, w, h, script);
        File.WriteAllBytes(Path.Combine(dir, name), png);
        Console.WriteLine($"  wrote {name} ({w}×{h}, {rasterName})");
    }

    public static byte[] Render(DrawToPixels draw, App app, int w, int h, Action<BibuiRuntime, int> script, int frames = 10)
    {
        var rt = BibuiRuntime.Create();
        rt.Mount(() => app.Build(rt), app.Theme(), new SurfaceOptions { Width = w, Height = h, ScaleFactor = 1f });
        for (int f = 0; f < frames; f++)
        {
            script(rt, f);
            rt.EndBuild();
        }
        return Png.Encode(Pixels(rt, draw), w, h, w * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);
    }

    /// <summary>The runtime's last frame, drawn by <paramref name="draw"/> into a new BGRA buffer.</summary>
    public static byte[] Pixels(BibuiRuntime rt, DrawToPixels draw)
    {
        int w = SurfaceOptions.PhysicalSize(rt.SurfaceWidth, rt.ScaleFactor);
        int h = SurfaceOptions.PhysicalSize(rt.SurfaceHeight, rt.ScaleFactor);
        var bgra = GC.AllocateArray<byte>(w * h * 4, pinned: true);
        draw(rt.DrawList, new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), w, h, w * 4));
        return bgra;
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
