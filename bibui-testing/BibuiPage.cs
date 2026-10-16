using Bibui.Imaging;
using Bibui.Draw;
using Bibui.Raster.Skia;
using Bibui.TextEdit;
using Bibui.Theming;
using Bibui.Runtime;

namespace Bibui.Testing;

/// <summary>
/// Playwright-style semantic test API for a bibui runtime, headless: no window
/// and no OS.
///
/// Tests read the tree the runtime laid out, and drive it by dispatching events
/// through the runtime's event system rather than the OS — the way Playwright
/// uses Chrome DevTools Protocol to talk to the browser's DOM. Every action
/// builds the frame it causes. <see cref="Screenshot"/> draws the last frame's
/// list with the page's renderer, Skia on the CPU unless another is given.
///
/// Usage:
///   page.Click("inc");     // a press and release at the box named "inc"
///   page.GetText("label");
/// </summary>
public sealed class BibuiPage : IDisposable
{
    private readonly BibuiRuntime _runtime;
    private readonly DrawToPixels _draw;
    private readonly SkiaRenderer? _ownRenderer;
    private bool _disposed;

    private BibuiPage(BibuiRuntime runtime, DrawToPixels? draw)
    {
        _runtime = runtime;
        if (draw is null)
        {
            _ownRenderer = new SkiaRenderer();
            draw = _ownRenderer.Draw;
        }
        _draw = draw;
        // Point the runtime's copy and paste at this page's own clipboard, so a
        // test never reaches the real one.
        _runtime.ClipboardGet = () => _clipboard;
        _runtime.ClipboardSet = text => _clipboard = text;

        // A page opens on a built frame, the way a window does. Actions
        // hit-test the frame that was on screen when they happened, so the
        // first one needs a frame to have happened.
        _runtime.EndBuild();
    }

