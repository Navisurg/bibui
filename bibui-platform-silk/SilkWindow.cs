using System.Diagnostics;
using Bibui.Draw;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
// Silk's key enum, for asking its keyboard what is held. What reaches the
// runtime is Bibui.Key / Bibui.MouseButton: the same values, by a cast.
using SilkKey = Silk.NET.Input.Key;

namespace Bibui.Runtime.Platform;

/// <summary>
/// A window on GLFW through Silk.NET, presenting with OpenGL: what
/// <see cref="Open"/> makes, and <see cref="BeginFrame"/> and
/// <see cref="EndFrame"/> bracket every frame of. It knows GL and memory,
/// and nothing of any renderer:
/// <list type="bullet">
/// <item><see cref="Gl"/> is its framebuffer, for a renderer that draws with GL
/// (<c>new SkiaRenderer(window.Gl)</c>).</item>
/// <item><see cref="Cpu"/> is memory the size of the framebuffer, made when
/// first asked for, for a renderer that draws on the CPU; once asked for,
/// <see cref="EndFrame"/> uploads and shows it.</item>
/// </list>
/// The clipboard goes through TextCopy (wl-clipboard on Wayland).
///
/// <para>One thread: the one that opened it runs every frame.</para>
/// <code>
/// using var window = SilkWindow.Open(runtime, new("My app", 960, 640));
/// using var skia = new SkiaRenderer(window.Gl);
/// while (SilkWindow.BeginFrame(window, runtime))
/// {
///     runtime.BeginBuild();
///     // ... build ...
///     runtime.EndBuild();
///     skia.Draw(runtime.DrawList);
///     SilkWindow.EndFrame(window, runtime);
/// }
/// </code>
/// </summary>
public sealed class SilkWindow : IDisposable
{
    internal readonly IWindow Native;
    internal readonly BibuiRuntime Runtime;
    internal GL GlApi = null!;

    // What the window's events mean; GLFW's callbacks append to its Events and
    // do nothing else, so no runtime call runs inside GLFW's own dispatch.
    internal readonly WindowInput Input = new();

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

    /// <summary>
    /// Memory the size of the framebuffer that <see cref="EndFrame"/> uploads
    /// and shows: for a renderer drawing on the CPU. Made when first asked for,
    /// and from then on every frame is presented from it.
    /// </summary>
    public CpuTarget Cpu
    {
        get
        {
            if (_cpu is null)
            {
                _cpu = new CpuTarget();
                SizeCpu();
            }
            return _cpu;
        }
    }

    private CpuTarget? _cpu;
    private byte[]? _cpuBuffer;

