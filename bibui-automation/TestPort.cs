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
/// <see cref="TestPort"/>'s request buffer by <see cref="TestPort.TryPullCmd"/> at the
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
/// <para>A request is one line holding one command, <c>{"$cmd":name,...}</c>. The
/// <c>test.*</c> names are bibui's (<see cref="NameOf"/>); any other name is the
/// app's, handed over as bytes. A client with several commands sends several lines
/// without waiting, and reads their answers in order.</para>
///
/// <para>The request buffer has one owner at a time, and its length says which:
/// 0, the port thread's, which writes a line into it and then the length; above 0,
/// the loop's, from <see cref="TryPullCmd"/> until the request is answered, when the
/// length goes back to 0. So a <see cref="TestCmd"/>'s spans stay put for the
/// whole request.</para>
/// </summary>
public sealed class TestPort
{
    /// <summary>The length the port thread publishes for a line longer than the request buffer.</summary>
    internal const int Oversized = -1;

    // The request buffer, its length (written last by the port thread, set back to 0
    // when the request is answered), and what the loop reads into from it. All
    // allocated once, for the port's life.
    private readonly byte[] _request;
    private int _requestLength;
    private readonly WindowEvent[] _events;                  // a test.events request's events
    private readonly byte[] _scratch;                        // an escaped path, unescaped
    private int _scratchUsed;

    // The loop's: whether the request in hand has been handed out.
    private bool _pulled;

    /// <param name="requestBytes">The request buffer: the longest line a client may send.</param>
    /// <param name="maxEvents">The most events one <c>test.events</c> may carry.</param>
    public TestPort(int requestBytes = 1 << 20, int maxEvents = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestBytes, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEvents, 1);
        _request = new byte[requestBytes];
        _events = new WindowEvent[maxEvents];
        _scratch = new byte[4096];
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
    /// The request the port thread has published, once: false when there is none,
    /// or when it has been handed out and not yet answered. The loop's, at the top of
    /// the frame: <c>while (port.TryPullCmd(out var cmd)) { ... }</c>. With nothing
    /// published, it costs one read of the request's length.
    /// </summary>
    public bool TryPullCmd(out TestCmd cmd)
    {
        cmd = default;
        if (_pulled) return false;
        int length = Volatile.Read(ref _requestLength);
        if (length == 0) return false;
        _pulled = true;

        if (length == Oversized)
        {
            cmd = Refused($"The request is longer than the request buffer ({_request.Length} bytes).");
            return true;
        }
        try { cmd = Read(_request.AsSpan(0, length)); }
        catch (JsonException e) { cmd = Refused($"Not JSON: {e.Message}"); }
        return true;
    }

    /// <summary>
    /// The request is answered: the buffer goes back to the port thread (the length
    /// last), and the next request starts fresh. Called by AnswerCmds.
    /// </summary>
    internal void Release()
    {
        _pulled = false;
        _scratchUsed = 0;
        Volatile.Write(ref _requestLength, 0);
    }

    // ── Reading a request ──────────────────────────────────────────────────

    // The line as a TestCmd: one JSON object, its kind from its $cmd, its arguments
    // read and checked. Not well-formed JSON throws, for TryPullCmd to refuse.
    private TestCmd Read(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return Refused("A request is one JSON object.");
        reader.Skip();
        if (reader.Read()) return Refused("A request is one JSON object.");

        var kind = KindOf(line, out var error);
        return error is not null ? Refused(error) : Read(line, kind);
    }

    // The request's kind, from its $cmd: one pass over its top level, $cmd anywhere in it.
    private static TestCmdKind KindOf(ReadOnlySpan<byte> line, out string? error)
    {
        error = null;
        var reader = new Utf8JsonReader(line);
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

    // The request as a TestCmd of its kind, its arguments read and checked.
    private TestCmd Read(ReadOnlySpan<byte> line, TestCmdKind kind)
    {
        switch (kind)
        {
            case TestCmdKind.App:
                return new TestCmd(TestCmdKind.App, json: line);

            case TestCmdKind.Events:
            {
                var reader = new Utf8JsonReader(line);
                if (!FindProperty(ref reader, "events"u8) || reader.TokenType != JsonTokenType.StartArray)
                    return Refused("test.events carries its events, a list.");
                var error = ReadEvents(ref reader, out int count);
                return error is null
                    ? new TestCmd(TestCmdKind.Events, events: _events.AsSpan(0, count))
                    : Refused(error);
            }

            case TestCmdKind.Screenshot:
            {
                var reader = new Utf8JsonReader(line);
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

    private static TestCmd Refused(string error) => new(TestCmdKind.None, error: error);

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
