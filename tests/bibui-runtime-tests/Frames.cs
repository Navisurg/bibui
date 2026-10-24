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
    public static Pixels Frame(UiNode root, SurfaceOptions surface, Bibui.Core.Theming.ThemeTokens theme)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => root, theme, surface);
        return runtime.Frame();
    }
}

/// <summary>
/// A test's app: its build, kept per runtime, and the frames it runs. An app
/// owns its loop and runs BeginBuild, its build, EndBuild; a test does the same
/// here, a frame at a time.
/// </summary>
public static class TestBuild
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BibuiRuntime, Func<UiNode>> s_builds = new();

    /// <summary>Keeps <paramref name="build"/> as the test's, on a surface (800×600 at 1× by default) in a theme.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, SurfaceOptions? surface = null, string theme = "bibui-dark")
        => runtime.SetBuild(build, Bibui.Core.Theming.ThemeRegistry.Load(theme), surface);

    /// <inheritdoc cref="SetBuild(BibuiRuntime, Func{UiNode}, SurfaceOptions?, string)"/>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, Bibui.Core.Theming.ThemeTokens theme, SurfaceOptions? surface = null)
    {
        s_builds.AddOrUpdate(runtime, build);
        runtime.Surface = surface ?? new SurfaceOptions { Width = 800, Height = 600, ScaleFactor = 1f };
        runtime.SetTheme(theme);
    }

    /// <summary>
    /// The test's build, between a BeginBuild and an EndBuild: what it returns
    /// is linked under the frame's root, unless the build linked it there already.
    /// </summary>
    public static void BuildTree(this BibuiRuntime runtime)
    {
        if (!s_builds.TryGetValue(runtime, out var build))
            throw new InvalidOperationException("This runtime has no test build: SetBuild first.");
        var tree = build();
        if (tree.Parent is null) UiNode.PushIntoParent(runtime.ParentInScope, tree);
    }

    /// <summary>One frame: BeginBuild with the frame's input (none by default), the test's build, EndBuild.</summary>
    public static void Step(this BibuiRuntime runtime, List<WindowEvent>? events = null)
    {
        runtime.BeginBuild(events);
        runtime.BuildTree();
        runtime.EndBuild();
    }
}

// A renderer's texture calls, as the texture contract drives them.
public delegate TextureHandle CreateTextureFn(TextureFormat format, TextureKind kind,
    int width, int height, ReadOnlySpan<byte> pixels, int rowBytes);
public delegate void UpdateTextureFn(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes);
