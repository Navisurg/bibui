using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bibui.Core.Draw;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Core.Runtime;

/// <summary>
/// A TCP test server that exposes <see cref="BibuiRuntime"/> as a line-delimited
/// JSON command API on the loopback interface, for workflow automation. The app holds it and steps it from its loop:
/// <code>
/// using var server = BibuiTestServer.StartIfEnabled(runtime, draw: renderer.Draw);
/// while (SilkWindow.BeginFrame(window, runtime))
/// {
///     // While a command runs, the server is the keyboard and mouse.
///     runtime.BeginBuild(server?.Step(window.Events) ?? window.Events);
///     ...
/// }
/// </code>
///
/// <para>It is off unless the app asks for it. <see cref="StartIfEnabled"/> starts
/// one only when the environment variable <c>BIBUI_TEST_SERVER</c> is <c>1</c>;
/// the samples use that and nothing else.</para>
///
/// <para>Every request carries the server's token (<c>"token":"…"</c>): a random
/// value per server, written to <see cref="TokenFile"/>, a file only the user can
/// read. A request without it, a line that is not a JSON object, and anything
/// that looks like HTTP are refused, and the connection is closed. Files are
/// written only inside <see cref="OutputDirectory"/>: <c>screenshot</c> takes
/// paths relative to it.</para>
///
/// <para>The server builds no frames and touches the runtime from no thread but
/// the loop's. A client's thread only queues its command and waits; the loop
/// runs it in <see cref="Step"/>, a frame at a time, as a hand would: a click is a
/// move, then a press, then a release, each in a frame of its own. The reply goes
/// back once the frames the command waits for are built. Screenshots
/// draw the runtime's <see cref="BibuiRuntime.DrawList"/> there too,
/// with the function the server was given (null refuses them): the app's
/// renderer when it draws into memory, so the textures the app made on it are in
/// them, or a CPU renderer of its own beside a GPU one.</para>
///
/// <para>Protocol: each request and response is a single JSON line, the response
/// sent once the command has run.</para>
/// </summary>
public sealed class BibuiTestServer : IDisposable
{
    /// <summary>The port a server listens on when none is given.</summary>
    public const int DefaultPort = 44334;

    /// <summary>The environment variable that turns the server on for <see cref="StartIfEnabled"/>: <c>1</c>.</summary>
    public const string EnableVariable = "BIBUI_TEST_SERVER";

    /// <summary>The environment variable naming the output directory for <see cref="StartIfEnabled"/>.</summary>
    public const string OutputVariable = "BIBUI_TEST_SERVER_OUT";

    private readonly BibuiRuntime _runtime;
    // What a screenshot draws the frame's list with; null refuses it.
    private readonly DrawToPixels? _draw;
    private readonly int _port;
    private readonly string _outputDir;
    private readonly byte[] _token;
    private readonly bool _writeTokenFile;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _stopping;

    // ── Client threads → the loop ──────────────────────────────────────────
    // The one thing that crosses threads: a command, queued by a client's
    // thread, which waits on Done for the reply the loop writes.
    private sealed class Pending(JsonObject request)
    {
        public readonly JsonObject Request = request;
        public string Reply = "";
        public readonly ManualResetEventSlim Done = new();
    }
    private readonly ConcurrentQueue<Pending> _queue = new();

    /// <summary>How long a client waits for the loop to run its command.</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    // ── The loop's ─────────────────────────────────────────────────────────
    // Loop thread only: the command running, its steps (one per frame it
    // waits for), the reply it has decided on, and this frame's input.
    private Pending? _running;
    private IEnumerator<bool>? _steps;
    private string _reply = "";
    private readonly List<WindowEvent> _events = [];
    // Where the server's pointer is: a release given no point lands here.
    private float _pointerX, _pointerY;

