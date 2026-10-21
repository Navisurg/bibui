using Bibui.Core.Runtime.Platform;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// What input means. A platform says what happened (<see cref="WindowEvent"/>s),
/// the loop hands that to <see cref="BeginBuild"/>, and it becomes what the
/// boxes act on (<see cref="BibUiEvent"/>s), queued for the frame. Whatever speaks for a
/// keyboard and mouse — the window, a test server, a test — hands over the same
/// kind of list, so they all take one path.
/// </summary>
public sealed partial class BibuiRuntime
{
    /// <summary>
    /// How this reaches the system clipboard. By default, the clipboard of the
    /// window the runtime runs in (<see cref="WindowClipboard"/>, set by the
    /// platform when it opens), and none (null, nothing written) for a runtime
    /// with no window. A seam because a test asserting on the real clipboard
    /// would clobber whatever the developer had copied, and headless CI may have
    /// none at all.
    /// </summary>
    public Func<string?> ClipboardGet
    {
        get => _clipboardGet ??= () => WindowClipboard.Get?.Invoke();
        set => _clipboardGet = value;
    }

    /// <inheritdoc cref="ClipboardGet"/>
    public Action<string> ClipboardSet
    {
        get => _clipboardSet ??= text => WindowClipboard.Set?.Invoke(text);
        set => _clipboardSet = value;
    }

    // Created once, on first read, so passing ClipboardGet along per key press
    // allocates nothing.
    private Func<string?>? _clipboardGet;
    private Action<string>? _clipboardSet;

    /// <summary>
    /// The clipboard of the window the runtime runs in: set by a platform when it
    /// opens. What the default <see cref="ClipboardGet"/> and
    /// <see cref="ClipboardSet"/> reach; an app that set its own keeps it.
    /// </summary>
    public (Func<string?>? Get, Action<string>? Set) WindowClipboard { get; set; }

    /// <summary>
    /// Queues events already translated, in order, for the frame built next: for
    /// a test or a tool that says exactly what happened (a press with a click
    /// count of 2, a press addressed to one box). What a keyboard and mouse did
    /// goes to <see cref="BeginBuild"/> instead.
    /// </summary>
    public void ProcessEvents(IReadOnlyList<BibUiEvent> events)
    {
        foreach (var ev in events)
        {
            NotePointer(ev);

            // Nothing is taken: every event stays in the queue for the boxes
            // that ask. A pointer event asks for a frame, because hot state
            // follows the pointer.
            PushEvent().CopyFrom(ev);
            if (ev.Kind is BibUiEventKind.MouseDown or BibUiEventKind.MouseUp or BibUiEventKind.MouseMove)
                RequestFrame();
        }
    }

    /// <summary>One event, for a caller that has exactly one.</summary>
    public void ProcessEvent(BibUiEvent ev) => ProcessEvents(new[] { ev });

    // ── WindowEvents to BibUiEvents ──────────────────────────────────────────
    // Loop thread. How many presses in a row the last one was: kept here, not
    // per frame, because a double-click's presses land in different frames.

    private long _lastClickMs;
    private float _lastClickX, _lastClickY;
    private int _clickStreak;
    private const long MultiClickWindowMs = 500;
    private const float MultiClickRadius = 5f;

