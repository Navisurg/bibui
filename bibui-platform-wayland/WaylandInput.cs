using System.Diagnostics;
using System.Runtime.InteropServices;
using Bibui.Core;
using Bibui.Platform.Wayland.Protocol;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>
/// Pointer and keyboard: wl_seat, wl_pointer and wl_keyboard, turned
/// into the <see cref="WindowEvent"/>s the Silk host produces, with the same
/// <c>Key</c> and <c>MouseButton</c> values.
///
/// <list type="bullet">
/// <item><b>Pointer</b>: enter, leave, motion, button and axis are gathered per
/// wl_pointer.frame and reported by <see cref="PointerFrame"/> — one move per
/// frame, then buttons, then one scroll in wheel notches (value120 when the
/// seat has it). Positions are surface-local: logical window coordinates.</item>
/// <item><b>Keyboard</b>: the compositor's keymap goes to libxkbcommon
/// (<see cref="XkbKeyboard"/>); a press reports its <c>Key</c>, then the text
/// it types after dead keys and Compose. Ctrl and Alt make it a shortcut, which
/// types nothing — as GLFW.</item>
/// <item><b>Modifiers</b> come from wl_keyboard.modifiers through the xkb state,
/// and are asked (<see cref="HeldModifiers"/>) as each event is reported and
/// stamped on it, never accumulated. None while the window does not have the
/// keyboard.</item>
/// <item><b>Repeat</b> is the client's on Wayland. Timed by
/// <see cref="KeyRepeat"/> on a timerfd that the host polls beside the display
/// (<see cref="RepeatFd"/>), so it runs on the loop thread. A repeat sends the
/// key down again, marked <see cref="WindowEvent.Repeat"/>, and types its text
/// again — as GLFW reports one, and as the Silk window passes it on.</item>
/// <item><b>Focus</b>: on wl_keyboard.leave every held key and button is
/// released, the repeat stops, and FocusLost follows — GLFW's order.</item>
/// </list>
///
/// <para>Listeners queue through <c>emit</c> and do nothing else: no runtime
/// call from inside dispatch.</para>
/// </summary>
internal sealed unsafe class WaylandInput : IWaylandFeature
{
    /// <summary>The highest wl_seat version asked for. 10 would let the compositor repeat keys itself; repeat stays the client's.</summary>
    public const uint SeatVersion = 9;

    private readonly Action<WindowEvent> _emit;
    private readonly PointerFrame _frame;
    private readonly KeyRepeat _repeat = new();
    private readonly HashSet<uint> _keysDown = [];

    private GCHandle _self;
    private uint _seatName;
    private WlSeat _seat;
    private WlPointer _pointer;
    private WlKeyboard _keyboard;
    private XkbKeyboard? _xkb;
    private bool _xkbUnavailable;
    private bool _keyboardFocused;
    private int _timerFd = -1;

    private readonly Action<uint>? _inputSerial;

    /// <param name="emit">Where events go: the host's queue.</param>
    /// <param name="inputSerial">
    /// Told the serial of every wl_keyboard.enter, wl_keyboard.key and
    /// wl_pointer.button — the host's <c>LastInputSerial</c>, which the
    /// clipboard quotes in set_selection.
    /// </param>
    /// <param name="cursor">
    /// The cursor: told when the pointer is made and released, and of
    /// every enter and leave — the shape is set per enter, quoting its serial.
    /// </param>
    public WaylandInput(Action<WindowEvent> emit, Action<uint>? inputSerial = null, IPointerCursorHooks? cursor = null)
    {
        _emit = emit;
        _inputSerial = inputSerial;
        _cursor = cursor;
        _frame = new PointerFrame(emit, () => HeldModifiers);
    }

    private readonly IPointerCursorHooks? _cursor;

    /// <summary>What the keyboard holds now; nothing while the window does not have it.</summary>
    public KeyModifiers HeldModifiers =>
        _keyboardFocused && _xkb is not null ? _xkb.HeldModifiers : KeyModifiers.None;