    /// <summary>
    /// A server on <paramref name="runtime"/> that writes only inside
    /// <paramref name="outputDirectory"/> (created on <see cref="Start"/>). Nothing
    /// listens until <see cref="Start"/>.
    /// </summary>
    /// <param name="runtime">The runtime the commands drive.</param>
    /// <param name="outputDirectory">The one directory files are written in.</param>
    /// <param name="port">The loopback port.</param>
    /// <param name="token">The token a request must carry, 16 characters or more; null for a random one.</param>
    /// <param name="writeTokenFile">Whether <see cref="Start"/> writes the token to <see cref="TokenFile"/>.</param>
    /// <param name="draw">
    /// Draws a frame's list into pixels, for <c>screenshot</c>:
    /// the app's renderer, on the CPU. Null refuses it.
    /// </param>
    public BibuiTestServer(BibuiRuntime runtime, string outputDirectory, int port = DefaultPort,
        string? token = null, bool writeTokenFile = true, DrawToPixels? draw = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (token is not null && token.Length < 16)
            throw new ArgumentException("A token must be at least 16 characters.", nameof(token));

        _runtime = runtime;
        _draw = draw;
        _port = port;
        _outputDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        Token = token ?? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _token = Encoding.UTF8.GetBytes(Token);
        _writeTokenFile = writeTokenFile;
    }

    /// <summary>The token every request must carry.</summary>
    public string Token { get; }

    /// <summary>The port it listens on, on 127.0.0.1.</summary>
    public int Port => _port;

    /// <summary>The one directory <c>screenshot</c> writes in.</summary>
    public string OutputDirectory => _outputDir;

    /// <summary>Where <see cref="Start"/> writes the token, readable by the user only.</summary>
    public string TokenFile => TokenFilePath(_port);

