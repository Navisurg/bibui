using System.Numerics;
using Bibui;
using Bibui.Raster;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using Bibui.TextEdit;
using Bibui.Theming;
using RtButton = Bibui.Runtime.Button;

/// <summary>
/// The damage oracle: every scene runs on two runtimes side by side, each on
/// its own managed backend — one drawing every frame whole, one with
/// <see cref="ManagedRasterBackend.DamageTracking"/> on — fed the same scripted
/// input on the same clock, and every frame of the damage-tracked one must
/// equal the whole one byte for byte. The damage-tracked one draws into a ring
/// of surfaces, as the Wayland host does, so a surface several frames old is
/// covered too.
/// </summary>
internal static class DamageOracle
{
    /// <summary>
    /// A scripted app: <c>Setup</c> runs once per runtime and returns that
    /// runtime's build function and the step applied before each frame.
    /// </summary>
    internal sealed record Scene(string Name, int Width, int Height, float Scale, int Frames,
        Func<BibuiRuntime, (Func<UiNode> Build, Action<int> Step)> Setup);

    internal sealed record Result(int Frames, int Mismatches, int Partial, string? FirstMismatch);

    internal static Result Run(Scene scene, int ring)
    {
        var fullBackend = new ManagedRasterBackend();
        var dmgBackend = new ManagedRasterBackend { DamageTracking = true };
        var full = Make(scene, fullBackend, out var stepFull);
        var dmg = Make(scene, dmgBackend, out var stepDmg);

        IRasterSurface? sf = null;
        var sd = new IRasterSurface?[ring];
        int w = 0, h = 0;
        byte[] a = [], b = [];
        int mismatches = 0, partial = 0;
        string? first = null;
        try
        {
            for (int f = 0; f < scene.Frames; f++)
            {
                stepFull(f);
                stepDmg(f);

                int pw = SurfaceOptions.PhysicalSize(full.SurfaceWidth, full.ScaleFactor);
                int ph = SurfaceOptions.PhysicalSize(full.SurfaceHeight, full.ScaleFactor);
                if (pw != w || ph != h)
                {
                    w = pw; h = ph;
                    sf?.Dispose();
                    sf = fullBackend.CreateSurface(w, h);
                    for (int i = 0; i < ring; i++) { sd[i]?.Dispose(); sd[i] = dmgBackend.CreateSurface(w, h); }
                    a = new byte[w * h * 4];
                    b = new byte[w * h * 4];
                }

                var target = sd[f % ring]!;
                full.RenderTo(sf!);
                dmg.RenderTo(target);
                if (dmgBackend.LastRepaint is not null) partial++;

                sf!.ReadPixels(a, w, h);
                target.ReadPixels(b, w, h);
                if (!a.AsSpan().SequenceEqual(b) && mismatches++ == 0)
                    first = $"frame {f}: {Describe(a, b, w, h)}; repaint {Rects(dmgBackend.LastRepaint)}";
            }
        }
        finally
        {
            sf?.Dispose();
            foreach (var s in sd) s?.Dispose();
        }
        return new Result(scene.Frames, mismatches, partial, first);
    }

    private static BibuiRuntime Make(Scene scene, ManagedRasterBackend backend, out Action<int> step)
    {
        var rt = new BibuiRuntime(backend) { TargetHz = 60f };
        var (build, s) = scene.Setup(rt);
        rt.Mount(build, ThemeLoader.BibuiDark(), new SurfaceOptions { Width = scene.Width, Height = scene.Height, ScaleFactor = scene.Scale });
        step = s;
        return rt;
    }

