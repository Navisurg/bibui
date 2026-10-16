using System.Runtime.InteropServices;
using Bibui.Platform.Wayland;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using Bibui.Raster.Skia;

namespace Bibui.WaylandTests;

/// <summary>
/// HiDPI and cursors without a window: scale arithmetic in 120ths, which scale wins, a
/// scale change reallocating the buffer pool, the cursor-shape mapping and
/// when a cursor is set. One live probe draws a 1.25× frame into a real wl_shm
/// buffer and compares it with the same frame drawn packed, creating no surface.
/// </summary>
internal static unsafe class ScaleTests
{
    private sealed class Buf(int w, int h)
    {
        public readonly int W = w, H = h;
    }

    private sealed class Alloc : IBufferAllocator<Buf>
    {
        public readonly List<Buf> Live = [];
        public readonly List<Buf> Destroyed = [];
        public Buf Allocate(int width, int height) { var b = new Buf(width, height); Live.Add(b); return b; }
        public void Destroy(Buf buffer) { Live.Remove(buffer); Destroyed.Add(buffer); }
    }

    private sealed class Sink : ICursorSink
    {
        public readonly List<(uint Serial, CursorShape Shape)> Shown = [];
        public void Show(uint serial, CursorShape shape) => Shown.Add((serial, shape));
    }

