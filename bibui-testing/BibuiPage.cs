using Bibui.Raster.Skia;
using Bibui.TextEdit;
using Bibui.Theming;
using Bibui.Runtime;

namespace Bibui.Testing;

/// <summary>
/// Headless implementation of IBibuiPage. Wraps a BibuiRuntime and exposes the
/// Playwright-style semantic test API. No window or OS interaction is involved.
/// </summary>
public sealed class BibuiPage : IBibuiPage
{
    private readonly BibuiRuntime _runtime;
    private bool _disposed;

    private BibuiPage(BibuiRuntime runtime)
    {
        _runtime = runtime;
        // Point the runtime's copy and paste at this page's own clipboard, so a
        // test never reaches the real one.
        _runtime.ClipboardGet = () => _clipboard;
        _runtime.ClipboardSet = text => _clipboard = text;

        // A page opens on a painted frame, the way a window does. Actions
        // hit-test the frame that was on screen when they happened, so the
        // first one needs a frame to have happened.
        _runtime.Render();
    }

    // ── Factories ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a page with a stateful interactive app.
    /// The build function receives the current state and a <see cref="SetState{T}"/> handle.
    /// Calling <c>setState.Set(newState)</c> from build code will cause
    /// the next inspection or action to re-render with the new state. From async
    /// continuations call <c>setState.Update(s =&gt; ...)</c> to avoid stale-closure clobbering.
    /// </summary>
    public static BibuiPage Launch<T>(
        T initialState,
        Func<T, SetState<T>, UiNode> build,
        SurfaceOptions? surface = null,
        string theme = "bibui-dark")
    {
        var runtime = new BibuiRuntime(new SkiaRasterBackend());
        runtime.MountStateful(initialState, build, surface, theme);
        return new BibuiPage(runtime);
    }

    /// <summary>
    /// Creates a stateful page driven by an explicit <see cref="ThemeTokens"/> object — for apps that
    /// need a custom theme (e.g. a custom accent) without reflecting onto private runtime state.
    /// </summary>
    public static BibuiPage Launch<T>(
        T initialState,
        Func<T, SetState<T>, UiNode> build,
        ThemeTokens theme,
        SurfaceOptions? surface = null)
    {
        var runtime = new BibuiRuntime(new SkiaRasterBackend());
        runtime.MountStateful(initialState, build, theme, surface);
        return new BibuiPage(runtime);
    }

    /// <summary>
    /// Creates a page with a static (non-interactive) UI tree. Useful for
    /// visual inspection. For apps with state, use Launch&lt;T&gt;.
    /// </summary>
    public static BibuiPage Launch(
        Func<UiNode> build,
        SurfaceOptions? surface = null,
        string theme = "bibui-dark")
    {
        var runtime = new BibuiRuntime(new SkiaRasterBackend());
        runtime.Mount(build, surface, theme);
        return new BibuiPage(runtime);
    }

    /// <summary>
    /// Creates a static page driven by an explicit <see cref="ThemeTokens"/> object — for visual
    /// inspection of a fixed tree under a fully custom theme, with no reflection on runtime state.
    /// </summary>
    public static BibuiPage Launch(
        Func<UiNode> build,
        ThemeTokens theme,
        SurfaceOptions? surface = null)
    {
        var runtime = new BibuiRuntime(new SkiaRasterBackend());
        runtime.Mount(build, theme, surface);
        return new BibuiPage(runtime);
    }

    /// <summary>
    /// Wraps an already-mounted <see cref="BibuiRuntime"/>. Use this when an
    /// external service (e.g. VideoService) needs to close over the runtime
    /// before mounting — create and configure the runtime yourself, then pass
    /// it here to get the full test API.
    /// </summary>
    public static BibuiPage From(BibuiRuntime runtime) => new(runtime);

    // ── IBibuiPage: tree query ────────────────────────────────────────────────

    public UiNode? FindById(string id)
    {
        Render();
        return _runtime.FindById(id);
    }

    // ── IBibuiPage: actions ───────────────────────────────────────────────────

    /// <summary>
    /// Presses and releases at the middle of an element, which is what a user
    /// does when they click it.
    ///
    /// Going through ProcessEvents rather than a dispatch method is the whole
    /// point: a test then exercises the path a real press takes — hit-testing,
    /// focus, the multi-click streak, drag state — instead of a shortcut past
    /// it. The consequence is that a test can now miss: an element covered by
    /// an overlay, or scrolled out of view, is not clicked, exactly as it would
    /// not be for a user.
    /// </summary>
    private void PressAt(string id, int clickCount = 1)
    {
        var b = GetBounds(id);
        float x = b.X + b.Width / 2f;
        float y = b.Y + b.Height / 2f;
        _runtime.ProcessEvents(new[]
        {
            new BibUiEvent { Kind = BibUiEventKind.MouseDown, Button = MouseButton.Left, X = x, Y = y, ClickCount = clickCount },
            new BibUiEvent { Kind = BibUiEventKind.MouseUp,   Button = MouseButton.Left, X = x, Y = y },
        });
    }