    // Keeps the CPU memory the framebuffer's size; pinned, so a renderer draws
    // into it where it lies.
    internal void SizeCpu()
    {
        if (_cpu is null) return;
        int w = FramebufferWidth, h = FramebufferHeight;
        if (_cpuBuffer is null || _cpuBuffer.Length != w * h * 4)
            _cpuBuffer = GC.AllocateArray<byte>(Math.Max(1, w * h * 4), pinned: true);
        _cpu.Pixels = new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(_cpuBuffer, 0), w, h, w * 4);
    }

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

    internal void WireInput()
    {
        var input = Native.CreateInput();
        var events = Input.Events;
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, m.Position.X, m.Position.Y, (Bibui.MouseButton)b));
            mouse.MouseUp   += (m, b) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseUp,   m.Position.X, m.Position.Y, (Bibui.MouseButton)b));
            mouse.MouseMove += (_, p) => events.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, p.X, p.Y, Bibui.MouseButton.Unknown));
            mouse.Scroll    += (m, w) => events.Add(WindowEvent.Wheel(m.Position.X, m.Position.Y, w.X, w.Y));
        }
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyChar += (_, c)    => events.Add(WindowEvent.Text(c));
            keyboard.KeyDown += (_, y, _) => events.Add(WindowEvent.KeyPress((Bibui.Key)y));
            keyboard.KeyUp   += (_, y, _) => events.Add(WindowEvent.KeyRelease((Bibui.Key)y));
        }
        _keyboard = input.Keyboards.FirstOrDefault();
        _mouse = input.Mice.FirstOrDefault();
        if (_cursor != CursorShape.Default) ApplyCursor();
    }

    /// <summary>
    /// The modifiers held now, asked of the keyboard rather than accumulated, so
    /// a key released while another window had focus cannot latch.
    /// </summary>
    public KeyModifiers HeldModifiers
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
    // runtime's TargetHz, which also sets what a second means to an animation.

    internal readonly Stopwatch Clock = Stopwatch.StartNew();
    internal double NextFrameMs;

    // ── Presenting the CPU memory ────────────────────────────────────────────
    // A texture the memory is uploaded to, drawn over the framebuffer as one
    // quad. Plain GL, made when the CPU memory is first shown.

    private uint _program, _vao, _texture;
    private int _texWidth, _texHeight;

    internal void PresentCpu()
    {
        if (_cpu is null || _cpuBuffer is null) return;
        var gl = GlApi;
        if (_program == 0)
        {
            _program = CreateShaderProgram();
            _vao = gl.GenVertexArray();
            _texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _texture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        }
        int w = _cpu.Pixels.Width, h = _cpu.Pixels.Height;
        gl.Viewport(0, 0, (uint)w, (uint)h);
        // Raw BGRA, swizzled by the driver on upload; in place when the size is unchanged.
        gl.BindTexture(TextureTarget.Texture2D, _texture);
        if (w == _texWidth && h == _texHeight)
            gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)w, (uint)h, PixelFormat.Bgra, PixelType.UnsignedByte, in _cpuBuffer[0]);
        else
        {
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)w, (uint)h, 0, PixelFormat.Bgra, PixelType.UnsignedByte, in _cpuBuffer[0]);
            (_texWidth, _texHeight) = (w, h);
        }
        gl.UseProgram(_program);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(gl.GetUniformLocation(_program, "uTexture"), 0);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6); // a fullscreen quad: two triangles
        gl.BindVertexArray(0);
        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <summary>
    /// Closes the window and destroys it with its GL context. Dispose a renderer
    /// bound to <see cref="Gl"/> first: its GPU context goes before the GL one.
    /// </summary>
    public void Dispose()
    {
        if (Runtime.WakeUp == Wake) Runtime.WakeUp = null;
        if (_program != 0) GlApi.DeleteProgram(_program);
        if (_vao != 0) GlApi.DeleteVertexArray(_vao);
        if (_texture != 0) GlApi.DeleteTexture(_texture);
        Native.Reset();
        Native.Dispose();
    }

    private uint CreateShaderProgram()
    {
        // Fullscreen quad via gl_VertexID — no VBO required.
        //
        // NDC positions for 6 vertices (two triangles covering [-1,1] x [-1,1]):
        //   tri 0: (-1,-1)  (1,-1)  (-1,1)
        //   tri 1: (-1, 1)  (1,-1)  ( 1,1)
        //
        // UV y-flip: Skia places row 0 at the image top; GL places TexImage2D row 0
        // at the texture bottom. We correct with: v = 1.0 - ((ndc_y + 1.0) * 0.5)
        const string vertSrc = """
            #version 330 core
            out vec2 v_uv;
            void main()
            {
                // 6-vertex fullscreen quad positions
                vec2 pos[6] = vec2[6](
                    vec2(-1.0, -1.0),
                    vec2( 1.0, -1.0),
                    vec2(-1.0,  1.0),
                    vec2(-1.0,  1.0),
                    vec2( 1.0, -1.0),
                    vec2( 1.0,  1.0)
                );
                vec2 p = pos[gl_VertexID];
                gl_Position = vec4(p, 0.0, 1.0);
                // UV: x = (ndc_x + 1) / 2 , y flipped
                float u = (p.x + 1.0) * 0.5;
                float v = 1.0 - (p.y + 1.0) * 0.5;
                v_uv = vec2(u, v);
            }
            """;

        const string fragSrc = """
            #version 330 core
            in vec2 v_uv;
            uniform sampler2D uTexture;
            out vec4 frag_color;
            void main()
            {
                frag_color = texture(uTexture, v_uv);
            }
            """;

        var vs = CompileShader(ShaderType.VertexShader, vertSrc);
        var fs = CompileShader(ShaderType.FragmentShader, fragSrc);

        var program = GlApi.CreateProgram();
        GlApi.AttachShader(program, vs);
        GlApi.AttachShader(program, fs);
        GlApi.LinkProgram(program);

        GlApi.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            var log = GlApi.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        GlApi.DeleteShader(vs);
        GlApi.DeleteShader(fs);

        return program;
    }

    private uint CompileShader(ShaderType type, string src)
    {
        var shader = GlApi.CreateShader(type);
        GlApi.ShaderSource(shader, src);
        GlApi.CompileShader(shader);

        GlApi.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            var log = GlApi.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Shader compile failed ({type}): {log}");
        }

        return shader;
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
            window.GlApi = GL.GetApi(native);
            var context = native.GLContext!;
            window.Gl.GetProcAddress = name => context.TryGetProcAddress(name, out var at) ? at : 0;
            Resize(window, native.FramebufferSize);
        };
        native.FramebufferResize += size =>
        {
            Resize(window, size);
            // GLFW is in its own pump during a drag on Windows and macOS, and the
            // app's loop is not running: draw from here. Linux never blocks there.
            if (window.OnRefresh is { } refresh && !OperatingSystem.IsLinux()) refresh();
        };
        native.FocusChanged += focused => { if (!focused) window.Input.Events.Add(WindowEvent.FocusLost()); };
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
        window.SizeCpu();
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
        while (true)
        {
            if (window.IsClosing) return false;
            // A frame owed must not wait on unrelated input.
            if (s_alwaysRender || runtime.FramesRequested > 0) window.Wake();
            window.Native.DoEvents();
            if (window.IsClosing) return false;
            bool any = window.Input.Apply(runtime, window.HeldModifiers);
            // Awake is not the same as having something to draw: a focus change, a
            // ping or a wake with nothing owed draws nothing, and waits again.
            if (any || s_alwaysRender || runtime.FramesRequested > 0)
            {
                // Taken before the frame is built, so what the build asks for is still owed after it.
                runtime.ConsumeFrame();
                return true;
            }
        }
    }

    /// <summary>
    /// Puts the frame on screen: uploads the CPU memory if a renderer draws there,
    /// swaps, and paces the loop to the runtime's <see cref="BibuiRuntime.TargetHz"/>.
    /// </summary>
    public static void EndFrame(SilkWindow window, BibuiRuntime runtime)
    {
        window.PresentCpu();
        window.Native.GLContext!.SwapBuffers();
        if (++window.Frames == 1) window.WireInput();

        double targetMs = 1000.0 / runtime.TargetHz;
        window.NextFrameMs += targetMs;
        double remaining = window.NextFrameMs - window.Clock.Elapsed.TotalMilliseconds;
        if (remaining > 1) Thread.Sleep((int)remaining);
        // Fell more than a frame behind (a stall, or an idle wait): start again from now.
        else if (remaining < -targetMs) window.NextFrameMs = window.Clock.Elapsed.TotalMilliseconds;
    }
}