    private static string Describe(byte[] a, byte[] b, int w, int h)
    {
        int n = 0, l = w, t = h, r = 0, bt = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3]) continue;
            n++;
            int p = i / 4, x = p % w, y = p / w;
            l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x + 1); bt = Math.Max(bt, y + 1);
        }
        return $"{n} px differ in ({l},{t})-({r},{bt})";
    }

    private static string Rects(IReadOnlyList<PixelRect>? rects) =>
        rects is null ? "whole" : "[" + string.Join(" ", rects.Select(r => $"({r.Left},{r.Top})-({r.Right},{r.Bottom})")) + "]";

    // ── Input helpers ────────────────────────────────────────────────────────

    private static void Move(BibuiRuntime rt, float x, float y) =>
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseMove, X = x, Y = y });

    private static void Press(BibuiRuntime rt, float x, float y) =>
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y });

    private static void Release(BibuiRuntime rt, float x, float y) =>
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseUp, Button = MouseButton.Left, X = x, Y = y });

    private static void Type(BibuiRuntime rt, char ch) =>
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Text, CodePoint = ch, Char = ch });

    private static void Wheel(BibuiRuntime rt, float x, float y, float dy) =>
        rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Scroll, X = x, Y = y, ScrollY = dy });

    /// <summary>The centre of the node named <paramref name="id"/> as last laid out, or null before it was.</summary>
    private static (float X, float Y)? Centre(BibuiRuntime rt, string id)
    {
        var n = rt.FindById(id);
        return n is null ? null : (n.Rect.X + n.Extent.X / 2f, n.Rect.Y + n.Extent.Y / 2f);
    }

    private static void Click(BibuiRuntime rt, string id, bool down)
    {
        if (Centre(rt, id) is not { } c) return;
        Move(rt, c.X, c.Y);
        if (down) Press(rt, c.X, c.Y); else Release(rt, c.X, c.Y);
    }

    /// <summary>A pointer path that wanders the whole surface, crossing many boxes and edges.</summary>
    private static (float X, float Y) Wander(int f, int w, int h) =>
        ((f * 37 + 11) % (w - 4) + 2, (f * 23 + 7) % (h - 4) + 2);

    // A text at the bold weight, where a PushFontFace scope cannot go: inside an expression.
    private static Text Bold(BibuiRuntime runtime, ReadOnlySpan<char> text, FontSize? fontSize = null, UiColor? textColor = null)
    {
        using (runtime.PushFontFace(weight: FontWeight.Bold))
            return runtime.Text(text, fontSize, textColor);
    }

    /// <summary>Lays <paramref name="nodes"/> out <paramref name="perRow"/> to a row, <paramref name="gap"/> px apart.</summary>
    private static Box Grid(IReadOnlyList<UiNode> nodes, int perRow, float gap)
    {
        var rows = new List<Arrange.Item>();
        for (int i = 0; i < nodes.Count; i += perRow)
        {
            var cells = new List<Arrange.Item>();
            for (int j = i; j < Math.Min(nodes.Count, i + perRow); j++)
            {
                if (j > i) cells.Add(Arrange.Space(gap));
                cells.Add(nodes[j]);
            }
            if (i > 0) rows.Add(Arrange.Space(gap));
            rows.Add(Arrange.Row([.. cells]));
        }
        return Arrange.Column([.. rows]);
    }

    /// <summary>A <paramref name="n"/>-pixel square checkerboard, straight-alpha RGBA.</summary>
    private static byte[] Checkerboard(int n)
    {
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            byte v = (x / 4 + y / 4) % 2 == 0 ? (byte)230 : (byte)40;
            int i = (y * n + x) * 4;
            px[i] = v; px[i + 1] = v; px[i + 2] = (byte)(255 - v); px[i + 3] = 255;
        }
        return px;
    }

    private static Box Page(Box content, float pad = 8f) =>
        Arrange.Column(content).FillX().FillY().Padded(EdgeInsets.All(pad));

    // ── Scenes ───────────────────────────────────────────────────────────────

    internal static IEnumerable<Scene> Scenes()
    {
        yield return new Scene("button-grid-hover", 900, 520, 1f, 90, rt =>
        {
            UiNode Build()
            {
                var kids = new UiNode[100];
                for (int i = 0; i < kids.Length; i++)
                    kids[i] = RtButton.Build(rt, $"b{i}", $"Item {i}", ghost: i % 3 == 1, disabled: i % 17 == 16, size: ComponentSize.Sm).Node;
                return Page(Grid(kids, 10, 4f));
            }
            return (Build, f =>
            {
                var (x, y) = Wander(f, 900, 520);
                Move(rt, x, y);
                if (f % 11 == 5) Press(rt, x, y);
                if (f % 11 == 7) Release(rt, x, y);
            });
        });

        yield return new Scene("button-grid-hover-2x", 450, 300, 2f, 60, rt =>
        {
            UiNode Build()
            {
                var kids = new UiNode[30];
                for (int i = 0; i < kids.Length; i++) kids[i] = RtButton.Build(rt, $"b{i}", $"B{i}", ghost: i % 2 == 0, size: ComponentSize.Sm).Node;
                return Page(Grid(kids, 6, 3f), 6f);
            }
            return (Build, f => { var (x, y) = Wander(f, 450, 300); Move(rt, x, y); });
        });

        yield return new Scene("retained-buttons", 900, 400, 1f, 120, rt =>
        {
            var clicks = new int[32];
            UiNode Build()
            {
                var kids = new UiNode[clicks.Length];
                for (int i = 0; i < kids.Length; i++)
                {
                    var b = RtButton.Build(rt, $"r{i}", clicks[i] == 0 ? $"Button {i}" : $"Clicked {clicks[i]}",
                        ghost: i % 4 == 1, disabled: i % 9 == 8, size: i % 2 == 0 ? ComponentSize.Sm : ComponentSize.Md);
                    if (b.Clicked) clicks[i]++;
                    kids[i] = b.Node;
                }
                return Page(Grid(kids, 8, 6f), 10f);
            }
            return (Build, f =>
            {
                var (x, y) = Wander(f, 900, 400);
                Move(rt, x, y);
                if (f % 13 == 3) Press(rt, x, y);
                if (f % 13 == 5) Release(rt, x, y);
            });
        });

        yield return new Scene("switches", 500, 360, 1f, 100, rt =>
        {
            var on = new bool[6];
            UiNode Build()
            {
                var items = new List<Arrange.Item>();
                for (int i = 0; i < 6; i++)
                {
                    var s = Switch.Build(rt, $"sw{i}", on[i], label: on[i] ? $"Option {i} on" : $"Option {i}", disabled: i == 5 && !on[0]);
                    if (s.Clicked) on[i] = !on[i];
                    if (i > 0) items.Add(Arrange.Space(14f));
                    items.Add(s.Node);
                }
                return Page(Arrange.Column([.. items]), 20f);
            }
            return (Build, f =>
            {
                // A switch clicked every 8 frames; the frames between are its knob easing.
                int i = (f / 8) % 6;
                if (f % 8 == 1) Click(rt, $"sw{i}", down: true);
                else if (f % 8 == 2) Click(rt, $"sw{i}", down: false);
                else if (f % 8 == 5) Move(rt, 480, 350);
            });
        });

        yield return new Scene("typing", 600, 380, 1f, 110, rt =>
        {
            var name = new BibuiStr(64);
            var other = new BibuiStr(64);
            var nameEdit = new TextEditState();
            var otherEdit = new TextEditState();
            int focus = -1;
            UiNode Field(string id, BibuiStr text, TextEditState edit, int index, string placeholder)
            {
                Box field = rt.Box();
                TextField.InputStyle(rt, field, focus == index);
                field.FillX().Px(height: 36f);
                BibuiWidget line;
                using (rt.PushParent(field))
                    line = TextField.SingleLine(rt, id, text, edit, focus == index, placeholder: placeholder);
                if (line.Pressed) focus = index;
                return field;
            }
            UiNode Build()
            {
                var a = Field("name", name, nameEdit, 0, "Full name");
                var b = Field("other", other, otherEdit, 1, "Account ID");
                return Page(Arrange.Column(
                    Bold(rt, "Account details"), Arrange.Space(16f),
                    a, Arrange.Space(16f), b, Arrange.Space(16f),
                    rt.Text($"{name.Length} chars typed", ComponentSize.Sm, TextColor.Secondary)).FillX(), 24f);
            }
            return (Build, f =>
            {
                if (f == 1) Click(rt, "name", down: true);
                else if (f == 2) Click(rt, "name", down: false);
                else if (f == 60) Click(rt, "other", down: true);
                else if (f == 61) Click(rt, "other", down: false);
                else if (f > 2 && f % 3 != 0) Type(rt, (char)('a' + f % 26));
                if (f % 17 == 0) rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Navigate, Delta = -2 });
                if (f % 23 == 0) rt.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Edit, Flags = TextActionFlags.Delete, Delta = -1 });
            });
        });

        yield return new Scene("app-state", 600, 420, 1f, 80, rt =>
        {
            int frame = 0;
            string[] palette = ["#ef4444", "#3b82f6", "#22c55e", "#eab308", "#a855f7"];
            UiNode Build()
            {
                int f = frame;
                var swatches = new List<Arrange.Item>();
                for (int i = 0; i < 6; i++)
                {
                    if (i > 0) swatches.Add(Arrange.Space(8f));
                    var sw = rt.Box().Px(60f, 40f);
                    sw.Style.CornerRadius = 8f;
                    sw.Style.BackgroundColor = UiColor.Hex(palette[(f / (i + 1) + i) % palette.Length]);
                    sw.Style.BorderColor = UiColor.Hex(i == f % 6 ? "#ffffff" : "#00000000");
                    sw.Style.BorderWidth = 2f;
                    swatches.Add(sw);
                }
                var rows = new List<Arrange.Item>
                {
                    f % 2 == 0 ? Bold(rt, $"Tick {f}") : rt.Text($"Tick {f}"), Arrange.Space(10f),
                    Arrange.Row([.. swatches]), Arrange.Space(10f),
                };
                if (f % 14 < 7) { rows.Add(Badge.Build(rt, "Notice")); rows.Add(Arrange.Space(10f)); }
                // A list that gains an entry every ten frames, sometimes with one pinned in front.
                var items = new List<Arrange.Item>();
                if ((f / 10) % 2 == 1) items.Add(rt.Text("Pinned"));
                for (int i = 0; i < 2 + f / 10; i++) items.Add(rt.Text($"Entry {i}"));
                rows.Add(Arrange.Column([.. items]));
                return Page(Arrange.Column([.. rows]), 16f);
            }
            return (Build, f => frame = f);
        });

        yield return new Scene("runtime-animate", 600, 300, 1f, 90, rt =>
        {
            int frame = 0;
            UiNode Build()
            {
                float w = rt.Animate("bar", "width", (frame / 25) % 2 == 0 ? 60f : 420f, speed: 8f);
                float a = rt.Animate("bar", "alpha", (frame / 30) % 2 == 0 ? 1f : 0.3f, speed: 6f);
                var bar = rt.Box().Px(w, 30f);
                bar.Style.CornerRadius = 6f;
                bar.Style.BackgroundColor = UiColor.Hex(new Rgba(59, 130, 246, (byte)Math.Clamp((int)(a * 255f), 0, 255)).ToString());
                var dot = rt.Box().Px(40f, 40f);
                dot.Style.CornerRadius = 20f;
                dot.Style.BackgroundColor = UiColor.Hex("#22c55e");
                return Page(Arrange.Column(bar, Arrange.Space(12f), Arrange.Row(Arrange.Space(w / 2f), dot), Arrange.Space(12f),
                    rt.Text("Animated by runtime.Animate")), 20f);
            }
            return (Build, f => frame = f);
        });

        yield return new Scene("overlays", 640, 420, 1f, 100, rt =>
        {
            int frame = 0;
            UiNode Build()
            {
                bool popover = (frame / 15) % 2 == 1;
                bool dialog = frame >= 30 && frame < 75;
                float shown = rt.Animate("dialog", "shown", dialog ? 1f : 0f, speed: 10f);
                var trigger = RtButton.Build(rt, "pop-trigger", "Details");
                var tipped = RtButton.Build(rt, "tip-trigger", "Hover for a tip", ghost: true);
                if (tipped.Hovering) Tooltip.Build(rt, "A tooltip over the grid", (Box)tipped.Node);
                if (popover) rt.OpenDropDown("pop", trigger.Node);
                else rt.CloseDropDown("pop");
                if (Popover.Begin(rt, "pop", trigger.Node, 240f) is { } p)
                {
                    rt.Text("A popover over the grid", ComponentSize.Sm, TextColor.Secondary);
                    p.End();
                }
                if (shown > 0.001f)
                {
                    var d = Dialog.Begin(rt, "dialog", shown, width: 320f);
                    using (rt.PushFontFace(weight: FontWeight.Bold)) rt.Text("Dialog");
                    rt.Gap(12f);
                    RtButton.Build(rt, "dialog-ok", "OK");
                    d.End();
                }
                var grid = new UiNode[16];
                for (int i = 0; i < grid.Length; i++) grid[i] = RtButton.Build(rt, $"u{i}", $"Under {i}").Node;
                return Page(Arrange.Column(Arrange.Row(trigger.Node, Arrange.Space(12f), tipped.Node), Arrange.Space(10f), Grid(grid, 4, 6f)), 16f);
            }
            return (Build, f => { frame = f; var (x, y) = Wander(f, 640, 420); Move(rt, x, y); });
        });

        yield return new Scene("scroll", 500, 400, 1f, 70, rt =>
        {
            UiNode Build()
            {
                var view = rt.BoxFromId("list", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip);
                rt.InteractionsFor(view);
                view.ChildLayoutAxis = LayoutAxis.Y;
                view.FillX().FillY();
                for (int i = 0; i < 60; i++)
                {
                    if (i > 0) Arrange.Gap(view, 4f);
                    var row = RtButton.Build(rt, $"row{i}", $"Row {i}", ghost: i % 2 == 1);
                    UiNode.PushIntoParent(view, row.Node);
                }
                return Page(view);
            }
            return (Build, f =>
            {
                Move(rt, 100, 100 + (f * 13) % 250);
                if (f % 10 == 4) Wheel(rt, 100, 200, -3f);
                if (f % 10 == 8) Wheel(rt, 100, 200, 1f);
            });
        });

        yield return new Scene("resize", 600, 400, 1f, 60, rt =>
        {
            UiNode Build()
            {
                var kids = new UiNode[36];
                for (int i = 0; i < kids.Length; i++) kids[i] = RtButton.Build(rt, $"z{i}", $"Z{i}").Node;
                return Page(Grid(kids, 6, 4f));
            }
            return (Build, f =>
            {
                if (f == 20) rt.ResizeSurface(700, 450, 1f);
                if (f == 40) rt.ResizeSurface(500, 360, 1f);
                var (x, y) = Wander(f, 480, 340);
                Move(rt, x, y);
            });
        });

        yield return new Scene("shadows-gradients", 700, 460, 1f, 100, rt =>
        {
            UiNode Card(int i)
            {
                var a = RtButton.Build(rt, $"c{i}a", "Open");
                var b = RtButton.Build(rt, $"c{i}b", "Share", ghost: true);
                var card = Arrange.Column(a.Node, Arrange.Space(6f), b.Node).Px(190f, 150f);
                card.Style = new Style
                {
                    CornerRadius = 14f, Padding = EdgeInsets.All(12f),
                    BackgroundGradient = i % 2 == 0
                        ? Gradient.Vertical(new ColorStop(0f, "#1e3a8a"), new ColorStop(1f, "#0f172a"))
                        : Gradient.Radial((0.3f, 0.3f), (1.1f, 1f), new ColorStop(0f, "#7c3aed"), new ColorStop(1f, "#111827")),
                    Shadow = i % 3 == 0 ? BoxShadow.Glow("#22d3ee88", blur: 30f) : BoxShadow.Elevation(),
                };
                return card;
            }
            UiNode Build()
            {
                var cards = new UiNode[6];
                for (int i = 0; i < 6; i++) cards[i] = Card(i);
                var glass = rt.Box().Px(220f, 90f).At(230f, 150f);
                glass.Style.CornerRadius = 10f;
                glass.Style.BackdropBlur = 8f;
                glass.Style.BackgroundColor = UiColor.Hex("#ffffff22");
                var page = Page(Grid(cards, 3, 24f), 24f);
                UiNode.PushIntoParent(page, glass);
                return page;
            }
            return (Build, f =>
            {
                var (x, y) = Wander(f, 700, 460);
                Move(rt, x, y);
                if (f % 9 == 4) Press(rt, x, y);
                if (f % 9 == 6) Release(rt, x, y);
            });
        });

        yield return new Scene("spinners-charts", 640, 360, 1f, 70, rt =>
        {
            int frame = 0;
            UiNode Build()
            {
                var data = Enumerable.Range(0, 12).Select(i => 20f + 15f * MathF.Sin((frame + i * 3) / 5f) + i).ToArray();
                var chart = new Box
                {
                    Draw = ctx =>
                    {
                        var r = ctx.Logical;
                        Span<(float X, float Y)> pts = stackalloc (float, float)[data.Length];
                        for (int i = 0; i < data.Length; i++)
                            pts[i] = (r.Left + (r.Right - r.Left) * i / (data.Length - 1), r.Bottom - (r.Bottom - r.Top) * data[i] / 60f);
                        ctx.DrawFilledPathGradient(pts, r.Top, r.Bottom, new Rgba(59, 130, 246, 90));
                        ctx.DrawPolyline(pts, new Rgba(59, 130, 246), 2f);
                        ctx.DrawEdge(r.Left, r.Bottom, r.Right, r.Bottom, 1f, "#3f3f46", dashed: true);
                    },
                }.Px(300f, 120f);
                return Page(Arrange.Column(
                    Arrange.Row(rt.Spinner(), Arrange.Space(16f), Progress.Build(rt, frame % 100).Px(width: 200f),
                        Arrange.Space(16f), rt.Icon(frame % 20 < 10 ? "check" : "x", 20f)),
                    Arrange.Space(12f), chart), 16f);
            }
            return (Build, f => frame = f);
        });

        yield return new Scene("opacity", 600, 300, 1f, 80, rt =>
        {
            int frame = 0;
            UiNode Build()
            {
                float o = rt.Animate("fade", "o", (frame / 20) % 2 == 0 ? 1f : 0.25f, speed: 8f);
                var kids = new List<Arrange.Item>();
                for (int i = 0; i < 4; i++)
                {
                    if (i > 0) kids.Add(Arrange.Space(6f));
                    kids.Add(RtButton.Build(rt, $"o{i}", $"Faded {i}").Node);
                }
                var panel = Arrange.Row([.. kids]);
                panel.Style = new Style { Opacity = o, Padding = EdgeInsets.All(8f), BackgroundColor = UiColor.Hex("#27272a"), CornerRadius = 8f };
                return Page(Arrange.Column(panel), 12f);
            }
            return (Build, f => { frame = f; var (x, y) = Wander(f, 600, 300); Move(rt, x, y); });
        });

        yield return new Scene("custom-draw", 500, 300, 1f, 40, rt =>
        {
            int frame = 0;
            UiNode Build()
            {
                var canvas = new Box
                {
                    Draw = ctx =>
                    {
                        // Cached geometry; scratch geometry; and a path the draw builds and drops.
                        float x = 20 + (frame * 9) % 300;
                        Span<(float X, float Y)> pts = stackalloc (float, float)[10];
                        for (int i = 0; i < 10; i++) pts[i] = (10f + i * 40f, 60f + 30f * MathF.Sin((frame + i) / 3f));
                        ctx.DrawPolyline(pts, new Rgba(249, 115, 22), 2f);
                        ctx.DrawArc(x, 40, 16, 3, new Rgba(56, 189, 248), sweepAngle: 90 + frame * 7 % 270);
                        if (frame % 10 >= 5)
                        {
                            using var own = ctx.Backend.CreatePath();
                            own.MoveTo(ctx.Rect.Left + 400, ctx.Rect.Top + 10);
                            own.LineTo(ctx.Rect.Left + 460, ctx.Rect.Top + 100);
                            own.LineTo(ctx.Rect.Left + 380, ctx.Rect.Top + 100);
                            own.Close();
                            ctx.DrawPath(own, ctx.Fill(new Rgba(234, 179, 8)));
                        }
                    },
                }.FillX().Px(height: 120f);
                return Page(Arrange.Column(canvas, Arrange.Space(8f), RtButton.Build(rt, "cd", "Hover me").Node).FillX(), 12f);
            }
            return (Build, f => { frame = f; Move(rt, 60 + (f * 5) % 120, 150); });
        });

        // Every canvas call the scenes above never make, drawn straight onto the
        // canvas by a box's own draw: images (one still, one moving), a pixel
        // buffer rewritten in place every frame, points, string text, a glyph run,
        // and shapes under a changing scale and a set matrix.
        yield return new Scene("canvas-calls", 520, 300, 1f, 60, rt =>
        {
            int frame = 0;
            RasterImage? image = null;
            var pixels = new byte[32 * 32 * 4];
            UiNode Build()
            {
                var canvas = new Box
                {
                    Draw = ctx =>
                    {
                        var c = ctx.Canvas;
                        float l = ctx.Rect.Left, t = ctx.Rect.Top;
                        var white = ctx.Fill(new Rgba(255, 255, 255));
                        image ??= ctx.Backend.CreateImage(Checkerboard(16), 16, 16);

                        c.DrawImage(image, new RectF(l + 20, t + 20, l + 84, t + 84), white);
                        float x = l + 120 + frame / 4 % 10 * 8;
                        c.DrawImage(image, new RectF(x, t + 20, x + 48, t + 68), white);

                        // The same array, rewritten in place: its content is what is drawn.
                        for (int i = 0; i < 32 * 32; i++)
                        {
                            pixels[i * 4] = (byte)(i * 7 + frame * 5);
                            pixels[i * 4 + 1] = (byte)(i / 32 * 8);
                            pixels[i * 4 + 2] = (byte)(frame * 11);
                            pixels[i * 4 + 3] = 255;
                        }
                        c.DrawPixels(pixels, 32, 32, new RectF(l + 260, t + 20, l + 324, t + 84), white);

                        Span<Vector2> pts = stackalloc Vector2[8];
                        for (int i = 0; i < 8; i++) pts[i] = new(l + 30 + i * 20, t + 120 + (i == frame % 8 ? 6 : 0));
                        c.DrawPoints(PointMode.Points, pts, ctx.Stroke(new Rgba(234, 179, 8), 4f));

                        var font = new RasterFont(ctx.Backend.DefaultTypeface, 14f);
                        c.DrawText($"frame {frame / 6}", l + 30, t + 170, font, new Rgba(255, 255, 255));
                        var run = ctx.Backend.ShapeText(font, "glyphs");
                        var at = new Vector2[run.Glyphs.Length];
                        for (int i = 1; i < at.Length; i++) at[i] = new(at[i - 1].X + run.Advances[i - 1], 0f);
                        c.DrawGlyphRun(run.Glyphs, at, l + 200, t + 170, font, new Rgba(56, 189, 248));

                        c.Save();
                        c.Translate(l + 400, t + 120);
                        c.Scale(1f + frame / 10 % 3 * 0.25f, 1f);
                        c.DrawRoundRect(new RectF(0, 0, 40, 30), 6, 6, ctx.Fill(new Rgba(34, 197, 94)));
                        c.Restore();
                        c.Save();
                        c.SetMatrix(Matrix3x2.CreateTranslation(l + 400, t + 180 + frame % 5));
                        c.DrawRoundRect(new RectF(0, 0, 40, 30), 6, 6, ctx.Fill(new Rgba(239, 68, 68)));
                        c.Restore();
                    },
                }.Px(520f, 240f);
                return Page(Arrange.Column(canvas, Arrange.Space(8f), RtButton.Build(rt, "cc", "Hover me").Node), 0f);
            }
            return (Build, f => { frame = f; Move(rt, 40 + (f * 7) % 200, 270); });
        });

        // The dense case damage tracking is for: 600 solid buttons filling a HiDPI surface.
        yield return new Scene("solid-grid-2x", 900, 700, 2f, 40, rt =>
        {
            UiNode Build()
            {
                Box column = rt.Box();
                column.ChildLayoutAxis = LayoutAxis.Y;
                column.FillX().FillY().Padded(EdgeInsets.All(8f));
                using (rt.PushParent(column))
                {
                    for (int start = 0; start < 600; start += 20)
                    {
                        if (start > 0) rt.Gap(4f);
                        Box row = rt.Box();
                        row.ChildLayoutAxis = LayoutAxis.X;
                        row.FillX();
                        row.SizeY = SemanticSize.ChildrenSum();
                        using (rt.PushParent(row))
                        {
                            for (int i = start; i < start + 20; i++)
                            {
                                if (i > start) rt.Gap(4f);
                                RtButton.Build(rt, $"s{i}", $"Item {i}").Node.FillX();
                            }
                        }
                    }
                }
                return column;
            }
            return (Build, f => { var (x, y) = Wander(f, 900, 700); Move(rt, x, y); });
        });
    }
}
