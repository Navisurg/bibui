using Bibui.Renderer;
using Silk.NET.Input;
using Silk.NET.Maths;
// Silk's key enum, for asking its keyboard what is held. What reaches the
// runtime is Bibui.Key / Bibui.MouseButton: the same values, by a cast.
using SilkKey = Silk.NET.Input.Key;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;

namespace Bibui.Runtime.Platform;

/// <summary>
/// The cross-platform host: a GLFW window through Silk.NET, presenting with
/// OpenGL, and the clipboard through TextCopy (wl-clipboard on Wayland).
///
/// The rendering pipeline (GPU, default):
///   BibuiRuntime.RenderTo(surface) → Skia GL backend rasterizes straight into
///   the window's default framebuffer — no CPU raster, no readback, no upload.
///
/// The rendering pipeline (CPU fallback, BIBUI_GPU=0 or no usable GL for Skia):
///   BibuiRuntime.Render() → raw BGRA bytes → GL texture → fullscreen quad blit
///
/// Uses only bibui's public API, so it can move to an assembly of its
/// own without change.
/// </summary>
public sealed class SilkPlatformHost : ISkiaGpuPlatformHost
{
    private BibuiRuntime _runtime = null!;

    // OpenGL handles — valid only while the window is open.
    // _program/_vao/_texture are only allocated on the CPU fallback path.
    private GL _gl = null!;
    private uint _program;
    private uint _vao;
    private uint _texture;
    private int _texAllocWidth;
    private int _texAllocHeight;

    // Skia GPU backend — non-null only while the GPU path is active.
    // The surface wraps the window's default framebuffer (FBO 0) and is
    // recreated whenever the framebuffer size changes.
    private GRContext? _grContext;
    private GRBackendRenderTarget? _grTarget;
    private SKSurface? _gpuSurface;
    private int _gpuSurfW, _gpuSurfH;

    private static readonly bool _gpuDisabled = Environment.GetEnvironmentVariable("BIBUI_GPU") == "0";
    private static readonly bool _profile = Environment.GetEnvironmentVariable("BIBUI_PROFILE") == "1";

    // Track last uploaded output to avoid redundant texture uploads (CPU fallback path)
    private RenderOutput? _lastUploaded;

    // Actual GL framebuffer dimensions (physical pixels), updated on load and resize
    private int _fbWidth, _fbHeight;
    private float _scale = 1f;

    private IWindow? _window;
    private bool _ranToCompletion;

    // ── The event queue ──────────────────────────────────────────────────────
    // GLFW callbacks translate what happened into a WindowEvent and append. That is
    // all they do: no runtime call, and therefore no render, can run inside
    // GLFW's own dispatch. The loop drains this and applies it at one point.
    // The list is the caller's, handed in by WaitEvents.
    private List<WindowEvent>? _events;

    // The render handler's timings, read when DoRender returns.
    private double _renderMs, _submitMs;

    /// <inheritdoc/>
    public GRContext? GraphicsContext => _grContext;

    /// <inheritdoc/>
    public long GpuResourceCacheLimit
    {
        get => _gpuResourceCacheLimit;
        set { _gpuResourceCacheLimit = value; _grContext?.SetResourceCacheLimit(value); }
    }
    private long _gpuResourceCacheLimit = 96 * 1024 * 1024;

    public int FramebufferWidth => _fbWidth;
    public int FramebufferHeight => _fbHeight;
    public float Scale => _scale;
    public bool IsClosing => _window?.IsClosing ?? true;
    public HostPresentation Presentation => _grContext is not null ? HostPresentation.GpuSurface : HostPresentation.CpuPixels;
    public bool PacesFrames => false;

    // How anything that is not an OS event gets the loop moving again:
    // glfwPostEmptyEvent underneath, documented safe from any thread.
    public void Wake() => _window?.ContinueEvents();

    public void Close() => _window?.Close();

    public string? GetClipboardText() => Clipboard.GetText();
    public void SetClipboardText(string text) => Clipboard.SetText(text);

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