    /// <summary>The seat's pointer, or a null proxy while there is none.</summary>
    public WlPointer Pointer => _pointer;

    /// <summary>The key-repeat timerfd for the host to poll, or -1 while there is none.</summary>
    public int RepeatFd => _timerFd;

    /// <summary>The repeat schedule; repeat_info sets it.</summary>
    internal KeyRepeat Repeat => _repeat;

    /// <summary>A keyboard compiled from names, standing in for the compositor's keymap — for tests, which have no compositor.</summary>
    internal void UseKeyboard(XkbKeyboard xkb)
    {
        _xkb?.Dispose();
        _xkb = xkb;
    }

    // ── IWaylandFeature ──────────────────────────────────────────────────────

    public bool TryBind(WlRegistry registry, uint name, string iface, uint version)
    {
        if (iface != WlSeat.InterfaceName || !_seat.IsNull) return false; // the first seat only
        if (!_self.IsAllocated) _self = GCHandle.Alloc(this);
        _seatName = name;
        _seat = registry.Bind<WlSeat>(name, Math.Min(version, SeatVersion));
        _seat.AddListener(SeatListener, Data);
        return true;
    }

    public void OnGlobalRemove(uint name)
    {
        if (_seat.IsNull || name != _seatName) return;
        DropKeyboard();
        DropPointer();
        ReleaseSeat();
    }

    public void Destroy()
    {
        DropKeyboard();
        DropPointer();
        ReleaseSeat();
        _xkb?.Dispose();
        _xkb = null;
        if (_timerFd >= 0) Libc.close(_timerFd);
        _timerFd = -1;
        if (_self.IsAllocated) _self.Free();
    }

    private void* Data => (void*)GCHandle.ToIntPtr(_self);

    private void ReleaseSeat()
    {
        if (_seat.IsNull) return;
        if (_seat.Version >= WlSeat.ReleaseSince) _seat.Release();
        else _seat.DestroyProxy();
        _seat = default;
    }

    // ── Devices ──────────────────────────────────────────────────────────────

    private void OnCapabilities(WlSeat.Capability caps)
    {
        bool pointer = (caps & WlSeat.Capability.Pointer) != 0;
        bool keyboard = (caps & WlSeat.Capability.Keyboard) != 0;

        if (pointer && _pointer.IsNull)
        {
            _pointer = _seat.GetPointer();
            _pointer.AddListener(PointerListener, Data);
            _frame.Framed = _pointer.Version >= WlPointer.Listener.FrameSince;
            _cursor?.AttachPointer(_pointer);
        }
        else if (!pointer) DropPointer();

        if (keyboard && _keyboard.IsNull)
        {
            _keyboard = _seat.GetKeyboard();
            _keyboard.AddListener(KeyboardListener, Data);
        }
        else if (!keyboard) DropKeyboard();
    }

    private void DropPointer()
    {
        if (_pointer.IsNull) return;
        _frame.Leave();
        _cursor?.DetachPointer();
        if (_pointer.Version >= WlPointer.ReleaseSince) _pointer.Release();
        else _pointer.DestroyProxy();
        _pointer = default;
    }

