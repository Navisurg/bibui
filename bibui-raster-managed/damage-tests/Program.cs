// Damage tracking in the managed raster backend. The runtime builds, lays out
// and paints every frame whole; ManagedRenderer.DamageTracking rasterises
// only what changed. DamageOracle.cs checks that byte for byte over scripted
// apps; the tests above it pin the policy a host relies on.
//
//   dotnet run -c Release --project bibui-raster-managed/damage-tests [filter]
using Bibui.Core;
using Bibui.Core.Draw;
using Surface = Bibui.Raster.Managed.Surface;
using Bibui.Raster.Managed.Backend;
using Bibui.Core.Runtime;
using Bibui.Core.Theming;

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

// One frame: built, then drawn onto target by renderer.
static void Frame(BibuiRuntime rt, ManagedRenderer renderer, Surface target)
{
    rt.Step();
    renderer.Draw(rt.DrawList, target);
}

// A grid of buttons, a tracking managed renderer, and a surface to draw it into.
static (BibuiRuntime Rt, ManagedRenderer Backend, Surface Target) Rig()
{
    var backend = new ManagedRenderer { DamageTracking = true };
    var rt = BibuiRuntime.Create();
    rt.TargetHz = 60f;
    rt.SetBuild(() =>
    {
        var page = rt.ColumnFill().Padded(EdgeInsets.All(8f));
        using (rt.PushParent(page))
            for (int r = 0; r < 3; r++)
            {
                if (r > 0) rt.Gap(6f);
                using (rt.PushParent(rt.RowFit()))
                    for (int c = 0; c < 4; c++)
                    {
                        if (c > 0) rt.Gap(6f);
                        rt.Button($"d{r * 4 + c}", $"Item {r * 4 + c}");
                    }
            }
        return page;
    }, ThemeLoader.BibuiDark(), new SurfaceOptions { Width = 400, Height = 160, ScaleFactor = 1f });
    return (rt, backend, new Surface(400, 160));
}

Run("A surface the renderer has not drawn, a new background and a resize are painted whole", () =>
{
    var (rt, backend, target) = Rig();
    Frame(rt, backend, target);
    Eq("first frame", backend.LastFullRepaintReason);
    var other = new Surface(400, 160);
    Frame(rt, backend, other);
    Eq("surface holds an unknown frame", backend.LastFullRepaintReason);
    Frame(rt, backend, target);
    IsTrue(backend.LastRepaint is not null, "a surface two frames old is repainted in part");
    for (int i = 0; i < 30; i++) Frame(rt, backend, target);
    IsTrue(backend.LastRepaint is { Count: 0 }, "an idle frame repaints nothing");

    rt.SetTheme(ThemeLoader.BibuiLight());
    Frame(rt, backend, target);
    Eq("background", backend.LastFullRepaintReason);

    rt.ResizeSurface(300, 160, 1f);
    var smaller = new Surface(300, 160);
    Frame(rt, backend, smaller);
    Eq("resize", backend.LastFullRepaintReason);
});

Run("Turning tracking off and on again forgets every surface", () =>
{
    var (rt, backend, target) = Rig();
    Frame(rt, backend, target);
    backend.DamageTracking = false;
    Frame(rt, backend, target);
    backend.DamageTracking = true;
    Frame(rt, backend, target);
    IsNull(backend.LastRepaint, "the surface was painted untracked in between");
});

Run("A retained box's draw callback follows a theme change, as a whole repaint does", () =>
{
    (Action, Action<ThemeTokens>, Func<uint[]>) Rig2(bool tracked)
    {
        var backend = new ManagedRenderer { DamageTracking = tracked };
        var rt = BibuiRuntime.Create();
        rt.TargetHz = 60f;
        rt.SetBuild(() =>
        {
            var page = rt.ColumnFill().Padded(EdgeInsets.All(8f));
            using var inPage = rt.PushParent(page);
            var chart = rt.BoxFromId("chart", InteractionKinds.LeftPress);
            chart.Px(120f, 60f);
            chart.Draw = (_, ctx) =>
            {
                var r = ctx.Logical;
                ctx.DrawRRect(r.Left, r.Top, r.Width, r.Height, 6f, ctx.Resolve(UiColor.Surface), ctx.Resolve(UiColor.Border), 1f);
                ctx.DrawEdge(r.Left + 8f, r.MidY, r.Right - 8f, r.MidY, 2f, ctx.Resolve(UiColor.Accent));
            };
            return page;
        }, ThemeLoader.BibuiDark(), new SurfaceOptions { Width = 160, Height = 80, ScaleFactor = 1f });
        var target = new Surface(160, 80);
        return (() => Frame(rt, backend, target), rt.SetTheme, () => { var px = new uint[160 * 80]; target.CopyTo(px); return px; });
    }

    var (dmg, dmgTheme, dpx) = Rig2(tracked: true);
    var (full, fullTheme, fpx) = Rig2(tracked: false);
    dmg(); full();
    dmg(); full();
    var before = dpx();
    IsTrue(before.AsSpan().SequenceEqual(fpx()), "tracked and whole agree before the change");

    dmgTheme(ThemeLoader.BibuiLight()); fullTheme(ThemeLoader.BibuiLight());
    dmg(); full();
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
