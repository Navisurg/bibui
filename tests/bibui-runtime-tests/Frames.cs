using Bibui.Core;
using Bibui.Core.Imaging;
using Bibui.Core.Draw;
using Bibui.Raster.Skia;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.TextEdit;
using Bibui.Core.Theming;
using Bibui.Testing;

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

    private sealed class App(Func<UiNode?> build, WindowFrame window)
    {
        // A tree to link under the frame's root, or null for a build that linked its own.
        public readonly Func<UiNode?> Build = build;
        public WindowFrame Window = window;
        // What the keyboard did since the last frame: handed to the next BeginBuild, which empties it.
        public readonly List<WindowEvent> Keys = [];
        // The runtime's clipboard, so a test never reaches the real one.
        public string Clipboard = "";
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BibuiRuntime, App> s_apps = new();

    /// <summary>Keeps <paramref name="build"/> as the test's, on <paramref name="window"/> (<see cref="DefaultWindow"/> by default) in a theme.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, WindowFrame? window = null, string theme = "bibui-dark")
        => runtime.SetBuild(build, Bibui.Core.Theming.ThemeRegistry.Load(theme), window);

    /// <inheritdoc cref="SetBuild(BibuiRuntime, Func{UiNode}, WindowFrame?, string)"/>
    public static void SetBuild(this BibuiRuntime runtime, Func<UiNode> build, Bibui.Core.Theming.ThemeTokens theme, WindowFrame? window = null)
        => Keep(runtime, build, window, theme);

    /// <summary>Keeps <paramref name="build"/>, which builds into the runtime itself, as the test's, on <paramref name="window"/>.</summary>
    public static void SetBuild(this BibuiRuntime runtime, Action build, WindowFrame? window = null, ThemeTokens? theme = null)
        => Keep(runtime, () => { build(); return null; }, window, theme);

    private static void Keep(BibuiRuntime runtime, Func<UiNode?> build, WindowFrame? window, ThemeTokens? theme)
    {
        var app = new App(build, window ?? DefaultWindow);
        s_apps.AddOrUpdate(runtime, app);
        runtime.ClipboardGet = () => app.Clipboard;
        runtime.ClipboardSet = text => app.Clipboard = text;
        if (theme is not null) runtime.SetTheme(theme);
    }

    /// <summary>A runtime of its own on <paramref name="build"/>, its first frame built, as a window opens on one.</summary>
    public static BibuiRuntime Launch(Func<UiNode> build, WindowFrame? window = null, string theme = "bibui-dark")
        => Launch(build, ThemeRegistry.Load(theme), window);

    /// <summary>A runtime of its own on <paramref name="build"/>, which builds into the runtime it is handed; its first frame built.</summary>
    public static BibuiRuntime Launch(Action<BibuiRuntime> build, WindowFrame? window = null, ThemeTokens? theme = null)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(() => build(runtime), window, theme);
        runtime.Step();
        return runtime;
    }

    /// <inheritdoc cref="Launch(Func{UiNode}, WindowFrame?, string)"/>
    public static BibuiRuntime Launch(Func<UiNode> build, ThemeTokens theme, WindowFrame? window = null)
    {
        var runtime = BibuiRuntime.Create();
        runtime.SetBuild(build, theme, window);
        runtime.Step();
        return runtime;
    }

    /// <summary>Moves the test's app to <paramref name="window"/>: a resize, a new scale or rate, from the next frame on.</summary>
    public static void SetWindow(this BibuiRuntime runtime, WindowFrame window) => AppOf(runtime).Window = window;

    /// <summary>The window the test's frames are built on.</summary>
    public static WindowFrame WindowOf(this BibuiRuntime runtime) => AppOf(runtime).Window;

    private static App AppOf(BibuiRuntime runtime) => s_apps.TryGetValue(runtime, out var app) ? app
        : throw new InvalidOperationException("This runtime has no test build: SetBuild first.");

    /// <summary>
    /// The test's build, between a BeginBuild and an EndBuild: what it returns
    /// is linked under the frame's root, unless the build linked it there already.
    /// </summary>
    public static void BuildTree(this BibuiRuntime runtime)
    {
        var tree = AppOf(runtime).Build();
        if (tree is { Parent: null }) UiNode.PushIntoParent(runtime.ParentInScope, tree);
    }

    /// <summary>One frame: BeginBuild on the test's window with the frame's input (the keys pressed since the last, by default), the test's build, EndBuild.</summary>
    public static void Step(this BibuiRuntime runtime, List<WindowEvent>? events = null)
    {
        var app = AppOf(runtime);
        runtime.BeginBuild(app.Window, events ?? app.Keys);
        runtime.BuildTree();
        runtime.EndBuild();
    }

    // ── Driving the app as a person would ────────────────────────────────────
    // Each action sends what a mouse or keyboard would and builds the frame it
    // causes, and each query builds a frame and reads its tree, so a test reads
    // what is on screen after whatever it did last.

    /// <summary>A frame, then the node named <paramref name="id"/> in its tree, or null.</summary>
    public static UiNode? NodeById(this BibuiRuntime runtime, string id)
    {
        runtime.Step();
        return Nodes.FindById(runtime.Root, id);
    }

    /// <summary>A frame, then the logical-px rect of the node named <paramref name="id"/>; throws when there is none.</summary>
    public static System.Drawing.RectangleF GetBounds(this BibuiRuntime runtime, string id)
    {
        var node = runtime.NodeById(id) ?? throw new InvalidOperationException($"No node with Id '{id}' in the last frame's tree.");
        return new(node.Rect.X, node.Rect.Y, node.Extent.X, node.Extent.Y);
    }

    /// <summary>A frame, then whether a node named <paramref name="id"/> is in its tree.</summary>
    public static bool IsVisible(this BibuiRuntime runtime, string id) => runtime.NodeById(id) is not null;

    /// <summary>A frame, then whether the box named <paramref name="id"/> claims Disabled.</summary>
    public static bool IsDisabled(this BibuiRuntime runtime, string id)
        => runtime.NodeById(id) is Box box && (box.Claims & InteractionKinds.Disabled) != 0;

    /// <summary>A frame, then the words of the text named <paramref name="id"/>, or of the first text under it — a button's label; null for none.</summary>
    public static string? GetText(this BibuiRuntime runtime, string id)
    {
        return runtime.NodeById(id) is { } node && FirstText(node) is { } text ? new string(text.Content) : null;

        static Text? FirstText(UiNode n)
        {
            if (n is Text t) return t;
            for (var c = n.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
                if (FirstText(c) is { } found) return found;
            return null;
        }
    }

    // A press and a release at the centre of the node named id, the streak stated
    // rather than raced: two clicks in a row would depend on how fast the test ran.
    private static void PressAt(BibuiRuntime runtime, string id, int clickCount)
    {
        var b = runtime.GetBounds(id);
        float x = b.X + b.Width / 2f, y = b.Y + b.Height / 2f;
        runtime.ProcessEvents(new[]
        {
            new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y, ClickCount = clickCount },
            new BibUiEvent { Kind = BibUiEventKind.MouseUp,   Button = MouseButton.Left, X = x, Y = y },
        });
    }

    /// <summary>A click at the centre of the node named <paramref name="id"/>, and the frame it causes. Covered or scrolled away, it misses, as it would for a person.</summary>
    public static void Click(this BibuiRuntime runtime, string id) { PressAt(runtime, id, 1); runtime.Step(); }

    /// <summary>Two clicks, which on text selects a word.</summary>
    public static void DoubleClick(this BibuiRuntime runtime, string id) { PressAt(runtime, id, 2); runtime.Step(); }

    /// <summary>Three clicks, which on text selects the line.</summary>
    public static void TripleClick(this BibuiRuntime runtime, string id) { PressAt(runtime, id, 3); runtime.Step(); }

    /// <summary>A right press at the centre of the node named <paramref name="id"/>, and the frame it causes.</summary>
    public static void RightClick(this BibuiRuntime runtime, string id)
    {
        var b = runtime.GetBounds(id);
        runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Right, X = b.X + b.Width / 2f, Y = b.Y + b.Height / 2f });
        runtime.Step();
    }

    // One key as the window reports it: the modifiers down around it, all of them up after.
    private static void PressKey(BibuiRuntime runtime, Key key, bool ctrl, bool shift)
    {
        var keys = AppOf(runtime).Keys;
        var held = (ctrl ? KeyModifiers.Ctrl : 0) | (shift ? KeyModifiers.Shift : 0);
        if (ctrl) keys.Add(WindowEvent.KeyPress(Key.ControlLeft, KeyModifiers.Ctrl));
        if (shift) keys.Add(WindowEvent.KeyPress(Key.ShiftLeft, held));
        keys.Add(WindowEvent.KeyPress(key, held));
        keys.Add(WindowEvent.KeyRelease(key, held));
        if (shift) keys.Add(WindowEvent.KeyRelease(Key.ShiftLeft, held & ~KeyModifiers.Shift));
        if (ctrl) keys.Add(WindowEvent.KeyRelease(Key.ControlLeft));
    }

    /// <summary>One key, with modifiers, and the frame it causes.</summary>
    public static void Press(this BibuiRuntime runtime, Key key, bool ctrl = false, bool shift = false) { PressKey(runtime, key, ctrl, shift); runtime.Step(); }

    /// <summary>Backspace, and the frame it causes.</summary>
    public static void PressBackspace(this BibuiRuntime runtime) => runtime.Press(Key.Backspace);

    /// <summary>The left arrow, and the frame it causes.</summary>
    public static void PressArrowLeft(this BibuiRuntime runtime) => runtime.Press(Key.Left);

    /// <summary>The right arrow, and the frame it causes.</summary>
    public static void PressArrowRight(this BibuiRuntime runtime) => runtime.Press(Key.Right);

    /// <summary>One character typed, and the frame it causes: the focused field reads it.</summary>
    public static void TypeChar(this BibuiRuntime runtime, char ch)
    {
        AppOf(runtime).Keys.Add(WindowEvent.Text(new System.Text.Rune(ch)));
        runtime.Step();
    }

    /// <summary>
    /// Replaces the text of the field named <paramref name="id"/> as a person
    /// would: presses it, which its app focuses; a frame later selects all and
    /// types <paramref name="value"/>, or deletes for an empty one.
    /// </summary>
    public static void Fill(this BibuiRuntime runtime, string id, string value)
    {
        PressAt(runtime, id, 1);
        runtime.Step();
        PressKey(runtime, Key.A, ctrl: true, shift: false);
        if (value.Length == 0) PressKey(runtime, Key.Backspace, ctrl: false, shift: false);
        foreach (var rune in value.EnumerateRunes()) AppOf(runtime).Keys.Add(WindowEvent.Text(rune));
        runtime.Step();
    }

    /// <summary>What the test's clipboard holds.</summary>
    public static string GetClipboard(this BibuiRuntime runtime) => AppOf(runtime).Clipboard;

    /// <summary>Puts <paramref name="text"/> on the test's clipboard.</summary>
    public static void SetClipboard(this BibuiRuntime runtime, string text) => AppOf(runtime).Clipboard = text;

    /// <summary>A frame, drawn with Skia, as a PNG.</summary>
    public static byte[] Screenshot(this BibuiRuntime runtime)
    {
        runtime.Step();
        return FramePixels.Png(runtime, Frames.Skia.Draw);
    }
}

// A renderer's texture calls, as the texture contract drives them.
public delegate TextureHandle CreateTextureFn(TextureFormat format, TextureKind kind,
    int width, int height, ReadOnlySpan<byte> pixels, int rowBytes);
public delegate void UpdateTextureFn(TextureHandle texture, ReadOnlySpan<byte> pixels, int rowBytes);