    public static void Register(Harness t)
    {
        // ── Arithmetic ───────────────────────────────────────────────────────

        t.Run("scale: 120ths to physical size", () =>
        {
            Assert.Eq(1.25f, SurfaceScale.ToFactor(150), "150/120");
            Assert.Eq((1000, 600), WaylandPlatformHost.FramebufferSize(800, 480, 150), "800x480 at 1.25");
            Assert.Eq((1600, 960), WaylandPlatformHost.FramebufferSize(800, 480, 240), "integer 2");
            Assert.Eq((800, 480), WaylandPlatformHost.FramebufferSize(800, 480, 120), "1");
            Assert.Eq((3840, 2160), WaylandPlatformHost.FramebufferSize(2048, 1152, 225), "1.875");
        });

        t.Run("scale: odd sizes round half away from zero", () =>
        {
            Assert.Eq(1251, SurfaceScale.Physical(1001, 150), "1251.25 → 1251");
            Assert.Eq(253, SurfaceScale.Physical(202, 150), "252.5 → 253, not banker's 252");
            Assert.Eq(126, SurfaceScale.Physical(101, 150), "126.25 → 126");
            Assert.Eq(1, SurfaceScale.Physical(1, 150), "1.25 → 1");
            Assert.Eq(3, SurfaceScale.Physical(2, 150), "2.5 → 3");
        });

        t.Run("scale: float rounding agrees with exact 120ths arithmetic everywhere", () =>
        {
            for (uint n = 120; n <= 480; n++)
                for (int l = 1; l <= 4000; l++)
                {
                    int exact = (int)((l * n + 60) / 120); // half away from zero, in integers
                    int got = SurfaceScale.Physical(l, n);
                    if (got != exact) throw new Exception($"{l} at {n}/120: {got}, expected {exact}");
                    int back = SurfaceOptions.LogicalSize(got, SurfaceScale.ToFactor(n));
                    if (back != l) throw new Exception($"{l} at {n}/120 → {got} → {back}: does not round-trip");
                }
        });

        t.Run("scale: ResizeSurface at 1.25 keeps an odd logical size, and the frame is the buffer's size", () =>
        {
            var runtime = BibuiRuntime.Create();
            runtime.Mount(() => Bibui.Arrange.FillY(Bibui.Arrange.FillX(runtime.Box())),
                new Bibui.SurfaceOptions { Width = 100, Height = 100, ScaleFactor = 1f });
            var (w, h) = WaylandPlatformHost.FramebufferSize(1001, 202, 150);
            runtime.ResizeSurface(w, h, SurfaceScale.ToFactor(150));
            Assert.Eq((1001, 202), (runtime.SurfaceWidth, runtime.SurfaceHeight), "logical, not truncated to 1000");
            runtime.EndBuild();
            Assert.Eq((1251, 253), (SurfaceOptions.PhysicalSize(runtime.SurfaceWidth, runtime.ScaleFactor),
                SurfaceOptions.PhysicalSize(runtime.SurfaceHeight, runtime.ScaleFactor)), "the frame's physical size");
        });

        // ── Which scale ──────────────────────────────────────────────────────

        t.Run("scale: fractional beats preferred_buffer_scale beats outputs beats 1", () =>
        {
            var s = new ScaleState();
            Assert.Eq(120u, s.Wanted120, "nothing known");
            s.OnOutputScale(1, 2);
            Assert.Eq(120u, s.Wanted120, "an output the surface is not on");
            s.OnEnter(1);
            Assert.Eq(240u, s.Wanted120, "the output it is on");
            s.OnOutputScale(2, 3);
            s.OnEnter(2);
            Assert.Eq(360u, s.Wanted120, "the largest of two");
            s.OnLeave(2);
            Assert.Eq(240u, s.Wanted120, "left one");
            s.OnPreferredBufferScale(1);
            Assert.Eq(120u, s.Wanted120, "preferred_buffer_scale overrides outputs");
            s.OnFractional(150);
            Assert.Eq(150u, s.Wanted120, "fractional overrides both");
            Assert.True(s.IsFractional, "fractional");
            s.OnFractional(0);
            s.OnPreferredBufferScale(0);
            Assert.Eq(240u, s.Wanted120, "nonsense falls back");
            s.OnOutputGone(1);
            Assert.Eq(120u, s.Wanted120, "unplugged");
        });

        t.Run("scale: a change mid-run reallocates the pool at the new size", () =>
        {
            var a = new Alloc();
            var pool = new BufferPool<Buf>(a, depth: 2);
            var (w1, h1) = WaylandPlatformHost.FramebufferSize(801, 601, 120);
            var shown = pool.TryAcquire(w1, h1)!;
            pool.Submitted(shown);
            var spare = pool.TryAcquire(w1, h1)!;
            Assert.Eq(2, a.Live.Count, "two at scale 1");

            // Moved to the 1.25× monitor: same window, new buffer size.
            var (w2, h2) = WaylandPlatformHost.FramebufferSize(801, 601, 150);
            Assert.Eq((1001, 751), (w2, h2), "1001.25 → 1001, 751.25 → 751");
            var next = pool.TryAcquire(w2, h2)!;
            Assert.Eq((1001, 751), (next.W, next.H), "drawn at the new size");
            Assert.True(a.Destroyed.Contains(spare), "the free old-size buffer went at once");
            Assert.True(!a.Destroyed.Contains(shown), "the one on screen waits for its release");
            pool.Released(shown);
            Assert.True(a.Destroyed.Contains(shown), "and goes on release");
            Assert.True(a.Live.All(b => (b.W, b.H) == (1001, 751)), "only new-size buffers left");

            // And back: a full new set at scale 1.
            pool.Submitted(next);
            var back = pool.TryAcquire(w1, h1)!;
            Assert.Eq((w1, h1), (back.W, back.H), "back at scale 1");
            Assert.Eq(1, pool.RetiredCount, "the 1.25 buffer on screen is retired");
        });

        // ── Cursor ───────────────────────────────────────────────────────────

        t.Run("cursor: every CursorShape maps to a cursor-shape-v1 shape and theme names", () =>
        {
            var expected = new Dictionary<CursorShape, WpCursorShapeDeviceV1.Shape>
            {
                [CursorShape.Default] = WpCursorShapeDeviceV1.Shape.Default,
                [CursorShape.Pointer] = WpCursorShapeDeviceV1.Shape.Pointer,
                [CursorShape.Text] = WpCursorShapeDeviceV1.Shape.Text,
                [CursorShape.Crosshair] = WpCursorShapeDeviceV1.Shape.Crosshair,
                [CursorShape.ResizeHorizontal] = WpCursorShapeDeviceV1.Shape.EwResize,
                [CursorShape.ResizeVertical] = WpCursorShapeDeviceV1.Shape.NsResize,
                [CursorShape.ResizeAll] = WpCursorShapeDeviceV1.Shape.Move,
                [CursorShape.NotAllowed] = WpCursorShapeDeviceV1.Shape.NotAllowed,
                [CursorShape.Wait] = WpCursorShapeDeviceV1.Shape.Wait,
            };
            foreach (var shape in Enum.GetValues<CursorShape>())
            {
                Assert.True(expected.ContainsKey(shape), $"{shape} has an expectation");
                Assert.Eq(expected[shape], CursorShapes.ToProtocol(shape), shape.ToString());
                var (css, legacy) = CursorShapes.ThemeNames(shape);
                Assert.True(css.Length > 0 && legacy.Length > 0, $"{shape} theme names");
            }
            Assert.Eq(Enum.GetValues<CursorShape>().Length,
                Enum.GetValues<CursorShape>().Select(CursorShapes.ToProtocol).Distinct().Count(), "no two share a shape");
            Assert.Eq(("text", "xterm"), CursorShapes.ThemeNames(CursorShape.Text), "I-beam");
        });

        t.Run("cursor: set on every enter with its serial, re-set inside on change, never outside", () =>
        {
            var sink = new Sink();
            var c = new CursorState(sink);
            c.Set(CursorShape.Text);
            Assert.Eq(0, sink.Shown.Count, "outside: nothing sent");
            c.Enter(7);
            Assert.Eq((7u, CursorShape.Text), sink.Shown[^1], "enter shows the wanted shape with the enter serial");
            c.Set(CursorShape.Text);
            Assert.Eq(1, sink.Shown.Count, "same shape: not resent");
            c.Set(CursorShape.Pointer);
            Assert.Eq((7u, CursorShape.Pointer), sink.Shown[^1], "a change inside reuses the enter serial");
            c.Leave();
            c.Set(CursorShape.Default);
            Assert.Eq(2, sink.Shown.Count, "left: nothing sent");
            c.Enter(9);
            Assert.Eq((9u, CursorShape.Default), sink.Shown[^1], "the next enter, its own serial");
        });

        t.Run("cursor: the feature takes SetCursor and pointer hooks with no pointer or connection", () =>
        {
            var f = new WaylandScaleAndCursor();
            IPointerCursorHooks hooks = f;
            f.SetCursor(CursorShape.Text);
            hooks.OnPointerEnter(3);
            hooks.OnPointerLeave();
            hooks.DetachPointer();
            Assert.Eq(CursorShape.Text, f.Cursor, "remembered for the next enter");
            Assert.Eq(120u, f.Wanted120, "scale 1 until the compositor says otherwise");
            f.Destroy();
        });

        // ── Live, with no surface ────────────────────────────────────────────

        t.Run("live probe: a 1.25x frame in a real wl_shm buffer equals Render(), byte for byte", () =>
        {
            if (!WaylandPlatformHost.IsSupported) throw new SkipException("no Wayland session");
            Probe();
        });
    }

