using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Bibui.Gl;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.SDL;
using Silk.NET.Windowing;
using Silk.NET.Windowing.Sdl.iOS;
using UIKit;
// Silk's key enum, for asking its keyboard what is held. What reaches the
// runtime is Bibui.Key / Bibui.MouseButton: the same values, by a cast.
using SilkKey = Silk.NET.Input.Key;

namespace Bibui.Runtime.Platform;

/// <summary>
/// A window on iOS: SDL2's UIKit window through Silk.NET, presenting with
/// OpenGL ES 3. The iOS counterpart of <c>SilkWindow</c>, with the same loop:
/// <see cref="Open"/> makes it, and <see cref="BeginFrame"/> and
/// <see cref="EndFrame"/> bracket every frame. It knows nothing of any
/// renderer. Its one target is <see cref="Gl"/>, the framebuffer SDL's view
/// presents: whatever draws a frame draws it there with GL, between
/// <see cref="BeginDraw"/> and <see cref="EndDraw"/>, which presents it
/// (<c>new SkiaGlRenderer(window.Gl)</c>).
///
/// <para>UIKit owns the process's main thread, so an app hands its whole loop
/// to <see cref="RunApp"/>, which starts UIKit and runs the loop on that
/// thread once the app has launched. The loop owns the thread from there, as
/// on the desktop: <see cref="BeginFrame"/> runs UIKit's run loop while it
/// waits.</para>
///
/// <para>Touches arrive as the left mouse button (SDL's touch-as-mouse): a
/// press, moves while held, a release. The text the keyboard types arrives
/// while <see cref="ShowKeyboard"/> has it up.</para>
///
/// <para>One window, on the main thread.</para>
/// <code>
/// SilkIosWindow.RunApp(() =>
/// {
///     var runtime = BibuiRuntime.Create();
///     using var window = SilkIosWindow.Open(runtime, new("My app", 0, 0));
///     using var skia = new SkiaGlRenderer(window.Gl);
///     while (SilkIosWindow.BeginFrame(window, runtime))
///     {
///         runtime.BeginBuild(window.Events);
///         // ... build ...
///         runtime.EndBuild();
///         SilkIosWindow.BeginDraw(window);
///         skia.Draw(runtime.DrawList);
///         SilkIosWindow.EndDraw(window);
///         SilkIosWindow.EndFrame(window, runtime);
///     }
/// });
/// </code>
/// </summary>
public sealed class SilkIosWindow : IDisposable
{
    internal readonly IView Native;
    internal readonly BibuiRuntime Runtime;

    /// <summary>
    /// What the touches and keyboard did since the last frame, in order: Silk's
    /// callbacks append here and do nothing else. The loop hands it to the
    /// runtime (<c>runtime.BeginBuild(window.Events)</c>), which empties it.
    /// </summary>
    public readonly List<WindowEvent> Events = [];

    internal SilkIosWindow(IView native, BibuiRuntime runtime)
    {
        Native = native;
        Runtime = runtime;
    }

    /// <summary>
    /// The framebuffer SDL's view presents, at the current size, 8 stencil bits.
    /// Not FBO 0: on iOS a GL view draws into a framebuffer it made, over its
    /// layer's renderbuffer.
    /// </summary>
    public GlTarget Gl { get; } = new() { StencilBits = 8 };

    /// <summary>Framebuffer size in physical px.</summary>
    public int FramebufferWidth => Gl.Width;

    /// <inheritdoc cref="FramebufferWidth"/>
    public int FramebufferHeight => Gl.Height;

    /// <summary>Physical px per logical px: the screen's scale (2 or 3).</summary>
    public float Scale { get; internal set; } = 1f;

    /// <summary>
    /// The part of the window the notch, the Dynamic Island, the home indicator
    /// and the status bar leave clear, as insets from each edge in logical px.
    /// The window covers the whole screen; an app keeps what must be seen or
    /// touched inside these. Current from the last resize (a rotation).
    /// </summary>
    public EdgeInsets SafeArea { get; internal set; }

