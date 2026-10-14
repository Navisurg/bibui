// Damage tracking in the managed raster backend. The runtime builds, lays out
// and paints every frame whole; ManagedRasterBackend.DamageTracking rasterises
// only what changed. DamageOracle.cs checks that byte for byte over scripted
// apps; the tests above it pin the policy a host relies on.
//
//   dotnet run -c Release --project bibui-raster-managed/damage-tests [filter]
using Bibui;
using Bibui.Raster;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using Bibui.Theming;

int passed = 0, failed = 0;
string? filter = args.Length > 0 ? args[0] : null;

void Run(string name, Action test)
{
    if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
    try
    {
        test();
        Console.WriteLine($"  [PASS] {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] {name}");
        Console.WriteLine($"         {ex.Message}");
        failed++;
    }
}

static void IsTrue(bool value, string message)
{
    if (!value) throw new Exception(message);
}

static void IsNull(object? value, string message)
{
    if (value is not null) throw new Exception(message);
}

static void Eq(string expected, string? actual)
{
    if (expected != actual) throw new Exception($"Expected \"{expected}\" but got \"{actual}\"");
}

// A grid of buttons on a tracking managed backend, and a surface to draw it into.
static (BibuiRuntime Rt, ManagedRasterBackend Backend, IRasterSurface Target) Rig()
{
    var backend = new ManagedRasterBackend { DamageTracking = true };
    var rt = new BibuiRuntime(backend) { TargetHz = 60f };
    rt.Mount(() =>
    {
        var rows = new List<Arrange.Item>();
        for (int r = 0; r < 3; r++)
        {
            var cells = new List<Arrange.Item>();
            for (int c = 0; c < 4; c++)
            {
                if (c > 0) cells.Add(Arrange.Space(6f));
                cells.Add(Button.Build(rt, $"d{r * 4 + c}", BibuiStr.FromStrLit(System.Text.Encoding.UTF8.GetBytes($"Item {r * 4 + c}"))).Node);
            }
            if (r > 0) rows.Add(Arrange.Space(6f));
            rows.Add(Arrange.Row([.. cells]));
        }
        return Arrange.Column([.. rows]).FillX().FillY().Padded(EdgeInsets.All(8f));
    }, ThemeLoader.BibuiDark(), new SurfaceOptions { Width = 400, Height = 160, ScaleFactor = 1f });
    return (rt, backend, backend.CreateSurface(400, 160));
}

Run("A surface the backend has not drawn, a new background and a resize are painted whole", () =>
{
    var (rt, backend, target) = Rig();
    rt.RenderTo(target);
    Eq("first frame", backend.LastFullRepaintReason);
    using var other = backend.CreateSurface(400, 160);
    rt.RenderTo(other);
    Eq("surface holds an unknown frame", backend.LastFullRepaintReason);
    rt.RenderTo(target);
    IsTrue(backend.LastRepaint is not null, "a surface two frames old is repainted in part");
    for (int i = 0; i < 30; i++) rt.RenderTo(target);
    IsTrue(backend.LastRepaint is { Count: 0 }, "an idle frame repaints nothing");

    rt.SetTheme(ThemeLoader.BibuiLight());
    rt.RenderTo(target);
    Eq("background", backend.LastFullRepaintReason);

    rt.ResizeSurface(300, 160, 1f);
    using var smaller = backend.CreateSurface(300, 160);
    rt.RenderTo(smaller);
    Eq("resize", backend.LastFullRepaintReason);
});

Run("Turning tracking off and on again forgets every surface", () =>
{
    var (rt, backend, target) = Rig();
    rt.RenderTo(target);
    backend.DamageTracking = false;
    rt.RenderTo(target);
    backend.DamageTracking = true;
    rt.RenderTo(target);
    IsNull(backend.LastRepaint, "the surface was painted untracked in between");
});

Run("A retained box's draw callback follows a theme change, as a whole repaint does", () =>
{
    (BibuiRuntime, IRasterSurface, Func<byte[]>) Rig2(bool tracked)
    {
        var backend = new ManagedRasterBackend { DamageTracking = tracked };
        var rt = new BibuiRuntime(backend) { TargetHz = 60f };
        rt.Mount(() =>
        {
            var chart = rt.BoxFromId("chart", InteractionKinds.LeftPress);
            chart.Px(120f, 60f);
            chart.Draw = ctx =>
            {
                var r = ctx.Logical;
                ctx.DrawRRect(r.Left, r.Top, r.Width, r.Height, 6f, ctx.Resolve(UiColor.Surface), ctx.Resolve(UiColor.Border), 1f);
                ctx.DrawEdge(r.Left + 8f, r.MidY, r.Right - 8f, r.MidY, 2f, ctx.Resolve(UiColor.Accent));
            };
            return Arrange.Column(chart).FillX().FillY().Padded(EdgeInsets.All(8f));
        }, ThemeLoader.BibuiDark(), new SurfaceOptions { Width = 160, Height = 80, ScaleFactor = 1f });
        var target = backend.CreateSurface(160, 80);
        return (rt, target, () => { var px = new byte[160 * 80 * 4]; target.ReadPixels(px, 160, 80); return px; });
    }

    var (dmg, dt, dpx) = Rig2(tracked: true);
    var (full, ft, fpx) = Rig2(tracked: false);
    dmg.RenderTo(dt); full.RenderTo(ft);
    dmg.RenderTo(dt); full.RenderTo(ft);
    var before = dpx();
    IsTrue(before.AsSpan().SequenceEqual(fpx()), "tracked and whole agree before the change");

    dmg.SetTheme(ThemeLoader.BibuiLight()); full.SetTheme(ThemeLoader.BibuiLight());
    dmg.RenderTo(dt); full.RenderTo(ft);
    var after = dpx();
    IsTrue(!after.AsSpan().SequenceEqual(before), "the callback's colours followed the theme");
    IsTrue(after.AsSpan().SequenceEqual(fpx()), "and the tracked frame equals a whole repaint");
});

foreach (var scene in DamageOracle.Scenes())
    foreach (int ring in new[] { 1, 3 })
        Run($"Oracle: {scene.Name}, ring {ring}: every frame equals a whole repaint", () =>
        {
            var r = DamageOracle.Run(scene, ring);
            Console.WriteLine($"         {r.Partial}/{r.Frames} frames repainted in part");
            IsTrue(r.Mismatches == 0, $"{r.Mismatches}/{r.Frames} frames differ; first at {r.FirstMismatch}");
            IsTrue(r.Partial > 0, "damage tracking repainted every frame whole");
        });

Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