    private static WlShm _shm;
    private static readonly HashSet<string> _globals = [];

    [UnmanagedCallersOnly]
    private static void OnGlobal(void* data, WlRegistry registry, uint name, byte* iface, uint version)
    {
        var s = WlUtf8.Read(iface)!;
        _globals.Add(s);
        if (s == WlShm.InterfaceName) _shm = registry.Bind<WlShm>(name, 1);
    }

    private static void Probe()
    {
        var display = WaylandNative.wl_display_connect(null);
        Assert.True(display != 0, "connect");
        var registryListener = WlRegistry.Listener.Create(new() { Global = &OnGlobal });
        var bufferListener = WlBuffer.Listener.Create(default);
        BufferPool<ShmBuffer>? pool = null;
        try
        {
            _shm = default;
            _globals.Clear();
            var registry = new WlDisplay(display).GetRegistry();
            registry.AddListener(registryListener);
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "roundtrip");
            Assert.True(!_shm.IsNull, "wl_shm");
            Console.WriteLine($"         compositor offers: fractional-scale {_globals.Contains(WpFractionalScaleManagerV1.InterfaceName)}, " +
                              $"viewporter {_globals.Contains(WpViewporter.InterfaceName)}, " +
                              $"cursor-shape {_globals.Contains(WpCursorShapeManagerV1.InterfaceName)}");

            // Odd logical size at 1.25: 202 × 101 → 253 × 126, one tie and one not.
            const uint scale120 = 150;
            var (physW, physH) = WaylandPlatformHost.FramebufferSize(202, 101, scale120);
            Assert.Eq((253, 126), (physW, physH), "physical size");

            var runtime = BibuiRuntime.Create();
            using var skia = new SkiaRenderer();
            runtime.Mount(() =>
            {
                var box = Bibui.Arrange.FillY(Bibui.Arrange.FillX(runtime.Box())).Styled(new Bibui.Style
                {
                    BackgroundColor = Bibui.UiColor.Accent, CornerRadius = 8f, Padding = new Bibui.EdgeInsets(6f, 6f, 6f, 6f),
                });
                box.ChildLayoutAxis = Bibui.LayoutAxis.Y;
                using (runtime.PushParent(box))
                {
                    runtime.Text("Sharp at 1.25×");
                    runtime.Text("ÆØÅ fig. 0123");
                }
                return box;
            }, new Bibui.SurfaceOptions { Width = 64, Height = 48, ScaleFactor = 1f });
            runtime.ResizeSurface(physW, physH, SurfaceScale.ToFactor(scale120)); // as the host does

            pool = new BufferPool<ShmBuffer>(new ShmAllocator(_shm, bufferListener, null), depth: 2);
            var buffer = pool.TryAcquire(physW, physH)!;
            runtime.EndBuild();
            skia.Draw(runtime.DrawList, buffer.Pixels);
            var shmPixels = new ReadOnlySpan<byte>((void*)buffer.Map, physW * physH * 4).ToArray();
            Assert.Eq((physW, physH), (SurfaceOptions.PhysicalSize(runtime.SurfaceWidth, runtime.ScaleFactor),
                SurfaceOptions.PhysicalSize(runtime.SurfaceHeight, runtime.ScaleFactor)), "the frame at the buffer's physical size");
            var reference = new byte[physW * physH * 4];
            fixed (byte* p = reference) skia.Draw(runtime.DrawList, new Bibui.Draw.RasterPixels((nint)p, physW, physH, physW * 4));
            Assert.True(shmPixels.AsSpan().SequenceEqual(reference), "shm pixels equal the frame drawn packed, byte for byte");
            Assert.True(shmPixels.Any(b => b != shmPixels[0]), "something was drawn");
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "the compositor accepted pool and buffer");

