using System.Diagnostics;
using Bibui.Core.Draw;
using Bibui.Gl;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
// Silk's key enum, for asking its keyboard what is held. What reaches the
// runtime is Bibui.Core.Key / Bibui.Core.MouseButton: the same values, by a cast.
using SilkKey = Silk.NET.Input.Key;

namespace Bibui.Core.Runtime.Platform;

/// <summary>
/// A window on GLFW through Silk.NET, presenting with OpenGL: what
/// <see cref="Open"/> makes, and <see cref="BeginFrame"/> and
/// <see cref="EndFrame"/> bracket every frame of. It knows nothing of any
/// renderer. Its one target is <see cref="Gl"/>, its GL framebuffer: whatever
/// draws a frame draws it there with GL, between <see cref="BeginDraw"/> and
/// <see cref="EndDraw"/>, which swaps. A
/// renderer on the GPU draws into it (<c>new SkiaGlRenderer(window.Gl)</c>); a
/// renderer on the CPU draws into memory that <c>Bibui.Gl</c>'s
/// <c>GlCpuFramebuffer</c> shows on it.
/// The clipboard goes through TextCopy (wl-clipboard on Wayland).
///
/// <para>One thread: the one that opened it runs every frame.</para>
/// <code>
/// using var window = SilkWindow.Open(runtime, new("My app", 960, 640));
/// using var skia = new SkiaGlRenderer(window.Gl);
/// while (SilkWindow.BeginFrame(window, runtime))
/// {
///     runtime.BeginBuild(window.Events);
///     // ... build ...
///     runtime.EndBuild();
///     SilkWindow.BeginDraw(window);
///     skia.Draw(runtime.DrawList);
///     SilkWindow.EndDraw(window);
///     SilkWindow.EndFrame(window, runtime);
/// }
/// </code>
/// </summary>
public sealed class SilkWindow : IDisposable
{
    internal readonly IWindow Native;
    internal readonly BibuiRuntime Runtime;

    /// <summary>
    /// What the keyboard and mouse did since the last frame, in order: GLFW's
    /// callbacks append here and do nothing else, so no runtime call runs inside
    /// GLFW's own dispatch. The loop hands it to the runtime
    /// (<c>runtime.BeginBuild(window.Events)</c>), which empties it.
    /// </summary>
    public readonly List<WindowEvent> Events = [];

    internal SilkWindow(IWindow native, BibuiRuntime runtime)
    {
        Native = native;
        Runtime = runtime;
    }

    /// <summary>
    /// The app's loop body, called from GLFW's resize callback while GLFW holds
    /// the thread in its own pump (a drag on Windows or macOS), so the window keeps
    /// drawing. Optional; Linux never calls it, as GLFW does not block there.
    /// </summary>
    public Action? OnRefresh { get; set; }

    /// <summary>The window's GL framebuffer: FBO 0 at the current size, 8 stencil bits.</summary>
    public GlTarget Gl { get; } = new() { Framebuffer = 0, StencilBits = 8 };

    /// <summary>Framebuffer size in physical px.</summary>
    public int FramebufferWidth => Gl.Width;

    /// <inheritdoc cref="FramebufferWidth"/>
    public int FramebufferHeight => Gl.Height;

    /// <summary>Physical px per logical px.</summary>
    public float Scale { get; internal set; } = 1f;

    /// <summary>True once the user or <see cref="Close"/> has asked the window to close.</summary>
    public bool IsClosing => Native.IsClosing;

    /// <summary>Asks the window to close: the next <see cref="BeginFrame"/> returns false.</summary>
    public void Close() => Native.Close();

    /// <summary>Makes a waiting <see cref="BeginFrame"/> return. Safe from any thread.</summary>
    public void Wake() => Native.ContinueEvents();

    // ── Input ────────────────────────────────────────────────────────────────

    // Wired after the first frame is shown: device enumeration does blocking
    // xkbcommon keymap reads and libinput probing that can take 300+ ms, enough
    // to miss several xdg_wm_base pings and fire Hyprland's not-responding
    // dialog. Wiring after the first commit keeps the first frame fast.
    internal int Frames;
    private IKeyboard? _keyboard;
    private IMouse? _mouse;
    private CursorShape _cursor;
    // Held so the GC keeps it while GLFW holds its function pointer.
    private global::Silk.NET.GLFW.GlfwCallbacks.CharCallback? _onChar;
    private global::Silk.NET.GLFW.GlfwCallbacks.KeyCallback? _onKey;