    public void Open(BibuiRuntime runtime, PlatformWindowOptions options)
    {
        _runtime = runtime;
        _ranToCompletion = false;

        var windowOptions = WindowOptions.Default with
        {
            Title = options.Title,
            Size = new Vector2D<int>(options.Width, options.Height),
            WindowBorder = WindowBorder.Resizable,
            API = new GraphicsAPI(
                ContextAPI.OpenGL,
                ContextProfile.Core,
                ContextFlags.ForwardCompatible,
                new APIVersion(3, 3)),
            // VSync = false: on Wayland the compositor stops sending frame
            // callbacks to occluded/unfocused windows. With VSync enabled,
            // SwapBuffers then blocks indefinitely waiting for vblank,
            // starving the event pump — no xdg_wm_pong is sent and
            // Hyprland fires the ANR dialog while the app is backgrounded.
            //
            // FramesPerSecond = 0: BibuiWindow's loop paces itself. Left at 60
            // the two compound — Silk skips the render on any iteration our
            // sleep lands early, halving that frame to 30 Hz. Measured ~37 fps.
            VSync = false,
            // IsEventDriven: DoEvents waits on the event queue instead of
            // polling it, so an idle window runs no frames at all — no build,
            // no layout, no rasterisation. ContinueEvents is how anything that
            // is not an OS event gets the thread moving again, and it is safe
            // from any thread.
            IsEventDriven = true,
            FramesPerSecond = 0.0,
            UpdatesPerSecond = 0.0,
            // Skia's GL backend uses the stencil buffer for complex clips
            // (rounded-rect scroll regions, overlay layers). Must match the
            // stencilBits passed to GRBackendRenderTarget in EnsureGpuSurface.
            PreferredStencilBufferBits = 8,
        };

        LoadBundledGlfw();
        // Silk.NET finds platforms by reflection, which Native AOT trims away.
        Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
        Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

        var window = Window.Create(windowOptions);
        _window = window;

        window.Load += () => OnLoad(window);
        window.Render += dt => OnRender(window);
        window.FramebufferResize += fbSize =>
        {
            _fbWidth  = fbSize.X;
            _fbHeight = fbSize.Y;
            var winSize = window.Size;
            float scale = winSize.X > 0 ? (float)fbSize.X / winSize.X : 1f;
            _scale = scale;
            _runtime.ResizeSurface(fbSize.X, fbSize.Y, scale);
        };
        window.Closing += OnClose;
        window.FocusChanged += focused => { if (!focused) _events?.Add(WindowEvent.FocusLost()); };

        // Run(Action) does NOT call Initialize() — we must bracket the
        // loop with Initialize/Reset ourselves.
        window.Initialize();
    }

    public void Run(Action iteration)
    {
        var window = _window ?? throw new InvalidOperationException("Open the host before running it.");

        // Silk.NET's default Run() is a busy spin loop — FramesPerSecond
        // only gates whether DoRender's user callback fires, not how often
        // the loop iterates. BibuiWindow drives each iteration itself so it can
        // pace and yield the CPU between frames.
        //
        // Through window.Run rather than a while loop of our own: GLFW's
        // refresh callback re-enters the running iteration from inside
        // DoEvents (an expose, a live resize), and only Run gives it one.
        //
        // Wayland note: xdg_wm_base.ping is an event, so it wakes the
        // thread and is answered on arrival rather than at the next 60 Hz
        // tick. That is strictly better for Hyprland's
        // misc:anr_missed_pings than the old poll loop, and it is the
        // reason a backgrounded window can stall indefinitely without
        // tripping the ANR dialog.
        window.Run(iteration);
        window.DoEvents();
        _ranToCompletion = true;
    }

    /// <summary>
    /// Everything the OS has told us since the last frame. Blocks until the OS
    /// has something, because IsEventDriven makes DoEvents wait rather than
    /// poll — so an idle window runs no iterations at all.
    /// </summary>
    public void WaitEvents(List<WindowEvent> into)
    {
        _events = into;
        into.Clear();
        _window!.DoEvents();
    }

    public PresentTimings Present(BibuiRuntime runtime)
    {
        var window = _window!;
        window.DoUpdate();
        window.DoRender(); // the render handler, then SwapBuffers
        return new PresentTimings(_renderMs, _submitMs);
    }

    public void Shutdown()
    {
        var window = _window;
        _window = null;
        _events = null;
        if (window is null) return;
        try
        {
            if (_ranToCompletion) window.Reset();
        }
        finally
        {
            window.Dispose();
        }
    }

    // ── Window event handlers ─────────────────────────────────────────────────

    private bool _inputWired;