    /// <summary>True once the window is closing. An iOS app is not closed by its user; <see cref="Close"/> ends the loop.</summary>
    public bool IsClosing => Native.IsClosing;

    /// <summary>Asks the window to close: the next <see cref="BeginFrame"/> returns false.</summary>
    public void Close() => Native.Close();

    /// <summary>Makes a waiting <see cref="BeginFrame"/> return. Safe from any thread.</summary>
    public void Wake() => Native.ContinueEvents();

    /// <summary>
    /// Brings up the on-screen keyboard and starts the text it types arriving as
    /// <see cref="WindowEventKind.KeyChar"/> events, or puts it away. A hardware
    /// keyboard's keys arrive either way.
    /// </summary>
    public void ShowKeyboard(bool show)
    {
        if (show) Sdl.StartTextInput();
        else Sdl.StopTextInput();
    }

    /// <summary>Does nothing: a touch screen shows no pointer. Here so a loop written for <c>SilkWindow</c> compiles unchanged.</summary>
    public void SetCursor(CursorShape shape) { }

    private static Sdl Sdl => SdlProvider.SDL.Value;

    // ── Input ────────────────────────────────────────────────────────────────

    private IKeyboard? _keyboard;
    // The first half of a character outside the BMP, waiting for its second:
    // Silk's KeyChar hands text over one UTF-16 unit at a time.
    private char _highSurrogate;