    internal unsafe void WireInput()
    {
        var input = Native.CreateInput();
        var events = Events;
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, m.Position.X, m.Position.Y, (Bibui.Core.MouseButton)b, HeldModifiers));
            mouse.MouseUp   += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseUp,   m.Position.X, m.Position.Y, (Bibui.Core.MouseButton)b, HeldModifiers));
            mouse.MouseMove += (_, p) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, p.X, p.Y, Bibui.Core.MouseButton.Unknown, HeldModifiers));
            mouse.Scroll    += (m, w) => events.Add(WindowEvent.Wheel(m.Position.X, m.Position.Y, w.X, w.Y, HeldModifiers));
        }
        // Text straight from GLFW, not Silk.NET's KeyChar: that casts GLFW's code
        // point to a char, which turns 😀 (U+1F600) into U+F600. Taking the window's
        // char callback replaces Silk.NET's, whose only listener was KeyChar.
        _onChar = (_, codePoint) =>
        {
            if (System.Text.Rune.TryCreate(codePoint, out var r)) events.Add(WindowEvent.Text(r));
        };
        global::Silk.NET.GLFW.GlfwProvider.GLFW.Value.SetCharCallback(
            (global::Silk.NET.GLFW.WindowHandle*)Native.Native!.Glfw!.Value, _onChar);
        // Keys straight from GLFW too: Silk.NET's KeyDown drops GLFW's repeats, so
        // a held Backspace deleted one character. bibui's Key values are GLFW's
        // key codes, so a key converts with a cast. Taking the key callback
        // replaces Silk.NET's, whose only listeners were KeyDown and KeyUp.
        _onKey = (_, key, _, action, _) => events.Add(action switch
        {
            global::Silk.NET.GLFW.InputAction.Press  => WindowEvent.KeyPress((Bibui.Core.Key)key, HeldModifiers),
            global::Silk.NET.GLFW.InputAction.Repeat => WindowEvent.KeyRepeat((Bibui.Core.Key)key, HeldModifiers),
            _                                        => WindowEvent.KeyRelease((Bibui.Core.Key)key, HeldModifiers),
        });
        global::Silk.NET.GLFW.GlfwProvider.GLFW.Value.SetKeyCallback(
            (global::Silk.NET.GLFW.WindowHandle*)Native.Native!.Glfw!.Value, _onKey);
        _keyboard = input.Keyboards.FirstOrDefault();
        _mouse = input.Mice.FirstOrDefault();
        if (_cursor != CursorShape.Default) ApplyCursor();
    }

    // The modifiers held now, asked of the keyboard as each event arrives and
    // stamped on it: GLFW knows them, though Silk.NET's events do not carry them.
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

    /// <summary>Shows <paramref name="shape"/> while the pointer is over the window: GLFW's standard cursors.</summary>
    public void SetCursor(CursorShape shape)
    {
        _cursor = shape;
        if (_mouse is not null) ApplyCursor();
    }

    private void ApplyCursor()
    {
        var cursor = _mouse!.Cursor;
        var standard = _cursor switch
        {
            CursorShape.Pointer          => StandardCursor.Hand,
            CursorShape.Text             => StandardCursor.IBeam,
            CursorShape.Crosshair        => StandardCursor.Crosshair,
            CursorShape.ResizeHorizontal => StandardCursor.HResize,
            CursorShape.ResizeVertical   => StandardCursor.VResize,
            CursorShape.ResizeAll        => StandardCursor.ResizeAll,
            CursorShape.NotAllowed       => StandardCursor.NotAllowed,
            CursorShape.Wait             => StandardCursor.Wait,
            _                            => StandardCursor.Default,
        };
        if (!cursor.IsSupported(standard)) standard = StandardCursor.Default;
        cursor.Type = CursorType.Standard;
        cursor.StandardCursor = standard;
    }

    // ── Pacing ───────────────────────────────────────────────────────────────
    // GLFW does not pace: EndFrame sleeps out what is left of the frame at the
    // runtime's TargetHz, which the window sets to its monitor's refresh rate
    // (ApplyRefresh), so it draws as often as the screen shows a frame.
    // BeginFrame measures the runtime's Dt on Clock, so an animation takes as
    // long at 120 Hz as at 60.

    /// <summary>
    /// Sets the runtime's TargetHz to the refresh rate of the monitor GLFW puts
    /// the window on, when it knows one. On Wayland GLFW cannot tell which
    /// monitor a window is on, so with several monitors this may be another's.
    /// </summary>
    private static void ApplyRefresh(SilkWindow window)
    {
        if (window.Native.Monitor?.VideoMode.RefreshRate is int hz && hz > 0)
            window.Runtime.TargetHz = hz;
    }

    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal double NextFrameMs;
    // When the last frame started, on Clock: what the next frame's Dt is measured from.
    internal double LastFrameSeconds;

    /// <summary>
    /// Closes the window and destroys it with its GL context. Dispose a renderer
    /// bound to <see cref="Gl"/> first: its GPU context goes before the GL one.
    /// </summary>
    public void Dispose()
    {
        if (Runtime.WakeUp == Wake) Runtime.WakeUp = null;
        Native.Reset();
        Native.Dispose();
    }

    // ── The module: open, then bracket every frame ──────────────────────────

    // Restores the continuous loop, for comparing against the event-driven one.
    private static readonly bool s_alwaysRender = Environment.GetEnvironmentVariable("BIBUI_ALWAYS_RENDER") == "1";

    /// <summary>
    /// Loads the GLFW that NuGet shipped (Ultz.Native.GLFW), by the path it
    /// shipped it to, and returns that path; null when there is none to load.
    /// <see cref="Open"/> calls it; so may anything that creates a Silk.NET
    /// window of its own, such as a hidden GPU context.
    ///
    /// Silk.NET 2.23 tries the bare name first (the system loader path), then
    /// runtimes/&lt;rid&gt;/native under the app — but it takes that RID from
    /// /etc/os-release (Microsoft.DotNet.PlatformAbstractions), not from the
    /// runtime, and falls back to linux-&lt;arch&gt; only for a fixed list of
    /// distro IDs. Omarchy's os-release says ID=omarchy, so Silk looks in
    /// runtimes/omarchy.4.0.4-x64/native and nowhere else. With no libglfw in
    /// /usr/lib the load fails, IsApplicable goes false, and Window.Create
    /// reports "Couldn't find a suitable window platform", which reads as a
    /// broken display rather than a missing file. The runtime's own RID is no
    /// help either: Arch's source-built .NET reports arch-x64, and NuGet packs
    /// natives under the portable linux-x64.
    ///
    /// So this looks where the build actually put it: beside the app (a
    /// RID-specific publish), under the runtime's RID, then under the portable
    /// linux-&lt;arch&gt;. Loading it by absolute path registers it under its
    /// SONAME, libglfw.so.3, so Silk's dlopen of that name finds it resident.
    /// Best effort by design: if no bundled copy loads, nothing changes and
    /// Silk.NET searches as before.
    /// </summary>
    public static string? LoadBundledGlfw()
    {
        if (!OperatingSystem.IsLinux()) return null;
        lock (_glfwLock)
        {
            if (_bundledGlfw is not null) return _bundledGlfw;

            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "libglfw.so.3"),
                Path.Combine(AppContext.BaseDirectory, "runtimes",
                    System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native", "libglfw.so.3"),
                Path.Combine(AppContext.BaseDirectory, "runtimes", $"linux-{arch}", "native", "libglfw.so.3"),
            ];
            foreach (var path in candidates)
            {
                if (File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out _))
                    return _bundledGlfw = path;
            }
            return null;
        }
    }
    private static readonly Lock _glfwLock = new();
    private static string? _bundledGlfw;

    /// <summary>
    /// Opens a window for <paramref name="runtime"/> and makes its GL context
    /// current on this thread. The runtime's surface is sized to the framebuffer,
    /// its <see cref="BibuiRuntime.WakeUp"/> wakes the window, and its default
    /// clipboard is the system's. Set <see cref="SilkWindow.OnRefresh"/> to the
    /// loop body to keep it drawing during a resize on Windows or macOS.
    /// </summary>
    public static SilkWindow Open(BibuiRuntime runtime, PlatformWindowOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var windowOptions = WindowOptions.Default with
        {
            Title = options.Title,
            Size = new Vector2D<int>(options.Width, options.Height),
            WindowBorder = WindowBorder.Resizable,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
            // VSync off: on Wayland the compositor stops sending frame callbacks
            // to an occluded window, and a vsynced SwapBuffers then blocks,
            // starving the event pump until Hyprland's not-responding dialog.
            VSync = false,
            // DoEvents waits on the event queue instead of polling it, so an idle
            // window runs no frames; ContinueEvents wakes it from any thread.
            IsEventDriven = true,
            FramesPerSecond = 0.0,
            UpdatesPerSecond = 0.0,
            // A renderer may clip with the stencil (Skia's GL backend does).
            PreferredStencilBufferBits = 8,
        };

        LoadBundledGlfw();
        // Silk.NET finds platforms by reflection, which Native AOT trims away.
        global::Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        global::Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var native = global::Silk.NET.Windowing.Window.Create(windowOptions);
        var window = new SilkWindow(native, runtime);
        native.Load += () =>
        {
            var context = native.GLContext!;
            // VSync = false above does not reach the context: on Wayland the swap
            // interval stays EGL's default of 1, Mesa asks for a frame callback on
            // every swap, and the hang the options comment describes happens
            // anyway. Set it here, where the context is current.
            context.SwapInterval(0);
            window.Gl.GetProcAddress = name => context.TryGetProcAddress(name, out var at) ? at : 0;
            Resize(window, native.FramebufferSize);
            ApplyRefresh(window);
        };
        // Moved onto another monitor (X11; Wayland reports no moves).
        native.Move += _ => ApplyRefresh(window);
        native.FramebufferResize += size =>
        {
            Resize(window, size);
            // GLFW is in its own pump during a drag on Windows and macOS, and the
            // app's loop is not running: draw from here. Linux never blocks there.
            if (window.OnRefresh is { } refresh && !OperatingSystem.IsLinux()) refresh();
        };
        native.FocusChanged += focused => { if (!focused) window.Events.Add(WindowEvent.FocusLost()); };
        native.Initialize();

        runtime.WakeUp = window.Wake;
        runtime.WindowClipboard = (Clipboard.GetText, Clipboard.SetText);
        return window;
    }

    private static void Resize(SilkWindow window, Vector2D<int> framebuffer)
    {
        var size = window.Native.Size;
        window.Gl.Width = framebuffer.X;
        window.Gl.Height = framebuffer.Y;
        window.Scale = size.X > 0 ? (float)framebuffer.X / size.X : 1f;
        window.Runtime.ResizeSurface(framebuffer.X, framebuffer.Y, window.Scale);
    }

    /// <summary>
    /// Waits until there is a frame to draw — input arrived, or the runtime owes
    /// frames (<see cref="BibuiRuntime.RequestFrame"/>: an animation, a hot
    /// reload, a worker) — and hands what happened to the runtime. False once the
    /// window is closing: the loop ends.
    /// </summary>
    public static bool BeginFrame(SilkWindow window, BibuiRuntime runtime)
    {
        // Nothing owed: the loop waits for input, and the wait is not frame time.
        bool waited = !s_alwaysRender && runtime.FramesRequested == 0;
        while (true)
        {
            if (window.IsClosing) return false;
            // A frame owed must not wait on unrelated input.
            if (s_alwaysRender || runtime.FramesRequested > 0) window.Wake();
            window.Native.DoEvents();
            if (window.IsClosing) return false;
            bool any = window.Events.Count > 0;
            // Awake is not the same as having something to draw: a focus change, a
            // ping or a wake with nothing owed draws nothing, and waits again.
            if (any || s_alwaysRender || runtime.FramesRequested > 0)
            {
                // Taken before the frame is built, so what the build asks for is still owed after it.
                runtime.ConsumeFrame();
                double now = window.Clock.Elapsed.TotalSeconds;
                runtime.Dt = waited || window.LastFrameSeconds == 0 ? 1f / runtime.TargetHz : (float)(now - window.LastFrameSeconds);
                window.LastFrameSeconds = now;
                return true;
            }
        }
    }

    /// <summary>
    /// Makes the window's GL context current, to draw this frame into its
    /// framebuffer: after the build, before the renderer draws. With one window
    /// it already is; with several, each draws in its own.
    /// </summary>
    public static void BeginDraw(SilkWindow window) => window.Native.GLContext!.MakeCurrent();

    /// <summary>Shows what was drawn into the framebuffer: swaps.</summary>
    public static void EndDraw(SilkWindow window)
    {
        window.Native.GLContext!.SwapBuffers();
        if (++window.Frames == 1) window.WireInput();
    }

    /// <summary>Paces the loop to the runtime's <see cref="BibuiRuntime.TargetHz"/>: sleeps until the next frame is due.</summary>
    public static void EndFrame(SilkWindow window, BibuiRuntime runtime)
    {

        double targetMs = 1000.0 / runtime.TargetHz;
        window.NextFrameMs += targetMs;
        double remaining = window.NextFrameMs - window.Clock.Elapsed.TotalMilliseconds;
        if (remaining > 1) Thread.Sleep((int)remaining);
        // Fell more than a frame behind (a stall, or an idle wait): start again from now.
        else if (remaining < -targetMs) window.NextFrameMs = window.Clock.Elapsed.TotalMilliseconds;
    }
}