    private void OnLoad(IWindow window)
    {
        // Keep OnLoad lean: only GL context + shader + texture setup. Input
        // device enumeration (window.CreateInput() + Mice/Keyboards) is
        // deferred to the second OnRender tick — it does blocking xkbcommon
        // keymap reads and libinput probing that can take 300+ms, enough to
        // miss several xdg_wm_base.ping cycles and fire Hyprland's ANR
        // dialog. Deferring lets the first frame come up before we pay that
        // cost.

        _gl = GL.GetApi(window);
        window.DoEvents();

        if (!_gpuDisabled)
            TryInitGpu(window);

        if (_grContext is null)
        {
            // CPU fallback: raster in Skia, blit via fullscreen-quad texture.
            _program = CreateShaderProgram();
            _vao = _gl.GenVertexArray();
            _texture = _gl.GenTexture();

            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        var fbSize = window.FramebufferSize;
        var winSize = window.Size;
        _fbWidth  = fbSize.X;
        _fbHeight = fbSize.Y;
        float actualScale = winSize.X > 0 ? (float)fbSize.X / winSize.X : 1f;
        _scale = actualScale;
        _runtime.ResizeSurface(fbSize.X, fbSize.Y, actualScale);

        if (_grContext is not null)
        {
            // Warm-up frame: the driver compiles Skia's GL shader pipelines on
            // first use (measured ~250ms). The compositor's ANR clock only
            // starts at the first buffer commit (first SwapBuffers), so paying
            // that cost here keeps the first visible frame fast — the same
            // trick as the CPU pre-warm render in BibuiWindow.Run().
            var w0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RenderGpuFrame();
            _gl.Finish();
            if (_profile)
            {
                double warmMs = (System.Diagnostics.Stopwatch.GetTimestamp() - w0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Console.WriteLine($"[BibuiWindow] GPU warm-up frame: {warmMs:F1}ms");
            }
        }
    }

    private void WireInputIfNeeded(IWindow window)
    {
        if (_inputWired) return;
        _inputWired = true;

        var input = window.CreateInput();
        foreach (var mouse in input.Mice)
        {
            mouse.MouseDown += (m, b) => _events?.Add(WindowEvent.Mouse(WindowEventKind.MouseDown, m.Position.X, m.Position.Y, (Bibui.MouseButton)b));
            mouse.MouseUp   += (m, b) => _events?.Add(WindowEvent.Mouse(WindowEventKind.MouseUp,   m.Position.X, m.Position.Y, (Bibui.MouseButton)b));
            mouse.MouseMove += (_, p) => _events?.Add(WindowEvent.Mouse(WindowEventKind.MouseMove, p.X, p.Y, Bibui.MouseButton.Unknown));
            mouse.Scroll    += (m, w) => _events?.Add(WindowEvent.Wheel(m.Position.X, m.Position.Y, w.X, w.Y));
        }
        foreach (var keyboard in input.Keyboards)
        {
            keyboard.KeyChar += (_, c)    => _events?.Add(WindowEvent.Text(c));
            keyboard.KeyDown += (_, y, _) => _events?.Add(WindowEvent.KeyPress((Bibui.Key)y));
            keyboard.KeyUp   += (_, y, _) => _events?.Add(WindowEvent.KeyRelease((Bibui.Key)y));
        }
        _primaryKeyboard = input.Keyboards.FirstOrDefault();
        _primaryMouse = input.Mice.FirstOrDefault();
        if (_cursor != CursorShape.Default) ApplyCursor();
    }

    // Read for the modifiers held — Shift+wheel, Ctrl+click and every shortcut.
    private IKeyboard? _primaryKeyboard;
    private IMouse? _primaryMouse;
    private CursorShape _cursor;

    /// <summary>
    /// What is held, asked rather than accumulated. A value that is asked for
    /// cannot go stale, so there is nothing to reset when focus is lost and no
    /// second copy to disagree with the first. Nothing before input is wired.
    /// </summary>
    public KeyModifiers HeldModifiers
    {
        get
        {
            var kb = _primaryKeyboard;
            if (kb is null) return KeyModifiers.None;

            var m = KeyModifiers.None;
            if (kb.IsKeyPressed(SilkKey.ShiftLeft)   || kb.IsKeyPressed(SilkKey.ShiftRight))   m |= KeyModifiers.Shift;
            if (kb.IsKeyPressed(SilkKey.ControlLeft) || kb.IsKeyPressed(SilkKey.ControlRight)) m |= KeyModifiers.Ctrl;
            if (kb.IsKeyPressed(SilkKey.AltLeft)     || kb.IsKeyPressed(SilkKey.AltRight))     m |= KeyModifiers.Alt;
            if (kb.IsKeyPressed(SilkKey.SuperLeft)   || kb.IsKeyPressed(SilkKey.SuperRight))   m |= KeyModifiers.Super;
            return m;
        }
    }

    /// <summary>
    /// GLFW's standard cursors. Held until input is wired (the mouse does not
    /// exist before the second frame), then applied.
    /// </summary>
    public void SetCursor(CursorShape shape)
    {
        _cursor = shape;
        if (_primaryMouse is not null) ApplyCursor();
    }

    private void ApplyCursor()
    {
        var cursor = _primaryMouse!.Cursor;
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

    private int _frameCount;
    private long _lastRenderReportTicks;
    private int _renderTickCount;
    private double _renderTotalMs;
    private double _renderMaxMs;

    private void OnRender(IWindow window)
    {
        var renderStart = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_grContext is not null)
            RenderGpuFrame();
        else
            RenderCpuFrame();

        // Wire input devices after the first frame has been drawn so the
        // window is mapped and responsive before we pay the ~300ms libinput
        // probe + xkbcommon keymap load. The xdg_wm_base.pong clock only
        // starts once the surface is committed (which happens when the
        // first SwapBuffers completes after this handler), so wiring at
        // frameCount == 1 keeps us well under the missed-ping threshold.
        if (_frameCount == 1)
            WireInputIfNeeded(window);

        _frameCount++;

        if (_profile)
        {
            var renderEnd = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (renderEnd - renderStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _renderTickCount++;
            _renderTotalMs += ms;
            if (ms > _renderMaxMs) _renderMaxMs = ms;
            if (renderEnd - _lastRenderReportTicks > System.Diagnostics.Stopwatch.Frequency)
            {
                Console.WriteLine(
                    $"[OnRender]    ticks={_renderTickCount} avg={_renderTotalMs/Math.Max(1,_renderTickCount):F2}ms max={_renderMaxMs:F2}ms");
                _renderTickCount = 0;
                _renderTotalMs = 0;
                _renderMaxMs = 0;
                _lastRenderReportTicks = renderEnd;
            }
        }
    }

    /// <summary>
    /// GPU path: Skia's GL backend rasterizes the frame directly into the
    /// window's default framebuffer. Immediate mode — RenderTo always paints,
    /// so no per-frame texture upload.
    /// </summary>
    private void RenderGpuFrame()
    {
        // Skia caches GL state between draws; anything else touching the
        // context (driver init, windowing library) invalidates that cache.
        // ResetContext is cheap when nothing changed.
        _grContext!.ResetContext();

        var surface = EnsureGpuSurface();
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _runtime.RenderTo(surface);
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();

        // Submit Skia's recorded GL work before the swap that follows this
        // render handler.
        surface.Canvas.Flush();
        _grContext.Flush();

        _renderMs = Ms(t1 - t0);
        _submitMs = Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t1);
    }

    /// <summary>
    /// CPU fallback path: raster with Skia's CPU backend, upload the BGRA
    /// buffer as a GL texture, blit via fullscreen quad.
    /// </summary>
    private void RenderCpuFrame()
    {
        _gl.Viewport(0, 0, (uint)_fbWidth, (uint)_fbHeight);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var output = _runtime.Render();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();

        if (!ReferenceEquals(output, _lastUploaded))
        {
            UploadTexture(output);
            _lastUploaded = output;
        }

        _gl.UseProgram(_program);

        // Bind texture to unit 0; the shader samples from uniform "uTexture" = 0
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uTexture"), 0);

        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6); // fullscreen quad = 2 triangles
        _gl.BindVertexArray(0);

        _renderMs = Ms(t1 - t0);
        _submitMs = Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t1);
    }

    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Creates the Skia GRContext against the window's live GL context.
    /// Leaves _grContext null (→ CPU fallback) if Skia can't drive this
    /// context (software GL without required extensions, remote X, etc.).
    /// </summary>
    private void TryInitGpu(IWindow window)
    {
        var glContext = window.GLContext;
        if (glContext is null) return;

        var glInterface = GRGlInterface.Create(name =>
            glContext.TryGetProcAddress(name, out var addr) ? addr : IntPtr.Zero);
        if (glInterface is null) return;

        _grContext = GRContext.CreateGl(glInterface);
        if (_grContext is null)
        {
            Console.Error.WriteLine("[BibuiWindow] Skia GL context unavailable — falling back to CPU rendering.");
            return;
        }

        // Bound Skia's GPU resource cache (glyph atlas, gradient/paint
        // textures). Long-running hosts would otherwise grow toward Skia's
        // much larger default budget.
        _grContext.SetResourceCacheLimit(_gpuResourceCacheLimit);
    }