    private void DropKeyboard()
    {
        if (_keyboard.IsNull) return;
        if (_keyboardFocused) LoseFocus();
        if (_keyboard.Version >= WlKeyboard.ReleaseSince) _keyboard.Release();
        else _keyboard.DestroyProxy();
        _keyboard = default;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnKeymap(WlKeyboard.KeymapFormat format, int fd, uint size)
    {
        try
        {
            if (format != WlKeyboard.KeymapFormat.XkbV1 || size == 0) return;
            var xkb = Xkb();
            if (xkb is null) return;
            // Version 7 and later require MAP_PRIVATE: the compositor may share one fd.
            nint map = Libc.mmap(0, size, Libc.PROT_READ, InputLibc.MAP_PRIVATE, fd, 0);
            if (map == Libc.MAP_FAILED) return;
            try { xkb.LoadKeymap((byte*)map, size, composeLocale: null); }
            finally { Libc.munmap(map, size); }
        }
        finally
        {
            Libc.close(fd);
        }
    }

    /// <summary>The xkb keyboard, made on first use; null on a machine without libxkbcommon, where keys still report but type nothing.</summary>
    private XkbKeyboard? Xkb()
    {
        if (_xkb is not null || _xkbUnavailable) return _xkb;
        if (!XkbNative.IsAvailable) { _xkbUnavailable = true; return null; }
        return _xkb = XkbKeyboard.Create();
    }

    internal void OnKeyboardEnter()
    {
        // The keys already held as focus arrives are not pressed here: GLFW
        // ignores them too, and reporting them would fire their shortcuts.
        _keyboardFocused = true;
    }

    internal void OnKeyboardLeave()
    {
        if (_keyboardFocused) LoseFocus();
    }

    /// <summary>
    /// Focus went elsewhere: what was held is released — keys, then buttons —
    /// the repeat stops, a half-typed Compose sequence is dropped, and
    /// FocusLost says so. GLFW's order on losing focus.
    /// </summary>
    private void LoseFocus()
    {
        _keyboardFocused = false;
        foreach (var code in _keysDown) _emit(WindowEvent.KeyRelease(KeyFor(code), HeldModifiers));
        _keysDown.Clear();
        _repeat.Stop();
        ArmRepeat();
        _xkb?.ResetCompose();
        _xkb?.UpdateMask(0, 0, 0, 0);
        _frame.ReleaseAll();
        _emit(WindowEvent.FocusLost());
    }

    private Key KeyFor(uint code) =>
        _xkb is { HasKeymap: true } xkb ? xkb.KeyFor(code) : KeyTable.FromEvdev(code);

    internal void OnKey(uint code, WlKeyboard.KeyState state)
    {
        if (state == WlKeyboard.KeyState.Released)
        {
            if (!_keysDown.Remove(code)) return;
            _emit(WindowEvent.KeyRelease(KeyFor(code), HeldModifiers));
            _repeat.Release(code);
            ArmRepeat();
            return;
        }
        if (state != WlKeyboard.KeyState.Pressed) return; // Repeated (v10) is not asked for

        _keysDown.Add(code);
        _emit(WindowEvent.KeyPress(KeyFor(code), HeldModifiers));
        EmitText(_xkb?.Press(code));

        // GLFW's rule: a key that repeats takes the repeat over; one that does
        // not (a modifier) leaves the held key repeating.
        if (_xkb is { HasKeymap: true } xkb && xkb.Repeats(code))
        {
            _repeat.Press(code, NowMs());
            ArmRepeat();
        }
    }

    internal void EmitText(string? text)
    {
        if (text is null) return;
        // One event per character: an emoji is two chars, and half of one types nothing.
        foreach (var r in text.EnumerateRunes()) _emit(WindowEvent.Text(r));
    }

    // ── Repeat ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The repeat timer fired: the host saw <see cref="RepeatFd"/> readable.
    /// For each repeat due, the held key goes down again (a
    /// <see cref="WindowEvent.KeyRepeat"/>, so a held Backspace or arrow
    /// repeats) and types its text again — its text now, so Shift pressed
    /// mid-repeat changes case — as GLFW reports a repeat; then it arms the
    /// timer for the next.
    /// </summary>
    public void OnRepeatTimer()
    {
        if (_timerFd < 0) return;
        ulong expirations;
        Libc.read(_timerFd, &expirations, sizeof(ulong)); // drain; the schedule counts, not the fd
        if (_repeat.Key is uint code && _keyboardFocused)
        {
            int n = _repeat.Take(NowMs());
            for (int i = 0; i < n; i++)
            {
                _emit(WindowEvent.KeyRepeat(KeyFor(code), HeldModifiers));
                EmitText(_xkb?.Text(code));
            }
        }
        ArmRepeat();
    }

    /// <summary>Arms the timerfd for the next repeat, or disarms it when none is due.</summary>
    private void ArmRepeat()
    {
        if (_repeat.NextDue is not double due)
        {
            if (_timerFd >= 0) SetTimer(0);
            return;
        }
        if (_timerFd < 0)
        {
            _timerFd = InputLibc.timerfd_create(InputLibc.CLOCK_MONOTONIC, InputLibc.TFD_CLOEXEC | InputLibc.TFD_NONBLOCK);
            if (_timerFd < 0) throw new InvalidOperationException($"timerfd_create failed: errno {Libc.Errno}");
        }
        // Relative, and never zero: a zero it_value disarms.
        SetTimer(Math.Max(1L, (long)((due - NowMs()) * 1_000_000.0)));
    }

    private void SetTimer(long ns)
    {
        var spec = new InputLibc.ITimerSpec { ValueSec = ns / 1_000_000_000L, ValueNsec = ns % 1_000_000_000L };
        InputLibc.timerfd_settime(_timerFd, 0, &spec, null);
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    // ── Listeners ────────────────────────────────────────────────────────────

    private static WaylandInput Self(void* data) => (WaylandInput)GCHandle.FromIntPtr((nint)data).Target!;

    private static readonly WlSeat.Listener* SeatListener =
        WlSeat.Listener.Create(new() { Capabilities = &OnSeatCapabilities });

    private static readonly WlPointer.Listener* PointerListener = WlPointer.Listener.Create(new()
    {
        Enter = &OnPointerEnter, Leave = &OnPointerLeave, Motion = &OnPointerMotion, Button = &OnPointerButton,
        Axis = &OnPointerAxis, Frame = &OnPointerFrame, AxisDiscrete = &OnPointerAxisDiscrete, AxisValue120 = &OnPointerAxisValue120,
    });

    private static readonly WlKeyboard.Listener* KeyboardListener = WlKeyboard.Listener.Create(new()
    {
        Keymap = &OnKeyboardKeymap, Enter = &OnKeyboardEnter, Leave = &OnKeyboardLeave, Key = &OnKeyboardKey,
        Modifiers = &OnKeyboardModifiers, RepeatInfo = &OnKeyboardRepeatInfo,
    });

    // A listener that throws would unwind through libwayland. The host's own
    // listeners hand the exception to the loop; these are its feature's, and
    // one bad event must not take input down with it, so they swallow it.
    private static void Guard(void* data, Action<WaylandInput> body)
    {
        try { body(Self(data)); }
        catch (Exception e) { Trace.TraceError($"[wayland input] {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnSeatCapabilities(void* data, WlSeat seat, WlSeat.Capability caps) =>
        Guard(data, s => s.OnCapabilities(caps));

    [UnmanagedCallersOnly]
    private static void OnPointerEnter(void* data, WlPointer pointer, uint serial, WlSurface surface, WlFixed x, WlFixed y)
    {
        var s = Self(data);
        s._frame.Enter((float)x.ToDouble(), (float)y.ToDouble());
        try { s._cursor?.OnPointerEnter(serial); }
        catch (Exception e) { Trace.TraceError($"[wayland input] pointer enter: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnPointerLeave(void* data, WlPointer pointer, uint serial, WlSurface surface)
    {
        var s = Self(data);
        s._frame.Leave();
        try { s._cursor?.OnPointerLeave(); }
        catch (Exception e) { Trace.TraceError($"[wayland input] pointer leave: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnPointerMotion(void* data, WlPointer pointer, uint time, WlFixed x, WlFixed y) =>
        Self(data)._frame.Motion((float)x.ToDouble(), (float)y.ToDouble());

    [UnmanagedCallersOnly]
    private static void OnPointerButton(void* data, WlPointer pointer, uint serial, uint time, uint button, WlPointer.ButtonState state)
    {
        var s = Self(data);
        s._inputSerial?.Invoke(serial);
        s._frame.Button(button, state == WlPointer.ButtonState.Pressed);
    }

    [UnmanagedCallersOnly]
    private static void OnPointerAxis(void* data, WlPointer pointer, uint time, WlPointer.Axis axis, WlFixed value) =>
        Self(data)._frame.AxisValue(axis == WlPointer.Axis.HorizontalScroll, (float)value.ToDouble());

    [UnmanagedCallersOnly]
    private static void OnPointerAxisDiscrete(void* data, WlPointer pointer, WlPointer.Axis axis, int discrete) =>
        Self(data)._frame.AxisDiscrete(axis == WlPointer.Axis.HorizontalScroll, discrete);

    [UnmanagedCallersOnly]
    private static void OnPointerAxisValue120(void* data, WlPointer pointer, WlPointer.Axis axis, int value120) =>
        Self(data)._frame.AxisValue120(axis == WlPointer.Axis.HorizontalScroll, value120);

    [UnmanagedCallersOnly]
    private static void OnPointerFrame(void* data, WlPointer pointer) => Self(data)._frame.Frame();

    [UnmanagedCallersOnly]
    private static void OnKeyboardKeymap(void* data, WlKeyboard keyboard, WlKeyboard.KeymapFormat format, int fd, uint size)
    {
        // The fd is ours to close whatever happens.
        try { Self(data).OnKeymap(format, fd, size); }
        catch (Exception e) { Trace.TraceError($"[wayland input] keymap: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnKeyboardEnter(void* data, WlKeyboard keyboard, uint serial, WlSurface surface, WlArray* keys)
    {
        var s = Self(data);
        s._inputSerial?.Invoke(serial);
        s.OnKeyboardEnter();
    }

    [UnmanagedCallersOnly]
    private static void OnKeyboardLeave(void* data, WlKeyboard keyboard, uint serial, WlSurface surface) =>
        Guard(data, s => s.OnKeyboardLeave());

    [UnmanagedCallersOnly]
    private static void OnKeyboardKey(void* data, WlKeyboard keyboard, uint serial, uint time, uint key, WlKeyboard.KeyState state)
    {
        Self(data)._inputSerial?.Invoke(serial);
        Guard(data, s => s.OnKey(key, state));
    }

    [UnmanagedCallersOnly]
    private static void OnKeyboardModifiers(void* data, WlKeyboard keyboard, uint serial, uint depressed, uint latched, uint locked, uint group) =>
        Self(data)._xkb?.UpdateMask(depressed, latched, locked, group);

    [UnmanagedCallersOnly]
    private static void OnKeyboardRepeatInfo(void* data, WlKeyboard keyboard, int rate, int delay) =>
        Self(data)._repeat.SetInfo(rate, delay);
}

/// <summary>The libc calls key repeat and the keymap need beyond <see cref="Libc"/>'s.</summary>
internal static unsafe class InputLibc
{
    private const string C = "libc";

    public const int MAP_PRIVATE = 2;
    public const int CLOCK_MONOTONIC = 1;
    public const int TFD_CLOEXEC = 0x80000, TFD_NONBLOCK = 0x800;

    /// <summary>struct itimerspec on LP64: interval, then value, each { time_t sec; long nsec; }.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ITimerSpec
    {
        public long IntervalSec, IntervalNsec, ValueSec, ValueNsec;
    }

    [DllImport(C, ExactSpelling = true)] public static extern int timerfd_create(int clockid, int flags);
    [DllImport(C, ExactSpelling = true)] public static extern int timerfd_settime(int fd, int flags, ITimerSpec* newValue, ITimerSpec* oldValue);
}