    /// <summary>
    /// Turns what a keyboard and mouse did into what the frame's boxes act on,
    /// queues it for the frame <see cref="BeginBuild"/> opens, and empties
    /// <paramref name="events"/>.
    ///   Mouse press → MouseDown, with a click count (double, triple click)
    ///   Mouse moves → one MouseMove, where the pointer ended up
    ///   Wheel → Scroll, in logical px; Shift+wheel horizontal
    ///   Key → a text-edit intent (<see cref="BibUiEvent.FromKey"/>), reading the clipboard on paste
    /// Modifiers are what each event says was held; Super reads as Ctrl as well,
    /// so Cmd on macOS is Ctrl elsewhere.
    /// </summary>
    private void ApplyInput(List<WindowEvent> events)
    {
        // This frame's move, held so later moves update it in place: it keeps its
        // place in the sequence and ends up carrying where the pointer finished.
        BibUiEvent? move = null;
        foreach (var e in events)
        {
            var held = (e.Modifiers & KeyModifiers.Super) != 0 ? e.Modifiers | KeyModifiers.Ctrl : e.Modifiers;
            switch (e.Kind)
            {
                case WindowEventKind.MouseDown:
                case WindowEventKind.MouseUp:
                {
                    var ev = PushEvent();
                    ev.Kind = e.Kind == WindowEventKind.MouseDown ? BibUiEventKind.MouseDown : BibUiEventKind.MouseUp;
                    (ev.Button, ev.X, ev.Y, ev.Modifiers) = (e.Button, e.X, e.Y, held);
                    if (e.Kind == WindowEventKind.MouseDown && e.Button == MouseButton.Left) ev.ClickCount = CountClick(e.X, e.Y);
                    Noted(ev);
                    break;
                }
                case WindowEventKind.MouseMove:
                    if (move is null)
                    {
                        move = PushEvent();
                        move.Kind = BibUiEventKind.MouseMove;
                    }
                    (move.X, move.Y, move.Modifiers) = (e.X, e.Y, held);
                    Noted(move);
                    break;

                case WindowEventKind.Scroll:
                {
                    // A notch is 32px. Wheel up is content up: a negative offset.
                    // Without a horizontal wheel, Shift+wheel scrolls across.
                    float dx = e.ScrollX * 32f, dy = -e.ScrollY * 32f;
                    if ((held & KeyModifiers.Shift) != 0 && dx == 0f) (dx, dy) = (dy, 0f);
                    var ev = PushEvent();
                    ev.Kind = BibUiEventKind.Scroll;
                    (ev.X, ev.Y, ev.ScrollX, ev.ScrollY, ev.Modifiers) = (e.X, e.Y, dx, dy, held);
                    Noted(ev);
                    break;
                }
                case WindowEventKind.KeyChar:
                    if (!System.Text.Rune.IsControl(e.CodePoint))
                    {
                        // Char is the character only where one char holds it; CodePoint always is.
                        var ev = PushEvent();
                        ev.Kind = BibUiEventKind.Text;
                        ev.Char = e.CodePoint.IsBmp ? (char)e.CodePoint.Value : null;
                        ev.CodePoint = (uint)e.CodePoint.Value;
                    }
                    break;

                case WindowEventKind.KeyDown:
                {
                    var ev = PushEvent();
                    BibUiEvent.FillFromKey(ev, e.Key, (held & KeyModifiers.Ctrl) != 0, (held & KeyModifiers.Shift) != 0, ClipboardGet);
                    // FromKey decided on Ctrl and Shift; the event reports all that
                    // was held, Alt and Super included.
                    ev.Modifiers = held;
                    break;
                }
                case WindowEventKind.KeyUp:
                {
                    var ev = PushEvent();
                    (ev.Kind, ev.Key) = (BibUiEventKind.Release, e.Key);
                    break;
                }
            }
        }
        events.Clear();
    }

    // The pointer's position, and a frame for pointer input: hot state follows it.
    private void Noted(BibUiEvent ev)
    {
        NotePointer(ev);
        RequestFrame();
    }

    // How many presses in a row this one is: 1, 2 or 3. A press restarts the
    // count unless it lands close to the last one, soon after it.
    private int CountClick(float x, float y)
    {
        long nowMs = Environment.TickCount64;
        bool inWindow = nowMs - _lastClickMs <= MultiClickWindowMs
                     && MathF.Abs(x - _lastClickX) <= MultiClickRadius
                     && MathF.Abs(y - _lastClickY) <= MultiClickRadius;
        _clickStreak = inWindow ? Math.Min(_clickStreak + 1, 3) : 1;
        (_lastClickMs, _lastClickX, _lastClickY) = (nowMs, x, y);
        return _clickStreak;
    }
}
