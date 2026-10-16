using Bibui.TextEdit;

namespace Bibui.Runtime.Platform;

/// <summary>
/// What a window's events mean, turned into what the runtime acts on: the part
/// of a platform's <c>BeginFrame</c> that is the same on every platform. A
/// platform appends what happened to <see cref="Events"/>; <see cref="Apply"/>
/// translates them and hands them to the runtime.
///   Mouse press → MouseDown with a click count (double/triple click)
///   Wheel → Scroll, Shift+wheel horizontal
///   Key → a text-edit intent (<see cref="BibUiEvent.FromKey"/>), reading the clipboard on paste
///
/// <para>Owned by one window, on its loop's thread.</para>
/// </summary>
public sealed class WindowInput
{
    /// <summary>
    /// What happened since the last frame, in order, positions in logical px.
    /// The platform's callbacks append here and do nothing else, so no runtime
    /// call runs inside the platform's own dispatch; <see cref="Apply"/> drains it.
    /// </summary>
    public readonly List<WindowEvent> Events = [];

    // What the window events mean, which is what the runtime acts on. Translated
    // here because this is the OS boundary — it is where a Paste can reach the
    // system clipboard, and where a key becomes a text-editing intent.
    private readonly List<BibUiEvent> _intents = new();

    // This frame's move, if one arrived. Held so later moves can update it in
    // place rather than pile up: the intent keeps its position in the sequence
    // and ends up carrying where the pointer finished.
    private BibUiEvent? _moveIntent;

    // Multi-click tracking. Here rather than in the runtime because it needs the
    // raw stream and a clock: presses that make up a double-click arrive in
    // different frames, and the runtime only ever sees one frame's worth.
    private long  _lastClickTicks;
    private float _lastClickX, _lastClickY;
    private int   _clickStreak;
    private const long MultiClickWindowMs = 500;
    private const float MultiClickRadius  = 5f;

    /// <summary>
    /// How many presses in a row this one is: 1, 2 or 3. A press restarts the
    /// count unless it lands close enough to the last one, soon enough after it.
    /// </summary>
    private int CountClick(float x, float y)
    {
        long nowMs = Environment.TickCount64;
        bool inWindow = (nowMs - _lastClickTicks) <= MultiClickWindowMs
                     && MathF.Abs(x - _lastClickX) <= MultiClickRadius
                     && MathF.Abs(y - _lastClickY) <= MultiClickRadius;
        _clickStreak = inWindow ? Math.Min(_clickStreak + 1, 3) : 1;
        _lastClickTicks = nowMs;
        _lastClickX = x; _lastClickY = y;
        return _clickStreak;
    }

    /// <summary>
    /// Translates <see cref="Events"/> and hands them to <paramref name="runtime"/>,
    /// then empties them. <paramref name="held"/> is the modifiers held now, asked
    /// of the platform: a value that is asked for cannot go stale. Super reports as
    /// Ctrl as well, so Cmd-drag on macOS means Ctrl-drag elsewhere. True when
    /// anything arrived.
    /// </summary>
    public bool Apply(BibuiRuntime runtime, KeyModifiers held)
    {
        if ((held & KeyModifiers.Super) != 0) held |= KeyModifiers.Ctrl;
        bool any = Events.Count > 0;
        _intents.Clear();
        _moveIntent = null;
        foreach (var e in Events) Translate(e, held, runtime);
        Events.Clear();
        if (_intents.Count > 0) runtime.ProcessEvents(_intents);
        return any;
    }

    /// <summary>
    /// Turns one window event into what it means, and raises the events that
    /// belong to the OS event rather than to its consequences. Mouse moves
    /// produce at most one intent between them: a frame needs where the pointer
    /// ended up, not the path it took.
    /// </summary>
    private void Translate(in WindowEvent e, KeyModifiers modifiers, BibuiRuntime runtime)
    {        

        switch (e.Kind)
        {
            case WindowEventKind.MouseDown:
                _intents.Add(new BibUiEvent
                {
                    Kind = BibUiEventKind.MouseDown, Button = e.Button, X = e.X, Y = e.Y,
                    Modifiers = modifiers,
                    ClickCount = e.Button == MouseButton.Left ? CountClick(e.X, e.Y) : 1,
                });
                break;

            case WindowEventKind.MouseUp:
                _intents.Add(new BibUiEvent
                {
                    Kind = BibUiEventKind.MouseUp, Button = e.Button, X = e.X, Y = e.Y,
                    Modifiers = modifiers,
                });
                break;

            case WindowEventKind.MouseMove:
                if (_moveIntent is null)
                {
                    _moveIntent = new BibUiEvent
                    {
                        Kind = BibUiEventKind.MouseMove, X = e.X, Y = e.Y,
                        Modifiers = modifiers,
                    };
                    _intents.Add(_moveIntent);
                }
                else
                {
                    _moveIntent.X = e.X;
                    _moveIntent.Y = e.Y;
                }
                break;

            case WindowEventKind.Scroll:
            {
                // Mice without a horizontal wheel/trackpad axis: Shift+wheel
                // scrolls horizontally instead (standard desktop convention).
                float dx = e.ScrollX * 32f;
                float dy = -e.ScrollY * 32f; // wheel up = content up = negative offset delta
                if ((modifiers & KeyModifiers.Shift) != 0 && dx == 0f) { dx = dy; dy = 0f; }

                _intents.Add(new BibUiEvent
                {
                    Kind = BibUiEventKind.Scroll, X = e.X, Y = e.Y, ScrollX = dx, ScrollY = dy,
                    Modifiers = modifiers,
                });
                break;
            }

            case WindowEventKind.KeyChar:
                if (!char.IsControl(e.Char))
                {
                    _intents.Add(new BibUiEvent
                    {
                        Kind = BibUiEventKind.Text, Key = Key.Unknown,
                        Char = e.Char, CodePoint = e.Char,
                    });
                }
                break;

            case WindowEventKind.KeyDown:
            {
                var keyEvent = BibUiEvent.FromKey(e.Key, (modifiers & KeyModifiers.Ctrl) != 0, (modifiers & KeyModifiers.Shift) != 0, runtime.ClipboardGet);
                // The switch above decided on Ctrl and Shift; this widens what
                // the event reports to everything that was held, Alt and Super
                // included, which the two bools cannot say.
                keyEvent.Modifiers = modifiers;
                _intents.Add(keyEvent);
                break;
            }

            case WindowEventKind.KeyUp:
                _intents.Add(new BibUiEvent { Kind = BibUiEventKind.Release, Key = e.Key });
                break;
        }
    }
}