    /// <summary>
    /// Returns an SKSurface wrapping the window's default framebuffer (FBO 0),
    /// recreating it when the framebuffer size changes. Cheap: the render
    /// target wraps existing GL storage, nothing is allocated per frame.
    /// </summary>
    private SKSurface EnsureGpuSurface()
    {
        if (_gpuSurface is not null && _gpuSurfW == _fbWidth && _gpuSurfH == _fbHeight)
            return _gpuSurface;

        _gpuSurface?.Dispose();
        _grTarget?.Dispose();

        const uint GlRgba8 = 0x8058;
        var fbInfo = new GRGlFramebufferInfo(fboId: 0, format: GlRgba8);
        // stencilBits must match PreferredStencilBufferBits in Open()'s WindowOptions.
        _grTarget = new GRBackendRenderTarget(_fbWidth, _fbHeight, sampleCount: 0, stencilBits: 8, fbInfo);
        _gpuSurface = SKSurface.Create(_grContext, _grTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("SKSurface.Create returned null for the GPU render target.");
        _gpuSurfW = _fbWidth;
        _gpuSurfH = _fbHeight;
        return _gpuSurface;
    }

    private void OnClose()
    {
        // GPU objects must go before the GL context does.
        _gpuSurface?.Dispose(); _gpuSurface = null;
        _grTarget?.Dispose();   _grTarget = null;
        _grContext?.Dispose();  _grContext = null;

        if (_program != 0) _gl.DeleteProgram(_program);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_texture != 0) _gl.DeleteTexture(_texture);
    }

