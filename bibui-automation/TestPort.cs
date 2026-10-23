using System.Text;
using System.Text.Json;
using Bibui.Core;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Automation;

/// <summary>What a <see cref="TestCmd"/> asks the frame loop for.</summary>
public enum TestCmdKind
{
    /// <summary>A refused request: <see cref="TestCmd.Error"/> says why.</summary>
    None,
    /// <summary><c>test.events</c>: one frame's input, in <see cref="TestCmd.Events"/>.</summary>
    Events,
    /// <summary><c>test.tree</c>: the UI tree after this frame.</summary>
    Tree,
    /// <summary><c>test.state</c>: the app's state after this frame.</summary>
    State,
    /// <summary><c>test.schema</c>: the <c>test.*</c> commands and the app's.</summary>
    Schema,
    /// <summary><c>test.screenshot</c>: this frame as a PNG at <see cref="TestCmd.Path"/>.</summary>
    Screenshot,
    /// <summary>Any other name: one of the app's commands, as sent, in <see cref="TestCmd.Json"/>.</summary>
    App,
}

/// <summary>
/// One request from a client, as the frame loop sees it: taken from the
/// <see cref="TestPort"/>'s request buffer by <see cref="TestPort.TryPull"/> at the
/// top of the frame, and answered at its end. A fat struct on purpose: every
/// request has one shape, so it is read with one switch and allocates nothing.
/// Each field means something for the kinds noted beside it, and nothing for the rest.
///
/// <para>Its spans point into memory the loop owns until the request is answered,
/// the request buffer and the port's event array, so it lives for the frame and
/// no longer: a <c>ref struct</c>, never stored, never handed to another thread.</para>
/// </summary>
public readonly ref struct TestCmd
{
    public readonly TestCmdKind Kind;
    /// <summary>Events: this frame's input, in the port's event array.</summary>
    public readonly ReadOnlySpan<WindowEvent> Events;
    /// <summary>Screenshot: the file to write, UTF-8.</summary>
    public readonly ReadOnlySpan<byte> Path;
    /// <summary>App: the command as sent, for the app's own deserializer.</summary>
    public readonly ReadOnlySpan<byte> Json;
    /// <summary>None: why the request was refused.</summary>
    public readonly string? Error;

    internal TestCmd(TestCmdKind kind, ReadOnlySpan<WindowEvent> events = default,
        ReadOnlySpan<byte> path = default, ReadOnlySpan<byte> json = default, string? error = null)
    {
        Kind = kind;
        Events = events;
        Path = path;
        Json = json;
        Error = error;
    }
}

/// <summary>
/// The app's end of the test protocol: a client's requests, handed to the frame
/// loop as <see cref="TestCmd"/>s.
///
/// <para>A request is one line: a command, <c>{"$cmd":name,...}</c>, or a batch of
/// them, <c>[{...},{...}]</c>. The <c>test.*</c> names are bibui's
/// (<see cref="NameOf"/>); any other name is the app's, handed over as bytes.</para>
///
/// <para>The request buffer has one owner at a time, and its length says which:
/// 0, the port thread's, which writes a line into it and then the length; above 0,
/// the loop's, from <see cref="TryPull"/> until the request is answered, when the
/// length goes back to 0. So a <see cref="TestCmd"/>'s spans stay put for the
/// whole request.</para>
///
/// <para>The loop drains <see cref="TryPull"/> at the top of each frame, and it hands
/// out what can run in that frame, in the order a frame runs it: the app's commands
/// (run before the build), then at most one frame of events (seen by the build),
/// then queries (answered after it). A step that would break that order waits for
/// a later frame.</para>
/// </summary>
public sealed class TestPort
{
    /// <summary>The length the port thread publishes for a line longer than the request buffer.</summary>
    internal const int Oversized = -1;

    // The request buffer, its length (written last by the port thread, set back to 0
    // when the request is answered), and what each part of the loop's work needs.
    // All allocated once, for the port's life.
    private readonly byte[] _request;
    private int _requestLength;
    private readonly WindowEvent[] _events;                  // the one events step a frame takes
    private readonly byte[] _scratch;                        // unescaped strings this frame: escaped paths
    private int _scratchUsed;
    private readonly int[] _stepStart, _stepLength;          // the request's steps: one, or a batch's elements