    /// <summary>One key, as the window would report it.</summary>
    private void PressKey(Key key, bool ctrl = false, bool shift = false)
        => _runtime.ProcessEvent(BibuiWindow.BibUiEventFromKey(key, ctrl, shift, _runtime.ClipboardGet));

    public void Click(string id)
    {
        PressAt(id);
        _runtime.Render();
    }

    /// <summary>
    /// Two clicks, which on text selects the word under the caret. States the
    /// count rather than racing two Click calls into one gesture, which would
    /// make the test depend on how fast the machine ran it.
    /// </summary>
    public void DoubleClick(string id)
    {
        PressAt(id, clickCount: 2);
        _runtime.Render();
    }

    /// <summary>Three clicks, which on text selects the line.</summary>
    public void TripleClick(string id)
    {
        PressAt(id, clickCount: 3);
        _runtime.Render();
    }

    /// <summary>
    /// The right button, at the middle of an element. No release: the runtime's
    /// press and drag machinery is left-button only, so a right press is the
    /// whole gesture.
    /// </summary>
    public void RightClick(string id)
    {
        var b = GetBounds(id);
        _runtime.ProcessEvent(new BibUiEvent
        {
            Kind   = BibUiEventKind.MouseDown,
            Button = MouseButton.Right,
            X      = b.X + b.Width / 2f,
            Y      = b.Y + b.Height / 2f,
        });
        _runtime.Render();
    }

    public void Fill(string inputId, string value)
    {
        _runtime.DispatchFill(inputId, value);
        _runtime.Render();
    }

    // ── IBibuiPage: state inspection ──────────────────────────────────────────

    public bool IsDisabled(string id)
        => FindById(id) is Box box && (box.Claims & InteractionKinds.Disabled) != 0;

    public bool IsVisible(string id)
        => FindById(id) is not null;

    public string? GetText(string id)
    {
        var node = FindById(id);
        return node is not null && FirstText(node) is { } text
            ? System.Text.Encoding.UTF8.GetString(text.Content.Span)
            : null;

        // A text's own words, or the first text under the node: a button's label.
        static Text? FirstText(UiNode n)
        {
            if (n is Text t) return t;
            for (var c = n.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
                if (FirstText(c) is { } found) return found;
            return null;
        }
    }

    public System.Drawing.RectangleF GetBounds(string id)
    {
        var node = FindById(id)
            ?? throw new InvalidOperationException($"No node with Id '{id}' in the last frame's tree.");
        return new(node.Rect.X, node.Rect.Y, node.Extent.X, node.Extent.Y);
    }

    // ── IBibuiPage: async helpers ─────────────────────────────────────────────

    public void WaitFor(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null, string? message = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            _runtime.Render(); // flush any pending invalidations
            if (condition()) return;
            Thread.Sleep(interval);
        }
        throw new TimeoutException(message ?? $"WaitFor condition was not met within {timeout.TotalSeconds:F1}s.");
    }

    // ── IBibuiPage: cursor and keyboard ──────────────────────────────────────

    public void TypeChar(char ch)
    {
        _runtime.ProcessEvent(new BibUiEvent
        {
            Kind = BibUiEventKind.Text, Key = Key.Unknown, Char = ch, CodePoint = ch,
        });
        _runtime.Render();
    }

    public void Press(Key key, bool ctrl = false, bool shift = false)
    {
        PressKey(key, ctrl, shift);
        _runtime.Render();
    }

    /// <summary>
    /// A clipboard that lives for as long as this page does. Seeding the real
    /// one from a test would clobber the developer's, and CI may have none.
    /// </summary>
    public string Clipboard
    {
        get => _clipboard;
        set => _clipboard = value;
    }
    private string _clipboard = "";

    public void PressBackspace()
    {
        PressKey(Key.Backspace);
        _runtime.Render();
    }

    public void PressArrowLeft()
    {
        PressKey(Key.Left);
        _runtime.Render();
    }

    public void PressArrowRight()
    {
        PressKey(Key.Right);
        _runtime.Render();
    }

    // ── IBibuiPage: visual ────────────────────────────────────────────────────

    public byte[] Screenshot()
        => Render().PngBytes;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _disposed = true;
        // BibuiRuntime holds no unmanaged resources in Phase 1/2.
        // IDisposable is implemented so tests can use `using var page = ...`.
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private global::Bibui.Renderer.RenderOutput Render()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BibuiPage));
        return _runtime.Render();
    }
}
