using System.Runtime.InteropServices;
using Bibui.Platform.Wayland;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using Bibui.Raster.Skia;

namespace Bibui.WaylandTests;

/// <summary>
/// The host's logic without a compositor: the buffer pool against a fake
/// allocator, the configure handshake as state, and the host's contract before
/// it is opened. One live probe connects to the session's compositor when there
/// is one, binds globals and draws into a real shm buffer — and creates no
/// surface, so nothing appears on screen.
/// </summary>
internal static unsafe class HostTests
{
    private sealed class FakeBuffer(int id, int w, int h)
    {
        public readonly int Id = id, W = w, H = h;
        public override string ToString() => $"#{Id} {W}x{H}";
    }

    private sealed class FakeAllocator : IBufferAllocator<FakeBuffer>
    {
        public readonly List<FakeBuffer> Live = [];
        public readonly List<FakeBuffer> Destroyed = [];
        private int _next;

        public FakeBuffer Allocate(int width, int height)
        {
            var b = new FakeBuffer(_next++, width, height);
            Live.Add(b);
            return b;
        }

        public void Destroy(FakeBuffer buffer)
        {
            if (!Live.Remove(buffer)) throw new Exception($"{buffer} destroyed twice");
            Destroyed.Add(buffer);
        }
    }

    public static void Register(Harness t)
    {
        // ── Buffer pool ──────────────────────────────────────────────────────

        t.Run("pool: a free buffer is reused rather than allocating", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 3);
            var b0 = pool.TryAcquire(100, 50)!;
            Assert.True(ReferenceEquals(b0, pool.TryAcquire(100, 50)), "same buffer while it is free");
            Assert.Eq(1, a.Live.Count, "allocated");
        });

        t.Run("pool: triple buffering holds three busy buffers, then has none", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 3);
            for (int i = 0; i < 3; i++) pool.Submitted(pool.TryAcquire(100, 50)!);
            Assert.Eq(3, pool.BusyCount, "busy");
            Assert.True(pool.TryAcquire(100, 50) is null, "none free at depth 3");
            Assert.Eq(3, a.Live.Count, "never more than depth");
        });

        t.Run("pool: double buffering alternates as the compositor releases", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 2);
            var first = pool.TryAcquire(10, 10)!; pool.Submitted(first);
            var second = pool.TryAcquire(10, 10)!; pool.Submitted(second);
            Assert.True(!ReferenceEquals(first, second), "two buffers");
            Assert.True(pool.TryAcquire(10, 10) is null, "both held");
            Assert.True(pool.Released(first), "release");
            Assert.True(ReferenceEquals(first, pool.TryAcquire(10, 10)), "the released one comes back");
        });

        t.Run("pool: extra buffers only when asked, up to the cap", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 2, maxExtra: 1);
            pool.Submitted(pool.TryAcquire(10, 10)!);
            pool.Submitted(pool.TryAcquire(10, 10)!);
            var extra = pool.TryAcquire(10, 10, allowExtra: true);
            Assert.True(extra is not null, "one extra");
            pool.Submitted(extra!);
            Assert.True(pool.TryAcquire(10, 10, allowExtra: true) is null, "cap reached");
        });

        t.Run("pool: a resize destroys free buffers now and busy ones on release", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 2);
            var busy = pool.TryAcquire(100, 50)!; pool.Submitted(busy);
            var free = pool.TryAcquire(100, 50)!;
            Assert.True(!ReferenceEquals(busy, free), "two buffers");

            var resized = pool.TryAcquire(200, 80)!;
            Assert.Eq(200, resized.W, "new size");
            Assert.True(a.Destroyed.Contains(free), "the free old buffer went at once");
            Assert.True(!a.Destroyed.Contains(busy), "the busy one waits for its release");
            Assert.Eq(1, pool.RetiredCount, "retired");

            Assert.True(pool.Released(busy), "release of the retired buffer");
            Assert.True(a.Destroyed.Contains(busy), "destroyed on release");
            Assert.Eq(0, pool.RetiredCount, "nothing retired");
            Assert.Eq(1, pool.Count, "only the new-size buffer is left");
        });

        t.Run("pool: retired buffers do not count against the new size's depth", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 2);
            pool.Submitted(pool.TryAcquire(10, 10)!);
            pool.Submitted(pool.TryAcquire(10, 10)!);
            var n1 = pool.TryAcquire(20, 20)!; pool.Submitted(n1);
            var n2 = pool.TryAcquire(20, 20);
            Assert.True(n2 is not null, "a second buffer at the new size although two old ones are held");
        });

        t.Run("pool: resizing back un-retires a held buffer of that size", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 2);
            var held = pool.TryAcquire(10, 10)!; pool.Submitted(held);
            pool.TryAcquire(20, 20);
            Assert.Eq(1, pool.RetiredCount, "retired by the resize");
            pool.TryAcquire(10, 10);
            Assert.Eq(0, pool.RetiredCount, "back in rotation");
            pool.Released(held);
            Assert.True(!a.Destroyed.Contains(held), "kept, not destroyed");
        });

        t.Run("pool: a release for an unknown buffer is ignored; clear destroys all", () =>
        {
            var a = new FakeAllocator();
            var pool = new BufferPool<FakeBuffer>(a, depth: 3);
            var b = pool.TryAcquire(10, 10)!; pool.Submitted(b);
            pool.TryAcquire(10, 10);
            Assert.True(!pool.Released(new FakeBuffer(99, 1, 1)), "unknown");
            pool.Clear();
            Assert.Eq(0, a.Live.Count, "everything destroyed, busy or not");
            Assert.Eq(0, pool.Count, "empty");
        });

        // ── Configure handshake ──────────────────────────────────────────────

        t.Run("configure: nothing is pending until xdg_surface.configure completes the sequence", () =>
        {
            var c = new ToplevelConfigure();
            c.OnToplevelConfigure(800, 600, []);
            Assert.True(!c.HasPending && !c.EverConfigured, "toplevel.configure alone");
            c.OnSurfaceConfigure(7);
            Assert.True(c.HasPending && c.EverConfigured, "completed");
            var r = c.Take(100, 100, 10, 10);
            Assert.Eq(new ResolvedConfigure(800, 600, 7, ToplevelStates.None), r);
            Assert.True(!c.HasPending, "taken");
        });

        t.Run("configure: zero means keep the current size", () =>
        {
            var c = new ToplevelConfigure();
            c.OnToplevelConfigure(0, 0, []);
            c.OnSurfaceConfigure(1);
            var r = c.Take(720, 420, 10, 10);
            Assert.Eq((720, 420), (r.Width, r.Height), "kept");
            c.OnToplevelConfigure(0, 300, []);
            c.OnSurfaceConfigure(2);
            r = c.Take(720, 420, 10, 10);
            Assert.Eq((720, 300), (r.Width, r.Height), "one axis");
        });

        t.Run("configure: the minimum size holds whatever the compositor asks", () =>
        {
            var c = new ToplevelConfigure();
            c.OnToplevelConfigure(100, 50, []);
            c.OnSurfaceConfigure(3);
            var r = c.Take(720, 420, 240, 160);
            Assert.Eq((240, 160), (r.Width, r.Height), "clamped");
        });

        t.Run("configure: only the latest of several sequences is acked", () =>
        {
            var c = new ToplevelConfigure();
            c.OnToplevelConfigure(500, 400, []);
            c.OnSurfaceConfigure(10);
            c.OnToplevelConfigure(900, 700, []);
            c.OnSurfaceConfigure(11);
            var r = c.Take(1, 1, 1, 1);
            Assert.Eq(11u, r.Serial, "latest serial");
            Assert.Eq((900, 700), (r.Width, r.Height), "latest size");
            Assert.Throws<InvalidOperationException>(() => c.Take(1, 1, 1, 1));
        });

        t.Run("configure: states arrive as an array and become flags", () =>
        {
            var c = new ToplevelConfigure();
            c.OnToplevelConfigure(10, 10, [(uint)XdgToplevel.State.Activated, (uint)XdgToplevel.State.Resizing, 99]);
            c.OnSurfaceConfigure(1);
            Assert.Eq(ToplevelStates.Activated | ToplevelStates.Resizing, c.Take(1, 1, 1, 1).States, "states");
        });

        // ── The host before it is opened ─────────────────────────────────────

        t.Run("window: closed and inert until opened; Wake is a no-op", () =>
        {
            var host = new WaylandWindow();
            Assert.True(host.IsClosing, "closing before open");
            Assert.Eq(1f, host.Scale, "scale");
            Assert.Eq(KeyModifiers.None, host.HeldModifiers, "modifiers");
            Assert.True(host.GetClipboardText() is null, "no clipboard");
            host.SetClipboardText("x");
            host.SetCursor(CursorShape.Text);
            host.Wake();
            var events = new List<WindowEvent>();
            host.WaitEvents(events);
            Assert.Eq(0, events.Count, "WaitEvents on a closed window returns nothing at once");
            host.Dispose(); // copes with never having opened
            Assert.Throws<ArgumentOutOfRangeException>(() => new WaylandWindow(new WaylandHostOptions { Buffers = 1 }));
        });

        // ── Live, with no surface ────────────────────────────────────────────

        t.Run("live probe: bind globals, draw a frame into a real wl_shm buffer (no surface)", () =>
        {
            if (!WaylandWindow.IsSupported) throw new SkipException("no Wayland session");
            Probe();
        });
    }

    // ── The probe ───────────────────────────────────────────────────────────

    private static WlShm _shm;
    private static int _formats;

    [UnmanagedCallersOnly]
    private static void OnGlobal(void* data, WlRegistry registry, uint name, byte* iface, uint version)
    {
        if (WlUtf8.Read(iface) == WlShm.InterfaceName) _shm = registry.Bind<WlShm>(name, 1);
    }

    [UnmanagedCallersOnly]
    private static void OnFormat(void* data, WlShm shm, WlShm.Format format)
    {
        if (format == WlShm.Format.Argb8888) _formats++;
    }

    private static void Probe()
    {
        var display = WaylandNative.wl_display_connect(null);
        Assert.True(display != 0, "connect");
        var registryListener = WlRegistry.Listener.Create(new() { Global = &OnGlobal });
        var shmListener = WlShm.Listener.Create(new() { Format = &OnFormat });
        var bufferListener = WlBuffer.Listener.Create(default);
        BufferPool<ShmBuffer>? pool = null;
        try
        {
            _shm = default;
            _formats = 0;
            var registry = new WlDisplay(display).GetRegistry();
            registry.AddListener(registryListener);
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "roundtrip");
            Assert.True(!_shm.IsNull, "wl_shm bound through the generated generic Bind");
            _shm.AddListener(shmListener);
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "roundtrip");
            Assert.Eq(1, _formats, "ARGB8888 advertised (enum-typed event)");

            // A real buffer — memfd, mmap, fd passed in create_pool — drawn
            // into by a renderer, and compared with the same frame drawn packed.
            var runtime = BibuiRuntime.Create();
            using var skia = new SkiaRenderer();
            runtime.SetBuild(() => Bibui.Arrange.FillY(Bibui.Arrange.FillX(runtime.Box()))
                    .Styled(new Bibui.Style { BackgroundColor = Bibui.UiColor.Accent, CornerRadius = 8f }),
                new Bibui.SurfaceOptions { Width = 64, Height = 48, ScaleFactor = 1f });
            pool = new BufferPool<ShmBuffer>(new ShmAllocator(_shm, bufferListener, null), depth: 2);
            var buffer = pool.TryAcquire(64, 48)!;
            runtime.Step();
            skia.Draw(runtime.DrawList, buffer.Pixels);
            var shmPixels = new ReadOnlySpan<byte>((void*)buffer.Map, 64 * 48 * 4).ToArray();
            var reference = new byte[64 * 48 * 4];
            fixed (byte* p = reference) skia.Draw(runtime.DrawList, new Bibui.Draw.RasterPixels((nint)p, 64, 48, 64 * 4));
            Assert.True(shmPixels.AsSpan().SequenceEqual(reference), "shm pixels equal the frame drawn packed");
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "the compositor accepted pool and buffer");
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
            WlShm.Listener.Free(shmListener);
            WlBuffer.Listener.Free(bufferListener);
        }
    }
}