            // The fallback cursor's theme: libwayland-cursor's structs as we
            // declare them, and every shape found under one of its names.
            if (ThemeCursor.IsAvailable)
            {
                nint theme = WaylandCursorNative.wl_cursor_theme_load(null, 24, _shm.Handle);
                Assert.True(theme != 0, "theme loaded");
                try
                {
                    int found = 0;
                    foreach (var shape in Enum.GetValues<CursorShape>())
                    {
                        var (css, legacy) = CursorShapes.ThemeNames(shape);
                        foreach (var name in new[] { css, legacy })
                        {
                            var p = WlUtf8.Alloc(name);
                            var cursor = WaylandCursorNative.wl_cursor_theme_get_cursor(theme, p);
                            WlUtf8.Free(p);
                            if (cursor == null) continue;
                            Assert.True(cursor->ImageCount > 0 && cursor->Images[0]->Width is > 0 and < 1024, $"{name}: sane image");
                            Assert.Eq(name, WlUtf8.Read(cursor->Name), "name read back through the struct");
                            Assert.True(WaylandCursorNative.wl_cursor_image_get_buffer(cursor->Images[0]) != 0, $"{name}: buffer");
                            found++;
                            break;
                        }
                    }
                    Console.WriteLine($"         cursor theme has {found}/{Enum.GetValues<CursorShape>().Length} shapes");
                    Assert.True(found > 0, "the theme has at least the default cursor");
                }
                finally { WaylandCursorNative.wl_cursor_theme_destroy(theme); }
            }
            pool.Clear();
            pool = null;
            _shm.DestroyProxy();
            registry.DestroyProxy();
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "clean teardown");
        }
        finally
        {
            pool?.Clear();
            WaylandNative.wl_display_disconnect(display);
            WlRegistry.Listener.Free(registryListener);
            WlBuffer.Listener.Free(bufferListener);
        }
    }
}