    internal void WireInput()
    {
        var input = Native.CreateInput();
        var events = Events;
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, m.Position.X, m.Position.Y, (Bibui.MouseButton)b, HeldModifiers));
            mouse.MouseUp   += (m, b) =>
            {
                events.Add(WindowEvent.Mouse(WindowEventKind.MouseUp, m.Position.X, m.Position.Y, (Bibui.MouseButton)b, HeldModifiers));
                // A finger lifted is no longer over anything; a mouse pointer
                // would still be. Moved off the window, so what was under the
                // finger does not stay hot.
                events.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, -1, -1, Bibui.MouseButton.Unknown, HeldModifiers));
            };
            mouse.MouseMove += (_, p) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, p.X, p.Y, Bibui.MouseButton.Unknown, HeldModifiers));
            mouse.Scroll    += (m, w) => events.Add(WindowEvent.Wheel(m.Position.X, m.Position.Y, w.X, w.Y, HeldModifiers));
        }
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyDown += (_, y, _) => events.Add(WindowEvent.KeyPress((Bibui.Key)y, HeldModifiers));
            keyboard.KeyUp   += (_, y, _) => events.Add(WindowEvent.KeyRelease((Bibui.Key)y, HeldModifiers));
            keyboard.KeyChar += (_, c) => OnChar(c);
        }
        _keyboard = input.Keyboards.FirstOrDefault();
    }

    private void OnChar(char c)
    {
        if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
        Rune rune;
        bool ok = char.IsLowSurrogate(c)
            ? Rune.TryCreate(_highSurrogate, c, out rune)
            : Rune.TryCreate(c, out rune);
        _highSurrogate = '\0';
        if (ok) Events.Add(WindowEvent.Text(rune));
    }

    // The modifiers a hardware keyboard holds now, stamped on each event as it arrives.
    private KeyModifiers HeldModifiers
    {
        get
        {
            var kb = _keyboard;
            if (kb is null) return KeyModifiers.None;
            var m = KeyModifiers.None;
            if (kb.IsKeyPressed(SilkKey.ShiftLeft)   || kb.IsKeyPressed(SilkKey.ShiftRight))   m |= KeyModifiers.Shift;
            if (kb.IsKeyPressed(SilkKey.ControlLeft) || kb.IsKeyPressed(SilkKey.ControlRight)) m |= KeyModifiers.Ctrl;
            if (kb.IsKeyPressed(SilkKey.AltLeft)     || kb.IsKeyPressed(SilkKey.AltRight))     m |= KeyModifiers.Alt;
            if (kb.IsKeyPressed(SilkKey.SuperLeft)   || kb.IsKeyPressed(SilkKey.SuperRight))   m |= KeyModifiers.Super;
            return m;
        }
    }

    // ── The app in the background ────────────────────────────────────────────
    // iOS kills an app that draws with GL in the background. SDL reports the
    // move to and from the background through an event watch, called from
    // UIKit's own notification, so the loop learns of it before it could draw:
    // BeginFrame waits through the background and owes a frame on return.

    private static SilkIosWindow? s_current;
    private volatile bool _inBackground;

    private const uint AppWillEnterBackground = 0x103, AppDidEnterForeground = 0x106;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int OnSdlEvent(void* userData, Event* e)
    {
        if (s_current is { } window)
        {
            if (e->Type == AppWillEnterBackground) window._inBackground = true;
            else if (e->Type == AppDidEnterForeground)
            {
                window._inBackground = false;
                window.Runtime.RequestFrame();
            }
        }
        return 1;
    }

    // ── Pacing ───────────────────────────────────────────────────────────────
    // EndFrame sleeps out what is left of the frame at the runtime's TargetHz,
    // which also sets what a second means to an animation.

    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal double NextFrameMs;

    /// <summary>
    /// Closes the window and destroys it with its GL context. Dispose a renderer
    /// bound to <see cref="Gl"/> first: its GPU context goes before the GL one.
    /// </summary>
    public unsafe void Dispose()
    {
        if (Runtime.WakeUp == Wake) Runtime.WakeUp = null;
        if (s_current == this)
        {
            Sdl.DelEventWatch(new PfnEventFilter(&OnSdlEvent), null);
            s_current = null;
        }
        Native.Reset();
        Native.Dispose();
    }

    // ── The module: run, open, then bracket every frame ──────────────────────

    /// <summary>
    /// Starts UIKit and runs <paramref name="main"/>, the app's loop, on the main
    /// thread once the app has launched. Call it from <c>Main</c>, before making
    /// a <see cref="BibuiRuntime"/>: it first points bibui's text at the FreeType
    /// and HarfBuzz this package ships. Returns when UIKit does, which an iOS app
    /// normally never sees.
    /// </summary>
    public static void RunApp(Action main)
    {
        ArgumentNullException.ThrowIfNull(main);
        TextLibraries.Register();
        global::Silk.NET.Input.Sdl.SdlInput.RegisterPlatform();
        SilkMobile.RunApp([], _ => main());
    }

    /// <summary>
    /// Opens the app's window for <paramref name="runtime"/>, over the whole
    /// screen, and makes its GL ES context current on this thread. The runtime's
    /// surface is sized to the framebuffer, its <see cref="BibuiRuntime.WakeUp"/>
    /// wakes the window, and its default clipboard is the system's. An app's
    /// window is the screen, and it has no title: <paramref name="options"/> is not used. Inside
    /// <see cref="RunApp"/>.
    /// </summary>
    public static unsafe SilkIosWindow Open(BibuiRuntime runtime, PlatformWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (!SilkMobile.IsRunning) throw new InvalidOperationException("Open the window inside SilkIosWindow.RunApp.");
        // A view, not a window: on iOS Silk's SDL platform makes only views,
        // which is what an app's screen is.
        var viewOptions = ViewOptions.Default with
        {
            API = new GraphicsAPI(ContextAPI.OpenGLES, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 0)),
            // SDL's iOS view picks a 16-bit RGB565 colour buffer unless asked
            // for more; the renderer draws RGBA8.
            PreferredBitDepth = new Vector4D<int>(8, 8, 8, 8),
            // A renderer may clip with the stencil (Skia's GL backend does).
            PreferredStencilBufferBits = 8,
            VSync = true,
            // DoEvents waits on the event queue instead of polling it, so an idle
            // app runs no frames; ContinueEvents wakes it from any thread.
            IsEventDriven = true,
            FramesPerSecond = 0.0,
            UpdatesPerSecond = 0.0,
        };

        var native = global::Silk.NET.Windowing.Window.GetView(viewOptions);
        var window = new SilkIosWindow(native, runtime);
        native.Load += () =>
        {
            var context = native.GLContext!;
            window.Gl.GetProcAddress = name => context.TryGetProcAddress(name, out var at) ? at : 0;
            Resize(window, native.FramebufferSize);
        };
        native.FramebufferResize += size => Resize(window, size);
        native.FocusChanged += focused => { if (!focused) window.Events.Add(WindowEvent.FocusLost()); };
        native.Initialize();
        window.WireInput();

        s_current = window;
        Sdl.AddEventWatch(new PfnEventFilter(&OnSdlEvent), null);

        runtime.WakeUp = window.Wake;
        runtime.WindowClipboard = (() => UIPasteboard.General.String, text => UIPasteboard.General.String = text);
        return window;
    }

    private static void Resize(SilkIosWindow window, Vector2D<int> framebuffer)
    {
        var size = window.Native.Size;
        // The view's framebuffer is made with its GL context, and remade with
        // the layer's storage on a resize: ask for it each time.
        if (window.Native.Native?.UIKit is { } uikit) window.Gl.Framebuffer = uikit.Framebuffer;
        window.Gl.Width = framebuffer.X;
        window.Gl.Height = framebuffer.Y;
        window.Scale = size.X > 0 ? (float)framebuffer.X / size.X : 1f;
        window.SafeArea = SafeAreaOf(window);
        window.Runtime.ResizeSurface(framebuffer.X, framebuffer.Y, window.Scale);
    }

    // UIKit's safe area is in points, which are the window's logical px.
    private static EdgeInsets SafeAreaOf(SilkIosWindow window)
    {
        if (window.Native.Native?.UIKit is not { } uikit) return EdgeInsets.Zero;
        if (ObjCRuntime.Runtime.GetNSObject<UIWindow>(uikit.Window) is not { } ui) return EdgeInsets.Zero;
        var inset = ui.SafeAreaInsets;
        return new EdgeInsets((float)inset.Top, (float)inset.Right, (float)inset.Bottom, (float)inset.Left);
    }

    /// <summary>
    /// Waits until there is a frame to draw — input arrived, or the runtime owes
    /// frames (<see cref="BibuiRuntime.RequestFrame"/>: an animation, a hot
    /// reload, a worker) — and hands what happened to the runtime. Waits through
    /// any time the app spends in the background, where it may not draw. False
    /// once the window is closing: the loop ends.
    /// </summary>
    public static bool BeginFrame(SilkIosWindow window, BibuiRuntime runtime)
    {
        while (true)
        {
            if (window.IsClosing) return false;
            // A frame owed must not wait on unrelated input.
            if (runtime.FramesRequested > 0 && !window._inBackground) window.Wake();
            window.Native.DoEvents();
            if (window.IsClosing) return false;
            if (window._inBackground) continue;
            // Awake is not the same as having something to draw: a focus change
            // or a wake with nothing owed draws nothing, and waits again.
            if (window.Events.Count > 0 || runtime.FramesRequested > 0)
            {
                // Taken before the frame is built, so what the build asks for is still owed after it.
                runtime.ConsumeFrame();
                return true;
            }
        }
    }

    /// <summary>Makes the window's GL ES context current, to draw this frame into its framebuffer.</summary>
    public static void BeginDraw(SilkIosWindow window) => window.Native.GLContext!.MakeCurrent();

    /// <summary>Shows what was drawn into the framebuffer: presents the view's renderbuffer.</summary>
    public static void EndDraw(SilkIosWindow window) => window.Native.GLContext!.SwapBuffers();

    /// <summary>Paces the loop to the runtime's <see cref="BibuiRuntime.TargetHz"/>: sleeps until the next frame is due.</summary>
    public static void EndFrame(SilkIosWindow window, BibuiRuntime runtime)
    {
        double targetMs = 1000.0 / runtime.TargetHz;
        window.NextFrameMs += targetMs;
        double remaining = window.NextFrameMs - window.Clock.Elapsed.TotalMilliseconds;
        if (remaining > 1) System.Threading.Thread.Sleep((int)remaining);
        // Fell more than a frame behind (a stall, or an idle wait): start again from now.
        else if (remaining < -targetMs) window.NextFrameMs = window.Clock.Elapsed.TotalMilliseconds;
    }
}
