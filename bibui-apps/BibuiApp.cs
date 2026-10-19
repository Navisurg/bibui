using Bibui.Runtime;
using Bibui.Runtime.Platform;

namespace Bibui.App;

/// <summary>
/// What a <see cref="BibuiApp"/> is opened with, beyond its window. The same in
/// every Bibui.App package.
/// </summary>
public sealed record BibuiAppOptions
{
    /// <summary>
    /// Starts the test server. It also starts when the environment variable
    /// <c>BIBUI_TEST_SERVER=1</c> is set; otherwise it is off.
    /// </summary>
    public bool TestServer { get; init; }

    /// <summary>The port the test server listens on, on 127.0.0.1.</summary>
    public int TestServerPort { get; init; } = BibuiTestServer.DefaultPort;

    /// <summary>The application id a Wayland compositor groups and themes the window by; the Silk window has none.</summary>
    public string AppId { get; init; } = "bibui";
}

/// <summary>
/// A window and a renderer for a <see cref="BibuiRuntime"/>, and its loop in two
/// calls. Each Bibui.App package is one combination of window and renderer,
/// with this same type and the same calls, so changing the package changes how
/// the app is drawn and nothing in its code:
/// <code>
/// using var app = BibuiApp.Open(runtime, new("App", 800, 600));
/// while (app.BeginFrame())
/// {
///     runtime.BeginBuild(app.Events);
///     Build();
///     runtime.EndBuild();
///     app.EndFrame();
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

    /// <summary>The test server, when it was started (<see cref="BibuiAppOptions.TestServer"/>); null otherwise.</summary>
    public readonly BibuiTestServer? TestServer;

    /// <summary>
    /// This frame's input, for <c>runtime.BeginBuild</c>: the window's events, or,
    /// while a test-server command runs, the server's. Set by <see cref="BeginFrame"/>.
    /// </summary>
    public List<WindowEvent> Events { get; private set; } = [];

    private BibuiApp(BibuiRuntime runtime, AppWindow window, BibuiAppOptions options)
    {
        Runtime = runtime;
        Window = window;
        Renderer = MakeRenderer();
        if (options.TestServer || BibuiTestServer.IsEnabled) TestServer = StartTestServer(runtime, options.TestServerPort);
    }

    /// <summary>Opens the window and makes the renderer on it.</summary>
    public static BibuiApp Open(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        options ??= new BibuiAppOptions();
        return new BibuiApp(runtime, OpenWindow(runtime, window, options), options);
    }

    /// <summary>
    /// Waits until a frame is wanted, collects its input into <see cref="Events"/>,
    /// and returns true; false once the window is closing.
    /// </summary>
    public bool BeginFrame()
    {
        if (!BeginWindowFrame()) return false;
        Events = TestServer?.Step(Window.Events) ?? Window.Events;
        return true;
    }

    /// <summary>Draws the runtime's <c>DrawList</c>, presents it, and paces the loop to the runtime's rate.</summary>
    public void EndFrame()
    {
        Present();
        EndWindowFrame();
    }

    // The server, with screenshots drawn as this combination can draw into memory.
    private BibuiTestServer StartTestServer(BibuiRuntime runtime, int port)
    {
        var output = Environment.GetEnvironmentVariable(BibuiTestServer.OutputVariable);
        if (string.IsNullOrWhiteSpace(output)) output = Path.Combine(Directory.GetCurrentDirectory(), "bibui-test-output");
        var server = new BibuiTestServer(runtime, output, port, draw: ScreenshotDraw());
        server.Start();
        Console.Error.WriteLine($"[BibuiTestServer] listening on 127.0.0.1:{port}; token in {server.TokenFile}; output in {server.OutputDirectory}");
        return server;
    }

    /// <summary>Stops the test server, then frees the renderer, then the window: GPU resources before the context they live on.</summary>
    public void Dispose()
    {
        TestServer?.Dispose();
        DisposeRenderer();
        Window.Dispose();
    }

    // What each combination says: how its window opens, its renderer is made and
    // a frame is drawn into it, and what draws a test-server screenshot.
    private static partial AppWindow OpenWindow(BibuiRuntime runtime, PlatformWindowOptions window, BibuiAppOptions options);
    private partial AppRenderer MakeRenderer();
    private partial bool BeginWindowFrame();
    private partial void Present();
    private partial void EndWindowFrame();
    private partial Bibui.Draw.DrawToPixels ScreenshotDraw();
    private partial void DisposeRenderer();
}
