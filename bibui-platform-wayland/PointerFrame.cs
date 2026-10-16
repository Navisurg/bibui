using Bibui.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>
/// Folds wl_pointer's events into <see cref="WindowEvent"/>s, one
/// wl_pointer.frame at a time. A frame groups what happened at once —
/// a motion and a button, both axes of a two-finger scroll, a wheel click's
/// axis value with its value120 — so it is reported once: at most one
/// <see cref="WindowEventKind.MouseMove"/> (where the pointer ended up), then
/// the buttons in the order they changed, then at most one
/// <see cref="WindowEventKind.Scroll"/> carrying both axes.
///
/// <para>A pointer older than version 5 has no frame event; every event is
/// then a frame of its own (<see cref="Framed"/> false).</para>
///
/// <para><b>Scroll units</b> are the Silk host's: GLFW's sign (positive Y is
/// the wheel turned away from the user — up; positive X is left) and one wheel
/// notch is 1.0, which the runtime multiplies by 32 px. A wheel says how far it
/// turned in value120 (120 a notch, a fraction of that from a high-resolution
/// wheel), or before version 8 in whole axis_discrete steps; a touchpad or
/// other continuous source has only the axis value, in surface pixels, which
/// is divided by 10 as GLFW divides it.</para>
///
/// <para>Positions are surface-local, which is logical window coordinates —
/// what the Silk host reports too. The scale does not change them.</para>
/// </summary>
internal sealed class PointerFrame(Action<WindowEvent> emit, Func<KeyModifiers> modifiers)
{
    /// <summary>Axis value per wheel notch for a continuous source: GLFW's divisor.</summary>
    public const float ContinuousPerNotch = 10f;

    /// <summary>value120 per wheel notch.</summary>
    public const float Value120PerNotch = 120f;

    private readonly Action<WindowEvent> _emit = emit;
    // The modifiers held now, stamped on each event as it is reported.
    private readonly Func<KeyModifiers> _modifiers = modifiers;

    /// <summary>True when the pointer sends wl_pointer.frame (version 5 and later).</summary>
    public bool Framed { get; set; } = true;

    /// <summary>Where the pointer is, in surface coordinates. Kept after it leaves.</summary>
    public float X { get; private set; }
    public float Y { get; private set; }

    /// <summary>True while the pointer is over the surface.</summary>
    public bool Inside { get; private set; }

    // Buttons held now, so a leave can release them; GLFW's state too.
    private readonly List<MouseButton> _held = [];

    // The frame being gathered.
    private bool _moved;
    private readonly List<(MouseButton Button, bool Down)> _buttons = [];
    private Axis _vertical, _horizontal;

    private struct Axis
    {
        public float Value;      // sum of wl_pointer.axis values
        public int Discrete;     // sum of axis_discrete steps (v5–7)
        public int Value120;     // sum of axis_value120 (v8+)
        public bool HasValue, HasDiscrete, HasValue120;

        /// <summary>Notches, in wl_pointer's sign (positive is down, or right).</summary>
        public readonly float Notches =>
            HasValue120 ? Value120 / Value120PerNotch
            : HasDiscrete ? Discrete
            : HasValue ? Value / ContinuousPerNotch
            : 0f;
    }

    public void Enter(float x, float y)
    {
        Inside = true;
        X = x; Y = y;
        _moved = true;
        EndUnframed();
    }

    /// <summary>
    /// The pointer left the surface. A button still held is released where the
    /// pointer was last seen: its release will go to whatever the pointer is
    /// over now, so without this a drag would never end.
    /// </summary>
    public void Leave()
    {
        Inside = false;
        _moved = false;
        Frame();
        ReleaseAll();
    }

    public void Motion(float x, float y)
    {
        X = x; Y = y;
        _moved = true;
        EndUnframed();
    }

    public void Button(uint code, bool pressed)
    {
        var b = KeyTable.Button(code);
        if (b == MouseButton.Unknown) return;
        _buttons.Add((b, pressed));
        EndUnframed();
    }

    /// <summary>wl_pointer.axis: <paramref name="horizontal"/> is axis 1; the value is in surface pixels.</summary>
    public void AxisValue(bool horizontal, float value)
    {
        ref var a = ref horizontal ? ref _horizontal : ref _vertical;
        a.Value += value;
        a.HasValue = true;
        EndUnframed();
    }

    /// <summary>wl_pointer.axis_discrete (versions 5–7): whole wheel steps.</summary>
    public void AxisDiscrete(bool horizontal, int steps)
    {
        ref var a = ref horizontal ? ref _horizontal : ref _vertical;
        a.Discrete += steps;
        a.HasDiscrete = true;
    }

    /// <summary>wl_pointer.axis_value120 (version 8): 120 to a wheel notch.</summary>
    public void AxisValue120(bool horizontal, int value120)
    {
        ref var a = ref horizontal ? ref _horizontal : ref _vertical;
        a.Value120 += value120;
        a.HasValue120 = true;
    }

    /// <summary>wl_pointer.frame: everything gathered since the last one happened together. Reports it.</summary>
    public void Frame()
    {
        if (_moved && Inside) _emit(WindowEvent.Mouse(WindowEventKind.MouseMove, X, Y, MouseButton.Unknown, _modifiers()));
        _moved = false;

        foreach (var (b, down) in _buttons)
        {
            if (down)
            {
                if (_held.Contains(b)) continue; // a press the compositor repeated: one is enough
                _held.Add(b);
                _emit(WindowEvent.Mouse(WindowEventKind.MouseDown, X, Y, b, _modifiers()));
            }
            else if (_held.Remove(b))
            {
                _emit(WindowEvent.Mouse(WindowEventKind.MouseUp, X, Y, b, _modifiers()));
            }
        }
        _buttons.Clear();

        // wl_pointer: positive is down or right. GLFW (and so the Silk host): positive is up or left.
        float sx = 0f - _horizontal.Notches, sy = 0f - _vertical.Notches; // 0f - x: no -0
        if (sx != 0f || sy != 0f) _emit(WindowEvent.Wheel(X, Y, sx, sy, _modifiers()));
        _horizontal = default;
        _vertical = default;
    }

    /// <summary>Releases every button still held, at the last position. Focus left, or the pointer did.</summary>
    public void ReleaseAll()
    {
        foreach (var b in _held) _emit(WindowEvent.Mouse(WindowEventKind.MouseUp, X, Y, b, _modifiers()));
        _held.Clear();
    }

    private void EndUnframed()
    {
        if (!Framed) Frame();
    }
}