    /// <summary>
    /// Whether the environment asks for a test server: <c>BIBUI_TEST_SERVER=1</c>.
    /// </summary>
    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableVariable) == "1";

    /// <summary>
    /// Starts a test server on <paramref name="runtime"/> when <see cref="IsEnabled"/>,
    /// and returns it; otherwise returns null and opens nothing. Its output directory
    /// is <c>BIBUI_TEST_SERVER_OUT</c>, or <c>bibui-test-output</c> under the working
    /// directory. Where the token is, not the token, is written to stderr.
    /// <paramref name="draw"/> is what screenshots are drawn with; null refuses them.
    /// </summary>
    public static BibuiTestServer? StartIfEnabled(BibuiRuntime runtime, int port = DefaultPort, DrawToPixels? draw = null)
    {
        if (!IsEnabled) return null;
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(output))
            output = Path.Combine(Directory.GetCurrentDirectory(), "bibui-test-output");
        var server = new BibuiTestServer(runtime, output, port, draw: draw);
        server.Start();
        Console.Error.WriteLine(
            $"[BibuiTestServer] listening on 127.0.0.1:{port}; token in {server.TokenFile}; output in {server.OutputDirectory}");
        return server;
    }

    /// <summary>
    /// The token file of a server on <paramref name="port"/>:
    /// <c>$XDG_RUNTIME_DIR/bibui-test-server-PORT.token</c>, or under the user's local
    /// application data when <c>XDG_RUNTIME_DIR</c> is not set.
    /// </summary>
    public static string TokenFilePath(int port = DefaultPort)
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolderOption.DoNotVerify), "bibui");
        return Path.Combine(dir, $"bibui-test-server-{port}.token");
    }

    public void Start()
    {
        Directory.CreateDirectory(_outputDir);
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        if (_writeTokenFile)
        {
            WriteTokenFile();
            // An app closed without disposing the server (a window's close, SIGTERM)
            // still takes its token file with it.
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "test-server-accept" };
        _acceptThread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _listener?.Stop();
        _acceptThread?.Join(2000);
        if (_writeTokenFile && _listener is not null)
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            DeleteTokenFile();
        }
    }

    private void OnProcessExit(object? sender, EventArgs e) => DeleteTokenFile();

    public void Dispose() => Stop();

    // ── Token file ─────────────────────────────────────────────────────────

    // Written fresh, never through what is there: the old file (or a link in its
    // place) is removed, and the new one is created exclusively, mode 0600, in a
    // directory only the user can enter when it has to be made.
    private void WriteTokenFile()
    {
        var path = TokenFile;
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        stream.Write(_token);
    }

    private void DeleteTokenFile()
    {
        try
        {
            var path = TokenFile;
            // Only our own: another server may have taken the port since.
            if (File.Exists(path) && CryptographicOperations.FixedTimeEquals(File.ReadAllBytes(path), _token))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── Accept loop ────────────────────────────────────────────────────────

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            TcpClient client;
            try { client = _listener!.AcceptTcpClient(); }
            catch { break; }

            var t = new Thread(() => HandleClient(client))
            {
                IsBackground = true,
                Name = "test-server-client"
            };
            t.Start();
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    var refusal = Admit(line, out var request);
                    var response = refusal ?? RunOnLoop(request!);
                    try { writer.WriteLine(response); }
                    catch (IOException) { break; } // client closed mid-response
                    // A refused line ends the connection: whatever follows it on the
                    // same stream (the rest of an HTTP request, say) is never read.
                    if (refusal is not null) break;
                }
            }
        }
        catch (IOException)
        {
            // Broken pipe / connection reset mid-read. Abandon this client; don't crash the app.
        }
    }

    // ── Admission ──────────────────────────────────────────────────────────

    private static readonly string[] HttpMethods =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "CONNECT", "TRACE", "PRI"];

    /// <summary>
    /// Null, with the parsed request, when <paramref name="line"/> may run; otherwise
    /// the error to answer with. Anything that looks like HTTP is refused before it
    /// is parsed — a web page can reach a loopback port with a POST — then anything
    /// that is not a JSON object, then anything without the token.
    /// </summary>
    private string? Admit(string line, out JsonObject? request)
    {
        request = null;
        var text = line.TrimStart('﻿').Trim();

        if (LooksLikeHttp(text)) return Err("Refused: this is not an HTTP server");
        if (!text.StartsWith('{')) return Err("Refused: a request is one JSON object per line");

        JsonObject obj;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject parsed)
                return Err("Refused: a request is one JSON object per line");
            obj = parsed;
        }
        catch (JsonException) { return Err("Refused: a request is one JSON object per line"); }

        var token = obj["token"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (token is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), _token))
            return Err("Refused: missing or wrong token");

        request = obj;
        return null;
    }

    private static bool LooksLikeHttp(string text)
    {
        if (text.Contains("HTTP/", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var method in HttpMethods)
            if (text.Length > method.Length && text[method.Length] == ' '
                && text.StartsWith(method, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ── Commands, on the loop ──────────────────────────────────────────────

    // A client's thread: queue the command, wake the loop, wait for its reply.
    private string RunOnLoop(JsonObject request)
    {
        var pending = new Pending(request);
        _queue.Enqueue(pending);
        _runtime.RequestFrame();
        return pending.Done.Wait(CommandTimeout)
            ? pending.Reply
            : Err("The app's loop did not run the command: does it call server.Step() once a frame?");
    }

    /// <summary>
    /// The loop's turn, once a frame before <see cref="BibuiRuntime.BeginBuild"/>:
    /// runs the command in hand a frame further, or starts the next one. Returns
    /// the frame's input: <paramref name="windowEvents"/> when no command is
    /// running; otherwise the keyboard and mouse events the command makes this
    /// frame, and <paramref name="windowEvents"/> is emptied — while a command
    /// runs, the server is the only keyboard and mouse.
    /// </summary>
    public List<WindowEvent> Step(List<WindowEvent> windowEvents)
    {
        if (_running is null)
        {
            if (!_queue.TryDequeue(out var next)) return windowEvents;
            (_running, _reply) = (next, Ok());
            _steps = Run(next.Request).GetEnumerator();
        }

        bool waiting;
        try { waiting = _steps!.MoveNext(); }
        catch (Exception ex) { (_reply, waiting) = (Err(ex.Message), false); }
        if (!waiting)
        {
            _running.Reply = _reply;
            _running.Done.Set();
            (_running, _steps) = (null, null);
        }
        // A command waiting on frames, or one queued behind it, keeps the loop going.
        if (_running is not null || !_queue.IsEmpty) _runtime.RequestFrame();
        windowEvents.Clear();
        return _events;
    }

    // A command is a sequence of frames: each `yield return true` builds one
    // before the command goes on. What it decides to answer goes in _reply.
    private IEnumerable<bool> Run(JsonNode node)
    {
        var cmd = node["cmd"]?.GetValue<string>() ?? "";
        return cmd switch
        {
            "ping"            => [],
            "click"           => Click(node),
            "click_at"        => ClickAt(node, MouseButton.Left, "click_at"),
            "right_click_at"  => ClickAt(node, MouseButton.Right, "right_click_at"),
            "mousedown_at"    => MouseDownAt(node),
            "mouseup"         => MouseUp(),
            "scroll"          => Scroll(node),
            "hover"           => Hover(node),
            "gesture"         => Gesture(node),
            "press"           => Press(node),
            "type"            => Type(node),
            "fill"            => Fill(node),
            "is_visible"      => Query(node, n => OkResult(n is not null)),
            "is_disabled"     => Query(node, n => OkResult(n is Box box && (box.Claims & InteractionKinds.Disabled) != 0)),
            "get_text"        => GetText(node),
            "screenshot"      => Screenshot(node),
            _                 => Refuse($"Unknown command: {cmd}"),
        };
    }

    private IEnumerable<bool> Refuse(string message)
    {
        _reply = Err(message);
        return [];
    }

    // ── Input commands ─────────────────────────────────────────────────────

    /// <summary>
    /// Frames a command waits for after its input, so what is on screen is the
    /// result of the command, not the frame that noticed it.
    ///
    /// <para>An interaction is answered by a box asking about it <em>during</em> a build, so
    /// anything the answer changes — state the app writes back from a click — lands after
    /// the tree for that frame has already been described. One frame sees the click; the
    /// next one shows what it did. A control that only exists once the click has happened —
    /// the second half of a confirm, a field that opens under a row — is therefore not in the
    /// tree until that second frame.</para>
    /// </summary>
    private const int SettleFrames = 2;

    // One mouse event, the way the window reports one; the server's pointer follows it.
    private void Mouse(WindowEventKind kind, float x, float y, MouseButton button)
    {
        (_pointerX, _pointerY) = (x, y);
        _events.Add(WindowEvent.Mouse(kind, x, y, button));
    }

    private static (float X, float Y) Point(JsonNode node, string cmd)
        => (node["x"]?.GetValue<float>() ?? throw new ArgumentException($"{cmd} requires 'x'"),
            node["y"]?.GetValue<float>() ?? throw new ArgumentException($"{cmd} requires 'y'"));

    // By name: a press and a release at the box's centre, addressed to that box
    // (DispatchClick), so a card is not clicked through the button drawn on it.
    private IEnumerable<bool> Click(JsonNode node)
    {
        _runtime.DispatchClick(RequireId(node));
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    // As a hand does it: onto the point, down, up, each in a frame of its own.
    private IEnumerable<bool> ClickAt(JsonNode node, MouseButton button, string cmd)
    {
        var (x, y) = Point(node, cmd);
        Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown);
        yield return true;
        Mouse(WindowEventKind.MouseDown, x, y, button);
        yield return true;
        Mouse(WindowEventKind.MouseUp, x, y, button);
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    // Onto the point and down; the button stays down until a mouseup.
    private IEnumerable<bool> MouseDownAt(JsonNode node)
    {
        var (x, y) = Point(node, "mousedown_at");
        Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown);
        yield return true;
        Mouse(WindowEventKind.MouseDown, x, y, MouseButton.Left);
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    // Where the server's pointer is.
    private IEnumerable<bool> MouseUp()
    {
        Mouse(WindowEventKind.MouseUp, _pointerX, _pointerY, MouseButton.Left);
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    private IEnumerable<bool> Hover(JsonNode node)
    {
        var (x, y) = Point(node, "hover");
        Mouse(WindowEventKind.MouseMove, x, y, MouseButton.Unknown);
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    // dx and dy are logical px, as a box reads them; a wheel notch is 32 of them,
    // and wheel up is content up.
    private IEnumerable<bool> Scroll(JsonNode node)
    {
        var (x, y) = Point(node, "scroll");
        float dy = node["dy"]?.GetValue<float>() ?? throw new ArgumentException("scroll requires 'dy'");
        float dx = node["dx"]?.GetValue<float>() ?? 0f;
        (_pointerX, _pointerY) = (x, y);
        _events.Add(WindowEvent.Wheel(x, y, dx / 32f, -dy / 32f));
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    /// <summary>
    /// One continuous pointer gesture: press at the first point, move through the rest,
    /// release at the last — all in one frame.
    ///
    /// <para>The per-command handlers each give the app frames, which means a test-driven
    /// drag gets a fresh frame between every move. A real pointer does not: mouse moves
    /// arrive far faster than frames, so build code acting on a drag is holding state from
    /// an older frame. This command reproduces that.</para>
    ///
    /// <para><c>points</c> is <c>[[x,y], …]</c>, at least two. <c>render_each</c> (default
    /// false) gives each step a frame of its own, for comparison: a test that passes with it
    /// true and fails with it false has found build code that holds a stale frame's state.</para>
    ///
    /// <para>Unlike <c>mousedown_at</c>, this does not dispatch a click: a drag is not a
    /// click, and a real window only clicks when the pointer did not travel.</para>
    /// </summary>
    private IEnumerable<bool> Gesture(JsonNode node)
    {
        if (node["points"] is not JsonArray pts || pts.Count < 2)
        {
            _reply = Err("gesture requires 'points': [[x,y], [x,y], ...] with at least two points");
            yield break;
        }
        bool renderEach = node["render_each"]?.GetValue<bool>() ?? false;

        (float X, float Y) At(int i)
        {
            var p = pts[i] as JsonArray ?? throw new ArgumentException($"points[{i}] must be [x, y]");
            return (p[0]!.GetValue<float>(), p[1]!.GetValue<float>());
        }

        var start = At(0);
        Mouse(WindowEventKind.MouseDown, start.X, start.Y, MouseButton.Left);
        if (renderEach) yield return true;
        for (int i = 1; i < pts.Count; i++)
        {
            var p = At(i);
            Mouse(WindowEventKind.MouseMove, p.X, p.Y, MouseButton.Unknown);
            if (renderEach) yield return true;
        }
        var end = At(pts.Count - 1);
        Mouse(WindowEventKind.MouseUp, end.X, end.Y, MouseButton.Left);
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    /// <summary>
    /// A real key press, with its modifiers pressed around it as a keyboard does:
    /// Ctrl and Shift down, the key down, each event saying what is held; then, in
    /// a later frame, all of them up.
    /// What an app's global shortcut handling (Delete, Ctrl+A, Escape) sees is what
    /// a keyboard gives it.
    ///
    /// <para>Names are <see cref="Bibui.Core.Key"/> values ("Delete", "Backspace", "Escape", "A"),
    /// with the Arrow* spellings accepted as aliases for the bare direction names.</para>
    /// </summary>
    private IEnumerable<bool> Press(JsonNode node)
    {
        var name = node["key"]?.GetValue<string>()
            ?? throw new ArgumentException("press requires 'key' (e.g. Delete, Backspace, Escape, A)");
        bool ctrl  = node["ctrl"]?.GetValue<bool>()  ?? false;
        bool shift = node["shift"]?.GetValue<bool>() ?? false;
        if (!TryParseKey(name, out var key))
        {
            _reply = Err($"Unknown key '{name}'. Use a Bibui.Core.Key name, e.g. Delete or Escape.");
            yield break;
        }

        var held = (ctrl ? KeyModifiers.Ctrl : 0) | (shift ? KeyModifiers.Shift : 0);
        if (ctrl) _events.Add(WindowEvent.KeyPress(Key.ControlLeft, KeyModifiers.Ctrl));
        if (shift) _events.Add(WindowEvent.KeyPress(Key.ShiftLeft, held));
        _events.Add(WindowEvent.KeyPress(key, held));
        for (int i = 0; i < SettleFrames; i++) yield return true;
        // And let go, in a frame of its own, as a keyboard does. A press that never
        // released was a key no user can type: an app that acted on the release as well as
        // the press — Tab switching a tab over and straight back — passed every scripted case
        // and failed on the first real keystroke.
        _events.Add(WindowEvent.KeyRelease(key, held));
        if (shift) _events.Add(WindowEvent.KeyRelease(Key.ShiftLeft, held & ~KeyModifiers.Shift));
        if (ctrl) _events.Add(WindowEvent.KeyRelease(Key.ControlLeft));
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    /// <summary>
    /// Types text into whatever holds focus, a character at a time, as a keyboard would:
    /// what a key types arrives apart from the key, so <c>press</c> reaches Delete and
    /// Escape but never a digit. Control characters are skipped; Enter and Tab are key
    /// presses. Distinct from <c>fill</c>, which presses the field first and replaces what
    /// it holds. Typing with nothing focused reaches no field, which is not an error.
    /// </summary>
    private IEnumerable<bool> Type(JsonNode node)
    {
        var text = node["text"]?.GetValue<string>() ?? throw new ArgumentException("type requires 'text'");
        foreach (var rune in text.EnumerateRunes())
            if (!Rune.IsControl(rune)) _events.Add(WindowEvent.Text(rune));
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    /// <summary>
    /// Replaces the whole text of the field named <c>id</c> with <c>value</c>, the way a
    /// person would: presses the field, which its app focuses; then, a frame later, Ctrl+A
    /// and the value typed, or Backspace for an empty one. Nothing when no box that asks
    /// carries the name.
    /// </summary>
    private IEnumerable<bool> Fill(JsonNode node)
    {
        var id    = RequireId(node);
        var value = node["value"]?.GetValue<string>() ?? "";
        if (!_runtime.DispatchClick(id)) yield break;
        yield return true;
        _events.Add(WindowEvent.KeyPress(Key.ControlLeft, KeyModifiers.Ctrl));
        _events.Add(WindowEvent.KeyPress(Key.A, KeyModifiers.Ctrl));
        _events.Add(WindowEvent.KeyRelease(Key.A, KeyModifiers.Ctrl));
        _events.Add(WindowEvent.KeyRelease(Key.ControlLeft));
        if (value.Length == 0)
        {
            _events.Add(WindowEvent.KeyPress(Key.Backspace));
            _events.Add(WindowEvent.KeyRelease(Key.Backspace));
        }
        foreach (var rune in value.EnumerateRunes()) _events.Add(WindowEvent.Text(rune));
        for (int i = 0; i < SettleFrames; i++) yield return true;
    }

    private static bool TryParseKey(string name, out Key key)
    {
        // The window reports directions as Left/Right/Up/Down; "ArrowLeft" is the spelling
        // every other UI test harness uses, so accept both rather than making callers care.
        var normalized = name.StartsWith("Arrow", StringComparison.OrdinalIgnoreCase)
            ? name["Arrow".Length..]
            : name;
        return Enum.TryParse(normalized, ignoreCase: true, out key)
            && Enum.IsDefined(typeof(Key), key);
    }

    // ── Queries ────────────────────────────────────────────────────────────

    // A frame first, so a query answers for the frame a person would be looking at.
    private IEnumerable<bool> Query(JsonNode node, Func<UiNode?, string> answer)
    {
        var id = RequireId(node);
        yield return true;
        _reply = answer(_runtime.FindById(id));
    }

    // A text's own words, or the first text under the node.
    private IEnumerable<bool> GetText(JsonNode node)
    {
        var id = RequireId(node);
        yield return true;
        var found = _runtime.FindById(id);
        _reply = found is null ? Err($"No node with id '{id}'")
            : OkResult(FirstText(found) is { } text ? new string(text.Content) : null);

        static Text? FirstText(UiNode n)
        {
            if (n is Text t) return t;
            for (var c = n.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
                if (FirstText(c) is { } found) return found;
            return null;
        }
    }

    // ── Pixels ─────────────────────────────────────────────────────────────

    private IEnumerable<bool> Screenshot(JsonNode node)
    {
        var path = ResolveOutputPath(
            node["path"]?.GetValue<string>() ?? throw new ArgumentException("screenshot requires 'path'"));
        if (_draw is null)
        {
            _reply = Err("This server was started without a renderer to draw screenshots with");
            yield break;
        }
        yield return true;
        var png = FramePixels.Png(_runtime, _draw);

        // Written beside the target and renamed over it, so a link put in the
        // target's place after the check is replaced, not written through.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            stream.Write(png);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// The full path of <paramref name="requested"/> inside the output directory, its
    /// directories created. Refused: an empty or absolute path, a <c>..</c> segment,
    /// and a symbolic link anywhere below the output directory on the way to it.
    /// </summary>
    internal string ResolveOutputPath(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            throw new ArgumentException("Refused: the path is empty");
        if (requested.Contains('\0'))
            throw new ArgumentException("Refused: the path holds a NUL");
        if (Path.IsPathRooted(requested) || requested.StartsWith('~'))
            throw new ArgumentException("Refused: a path is relative to the output directory");
        foreach (var segment in requested.Split('/', '\\'))
            if (segment == "..")
                throw new ArgumentException("Refused: a path may not hold '..'");

        var full = Path.GetFullPath(Path.Combine(_outputDir, requested));
        if (!full.StartsWith(_outputDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Refused: the path leaves the output directory");

        // Every step below the output directory, the file itself included, must be
        // what it says rather than a link to somewhere else.
        var step = _outputDir;
        var segments = Path.GetRelativePath(_outputDir, full).Split(Path.DirectorySeparatorChar);
        for (int i = 0; i < segments.Length; i++)
        {
            step = Path.Combine(step, segments[i]);
            if (new FileInfo(step).LinkTarget is not null)
                throw new ArgumentException("Refused: the path goes through a symbolic link");
            if (i < segments.Length - 1) Directory.CreateDirectory(step);
        }
        return full;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string RequireId(JsonNode node)
        => node["id"]?.GetValue<string>()
           ?? throw new ArgumentException("Command is missing the 'id' field");

    private static string Ok()
        => "{\"ok\":true}";

    private static string OkResult(bool value)
        => JsonSerializer.Serialize(new BoolResponse(true, value), TestServerJson.Default.BoolResponse);

    private static string OkResult(string? value)
        => JsonSerializer.Serialize(new StringResponse(true, value), TestServerJson.Default.StringResponse);

    private static string Err(string message)
        => JsonSerializer.Serialize(new ErrorResponse(false, message), TestServerJson.Default.ErrorResponse);
}

// The test server's responses, serialised through a source-generated context so an
// app published with Native AOT carries no reflection JSON. The wire format
// is unchanged: {"ok":true,"result":…} and {"ok":false,"error":"…"}, null included.
internal sealed record BoolResponse(bool Ok, bool Result);
internal sealed record StringResponse(bool Ok, string? Result);
internal sealed record ErrorResponse(bool Ok, string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BoolResponse))]
[JsonSerializable(typeof(StringResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class TestServerJson : JsonSerializerContext;
