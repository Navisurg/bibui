using Bibui.Core.Draw;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.App;

/// <summary>
/// What a <see cref="BibuiApp"/> is opened with, beyond its window. The same in
/// every Bibui.App package.
/// </summary>
public sealed record BibuiAppOptions
{
    /// <summary>The application id a Wayland compositor groups and themes the window by; the Silk window has none.</summary>
    public string AppId { get; init; } = "bibui";

    /// <summary>The smallest logical width a Wayland compositor may give the window; the Silk window has none.</summary>
    public int MinWidth { get; init; } = 240;

    /// <summary>The smallest logical height a Wayland compositor may give the window; the Silk window has none.</summary>
    public int MinHeight { get; init; } = 160;
}

/// <summary>
/// A window and a renderer for a <see cref="BibuiRuntime"/>, and what its loop
/// calls. Each Bibui.App package is one combination of window and renderer,
/// with this same type and the same calls, so changing the package changes how
/// the app is drawn and nothing in its code. The app owns the loop; its frame
/// builds and presents, and the window calls it too (during a resize):
/// <code>
/// using var app = BibuiApp.Open(runtime, new("App", 800, 600));
/// app.Frame = Frame;
/// while (app.WaitForFrame()) Frame();
///
/// void Frame()
/// {
///     runtime.BeginBuild(app.Window.Frame, app.Events);
///     Build();
///     runtime.EndBuild();
///     app.Present();
/// }
/// </code>
/// The parts it is made of (the window, the renderer) are packages of their
/// own, for an app that writes the loop itself.
/// </summary>
public sealed partial class BibuiApp : IDisposable
{
    /// <summary>The runtime this app draws.</summary>
    public readonly BibuiRuntime Runtime;

    /// <summary>The window. Its own module calls take it.</summary>
    public readonly AppWindow Window;

    /// <summary>
    /// The renderer, bound to the window. What textures are made on
    /// (<c>CreateTexture</c>, <c>UpdateTexture</c>, <c>ReleaseTexture</c>, the same
    /// calls in every renderer).
    /// </summary>
    public readonly AppRenderer Renderer;

    /// <summary>
    /// This frame's input, for <c>runtime.BeginBuild</c>: the window's events. Set by
    /// <see cref="WaitForFrame"/>. An app driven by a test port adds the port's events
    /// to it.
    /// </summary>
    public List<WindowEvent> Events { get; private set; } = [];

    /// <summary>
    /// The app's frame, the loop's body: the window calls it when it must be
    /// drawn and the loop cannot (a resize, which on Windows and macOS holds the
    /// loop). It builds and presents (<see cref="Present"/>), and does not wait
    /// (<see cref="WaitForFrame"/>). Without one, the window asks the loop for a
    /// frame instead, drawn once the loop runs again.
    /// </summary>
    public Action? Frame
    {
        get => _frame;
        set
        {
            _frame = value;
            Window.OnRefresh = value ?? Runtime.RequestFrame;
        }
    }
    private Action? _frame;

    private BibuiApp(BibuiRuntime runtime, AppWindow window, BibuiAppOptions options)
    {
        Runtime = runtime;
        Window = window;
        Renderer = MakeRenderer();
        Window.OnRefresh = runtime.RequestFrame;
    }

    /// <summary>Opens the window and makes the renderer on it.</summary>
    public static BibuiApp Open(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        options ??= new BibuiAppOptions();
        return new BibuiApp(runtime, OpenWindow(runtime, window, options), options);
    }

    /// <summary>
    /// Waits until there is a frame to draw, and returns true; false once the
    /// window is closing. With nothing owed and no input it waits on the OS and
    /// uses nothing. With a frame owed (<c>runtime.RequestFrame</c>) it sleeps
    /// until it is due, at the window's refresh rate, and takes the input that
    /// came meanwhile. The frame's input is then in <see cref="Events"/>, and one
    /// owed frame is taken.
    /// </summary>
    public bool WaitForFrame()
    {
        while (!Window.IsClosing)
        {
            bool idle = Runtime.FramesRequested == 0 && Window.Events.Count == 0;
            // A window whose wait paces the loop (a compositor's frame callback) needs no sleep.
            if (!idle && !Window.PacesFrames) Runtime.SleepUntilNextFrame();
            Window.GetEvents(wait: idle);
            if (Window.IsClosing) break;
            // Awake is not the same as having something to draw: a focus change or
            // a wake with nothing owed draws nothing, and waits again.
            if (Runtime.FramesRequested == 0 && Window.Events.Count == 0) continue;
            Events = Window.Events;
            // Taken before the frame is built, so what the build asks for is still owed after it.
            Runtime.ConsumeFrame();
            return true;
        }
        return false;
    }

    /// <summary>Frees the renderer, then the window: GPU resources before the context they live on.</summary>
    public void Dispose()
    {
        DisposeRenderer();
        Window.Dispose();
    }

    // What each combination says: how its window opens, and its renderer is made
    // and a frame is drawn into it.
    private static partial AppWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options);
    private partial AppRenderer MakeRenderer();
    /// <summary>
    /// Draws the runtime's <c>DrawList</c> into the window and shows it: the end of
    /// the app's frame. With <paramref name="into"/>, the frame is copied there
    /// first, before it is shown: BGRA8888 premultiplied, top row first, at the
    /// frame's size in physical pixels (<c>runtime.Window</c>), into memory the
    /// caller owns. That is a screenshot of what the window shows, drawn by the
    /// window's own renderer, textures and all. False when <paramref name="into"/>
    /// is not the frame's size (a resize came between): nothing is copied, and
    /// the next frame can be.
    /// </summary>
    public partial bool Present(RasterPixels? into = null);
    private partial void DisposeRenderer();
}
