using System.Numerics;
using Bibui.Core;
using Bibui.Core.Draw;
using Surface = Bibui.Raster.Managed.Surface;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;

/// <summary>
/// The damage oracle: every scene runs on two runtimes side by side, each drawn
/// by its own managed renderer — one drawing every frame whole, one with
/// <see cref="ManagedRenderer.DamageTracking"/> on — fed the same scripted
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
        Func<BibuiRuntime, ManagedRenderer, (Func<UiNode> Build, Action<int> Step)> Setup);

    internal sealed record Result(int Frames, int Mismatches, int Partial, string? FirstMismatch);

    internal static Result Run(Scene scene, int ring)
    {
        var fullRenderer = new ManagedRenderer();
        var dmgRenderer = new ManagedRenderer { DamageTracking = true };
        var full = Make(scene, fullRenderer, out var stepFull);
        var dmg = Make(scene, dmgRenderer, out var stepDmg);

        Surface? sf = null;
        var sd = new Surface?[ring];
        int w = 0, h = 0;
        uint[] a = [], b = [];
        int mismatches = 0, partial = 0;
        string? first = null;
        for (int f = 0; f < scene.Frames; f++)
        {
            stepFull(f);
            stepDmg(f);

            int pw = SurfaceOptions.PhysicalSize(full.SurfaceWidth, full.ScaleFactor);
            int ph = SurfaceOptions.PhysicalSize(full.SurfaceHeight, full.ScaleFactor);
            if (pw != w || ph != h)
            {
                w = pw; h = ph;
                sf = new Surface(w, h);
                for (int i = 0; i < ring; i++) sd[i] = new Surface(w, h);
                a = new uint[w * h];
                b = new uint[w * h];
            }

            var target = sd[f % ring]!;
            full.Step();
            fullRenderer.Draw(full.DrawList, sf!);
            dmg.Step();
            dmgRenderer.Draw(dmg.DrawList, target);
            if (dmgRenderer.LastRepaint is not null) partial++;

            sf!.CopyTo(a);
            target.CopyTo(b);
            if (!a.AsSpan().SequenceEqual(b) && mismatches++ == 0)
                first = $"frame {f}: {Describe(a, b, w, h)}; repaint {Rects(dmgRenderer.LastRepaint)}";
        }
        return new Result(scene.Frames, mismatches, partial, first);
    }

    private static BibuiRuntime Make(Scene scene, ManagedRenderer renderer, out Action<int> step)
    {
        var rt = BibuiRuntime.Create();
        rt.TargetHz = 60f;
        var (build, s) = scene.Setup(rt, renderer);
        rt.SetBuild(build, ThemeLoader.BibuiDark(), new SurfaceOptions { Width = scene.Width, Height = scene.Height, ScaleFactor = scene.Scale });
        step = s;
        return rt;
    }

    private static string Describe(uint[] a, uint[] b, int w, int h)
    {
        int n = 0, l = w, t = h, r = 0, bt = 0;
        for (int p = 0; p < a.Length; p++)
        {
            if (a[p] == b[p]) continue;
            n++;
            int x = p % w, y = p / w;
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

    // A text at the bold weight.
    private static Text Bold(BibuiRuntime runtime, ReadOnlySpan<char> text, FontSize? fontSize = null, UiColor? textColor = null)
    {
        using (runtime.FontFace(weight: FontWeight.Bold))
            return runtime.Text(text, fontSize, textColor);
    }

    /// <summary>
    /// Lays <paramref name="nodes"/>, built already, out <paramref name="perRow"/>
    /// to a row, <paramref name="gap"/> px apart, down the column in scope.
    /// </summary>
    private static void Grid(BibuiRuntime rt, IReadOnlyList<UiNode> nodes, int perRow, float gap)
    {
        for (int i = 0; i < nodes.Count; i += perRow)
        {
            if (i > 0) rt.Gap(gap);
            var row = rt.Row();
            using (rt.Parent(row))
                for (int j = i; j < Math.Min(nodes.Count, i + perRow); j++)
                {
                    if (j > i) rt.Gap(gap);
                    UiNode.PushIntoParent(row, nodes[j]);
                }
        }
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

    /// <summary>A scene's page: a column the size of the surface, padded, holding what <paramref name="build"/> makes.</summary>
    private static Box Page(BibuiRuntime rt, float pad, Action build)
    {
        var page = rt.Column().Sized(Size.Fill, Size.Fill);
        page.Style.Padding = EdgeInsets.All(pad);
        using (rt.Parent(page)) build();
        return page;
    }

    // ── Scenes ───────────────────────────────────────────────────────────────

    internal static IEnumerable<Scene> Scenes()
    {
        yield return new Scene("button-grid-hover", 900, 520, 1f, 90, (rt, renderer) =>
        {
            UiNode Build()
            {
                var kids = new UiNode[100];
                for (int i = 0; i < kids.Length; i++)
                    kids[i] = rt.Button($"b{i}", $"Item {i}", ghost: i % 3 == 1, disabled: i % 17 == 16, size: ComponentSize.Sm).Node;
                return Page(rt, 8f, () => Grid(rt, kids, 10, 4f));
            }
            return (Build, f =>
            {
                var (x, y) = Wander(f, 900, 520);
                Move(rt, x, y);
                if (f % 11 == 5) Press(rt, x, y);
                if (f % 11 == 7) Release(rt, x, y);
            });
        });

        yield return new Scene("button-grid-hover-2x", 450, 300, 2f, 60, (rt, renderer) =>
        {
            UiNode Build()
            {
                var kids = new UiNode[30];
                for (int i = 0; i < kids.Length; i++) kids[i] = rt.Button($"b{i}", $"B{i}", ghost: i % 2 == 0, size: ComponentSize.Sm).Node;
                return Page(rt, 6f, () => Grid(rt, kids, 6, 3f));
            }
            return (Build, f => { var (x, y) = Wander(f, 450, 300); Move(rt, x, y); });
        });

        yield return new Scene("retained-buttons", 900, 400, 1f, 120, (rt, renderer) =>
        {
            var clicks = new int[32];
            UiNode Build()
            {
                var kids = new UiNode[clicks.Length];
                for (int i = 0; i < kids.Length; i++)
                {
                    var b = rt.Button($"r{i}", clicks[i] == 0 ? $"Button {i}" : $"Clicked {clicks[i]}",
                        ghost: i % 4 == 1, disabled: i % 9 == 8, size: i % 2 == 0 ? ComponentSize.Sm : ComponentSize.Md);
                    if (b.Clicked) clicks[i]++;
                    kids[i] = b.Node;
                }
                return Page(rt, 10f, () => Grid(rt, kids, 8, 6f));
            }
            return (Build, f =>
            {
                var (x, y) = Wander(f, 900, 400);
                Move(rt, x, y);
                if (f % 13 == 3) Press(rt, x, y);
                if (f % 13 == 5) Release(rt, x, y);
            });
        });

        yield return new Scene("switches", 500, 360, 1f, 100, (rt, renderer) =>
        {
            var on = new bool[6];
            UiNode Build()
            {
                return Page(rt, 20f, () =>
                {
                    for (int i = 0; i < 6; i++)
                    {
                        if (i > 0) rt.Gap(14f);
                        var s = rt.Switch($"sw{i}", on[i], label: on[i] ? $"Option {i} on" : $"Option {i}", disabled: i == 5 && !on[0]);
                        if (s.Clicked) on[i] = !on[i];
                    }
                });
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

        yield return new Scene("typing", 600, 380, 1f, 110, (rt, renderer) =>
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
                field.Sized(Size.Fill, Size.Px(36f));
                BibuiWidget line;
                using (rt.Parent(field))
                    line = rt.TextField(id, text, edit, focus == index, placeholder: placeholder);
                if (line.Pressed) focus = index;
                return field;
            }
            UiNode Build()
            {
                return Page(rt, 24f, () =>
                {
                    Bold(rt, "Account details");
                    rt.Gap(16f);
                    Field("name", name, nameEdit, 0, "Full name");
                    rt.Gap(16f);
                    Field("other", other, otherEdit, 1, "Account ID");
                    rt.Gap(16f);
                    rt.Text($"{name.Length} chars typed", ComponentSize.Sm, TextColor.Secondary);
                });
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

        yield return new Scene("app-state", 600, 420, 1f, 80, (rt, renderer) =>
        {
            int frame = 0;
            string[] palette = ["#ef4444", "#3b82f6", "#22c55e", "#eab308", "#a855f7"];
            UiNode Build()
            {
                int f = frame;
                return Page(rt, 16f, () =>
                {
                    if (f % 2 == 0) Bold(rt, $"Tick {f}"); else rt.Text($"Tick {f}");
                    rt.Gap(10f);
                    using (rt.Parent(rt.Row()))
                        for (int i = 0; i < 6; i++)
                        {
                            if (i > 0) rt.Gap(8f);
                            var sw = rt.Box().Sized(Size.Px(60f), Size.Px(40f));
                            sw.Style.CornerRadius = 8f;
                            sw.Style.BackgroundColor = UiColor.Hex(palette[(f / (i + 1) + i) % palette.Length]);
                            sw.Style.BorderColor = UiColor.Hex(i == f % 6 ? "#ffffff" : "#00000000");
                            sw.Style.BorderWidth = 2f;
                        }
                    rt.Gap(10f);
                    if (f % 14 < 7) { Badge.Build(rt, "Notice"); rt.Gap(10f); }
                    // A list that gains an entry every ten frames, sometimes with one pinned in front.
                    using (rt.Parent(rt.Column()))
                    {
                        if ((f / 10) % 2 == 1) rt.Text("Pinned");
                        for (int i = 0; i < 2 + f / 10; i++) rt.Text($"Entry {i}");
                    }
                });
            }
            return (Build, f => frame = f);
        });

        yield return new Scene("runtime-animate", 600, 300, 1f, 90, (rt, renderer) =>
        {
            int frame = 0;
            UiNode Build()
            {
                float w = rt.Animate("bar", "width", (frame / 25) % 2 == 0 ? 60f : 420f, rt.Rate(8f));
                float a = rt.Animate("bar", "alpha", (frame / 30) % 2 == 0 ? 1f : 0.3f, rt.Rate(6f));
                return Page(rt, 20f, () =>
                {
                    var bar = rt.Box().Sized(Size.Px(w), Size.Px(30f));
                    bar.Style.CornerRadius = 6f;
                    bar.Style.BackgroundColor = UiColor.Hex(new Rgba(59, 130, 246, (byte)Math.Clamp((int)(a * 255f), 0, 255)).ToString());
                    rt.Gap(12f);
                    using (rt.Parent(rt.Row()))
                    {
                        rt.Gap(w / 2f);
                        var dot = rt.Box().Sized(Size.Px(40f), Size.Px(40f));
                        dot.Style.CornerRadius = 20f;
                        dot.Style.BackgroundColor = UiColor.Hex("#22c55e");
                    }
                    rt.Gap(12f);
                    rt.Text("Animated by runtime.Animate");
                });
            }
            return (Build, f => frame = f);
        });

        yield return new Scene("overlays", 640, 420, 1f, 100, (rt, renderer) =>
        {
            int frame = 0;
            UiNode Build()
            {
                bool popover = (frame / 15) % 2 == 1;
                bool dialog = frame >= 30 && frame < 75;
                float shown = rt.Animate("dialog", "shown", dialog ? 1f : 0f, rt.Rate(10f));
                var trigger = rt.Button("pop-trigger", "Details");
                var tipped = rt.Button("tip-trigger", "Hover for a tip", ghost: true);
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
                    using (rt.FontFace(weight: FontWeight.Bold)) rt.Text("Dialog");
                    rt.Gap(12f);
                    rt.Button("dialog-ok", "OK");
                    d.End();
                }
                var grid = new UiNode[16];
                for (int i = 0; i < grid.Length; i++) grid[i] = rt.Button($"u{i}", $"Under {i}").Node;
                return Page(rt, 16f, () =>
                {
                    using (rt.Parent(rt.Row()))
                    {
                        UiNode.PushIntoParent(rt.ParentInScope, trigger.Node);
                        rt.Gap(12f);
                        UiNode.PushIntoParent(rt.ParentInScope, tipped.Node);
                    }
                    rt.Gap(10f);
                    Grid(rt, grid, 4, 6f);
                });
            }
            return (Build, f => { frame = f; var (x, y) = Wander(f, 640, 420); Move(rt, x, y); });
        });

        yield return new Scene("scroll", 500, 400, 1f, 70, (rt, renderer) =>
        {
            UiNode Build()
            {
                var view = rt.BoxFromId("list", InteractionKinds.ViewScrollY | InteractionKinds.ClampY | InteractionKinds.Clip);
                rt.InteractionsFor(view);
                view.ChildLayoutAxis = LayoutAxis.Y;
                view.Sized(Size.Fill, Size.Fill);
                rt.BeginParent(view);
                for (int i = 0; i < 60; i++)
                {
                    if (i > 0) rt.Gap(4f);
                    rt.Button($"row{i}", $"Row {i}", ghost: i % 2 == 1);
                }
                rt.EndParent(view);
                return Page(rt, 8f, () => UiNode.PushIntoParent(rt.ParentInScope, view));
            }
            return (Build, f =>
            {
                Move(rt, 100, 100 + (f * 13) % 250);
                if (f % 10 == 4) Wheel(rt, 100, 200, -3f);
                if (f % 10 == 8) Wheel(rt, 100, 200, 1f);
            });
        });

        yield return new Scene("resize", 600, 400, 1f, 60, (rt, renderer) =>
        {
            UiNode Build()
            {
                var kids = new UiNode[36];
                for (int i = 0; i < kids.Length; i++) kids[i] = rt.Button($"z{i}", $"Z{i}").Node;
                return Page(rt, 8f, () => Grid(rt, kids, 6, 4f));
            }
            return (Build, f =>
            {
                if (f == 20) rt.ResizeSurface(700, 450, 1f);
                if (f == 40) rt.ResizeSurface(500, 360, 1f);
                var (x, y) = Wander(f, 480, 340);
                Move(rt, x, y);
            });
        });

        yield return new Scene("shadows-gradients", 700, 460, 1f, 100, (rt, renderer) =>
        {
            UiNode Card(int i)
            {
                var card = rt.Column().Sized(Size.Px(190f), Size.Px(150f));
                using (rt.Parent(card))
                {
                    rt.Button($"c{i}a", "Open");
                    rt.Gap(6f);
                    rt.Button($"c{i}b", "Share", ghost: true);
                }
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
                return Page(rt, 24f, () =>
                {
                    Grid(rt, cards, 3, 24f);
                    var glass = rt.Box().Sized(Size.Px(220f), Size.Px(90f)).At(230f, 150f);
                    glass.Style.CornerRadius = 10f;
                    glass.Style.BackdropBlur = 8f;
                    glass.Style.BackgroundColor = UiColor.Hex("#ffffff22");
                });
            }
            return (Build, f =>
            {
                var (x, y) = Wander(f, 700, 460);
                Move(rt, x, y);
                if (f % 9 == 4) Press(rt, x, y);
                if (f % 9 == 6) Release(rt, x, y);
            });
        });

        yield return new Scene("spinners-charts", 640, 360, 1f, 70, (rt, renderer) =>
        {
            int frame = 0;
            UiNode Build()
            {
                var data = Enumerable.Range(0, 12).Select(i => 20f + 15f * MathF.Sin((frame + i * 3) / 5f) + i).ToArray();
                var chart = new Box
                {
                    Draw = (_, ctx) =>
                    {
                        var r = ctx.Logical;
                        Span<(float X, float Y)> pts = stackalloc (float, float)[data.Length];
                        for (int i = 0; i < data.Length; i++)
                            pts[i] = (r.Left + (r.Right - r.Left) * i / (data.Length - 1), r.Bottom - (r.Bottom - r.Top) * data[i] / 60f);
                        ctx.DrawFilledPathGradient(pts, r.Top, r.Bottom, new Rgba(59, 130, 246, 90));
                        ctx.DrawPolyline(pts, new Rgba(59, 130, 246), 2f);
                        ctx.DrawEdge(r.Left, r.Bottom, r.Right, r.Bottom, 1f, DrawContext.Color("#3f3f46"), dashed: true);
                    },
                }.Sized(Size.Px(300f), Size.Px(120f));
                return Page(rt, 16f, () =>
                {
                    using (rt.Parent(rt.Row()))
                    {
                        rt.Spinner();
                        rt.Gap(16f);
                        Progress.Build(rt, frame % 100).Sized(Size.Px(200f));
                        rt.Gap(16f);
                        rt.Icon(frame % 20 < 10 ? "check" : "x", 20f);
                    }
                    rt.Gap(12f);
                    UiNode.PushIntoParent(rt.ParentInScope, chart);
                });
            }
            return (Build, f => frame = f);
        });

        yield return new Scene("opacity", 600, 300, 1f, 80, (rt, renderer) =>
        {
            int frame = 0;
            UiNode Build()
            {
                float o = rt.Animate("fade", "o", (frame / 20) % 2 == 0 ? 1f : 0.25f, rt.Rate(8f));
                return Page(rt, 12f, () =>
                {
                    var panel = rt.Row();
                    panel.Style = new Style { Opacity = o, Padding = EdgeInsets.All(8f), BackgroundColor = UiColor.Hex("#27272a"), CornerRadius = 8f };
                    using (rt.Parent(panel))
                        for (int i = 0; i < 4; i++)
                        {
                            if (i > 0) rt.Gap(6f);
                            rt.Button($"o{i}", $"Faded {i}");
                        }
                });
            }
            return (Build, f => { frame = f; var (x, y) = Wander(f, 600, 300); Move(rt, x, y); });
        });

        yield return new Scene("custom-draw", 500, 300, 1f, 40, (rt, renderer) =>
        {
            int frame = 0;
            UiNode Build()
            {
                var canvas = new Box
                {
                    Draw = (_, ctx) =>
                    {
                        // Cached geometry; scratch geometry; and a path the draw builds and drops.
                        float x = 20 + (frame * 9) % 300;
                        Span<(float X, float Y)> pts = stackalloc (float, float)[10];
                        for (int i = 0; i < 10; i++) pts[i] = (10f + i * 40f, 60f + 30f * MathF.Sin((frame + i) / 3f));
                        ctx.DrawPolyline(pts, new Rgba(249, 115, 22), 2f);
                        ctx.DrawArc(x, 40, 16, 3, new Rgba(56, 189, 248), sweepAngle: 90 + frame * 7 % 270);
                        if (frame % 10 >= 5)
                        {
                            var own = new VectorPath();
                            own.MoveTo(400, 10);
                            own.LineTo(460, 100);
                            own.LineTo(380, 100);
                            own.Close();
                            ctx.DrawPath(own, Paint.Filled(new Rgba(234, 179, 8)));
                        }
                    },
                }.Sized(Size.Fill, Size.Px(120f));
                return Page(rt, 12f, () =>
                {
                    UiNode.PushIntoParent(rt.ParentInScope, canvas);
                    rt.Gap(8f);
                    rt.Button("cd", "Hover me");
                });
            }
            return (Build, f => { frame = f; Move(rt, 60 + (f * 5) % 120, 150); });
        });

        // Every draw the scenes above never make, by a box's own draw: a texture
        // (drawn still and moving), a texture whose pixels are updated every frame,
        // points, two runs of text (one changing), and shapes under a changing
        // scale and a translation.
        yield return new Scene("canvas-calls", 520, 300, 1f, 60, (rt, renderer) =>
        {
            int frame = 0;
            TextureHandle image = default, video = default;
            var pixels = new byte[32 * 32 * 4];
            UiNode Build()
            {
                var canvas = new Box
                {
                    Draw = (_, ctx) =>
                    {
                        // The scene is at scale 1, so logical and physical px agree.
                        var white = Paint.Filled(new Rgba(255, 255, 255));
                        if (image.IsNone) image = renderer.CreateTexture(TextureFormat.RGBA8, TextureKind.Static, 16, 16, Checkerboard(16));

                        ctx.DrawTexture(image, new RectF(20, 20, 84, 84), white);
                        float x = 120 + frame / 4 % 10 * 8;
                        ctx.DrawTexture(image, new RectF(x, 20, x + 48, 68), white);

                        // The same array, rewritten in place: its content is what is drawn.
                        for (int i = 0; i < 32 * 32; i++)
                        {
                            pixels[i * 4] = (byte)(i * 7 + frame * 5);
                            pixels[i * 4 + 1] = (byte)(i / 32 * 8);
                            pixels[i * 4 + 2] = (byte)(frame * 11);
                            pixels[i * 4 + 3] = 255;
                        }
                        // One handle, its pixels updated every frame: damage must see the change.
                        if (video.IsNone) video = renderer.CreateTexture(TextureFormat.RGBA8, TextureKind.Stream, 32, 32, pixels);
                        else renderer.UpdateTexture(video, pixels);
                        ctx.DrawTexture(video, new RectF(260, 20, 324, 84), white);

                        Span<Vector2> pts = stackalloc Vector2[8];
                        for (int i = 0; i < 8; i++) pts[i] = new(30 + i * 20, 120 + (i == frame % 8 ? 6 : 0));
                        ctx.DrawPoints(PointMode.Points, pts, Paint.Stroked(new Rgba(234, 179, 8), 4f));

                        var font = rt.FontInScope();
                        ctx.DrawText($"frame {frame / 6}", 30, 156, font, new Rgba(255, 255, 255));
                        ctx.DrawText("glyphs", 200, 156, font, new Rgba(56, 189, 248));

                        ctx.PushTransform(Matrix3x2.CreateScale(1f + frame / 10 % 3 * 0.25f, 1f) * Matrix3x2.CreateTranslation(400, 120));
                        ctx.DrawRect(new RectF(0, 0, 40, 30), 6, Paint.Filled(new Rgba(34, 197, 94)));
                        ctx.PopTransform();
                        // The same paint for what follows, pushed once.
                        ctx.PushPaint(Paint.Filled(new Rgba(239, 68, 68)));
                        ctx.PushTransform(Matrix3x2.CreateTranslation(400, 180 + frame % 5));
                        ctx.DrawRect(new RectF(0, 0, 40, 30), 6);
                        ctx.PopTransform();
                        ctx.PopPaint();
                    },
                }.Sized(Size.Px(520f), Size.Px(240f));
                return Page(rt, 0f, () =>
                {
                    UiNode.PushIntoParent(rt.ParentInScope, canvas);
                    rt.Gap(8f);
                    rt.Button("cc", "Hover me");
                });
            }
            return (Build, f => { frame = f; Move(rt, 40 + (f * 7) % 200, 270); });
        });

        // Groups: blend modes and colour matrices, some held still while a
        // square moves through and past them. A Src layer and a matrix that adds
        // alpha write all of their clip, transparent pixels included, so a
        // repaint must hold them to its rect.
        yield return new Scene("groups", 560, 260, 1f, 70, (rt, renderer) =>
        {
            int frame = 0;
            UiNode Build()
            {
                var canvas = new Box
                {
                    Draw = (_, ctx) =>
                    {
                        float x = 10 + (frame * 7) % 500;
                        ctx.DrawRect(new RectF(0, 0, 560, 200), 0, Paint.Filled(new Rgba(40, 60, 90)));
                        ctx.DrawRect(new RectF(x, 60, x + 30, 90), 4, Paint.Filled(new Rgba(250, 200, 60)));

                        ctx.PushBlendMode(BlendMode.Multiply);
                        ctx.DrawRect(new RectF(20, 20, 120, 120), 8, Paint.Filled(new Rgba(255, 120, 120)));
                        ctx.DrawRect(new RectF(60, 40, 160, 140), 8, Paint.Filled(new Rgba(120, 255, 120, 180)));
                        ctx.PopBlendMode();

                        ctx.PushClip(new RectF(180, 20, 300, 140));
                        ctx.PushBlendMode(BlendMode.Src);
                        ctx.DrawRect(new RectF(200, 40, 260, 100), 6, Paint.Filled(new Rgba(80, 160, 255, 128)));
                        ctx.PopBlendMode();
                        ctx.PopClip();

                        ctx.PushClip(new RectF(320, 20, 440, 140));
                        ctx.PushColorMatrix([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0.3f, 0, 0, 0, 1, 0.25f]);
                        ctx.DrawRect(new RectF(340, 40, 400, 100), 6, Paint.Filled(new Rgba(255, 80, 80)));
                        ctx.PopColorMatrix();
                        ctx.PopClip();

                        float sat = frame / 20 % 2 == 0 ? 0f : 1f;
                        float ir = 0.2126f * (1 - sat), ig = 0.7152f * (1 - sat), ib = 0.0722f * (1 - sat);
                        ctx.PushColorMatrix([ir + sat, ig, ib, 0, 0, ir, ig + sat, ib, 0, 0, ir, ig, ib + sat, 0, 0, 0, 0, 0, 1, 0]);
                        ctx.DrawRect(new RectF(460, 30, 540, 110), 10, Paint.Filled(new Rgba(40, 200, 120)));
                        ctx.PushBlendMode(BlendMode.Screen);
                        ctx.DrawRect(new RectF(480, 60, 530, 130), 10, Paint.Filled(new Rgba(200, 40, 200)));
                        ctx.PopBlendMode();
                        ctx.PopColorMatrix();
                    },
                }.Sized(Size.Px(560f), Size.Px(200f));
                return Page(rt, 0f, () =>
                {
                    UiNode.PushIntoParent(rt.ParentInScope, canvas);
                    rt.Gap(8f);
                    rt.Button("gb", "Hover me");
                });
            }
            return (Build, f => { frame = f; Move(rt, 40 + (f * 7) % 200, 225); });
        });

        // The dense case damage tracking is for: 600 solid buttons filling a HiDPI surface.
        yield return new Scene("solid-grid-2x", 900, 700, 2f, 40, (rt, renderer) =>
        {
            UiNode Build()
            {
                Box column = rt.Box();
                column.ChildLayoutAxis = LayoutAxis.Y;
                column.Sized(Size.Fill, Size.Fill);
                column.Style.Padding = EdgeInsets.All(8f);
                using (rt.Parent(column))
                {
                    for (int start = 0; start < 600; start += 20)
                    {
                        if (start > 0) rt.Gap(4f);
                        Box row = rt.Box();
                        row.ChildLayoutAxis = LayoutAxis.X;
                        row.Sized(Size.Fill);
                        row.SizeY = SemanticSize.ChildrenSum();
                        using (rt.Parent(row))
                        {
                            for (int i = start; i < start + 20; i++)
                            {
                                if (i > start) rt.Gap(4f);
                                rt.Button($"s{i}", $"Item {i}").Node.Sized(Size.Fill);
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