    // ── Texture upload ────────────────────────────────────────────────────────

    private void UploadTexture(RenderOutput output)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        // Upload raw BGRA pixels directly — no PNG decode, no color conversion.
        // GL_BGRA tells the driver to swizzle channels on upload; GPU stores as RGBA.
        // Use TexSubImage2D when size is unchanged so the driver updates the
        // existing storage in place instead of reallocating the texture each frame.
        _gl.BindTexture(TextureTarget.Texture2D, _texture);
        if (output.Width == _texAllocWidth && output.Height == _texAllocHeight)
        {
            _gl.TexSubImage2D(
                TextureTarget.Texture2D,
                level: 0,
                xoffset: 0,
                yoffset: 0,
                width: (uint)output.Width,
                height: (uint)output.Height,
                format: PixelFormat.Bgra,
                type: PixelType.UnsignedByte,
                in output.RawBgraBytes[0]);
        }
        else
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                level: 0,
                internalformat: InternalFormat.Rgba,
                width: (uint)output.Width,
                height: (uint)output.Height,
                border: 0,
                format: PixelFormat.Bgra,
                type: PixelType.UnsignedByte,
                in output.RawBgraBytes[0]);
            _texAllocWidth = output.Width;
            _texAllocHeight = output.Height;
        }
        _gl.BindTexture(TextureTarget.Texture2D, 0);

        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_profile)
        {
            double freq = System.Diagnostics.Stopwatch.Frequency / 1000.0;
            Console.WriteLine(
                $"[UploadTexture]   TexImage2D(BGRA direct)={Ms(t1-t0,freq):F2}ms");
        }

        static double Ms(long ticks, double freq) => ticks / freq;
    }

    // ── Shader helpers ────────────────────────────────────────────────────────

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

        var program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);

        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            var log = _gl.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Shader link failed: {log}");
        }

        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        return program;
    }

    private uint CompileShader(ShaderType type, string src)
    {
        var shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, src);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            var log = _gl.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Shader compile failed ({type}): {log}");
        }

        return shader;
    }
}
