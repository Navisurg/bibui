
namespace Bibui.Runtime.Platform;

/// <summary>
/// The operating-system side of a <see cref="BibuiWindow"/>: one native window,
/// its event pump, its framebuffer, how a frame reaches the screen, and the
/// clipboard and cursor that belong to the same session.
///
/// <para><see cref="BibuiWindow"/> keeps everything that is the same on every
/// platform — the frame loop, deciding whether a wake draws, pacing, turning
/// what happened into intents (multi-click, Shift+wheel, key → text-edit), the
/// frame profile. A host says what happened and puts pixels on screen; it does
/// not decide what either means.</para>
///
/// <para>Selection is explicit: <c>new BibuiWindow(runtime, host)</c>. There is
/// no discovery and no reflection, so a host is trimmed and AOT-compiled like
/// any other type. The cross-platform host is SilkPlatformHost (GLFW + OpenGL)
/// in Bibui.Platform.Silk; bibui itself names no host.</para>
///
/// <para>Threading: everything here is called on the thread that called
/// <see cref="BibuiWindow.Run"/>, except <see cref="Wake"/>, which any thread may
/// call at any time, including before <see cref="Open"/> and after
/// <see cref="Shutdown"/>.</para>
/// </summary>
public interface IPlatformHost
{
    /// <summary>
    /// Creates and shows the window and makes its graphics ready to present.
    /// On return the runtime's surface has been sized to the framebuffer
    /// (<see cref="BibuiRuntime.ResizeSurface"/>) and the framebuffer properties
    /// below are valid.
    ///
    /// <para>The runtime has already rendered once before this is called, so
    /// Skia, the JIT and fonts are warm before any compositor clock starts. A
    /// host that has further one-off costs (GPU shader compilation) should pay
    /// them here, before the first frame is shown — a compositor that pings a
    /// mapped surface (Hyprland's ANR) is not waiting yet.</para>
    /// </summary>
    void Open(BibuiRuntime runtime, PlatformWindowOptions options);

    /// <summary>
    /// Calls <paramref name="iteration"/> until the window is closing, then
    /// drains what the close left in the queue. A host whose toolkit can repaint
    /// re-entrantly (GLFW calls back into the loop on an expose during its own
    /// dispatch) runs the iteration through that toolkit so the re-entry still
    /// happens; anything else is <c>while (!IsClosing) iteration();</c>.
    /// </summary>
    void Run(Action iteration);

    /// <summary>
    /// Blocks until the OS has something or <see cref="Wake"/> is called, then
    /// clears <paramref name="into"/> and appends everything that arrived, which
    /// may be nothing. Waits once: deciding what an empty wake means belongs to
    /// the paced caller, and waiting again in here would make it a spin.
    ///
    /// <para>Events are appended in the order they happened, positions in
    /// logical pixels. A resize is not an event: the host applies it to the
    /// runtime itself (<see cref="BibuiRuntime.ResizeSurface"/>, which asks for a
    /// frame).</para>
    /// </summary>
    void WaitEvents(List<WindowEvent> into);

    /// <summary>
    /// Makes a blocked <see cref="WaitEvents"/> return. Safe from any thread,
    /// non-blocking, and a no-op while no window is open.
    /// </summary>
    void Wake();

    /// <summary>True once the user or <see cref="Close"/> has asked the window to close.</summary>
    bool IsClosing { get; }

    /// <summary>Asks the window to close; <see cref="Run"/> returns after the current iteration.</summary>
    void Close();

    /// <summary>
    /// Destroys the window and everything created with it. Always called once
    /// after <see cref="Open"/> was attempted, in a <c>finally</c>, whether or
    /// not <see cref="Run"/> completed — so it must cope with a half-opened
    /// window. The host may be opened again afterwards.
    /// </summary>
    void Shutdown();

    /// <summary>Framebuffer width in physical pixels.</summary>
    int FramebufferWidth { get; }

    /// <summary>Framebuffer height in physical pixels.</summary>
    int FramebufferHeight { get; }

    /// <summary>Physical pixels per logical pixel.</summary>
    float Scale { get; }

    /// <summary>
    /// The modifier keys held right now, exactly as the keyboard has them —
    /// Super is not folded into Ctrl here; <see cref="BibuiWindow"/> does that.
    /// Asked rather than accumulated, so a key released while another window
    /// had focus cannot latch.
    /// </summary>
    KeyModifiers HeldModifiers { get; }

    /// <summary>How <see cref="Present"/> got the last frame to the screen.</summary>
    HostPresentation Presentation { get; }

    /// <summary>
    /// True when <see cref="Present"/> itself waits for the compositor to want
    /// a frame (e.g. a <c>wl_surface.frame</c> callback). The loop then does not
    /// sleep out a frame budget on top. False means the loop paces at
    /// <see cref="BibuiRuntime.TargetHz"/> by sleeping, as it does for GLFW.
    /// </summary>
    bool PacesFrames { get; }

    /// <summary>
    /// Draws one whole frame from the runtime and puts it on screen. A Skia GPU
    /// host hands the runtime an SKSurface over (RenderTo(SKSurface), Bibui.Platform.Silk)
    /// its framebuffer. A CPU host needs no Skia type: it asks the runtime's
    /// raster backend for a surface over memory the compositor reads
    /// (<c>runtime.Raster.CreateSurface(new RasterPixels(...))</c> over a
    /// <c>wl_shm</c> buffer) and hands that to
    /// <see cref="BibuiRuntime.RenderTo(Bibui.Raster.IRasterSurface)"/>, or copies
    /// <see cref="BibuiRuntime.Render"/>'s bytes. Returns how long the runtime took
    /// and how long handing the result over took; the caller times the whole
    /// call, so the rest is presenting.
    /// </summary>
    PresentTimings Present(BibuiRuntime runtime);

    /// <summary>The session clipboard's text, or null when there is none or it cannot be reached. Must not throw.</summary>
    string? GetClipboardText();

    /// <summary>Puts text on the session clipboard. Best effort; must not throw.</summary>
    void SetClipboardText(string text);

    /// <summary>Shows <paramref name="shape"/> while the pointer is over the window. Best effort.</summary>
    void SetCursor(CursorShape shape);
}

/// <summary>What the window is created with.</summary>
/// <param name="Title">Window title.</param>
/// <param name="Width">Requested width in logical pixels (a tiling compositor may decide otherwise).</param>
/// <param name="Height">Requested height in logical pixels.</param>
public readonly record struct PlatformWindowOptions(string Title, int Width, int Height);

/// <summary>Where the time inside one <see cref="IPlatformHost.Present"/> went, in milliseconds.</summary>
/// <param name="RenderMs">The runtime: build, layout, and raster or GPU command recording.</param>
/// <param name="SubmitMs">Handing the frame over: a GPU flush, or a texture upload and blit.</param>
public readonly record struct PresentTimings(double RenderMs, double SubmitMs);

/// <summary>How a host gets a frame to the screen.</summary>
public enum HostPresentation
{
    /// <summary>Skia rasterises on the CPU into memory the host then shows.</summary>
    CpuPixels,

    /// <summary>Skia's GPU backend draws straight into the window's framebuffer.</summary>
    GpuSurface,
}

/// <summary>Pointer shapes a host can show. A host maps each to the nearest it has.</summary>
public enum CursorShape
{
    Default,
    Pointer,
    Text,
    Crosshair,
    ResizeHorizontal,
    ResizeVertical,
    ResizeAll,
    NotAllowed,
    Wait,
}