    // The loop's, about the request in hand: its steps once found (-1 before),
    // the next to hand out, whether it was a batch, whether a refusal ended it,
    // and what this frame has taken so far.
    private int _stepCount = -1;
    private int _next;
    private bool _isBatch;
    private bool _ended;
    private FramePhase _phase;

    private enum FramePhase { Commands, Events, Queries }

    /// <param name="requestBytes">The request buffer: the longest line a client may send.</param>
    /// <param name="maxEvents">The most events one <c>test.events</c> may carry.</param>
    /// <param name="maxSteps">The most commands one batch may carry.</param>
    public TestPort(int requestBytes = 1 << 20, int maxEvents = 256, int maxSteps = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestBytes, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSteps, 1);
        _request = new byte[requestBytes];
        _events = new WindowEvent[maxEvents];
        _scratch = new byte[4096];
        _stepStart = new int[maxSteps];
        _stepLength = new int[maxSteps];
    }

    /// <summary>
    /// The <c>test.*</c> commands' names on the wire: the protocol's one list, and its
    /// public contract (renaming a kind must not change them). <see cref="TestCmdKind.App"/>
    /// and <see cref="TestCmdKind.None"/> have none: an app command is any name not in here.
    /// </summary>
    public static ReadOnlySpan<byte> NameOf(TestCmdKind kind) => kind switch
    {
        TestCmdKind.Events     => "test.events"u8,
        TestCmdKind.Tree       => "test.tree"u8,
        TestCmdKind.State      => "test.state"u8,
        TestCmdKind.Schema     => "test.schema"u8,
        TestCmdKind.Screenshot => "test.screenshot"u8,
        _                      => ""u8,
    };

    // ── The port thread's side ─────────────────────────────────────────────

    /// <summary>
    /// Puts <paramref name="line"/> (without its newline) in the request buffer and
    /// publishes it: the bytes first, the length last. The port thread's, while the
    /// buffer is its own; a line longer than the buffer is published as
    /// <see cref="Oversized"/>, for the loop to refuse.
    /// </summary>
    internal void Publish(ReadOnlySpan<byte> line)
    {
        if (Volatile.Read(ref _requestLength) != 0)
            throw new InvalidOperationException("The request buffer is the loop's until its request is answered.");
        if (line.Length > _request.Length) { Volatile.Write(ref _requestLength, Oversized); return; }
        if (line.IsEmpty) return;
        line.CopyTo(_request);
        Volatile.Write(ref _requestLength, line.Length);
    }

    // ── The loop's side ────────────────────────────────────────────────────

    /// <summary>
    /// The next step that can run in this frame, if there is one: a single command,
    /// or a batch's next. False when nothing is published, every step has been
    /// handed out, or the next must wait for a later frame. The loop's, at the top
    /// of the frame: <c>while (port.TryPull(out var cmd)) { ... }</c>.
    /// </summary>
    public bool TryPull(out TestCmd cmd)
    {
        cmd = default;
        if (_ended) return false;
        int length = Volatile.Read(ref _requestLength);
        if (length == 0) return false;
        if (length == Oversized)
            return Refuse($"The request is longer than the request buffer ({_request.Length} bytes).", out cmd);

        var line = _request.AsSpan(0, length);
        if (_stepCount < 0)
        {
            var refusal = FindSteps(line);
            if (refusal is not null) return Refuse(refusal, out cmd);
        }
        if (_next == _stepCount) return false;

        var step = line.Slice(_stepStart[_next], _stepLength[_next]);
        TestCmdKind kind;
        string? error;
        try { kind = KindOf(step, out error); }
        catch (JsonException e) { return Refuse(StepError($"Not JSON: {e.Message}"), out cmd); }
        if (error is not null) return Refuse(StepError(error), out cmd);

        //- one frame runs the app's commands, then the build sees the events, then the queries are answered
        if (!Fits(kind)) return false;

        try { cmd = Read(step, kind); }
        catch (JsonException e) { return Refuse(StepError($"Not JSON: {e.Message}"), out cmd); }
        if (cmd.Kind == TestCmdKind.None) { _ended = true; return true; }

        _phase = kind switch
        {
            TestCmdKind.App    => FramePhase.Commands,
            TestCmdKind.Events => FramePhase.Events,
            _                  => FramePhase.Queries,
        };
        _next++;
        return true;
    }

    /// <summary>
    /// The batch step <see cref="TryPull"/> last handed out (0 up), or -1 for a single
    /// command or a request refused as a whole: what a refusal's answer names as failed.
    /// </summary>
    internal int Step => _isBatch && _stepCount > 0 ? Math.Min(_next, _stepCount - 1) : -1;

    /// <summary>
    /// The frame is over: the next <see cref="TryPull"/> starts a new one. Called when
    /// the frame's steps are answered.
    /// </summary>
    internal void EndFrame()
    {
        _phase = FramePhase.Commands;
        _scratchUsed = 0;
    }

    /// <summary>
    /// The request is answered: the buffer goes back to the port thread (the length
    /// last), and the next request starts fresh.
    /// </summary>
    internal void Release()
    {
        _stepCount = -1;
        _next = 0;
        _isBatch = false;
        _ended = false;
        EndFrame();
        Volatile.Write(ref _requestLength, 0);
    }

    // Whether a step of this kind runs in this frame, after what the frame has taken.
    private bool Fits(TestCmdKind kind) => kind switch
    {
        TestCmdKind.App    => _phase == FramePhase.Commands,
        TestCmdKind.Events => _phase == FramePhase.Commands,
        _                  => true,      // queries are answered after everything before them; a refusal at once
    };

    private bool Refuse(string error, out TestCmd cmd)
    {
        _ended = true;
        cmd = new TestCmd(TestCmdKind.None, error: error);
        return true;
    }

    private string StepError(string error) => _isBatch ? $"Step {_next}: {error}" : error;

    // ── Reading a request ──────────────────────────────────────────────────

    // The request's steps, found once: one object, or a batch's elements. A whole
    // request that is not well-formed JSON is refused here, before any step runs.
    private string? FindSteps(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read()) return "An empty request.";
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                reader.Skip();
                if (reader.Read()) return "A request is one JSON object, or a batch of them.";
                (_stepStart[0], _stepLength[0], _stepCount) = (0, line.Length, 1);
                return null;
            }
            if (reader.TokenType != JsonTokenType.StartArray)
                return "A request is one JSON object, or a batch of them.";

            _isBatch = true;
            int count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.StartArray) return "A batch cannot hold a batch.";
                if (reader.TokenType != JsonTokenType.StartObject) return $"Step {count}: a batch holds commands, JSON objects.";
                if (count == _stepStart.Length) return $"A batch holds at most {_stepStart.Length} commands.";
                int start = (int)reader.TokenStartIndex;
                reader.Skip();
                (_stepStart[count], _stepLength[count]) = (start, (int)reader.BytesConsumed - start);
                count++;
            }
            if (reader.Read()) return "A request is one JSON object, or a batch of them.";
            if (count == 0) return "An empty batch.";
            _stepCount = count;
            return null;
        }
        catch (JsonException e)
        {
            _isBatch = false;
            return $"Not JSON: {e.Message}";
        }
    }

    // The step's kind, from its $cmd: one pass over its top level, $cmd anywhere in it.
    private static TestCmdKind KindOf(ReadOnlySpan<byte> step, out string? error)
    {
        error = null;
        var reader = new Utf8JsonReader(step);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("$cmd"u8)) { reader.Read(); reader.Skip(); continue; }

            reader.Read();
            if (reader.TokenType != JsonTokenType.String) { error = "$cmd is a name, a string."; return TestCmdKind.None; }
            for (var kind = TestCmdKind.Events; kind <= TestCmdKind.Screenshot; kind++)
                if (reader.ValueTextEquals(NameOf(kind))) return kind;
            if (StartsWithTest(ref reader)) { error = $"Unknown test command: {reader.GetString()}."; return TestCmdKind.None; }
            return TestCmdKind.App;
        }
        error = "A request names its $cmd.";
        return TestCmdKind.None;
    }

    // Whether the string the reader is on starts with "test.", compared unescaped.
    private static bool StartsWithTest(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped) return reader.ValueSpan.StartsWith("test."u8);
        Span<byte> name = stackalloc byte[256];
        return reader.ValueSpan.Length <= name.Length
            && name[..reader.CopyString(name)].StartsWith("test."u8);
    }

    // The step as a TestCmd of its kind, its arguments read and checked.
    private TestCmd Read(ReadOnlySpan<byte> step, TestCmdKind kind)
    {
        switch (kind)
        {
            case TestCmdKind.App:
                return new TestCmd(TestCmdKind.App, json: step);

            case TestCmdKind.Events:
            {
                var reader = new Utf8JsonReader(step);
                if (!FindProperty(ref reader, "events"u8) || reader.TokenType != JsonTokenType.StartArray)
                    return Refused("test.events carries its events, a list.");
                var error = ReadEvents(ref reader, out int count);
                return error is null
                    ? new TestCmd(TestCmdKind.Events, events: _events.AsSpan(0, count))
                    : Refused(error);
            }

            case TestCmdKind.Screenshot:
            {
                var reader = new Utf8JsonReader(step);
                if (!FindProperty(ref reader, "path"u8) || reader.TokenType != JsonTokenType.String
                    || reader.ValueSpan.IsEmpty)
                    return Refused("test.screenshot carries the path to write, a string.");
                if (!reader.ValueIsEscaped) return new TestCmd(TestCmdKind.Screenshot, path: reader.ValueSpan);
                var room = _scratch.AsSpan(_scratchUsed);
                if (reader.ValueSpan.Length > room.Length) return Refused("The path is too long.");
                int n = reader.CopyString(room);
                _scratchUsed += n;
                return new TestCmd(TestCmdKind.Screenshot, path: room[..n]);
            }

            default:
                return new TestCmd(kind);
        }
    }

    private TestCmd Refused(string error) => new(TestCmdKind.None, error: StepError(error));

    // Moves the reader onto the value of the top-level property named name.
    private static bool FindProperty(ref Utf8JsonReader reader, ReadOnlySpan<byte> name)
    {
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool found = reader.ValueTextEquals(name);
            reader.Read();
            if (found) return true;
            reader.Skip();
        }
        return false;
    }

    // ── Events ─────────────────────────────────────────────────────────────

    // The events list the reader is on, into the port's event array through
    // WindowEvent's factories. Null when every event is good; otherwise why not.
    private string? ReadEvents(ref Utf8JsonReader reader, out int count)
    {
        count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count == _events.Length) return $"test.events carries at most {_events.Length} events.";
            if (reader.TokenType != JsonTokenType.StartObject) return $"Event {count}: an event is a JSON object.";
            var error = ReadEvent(ref reader, out _events[count]);
            if (error is not null) return $"Event {count}: {error}";
            count++;
        }
        return null;
    }

    private enum EventName { Unknown, MouseMove, MouseDown, MouseUp, Scroll, KeyDown, KeyUp, KeyChar, FocusLost }

    // One event object, its fields in any order, checked for what its kind needs.
    private static string? ReadEvent(ref Utf8JsonReader reader, out WindowEvent ev)
    {
        ev = default;
        var name = EventName.Unknown;
        bool hasKind = false, hasX = false, hasY = false, hasDy = false;
        float x = 0, y = 0, dx = 0, dy = 0;
        MouseButton? button = null;
        Key? key = null;
        Rune? text = null;
        var modifiers = KeyModifiers.None;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("kind"u8))
            {
                reader.Read();
                hasKind = true;
                name = reader.TokenType == JsonTokenType.String ? EventNameOf(ref reader) : EventName.Unknown;
                if (name == EventName.Unknown) return "unknown kind; one of mouse_move, mouse_down, mouse_up, scroll, key_down, key_up, key_char, focus_lost.";
            }
            else if (reader.ValueTextEquals("x"u8))  { if (!Number(ref reader, out x))  return "x is a number."; hasX = true; }
            else if (reader.ValueTextEquals("y"u8))  { if (!Number(ref reader, out y))  return "y is a number."; hasY = true; }
            else if (reader.ValueTextEquals("dx"u8)) { if (!Number(ref reader, out dx)) return "dx is a number."; }
            else if (reader.ValueTextEquals("dy"u8)) { if (!Number(ref reader, out dy)) return "dy is a number."; hasDy = true; }
            else if (reader.ValueTextEquals("button"u8))
            {
                reader.Read();
                button = reader.TokenType != JsonTokenType.String ? null
                    : reader.ValueTextEquals("left"u8) ? MouseButton.Left
                    : reader.ValueTextEquals("right"u8) ? MouseButton.Right
                    : reader.ValueTextEquals("middle"u8) ? MouseButton.Middle
                    : null;
                if (button is null) return "unknown button; one of left, right, middle.";
            }
            else if (reader.ValueTextEquals("key"u8))
            {
                reader.Read();
                if (!KeyOf(ref reader, out var k)) return "unknown key; a bibui Key name, such as Enter, A or Left.";
                key = k;
            }
            else if (reader.ValueTextEquals("char"u8))
            {
                reader.Read();
                if (!RuneOf(ref reader, out var r)) return "char is one character, a string.";
                text = r;
            }
            else if (reader.ValueTextEquals("modifiers"u8))
            {
                reader.Read();
                if (reader.TokenType != JsonTokenType.StartArray) return "modifiers is a list, such as [\"ctrl\",\"shift\"].";
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var m = reader.TokenType != JsonTokenType.String ? KeyModifiers.None
                        : reader.ValueTextEquals("ctrl"u8) ? KeyModifiers.Ctrl
                        : reader.ValueTextEquals("shift"u8) ? KeyModifiers.Shift
                        : reader.ValueTextEquals("alt"u8) ? KeyModifiers.Alt
                        : reader.ValueTextEquals("super"u8) ? KeyModifiers.Super
                        : KeyModifiers.None;
                    if (m == KeyModifiers.None) return "unknown modifier; ctrl, shift, alt or super.";
                    modifiers |= m;
                }
            }
            else { reader.Read(); reader.Skip(); }
        }

        if (!hasKind) return "an event names its kind.";
        switch (name)
        {
            case EventName.MouseMove:
                if (!hasX || !hasY) return "mouse_move carries x and y.";
                ev = WindowEvent.Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown, modifiers);
                return null;
            case EventName.MouseDown:
            case EventName.MouseUp:
                if (!hasX || !hasY || button is null) return "mouse_down and mouse_up carry x, y and button.";
                ev = WindowEvent.Mouse(name == EventName.MouseDown ? WindowEventKind.MouseDown : WindowEventKind.MouseUp,
                    x, y, button.Value, modifiers);
                return null;
            case EventName.Scroll:
                if (!hasX || !hasY || !hasDy) return "scroll carries x, y and dy (and dx).";
                ev = WindowEvent.Wheel(x, y, dx, dy, modifiers);
                return null;
            case EventName.KeyDown:
            case EventName.KeyUp:
                if (key is null) return "key_down and key_up carry key.";
                ev = name == EventName.KeyDown ? WindowEvent.KeyPress(key.Value, modifiers) : WindowEvent.KeyRelease(key.Value, modifiers);
                return null;
            case EventName.KeyChar:
                if (text is null) return "key_char carries char.";
                ev = WindowEvent.Text(text.Value);
                return null;
            default:
                ev = WindowEvent.FocusLost();
                return null;
        }
    }

    private static EventName EventNameOf(ref Utf8JsonReader reader) =>
        reader.ValueTextEquals("mouse_move"u8) ? EventName.MouseMove
        : reader.ValueTextEquals("mouse_down"u8) ? EventName.MouseDown
        : reader.ValueTextEquals("mouse_up"u8) ? EventName.MouseUp
        : reader.ValueTextEquals("scroll"u8) ? EventName.Scroll
        : reader.ValueTextEquals("key_down"u8) ? EventName.KeyDown
        : reader.ValueTextEquals("key_up"u8) ? EventName.KeyUp
        : reader.ValueTextEquals("key_char"u8) ? EventName.KeyChar
        : reader.ValueTextEquals("focus_lost"u8) ? EventName.FocusLost
        : EventName.Unknown;

    private static bool Number(ref Utf8JsonReader reader, out float value)
    {
        value = 0;
        reader.Read();
        return reader.TokenType == JsonTokenType.Number && reader.TryGetSingle(out value) && float.IsFinite(value);
    }

    // A Key by its one name, as the enum spells it, in any case: letters and digits,
    // starting with a letter, so never a number and never Enum.TryParse's "A,B".
    private static bool KeyOf(ref Utf8JsonReader reader, out Key key)
    {
        key = Key.Unknown;
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 32) return false;
        Span<char> name = stackalloc char[32];
        name = name[..reader.CopyString(name)];
        if (name.IsEmpty || !char.IsAsciiLetter(name[0]) || name.ContainsAnyExcept(KeyNameChars)) return false;
        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.Unknown && Enum.IsDefined(key);
    }

    private static readonly System.Buffers.SearchValues<char> KeyNameChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");

    // Exactly one character, which may take two UTF-16 units.
    private static bool RuneOf(ref Utf8JsonReader reader, out Rune rune)
    {
        rune = default;
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 32) return false;
        Span<char> chars = stackalloc char[32];
        int n = reader.CopyString(chars);
        return Rune.DecodeFromUtf16(chars[..n], out rune, out int used) == System.Buffers.OperationStatus.Done && used == n;
    }
}