    // ── Factories ────────────────────────────────────────────────────────────

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
        var runtime = BibuiRuntime.Create();
        runtime.MountStateful(initialState, build, surface, theme);
        return new BibuiPage(runtime, null);
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
        var runtime = BibuiRuntime.Create();
        runtime.MountStateful(initialState, build, theme, surface);
        return new BibuiPage(runtime, null);
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
        var runtime = BibuiRuntime.Create();
        runtime.Mount(build, surface, theme);
        return new BibuiPage(runtime, null);
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
        var runtime = BibuiRuntime.Create();
        runtime.Mount(build, theme, surface);
        return new BibuiPage(runtime, null);
    }

    /// <summary>
    /// Wraps an already-mounted <see cref="BibuiRuntime"/>. Use this when an
    /// external service (e.g. VideoService) needs to close over the runtime
    /// before mounting — create and configure the runtime yourself, then pass
    /// it here to get the full test API. <paramref name="draw"/> is what
    /// <see cref="Screenshot"/> draws with; null for Skia on the CPU.
    /// </summary>
    public static BibuiPage From(BibuiRuntime runtime, DrawToPixels? draw = null) => new(runtime, draw);

    // ── Tree query ───────────────────────────────────────────────────────────

    /// <summary>
    /// The node with the given Id in the tree just rendered, or null. Read what
    /// is needed from it; the next frame resets and reuses it.
    /// </summary>
    public UiNode? FindById(string id)
    {
        Render();
        return _runtime.FindById(id);
    }

    // ── Actions ──────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Clicks the box with the given Id: a press and a release at its centre.
    /// No-op if no box that claims a press carries the name.
    /// </summary>
    public void Click(string id)
    {
        PressAt(id);
        _runtime.EndBuild();
    }

    /// <summary>
    /// Two clicks, which on text selects the word under the caret. States the
    /// count rather than racing two Click calls into one gesture, which would
    /// make the test depend on how fast the machine ran it.
    /// </summary>
    public void DoubleClick(string id)
    {
        PressAt(id, clickCount: 2);
        _runtime.EndBuild();
    }

    /// <summary>Three clicks, which on text selects the line.</summary>
    public void TripleClick(string id)
    {
        PressAt(id, clickCount: 3);
        _runtime.EndBuild();
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
        _runtime.EndBuild();
    }

    /// <summary>
    /// Replaces the whole text of the field with the given Id, the way a
    /// person would: presses it, then selects all and types the value.
    /// </summary>
    public void Fill(string inputId, string value)
    {
        _runtime.DispatchFill(inputId, value);
        _runtime.EndBuild();
    }

    // ── State inspection ─────────────────────────────────────────────────────

    /// <summary>Returns true if the box with the given Id claims Disabled.</summary>
    public bool IsDisabled(string id)
        => FindById(id) is Box box && (box.Claims & InteractionKinds.Disabled) != 0;

    /// <summary>Returns true if a node with the given Id is in the tree just rendered.</summary>
    public bool IsVisible(string id)
        => FindById(id) is not null;

    /// <summary>
    /// The words of a text, or of the first text under the node — a button's
    /// label. Null when there is none.
    /// </summary>
    public string? GetText(string id)
    {
        var node = FindById(id);
        return node is not null && FirstText(node) is { } text
            ? new string(text.Content)
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

    /// <summary>The logical-pixel rect of the node with the given Id.</summary>
    public System.Drawing.RectangleF GetBounds(string id)
    {
        var node = FindById(id)
            ?? throw new InvalidOperationException($"No node with Id '{id}' in the last frame's tree.");
        return new(node.Rect.X, node.Rect.Y, node.Extent.X, node.Extent.Y);
    }

    // ── Async helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="condition"/> up to <paramref name="timeout"/>,
    /// checking every <paramref name="pollInterval"/>. Throws if the condition
    /// is not satisfied within the timeout.
    /// </summary>
    public void WaitFor(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null, string? message = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            _runtime.EndBuild(); // flush any pending invalidations
            if (condition()) return;
            Thread.Sleep(interval);
        }
        throw new TimeoutException(message ?? $"WaitFor condition was not met within {timeout.TotalSeconds:F1}s.");
    }

    // ── Cursor and keyboard ──────────────────────────────────────────────────

    /// <summary>
    /// Types one character, as the keyboard would. It reaches the field the
    /// application has focused, which reads it when it asks.
    /// </summary>
    public void TypeChar(char ch)
    {
        _runtime.ProcessEvent(new BibUiEvent
        {
            Kind = BibUiEventKind.Text, Key = Key.Unknown, Char = ch, CodePoint = ch,
        });
        _runtime.EndBuild();
    }

    /// <summary>
    /// One key, with modifiers, as the window would report it. Says what the
    /// per-key helpers cannot: Delete, Home and End, shift-extends-selection,
    /// ctrl-jumps-a-word, Ctrl+A.
    /// </summary>
    public void Press(Key key, bool ctrl = false, bool shift = false)
    {
        PressKey(key, ctrl, shift);
        _runtime.EndBuild();
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

    /// <summary>Presses Backspace.</summary>
    public void PressBackspace()
    {
        PressKey(Key.Backspace);
        _runtime.EndBuild();
    }

    /// <summary>Presses the left arrow.</summary>
    public void PressArrowLeft()
    {
        PressKey(Key.Left);
        _runtime.EndBuild();
    }

    /// <summary>Presses the right arrow.</summary>
    public void PressArrowRight()
    {
        PressKey(Key.Right);
        _runtime.EndBuild();
    }

    // ── Visual ───────────────────────────────────────────────────────────────

    /// <summary>Returns the current render as a PNG byte array.</summary>
    public byte[] Screenshot()
    {
        Render();
        int w = SurfaceOptions.PhysicalSize(_runtime.SurfaceWidth, _runtime.ScaleFactor);
        int h = SurfaceOptions.PhysicalSize(_runtime.SurfaceHeight, _runtime.ScaleFactor);
        var bgra = GC.AllocateArray<byte>(w * h * 4, pinned: true);
        _draw(_runtime.DrawList, new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), w, h, w * 4));
        return Png.Encode(bgra, w, h, w * 4, ChannelOrder.Bgra, AlphaMode.Premultiplied);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _disposed = true;
        _ownRenderer?.Dispose();
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private void Render()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(BibuiPage));
        _runtime.EndBuild();
    }
}
