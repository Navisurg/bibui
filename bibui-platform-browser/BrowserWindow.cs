using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Bibui.Core.Draw;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Platform.Browser;

/// <summary>
/// A window in the browser: a <c>&lt;canvas&gt;</c> on the page, drawn on the
/// CPU and shown with <c>putImageData</c>, in a .NET WebAssembly app. What
/// <see cref="OpenAsync(string)"/> makes, and <see cref="Run"/> draws. It
/// knows no runtime: the frame hands the runtime <see cref="Frame"/> and
/// <see cref="Events"/>, and the app wires <see cref="Wake"/> and the
/// clipboard to it.
///
/// <para>The browser owns the loop. A page's code may not block, so there is no
/// <c>while</c> loop: the app hands its frame to <see cref="Run"/>, which
/// returns at once, and the browser calls it on <c>requestAnimationFrame</c>
/// whenever the window has a frame to draw. The frame is the same body a
/// desktop loop has:</para>
/// <code>
/// var window = await BrowserWindow.OpenAsync("app");   // &lt;canvas id="app"&gt;
/// runtime.WakeUp = window.Wake;
/// runtime.WindowClipboard = (window.GetClipboardText, window.SetClipboardText);
/// var managed = new ManagedRenderer(window.Cpu);
/// BrowserWindow.Run(window, () => runtime.FramesRequested > 0, () =>
/// {
///     runtime.ConsumeFrame();
///     runtime.BeginBuild(window.Frame, window.Events);
///     // ... build ...
///     runtime.EndBuild();
///     BrowserWindow.BeginDraw(window);
///     managed.Draw(runtime.DrawList);
///     BrowserWindow.EndDraw(window);
/// });
/// </code>
///
/// <list type="bullet">
/// <item><b>Pacing</b>: every window on the page is driven from one
/// <c>requestAnimationFrame</c> loop, which the browser runs once a display
/// refresh, as vsync would. A window draws when it has input, its app owes
/// frames (<c>runtime.RequestFrame</c>, asked through <see cref="Run"/>) or its
/// canvas changed size, and only while the canvas is in the viewport. When no
/// window owes anything the loop stops, and input, a resize or a
/// <see cref="Wake"/> starts it again, so an idle page costs nothing. The
/// refresh rate, which the browser does not tell, is measured from the
/// callbacks.</item>
/// <item><b>Size</b>: the page sets the canvas's CSS size; the window keeps
/// its backing store at that size in device pixels (from a ResizeObserver on
/// the device-pixel content box where the browser has one, and a watch on
/// devicePixelRatio), and draws every frame at exactly that size.
/// devicePixelRatio is the frame's scale; positions stay in CSS pixels.</item>
/// <item><b>Input</b>: pointer, wheel and keys while the canvas has focus
/// (it takes focus when pressed). Keys arrive as the desktop hosts report
/// them: a physical <see cref="Key"/>, then the character typed. There is no
/// text-input element behind the canvas, so dead keys, IME composition and
/// on-screen keyboards do not type.</item>
/// <item><b>Clipboard</b>: copying writes through <c>navigator.clipboard</c>;
/// pasting takes the text of the page's paste event, which the browser fires
/// for Ctrl+V and Cmd+V.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserWindow : IDisposable
{
    private readonly int _handle;
    private Func<bool>? _frameOwed;
    private Action? _frame;
    private CanvasSize _size;
    private RefreshClock _refresh;
    private bool _sized, _visible, _closing, _failed;

    // The frame's memory: BGRA premultiplied, which the renderer draws; RGBA
    // straight, which the canvas takes. Both the canvas's size in device pixels.
    private byte[] _bgra = [], _rgba = [];

    // The text the last paste event carried: what GetClipboardText reads.
    // The browser hands the clipboard over only in a paste event.
    private string? _pasted;

    private BrowserWindow(int handle) => _handle = handle;

    /// <summary>
    /// The memory this frame is drawn into, from <see cref="BeginDraw"/> to
    /// <see cref="EndDraw"/>: for a renderer bound to it
    /// (<c>new ManagedRenderer(window.Cpu)</c>), BGRA premultiplied at the
    /// canvas's size in device pixels.
    /// </summary>
    public CpuTarget Cpu { get; } = new();

    /// <summary>
    /// What the keyboard and mouse did on the canvas since the last frame, in
    /// order. The frame hands it to the runtime
    /// (<c>runtime.BeginBuild(window.Frame, window.Events)</c>), which empties it.
    /// </summary>
    public readonly List<WindowEvent> Events = [];

    /// <summary>The canvas's width in device pixels: the frame's.</summary>
    public int FramebufferWidth => _size.Width;

    /// <summary>The canvas's height in device pixels: the frame's.</summary>
    public int FramebufferHeight => _size.Height;

    /// <summary>devicePixelRatio: device pixels per CSS pixel.</summary>
    public float Scale => _size.Ratio;

    /// <summary>The display's refresh rate, in Hz, as <c>requestAnimationFrame</c> shows it; 60 until measured.</summary>
    public float RefreshRate => _refresh.Hz;

    /// <summary>This frame's window, for <c>runtime.BeginBuild(window.Frame, window.Events)</c>.</summary>
    public WindowFrame Frame => new(Math.Max(1, _size.Width), Math.Max(1, _size.Height), _size.Ratio > 0f ? _size.Ratio : 1f, RefreshRate);

    /// <summary>The text the page's last paste event carried: the browser hands the clipboard over only in a paste event.</summary>
    public string? GetClipboardText() => _pasted;

    /// <summary>Writes <paramref name="text"/> to the clipboard, through <c>navigator.clipboard</c>.</summary>
    public void SetClipboardText(string text) => BrowserInterop.WriteClipboard(text);

    /// <summary>Starts the page's frame loop when it stopped, so a frame asked for from anywhere is drawn.</summary>
    public void Wake() => BrowserInterop.Wake();

    /// <summary>Whether the canvas is in the viewport. A window out of it draws nothing.</summary>
    public bool IsVisible => _visible;

    /// <summary>Closed, or its frame threw: it draws no more frames.</summary>
    public bool IsClosing => _closing;

    /// <summary>
    /// Opens a window on the <c>&lt;canvas&gt;</c> whose id is
    /// <paramref name="canvasId"/>. The first window opened loads the package's
    /// module (bibui-browser.js).
    /// </summary>
    public static async Task<BrowserWindow> OpenAsync(string canvasId)
    {
        ArgumentException.ThrowIfNullOrEmpty(canvasId);
        await BrowserInterop.LoadAsync();
        int handle = BrowserInterop.AttachById(canvasId);
        if (handle <= 0) throw new InvalidOperationException($"There is no <canvas id=\"{canvasId}\"> on the page.");
        return Bind(new BrowserWindow(handle));
    }

    /// <summary>
    /// Opens a window on <paramref name="canvas"/>, a <c>&lt;canvas&gt;</c>
    /// element the page handed to .NET: for a page that embeds several apps and
    /// passes each its canvas, through a <c>[JSExport]</c> of the app's own.
    /// </summary>
    public static async Task<BrowserWindow> OpenAsync(JSObject canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        await BrowserInterop.LoadAsync();
        int handle = BrowserInterop.Attach(canvas);
        if (handle <= 0) throw new ArgumentException("Not a <canvas> element.", nameof(canvas));
        return Bind(new BrowserWindow(handle));
    }

    // The canvas's size, before the first frame: it is owed one.
    private static BrowserWindow Bind(BrowserWindow window)
    {
        window.Poll();
        return window;
    }

    /// <summary>
    /// Starts drawing <paramref name="window"/>: from now on the browser calls
    /// <paramref name="frame"/> on <c>requestAnimationFrame</c> whenever the
    /// window has a frame to draw (input, a resize, or
    /// <paramref name="frameOwed"/>: the app's runtime owes frames) and its
    /// canvas is in the viewport, until it is closed. Returns at once. Before
    /// each call <see cref="Events"/> holds the frame's input. The loop keeps
    /// running while <paramref name="frameOwed"/> says so after a frame.
    ///
    /// <para>A frame that throws closes its window, and the exception is written
    /// to the console: the other windows on the page keep drawing.</para>
    /// </summary>
    public static void Run(BrowserWindow window, Func<bool> frameOwed, Action frame)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(frameOwed);
        ArgumentNullException.ThrowIfNull(frame);
        if (window._frame is not null) throw new InvalidOperationException("The window is already running.");
        if (window._closing) throw new ObjectDisposedException(nameof(BrowserWindow));
        (window._frameOwed, window._frame) = (frameOwed, frame);
        Loop.Add(window);
    }

    /// <summary>
    /// Points <see cref="Cpu"/> at memory the size of the canvas in device
    /// pixels, to draw this frame into: after the build, before the renderer draws.
    /// </summary>
    public static void BeginDraw(BrowserWindow window)
    {
        int w = Math.Max(1, window._size.Width), h = Math.Max(1, window._size.Height);
        if (window._bgra.Length != w * h * 4)
        {
            window._bgra = GC.AllocateArray<byte>(w * h * 4, pinned: true);
            window._rgba = new byte[w * h * 4];
        }
        window.Cpu.Pixels = new RasterPixels(Marshal.UnsafeAddrOfPinnedArrayElement(window._bgra, 0), w, h, w * 4);
    }

    /// <summary>Shows what was drawn into <see cref="Cpu"/> on the canvas.</summary>
    public static void EndDraw(BrowserWindow window)
    {
        var px = window.Cpu.Pixels;
        if (window._closing || px.Address == 0) return;
        int bytes = px.Width * px.Height * 4;
        CanvasPixels.BgraPremultipliedToRgba(window._bgra.AsSpan(0, bytes), window._rgba);
        BrowserInterop.Present(window._handle, window._rgba.AsSpan(0, bytes), px.Width, px.Height);
    }

    /// <summary>The pointer's shape over the canvas.</summary>
    public void SetCursor(CursorShape shape)
    {
        if (!_closing) BrowserInterop.SetCursor(_handle, CanvasCursor.Css(shape));
    }

    /// <summary>Stops drawing the window and lets go of its canvas, which keeps its last frame.</summary>
    public void Close() => Dispose();

    /// <inheritdoc cref="Close"/>
    public void Dispose()
    {
        if (_closing) return;
        _closing = true;
        Loop.Remove(this);
        BrowserInterop.Detach(_handle);
    }

    // ── The loop ─────────────────────────────────────────────────────────────

    // What the canvas reported since the last poll: its size, whether it is in
    // view, and its input into Events. True when the size changed.
    private bool Poll()
    {
        double[] data = BrowserInterop.Poll(_handle);
        var state = BrowserInput.ReadHeader(data);
        _visible = state.Visible;
        bool resized = _size.Update(state.DeviceWidth, state.DeviceHeight, state.Ratio);
        _sized |= resized;
        if (state.Count > 0)
        {
            string[] strings = BrowserInterop.Strings(_handle);
            BrowserInput.Decode(data, strings, Events, ref _pasted);
        }
        return resized;
    }

    // One requestAnimationFrame for this window: draws a frame if one is owed
    // and the canvas is in view. True when it still owes frames after.
    private bool Tick(double nowMs)
    {
        if (_closing || _failed || _frameOwed is not { } frameOwed || _frame is not { } frame) return false;
        _refresh.Tick(nowMs);
        bool resized = Poll();
        bool owed = resized || Events.Count > 0 || frameOwed() || _sized;
        if (!_visible || !owed) return false;
        _sized = false;
        try { frame(); }
        catch (Exception e)
        {
            _failed = true;
            Console.Error.WriteLine($"bibui: a frame threw, and its window stops drawing: {e}");
            return false;
        }
        return frameOwed();
    }

    /// <summary>Every running window on the page, and the one requestAnimationFrame loop that draws them.</summary>
    private static class Loop
    {
        private static readonly List<BrowserWindow> Windows = [];
        private static bool _started;

        public static void Add(BrowserWindow window)
        {
            Windows.Add(window);
            if (!_started)
            {
                _started = true;
                BrowserInterop.Start(Tick);
            }
            BrowserInterop.Wake();
        }

        public static void Remove(BrowserWindow window) => Windows.Remove(window);

        // requestAnimationFrame's callback. True asks for the next one; false
        // lets the loop stop until input, a resize or a wake starts it.
        private static bool Tick(double nowMs)
        {
            bool more = false;
            // A frame may open or close windows: walk a copy.
            foreach (var window in Windows.ToArray()) more |= window.Tick(nowMs);
            return more;
        }
    }
}
