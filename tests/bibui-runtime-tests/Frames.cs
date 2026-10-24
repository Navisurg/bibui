using Bibui.Core;
using Bibui.Core.Imaging;
using Bibui.Core.Draw;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

/// <summary>A frame's pixels, as a test reads them: BGRA8888 premultiplied, top row first.</summary>
public sealed record Pixels(byte[] RawBgraBytes, int Width, int Height)
{
    public byte[] PngBytes => Png.Encode(RawBgraBytes, Width, Height, Width * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);

    /// <summary><paramref name="list"/> drawn by <paramref name="draw"/> into a new <paramref name="width"/> × <paramref name="height"/> buffer.</summary>
    public static Pixels Of(DrawList list, int width, int height, DrawToPixels draw)
    {
        var bgra = GC.AllocateArray<byte>(width * height * 4, pinned: true);
        draw(list, new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), width, height, width * 4));
        return new Pixels(bgra, width, height);
    }
}

/// <summary>
/// Tests read pixels the way an app gets them: the frame built, then its list
/// drawn by a renderer. Skia on the CPU unless a test names another.
/// </summary>
public static class Frames
{
    public static readonly SkiaCpuRenderer Skia = new();

    /// <summary>Builds the runtime's next frame and draws it with <paramref name="draw"/>, Skia by default.</summary>
    public static Pixels Frame(this BibuiRuntime runtime, DrawToPixels? draw = null)
    {
        runtime.Step();
        return runtime.LastFrame(draw);
    }

    /// <summary>The runtime's last frame, drawn again (Skia by default): what is on screen, without building another.</summary>
    public static Pixels LastFrame(this BibuiRuntime runtime, DrawToPixels? draw = null)
    {
        var bgra = FramePixels.Bgra(runtime, draw ?? Skia.Draw, out int w, out int h);
        return new Pixels(bgra, w, h);
    }

    /// <summary>A free-standing tree as a runtime's whole frame, built and drawn with Skia.</summary>
    public static Pixels Frame(UiNode root, WindowFrame window, Bibui.Core.Theming.ThemeTokens theme)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => root, theme, window);
        return runtime.Frame();
    }
}

/// <summary>
/// A test's app: its build and its window, kept per runtime, and the frames it
/// runs. An app owns its loop and runs BeginBuild on its window, its build,
/// EndBuild; a test does the same here, a frame at a time.
/// </summary>
public static class TestBuild
{
    /// <summary>The window a test runs on unless given one: 800 × 600 at 1×, 60 Hz, so a frame's Dt is 1/60.</summary>
    public static readonly WindowFrame DefaultWindow = new(800, 600, 1f, 60f);

    /// <summary>
    /// A window <paramref name="logicalWidth"/> × <paramref name="logicalHeight"/>
    /// logical px at <paramref name="scale"/>, 60 Hz: its framebuffer is the
    /// physical size a window would give it (<see cref="WindowFrame.PhysicalSize"/>).
    /// </summary>
    public static WindowFrame Window(int logicalWidth, int logicalHeight, float scale = 1f) =>
        new(WindowFrame.PhysicalSize(logicalWidth, scale), WindowFrame.PhysicalSize(logicalHeight, scale), scale, 60f);

    private sealed class App(Func<UiNode> build, WindowFrame window)
    {
        public readonly Func<UiNode> Build = build;
        public WindowFrame Window = window;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BibuiRuntime, App> s_apps = new();

    /// <summary>Keeps <paramref name="build"/> as the test's, on <paramref name="window"/> (<see cref="DefaultWindow"/> by default) in a theme.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, WindowFrame? window = null, string theme = "bibui-dark")
        => runtime.SetBuild(build, Bibui.Core.Theming.ThemeRegistry.Load(theme), window);

    /// <inheritdoc cref="SetBuild(BibuiRuntime, Func{UiNode}, WindowFrame?, string)"/>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, Bibui.Core.Theming.ThemeTokens theme, WindowFrame? window = null)
    {
        s_apps.AddOrUpdate(runtime, new App(build, window ?? DefaultWindow));
        runtime.SetTheme(theme);
    }

    /// <summary>Moves the test's app to <paramref name="window"/>: a resize, a new scale or rate, from the next frame on.</summary>
    public static void SetWindow(this BibuiRuntime runtime, WindowFrame window) => AppOf(runtime).Window = window;

    /// <summary>The window the test's frames are built on.</summary>
    public static WindowFrame WindowOf(this BibuiRuntime runtime) => AppOf(runtime).Window;

    private static App AppOf(BibuiRuntime runtime) => s_apps.TryGetValue(runtime, out var app) ? app
        : throw new InvalidOperationException("This runtime has no test build: SetBuild first.");

    /// <summary>A page driving the test's app: its build, on its window.</summary>
    public static Bibui.Testing.BibuiPage Page(this BibuiRuntime runtime) =>
        Bibui.Testing.BibuiPage.From(runtime, runtime.BuildTree, window: AppOf(runtime).Window);

    /// <summary>
    /// The test's build, between a BeginBuild and an EndBuild: what it returns
    /// is linked under the frame's root, unless the build linked it there already.
    /// </summary>
    public static void BuildTree(this BibuiRuntime runtime)
    {
        var tree = AppOf(runtime).Build();
        if (tree.Parent is null) UiNode.PushIntoParent(runtime.ParentInScope, tree);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BibuiRuntime, BibuiTestServer> s_servers = new();

    /// <summary>The server <see cref="RunLoop"/> steps once a frame, as an app's loop steps its own.</summary>
    public static void Serve(this BibuiRuntime runtime, BibuiTestServer server) => s_servers.AddOrUpdate(runtime, server);

    /// <summary>
    /// The app's loop, on a thread of its own: a frame whenever one is owed, as a
    /// window's loop builds them, stepping the runtime's test server (if
    /// <see cref="Serve"/> gave it one) and applying its input first. Background,
    /// so it ends with the process; a frame that throws is the test's to notice.
    /// </summary>
    public static void RunLoop(this BibuiRuntime runtime)
    {
        new Thread(() =>
        {
            var noWindow = new List<WindowEvent>();   // a window's events, of which a test has none
            while (true)
            {
                if (runtime.FramesRequested == 0) { Thread.Sleep(2); continue; }
                runtime.ConsumeFrame();
                try { runtime.Step(s_servers.TryGetValue(runtime, out var server) ? server.Step(noWindow) : null); }
                catch (Exception) { }
            }
        }) { IsBackground = true, Name = "test app loop" }.Start();
    }

    /// <summary>One frame: BeginBuild on the test's window with the frame's input (none by default), the test's build, EndBuild.</summary>
    public static void Step(this BibuiRuntime runtime, List<WindowEvent>? events = null)
    {
        runtime.BeginBuild(AppOf(runtime).Window, events);
        runtime.BuildTree();
        runtime.EndBuild();
    }
}

// A renderer's texture calls, as the texture contract drives them.
public delegate TextureHandle CreateTextureFn(TextureFormat format, TextureKind kind,
    int width, int height, ReadOnlySpan<byte> pixels, int rowBytes);
public delegate void UpdateTextureFn(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes);
