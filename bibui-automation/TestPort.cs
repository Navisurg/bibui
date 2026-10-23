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
    /// <summary>
    /// <c>test.screenshot</c>: the app writes the frame on screen as a PNG at
    /// <see cref="TestCmd.Path"/>, drawn with a renderer of its choosing, or refuses
    /// it (<see cref="TestPort.RefuseCmd"/>). At the top of the frame the last frame's
    /// draw list is still the runtime's, so it is the frame on screen that is drawn.
    /// </summary>
    Screenshot,
    /// <summary>Any other name: one of the app's commands, as sent, in <see cref="TestCmd.Json"/>.</summary>
    App,
}

/// <summary>
/// One request from a client, as the frame loop sees it: taken from the
/// <see cref="TestPort"/>'s request buffer by <see cref="TestPort.TryPullCmd"/> at the
/// top of the frame, and answered at its end. A fat struct on purpose: every
/// request has one shape, read with one switch. Each field means something for the
/// kinds noted beside it, and nothing for the rest.
///
/// <para>Its spans point into memory the loop owns until the request is answered,
/// so it lives for the frame and no longer: a <c>ref struct</c>, never stored, never
/// handed to another thread.</para>
/// </summary>
public readonly ref struct TestCmd
{
    public readonly TestCmdKind Kind;
    /// <summary>Events: this frame's input.</summary>
    public readonly ReadOnlySpan<WindowEvent> Events;
    /// <summary>Screenshot: the file the app writes the frame to.</summary>
    public readonly string? Path;
    /// <summary>App: the command as sent, for the app's own deserializer.</summary>
    public readonly ReadOnlySpan<byte> Json;
    /// <summary>None: why the request was refused.</summary>
    public readonly string? Error;

    internal TestCmd(TestCmdKind kind, ReadOnlySpan<WindowEvent> events = default,
        string? path = null, ReadOnlySpan<byte> json = default, string? error = null)
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
/// <c>test.*</c> commands are the port's; any other name is the app's, handed over
/// as bytes. A client with several commands sends several lines without waiting,
/// and reads their answers in order.</para>
///
/// <para>The request buffer has one owner at a time, and its length says which:
/// 0, the port thread's, which writes a line into it and then the length; above 0,
/// the loop's, from <see cref="TryPullCmd"/> until the request is answered, when the
/// length goes back to 0. So a <see cref="TestCmd"/>'s spans stay put for the
/// whole request.</para>
///
/// <para>An app opens one with <see cref="Open"/>, and calls two things a frame:
/// <see cref="TryPullCmd"/> at its top and <see cref="AnswerCmds"/> at its end.</para>
/// </summary>
public sealed partial class TestPort
{
    /// <summary>The length the port thread publishes for a line longer than the request buffer.</summary>
    internal const int Oversized = -1;

    // The request buffer and its length (written last by the port thread, set back to
    // 0 when the request is answered). Allocated once, for the port's life.
    private readonly byte[] _request;
    private int _requestLength;
    private readonly ManualResetEventSlim _bufferFree;       // set while the length is 0: the port thread waits on it

    // The loop's: a test.events request's events, the array kept and grown when one
    // carries more than it holds.
    private WindowEvent[] _events = new WindowEvent[16];

    // The loop's, about the request in hand: whether it has been handed out, and
    // what AnswerCmds needs of it at the end of the frame.
    private bool _pulled;
    private TestCmdKind _heldKind;
    private string? _heldError;                              // None: why it was refused
    private string? _heldPath;                               // Screenshot: where to write

    // A port with no socket: the request buffer and what reads it, for Open, and for
    // tests that publish lines themselves.
    internal TestPort(int requestBytes = 1 << 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestBytes, 16);
        _request = new byte[requestBytes];
        _bufferFree = new ManualResetEventSlim(initialState: true);
    }

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
        if (line.Length > _request.Length) { PublishLength(Oversized); return; }
        if (line.IsEmpty) return;
        line.CopyTo(_request);
        PublishLength(line.Length);
    }

    // The line is in the buffer (or Oversized): the buffer is the loop's from here.
    // Marked taken before the length is written, so the loop cannot hand it back
    // before the port thread has stopped thinking it free.
    private void PublishLength(int length)
    {
        _bufferFree.Reset();
        Volatile.Write(ref _requestLength, length);
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
            cmd = Refused($"The request is longer than the request buffer ({_request.Length} bytes).");
        else
        {
            try { cmd = Read(_request.AsSpan(0, length)); }
            catch (JsonException e) { cmd = Refused($"Not a request: {e.Message}"); }
        }
        (_heldKind, _heldError, _heldPath) = (cmd.Kind, cmd.Error, cmd.Path);
        return true;
    }

    /// <summary>
    /// The request is answered: the buffer goes back to the port thread (the length
    /// last), and the next request starts fresh. Called by AnswerCmds.
    /// </summary>
    internal void Release()
    {
        _pulled = false;
        (_heldKind, _heldError, _heldPath) = (TestCmdKind.None, null, null);
        Volatile.Write(ref _requestLength, 0);
        _bufferFree.Set();
    }

    // ── Reading a request ──────────────────────────────────────────────────

    // The line as a TestCmd. A test.* command is read into its record by the JSON
    // source generator; any other $cmd is the app's, handed over as it is. Not
    // well-formed JSON, and a test.* command that is not one, throw: TryPullCmd
    // refuses them.
    private TestCmd Read(ReadOnlySpan<byte> line)
    {
        var reader = new Utf8JsonReader(line);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return Refused("A request is one JSON object.");
        reader.Skip();
        if (reader.Read()) return Refused("A request is one JSON object.");

        if (!NamesATestCommand(line, out var error))
            return error is null ? new TestCmd(TestCmdKind.App, json: line) : Refused(error);

        return JsonSerializer.Deserialize(line, TestRequestJson.Default.TestRequest) switch
        {
            EventsRequest e => Events(e.Events),
            TreeRequest => new TestCmd(TestCmdKind.Tree),
            StateRequest => new TestCmd(TestCmdKind.State),
            SchemaRequest => new TestCmd(TestCmdKind.Schema),
            ScreenshotRequest { Path.Length: > 0 } s => new TestCmd(TestCmdKind.Screenshot, path: s.Path),
            ScreenshotRequest => Refused("test.screenshot carries the path to write."),
            _ => Refused("Not a request."),
        };
    }

    // Whether the request's $cmd starts with "test.", compared unescaped; $cmd may
    // come anywhere in it. Error when it has none.
    private static bool NamesATestCommand(ReadOnlySpan<byte> line, out string? error)
    {
        error = null;
        var reader = new Utf8JsonReader(line);
        reader.Read();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (!reader.ValueTextEquals("$cmd"u8)) { reader.Read(); reader.Skip(); continue; }
            reader.Read();
            if (reader.TokenType != JsonTokenType.String) { error = "$cmd is a name, a string."; return false; }
            if (!reader.ValueIsEscaped) return reader.ValueSpan.StartsWith("test."u8);
            Span<byte> name = stackalloc byte[256];
            return reader.ValueSpan.Length <= name.Length && name[..reader.CopyString(name)].StartsWith("test."u8);
        }
        error = "A request names its $cmd.";
        return false;
    }

    private static TestCmd Refused(string error) => new(TestCmdKind.None, error: error);

    // ── Events ─────────────────────────────────────────────────────────────

    // A test.events request's events as WindowEvents, through WindowEvent's
    // factories, into the port's array.
    private TestCmd Events(EventJson[] events)
    {
        if (_events.Length < events.Length) _events = new WindowEvent[events.Length];
        for (int i = 0; i < events.Length; i++)
        {
            var error = ToWindowEvent(events[i], out _events[i]);
            if (error is not null) return Refused($"Event {i}: {error}");
        }
        return new TestCmd(TestCmdKind.Events, events: _events.AsSpan(0, events.Length));
    }

    private static string? ToWindowEvent(in EventJson e, out WindowEvent ev)
    {
        ev = default;
        var modifiers = KeyModifiers.None;
        foreach (var m in e.Modifiers ?? [])
        {
            var bit = m switch { "ctrl" => KeyModifiers.Ctrl, "shift" => KeyModifiers.Shift, "alt" => KeyModifiers.Alt, "super" => KeyModifiers.Super, _ => KeyModifiers.None };
            if (bit == KeyModifiers.None) return "unknown modifier; ctrl, shift, alt or super.";
            modifiers |= bit;
        }

        switch (e.Kind)
        {
            case "mouse_move":
                if (e is not { X: { } mx, Y: { } my }) return "mouse_move carries x and y.";
                ev = WindowEvent.Mouse(WindowEventKind.MouseMove, mx, my, MouseButton.Unknown, modifiers);
                return null;
            case "mouse_down":
            case "mouse_up":
                if (e is not { X: { } bx, Y: { } by }) return "mouse_down and mouse_up carry x, y and button.";
                var button = e.Button switch { "left" => MouseButton.Left, "right" => MouseButton.Right, "middle" => MouseButton.Middle, _ => MouseButton.Unknown };
                if (button == MouseButton.Unknown) return "mouse_down and mouse_up carry x, y and button (left, right or middle).";
                ev = WindowEvent.Mouse(e.Kind == "mouse_down" ? WindowEventKind.MouseDown : WindowEventKind.MouseUp, bx, by, button, modifiers);
                return null;
            case "scroll":
                if (e is not { X: { } sx, Y: { } sy, Dy: { } dy }) return "scroll carries x, y and dy (and dx).";
                ev = WindowEvent.Wheel(sx, sy, e.Dx ?? 0, dy, modifiers);
                return null;
            case "key_down":
            case "key_up":
                if (!KeyOf(e.Key, out var key)) return "unknown key; a bibui Key name, such as Enter, A or Left.";
                ev = e.Kind == "key_down" ? WindowEvent.KeyPress(key, modifiers) : WindowEvent.KeyRelease(key, modifiers);
                return null;
            case "key_char":
                if (e.Char is null || Rune.DecodeFromUtf16(e.Char, out var rune, out int used) != System.Buffers.OperationStatus.Done || used != e.Char.Length)
                    return "key_char carries char, one character.";
                ev = WindowEvent.Text(rune);
                return null;
            case "focus_lost":
                ev = WindowEvent.FocusLost();
                return null;
            default:
                return "unknown kind; one of mouse_move, mouse_down, mouse_up, scroll, key_down, key_up, key_char, focus_lost.";
        }
    }

    // A Key by its one name, as the enum spells it, in any case: letters and digits,
    // starting with a letter, so never a number and never Enum.TryParse's "A,B".
    private static bool KeyOf(string? name, out Key key)
    {
        key = Key.Unknown;
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetter(name[0]) || name.AsSpan().ContainsAnyExcept(KeyNameChars)) return false;
        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.Unknown && Enum.IsDefined(key);
    }

    private static readonly System.Buffers.SearchValues<char> KeyNameChars =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789");
}
