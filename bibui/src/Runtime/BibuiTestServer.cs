using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bibui.Draw;
using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// A TCP test server that exposes <see cref="BibuiRuntime"/> as a line-delimited
/// JSON command API on the loopback interface, for workflow automation and video
/// recording. Start alongside <see cref="BibuiWindow"/>.
///
/// <para>It is off unless the app asks for it. <see cref="StartIfEnabled"/> starts
/// one only when the environment variable <c>BIBUI_TEST_SERVER</c> is <c>1</c>;
/// the samples use that and nothing else.</para>
///
/// <para>Every request carries the server's token (<c>"token":"…"</c>): a random
/// value per server, written to <see cref="TokenFile"/>, a file only the user can
/// read. A request without it, a line that is not a JSON object, and anything
/// that looks like HTTP are refused, and the connection is closed. Files are
/// written only inside <see cref="OutputDirectory"/>: <c>screenshot</c> and
/// <c>start_recording</c> take paths relative to it.</para>
///
/// <para>A command builds frames on the server's thread, under the runtime's
/// <see cref="BibuiRuntime.FrameLock"/>, as the window's loop does on its own.
/// Screenshots and recordings draw the runtime's <see cref="BibuiRuntime.DrawList"/>
/// with the function the server was given, under the same lock; without one
/// they are refused.</para>
///
/// <para>Protocol: each request and response is a single JSON line. Commands are
/// dispatched synchronously — the response is sent once the action completes.
/// The <c>start_recording</c> command begins streaming frames to FFmpeg;
/// <c>stop_recording</c> finalises the video.</para>
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
    // What a screenshot and a recording draw the frame's list with; null refuses both.
    private readonly DrawToPixels? _draw;
    private readonly int _port;
    private readonly string _outputDir;
    private readonly byte[] _token;
    private readonly bool _writeTokenFile;
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _stopping;

    // Recording state
    private readonly object _recordLock = new();
    private volatile bool _recording;
    private Thread? _recordThread;

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
    /// Draws a frame's list into pixels, for <c>screenshot</c> and <c>start_recording</c>:
    /// the app's renderer, on the CPU. Null refuses both.
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

    /// <summary>The one directory <c>screenshot</c> and <c>start_recording</c> write in.</summary>
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
                    var response = refusal ?? ProcessCommand(request!);
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

    // ── Command dispatch ───────────────────────────────────────────────────

    private string ProcessCommand(JsonNode node)
    {
        try
        {
            var cmd  = node["cmd"]?.GetValue<string>() ?? "";

            return cmd switch
            {
                "ping"            => Ok(),
                "click"           => HandleClick(node),
                "click_at"        => HandleClickAt(node),
                "right_click_at"  => HandleRightClickAt(node),
                "mousedown_at"    => HandleMouseDownAt(node),
                "mouseup"         => HandleMouseUp(),
                "scroll"          => HandleScroll(node),
                "hover"           => HandleHover(node),
                "gesture"         => HandleGesture(node),
                "press"           => HandlePress(node),
                "type"            => HandleType(node),
                "fill"            => HandleFill(node),
                "is_visible"      => HandleIsVisible(node),
                "is_disabled"     => HandleIsDisabled(node),
                "get_text"        => HandleGetText(node),
                "screenshot"      => HandleScreenshot(node),
                "start_recording" => HandleStartRecording(node),
                "stop_recording"  => HandleStopRecording(),
                _                 => Err($"Unknown command: {cmd}"),
            };
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    // ── Action handlers ────────────────────────────────────────────────────

    /// <summary>
    /// One synthesised mouse event. The whole point of building these rather
    /// than calling the runtime's dispatch methods: a scripted press takes the
    /// path a real one takes, so what a test proves is what a user gets.
    /// </summary>
    private static BibUiEvent Mouse(BibUiEventKind kind, MouseButton button, float x, float y)
        => new() { Kind = kind, Button = button, X = x, Y = y };

    /// <summary>
    /// Renders until what is on screen is the result of the command, not the frame that
    /// noticed it.
    ///
    /// <para>An interaction is answered by a box asking about it <em>during</em> a build, so
    /// anything the answer changes — state the host writes back from a click — lands after
    /// the tree for that frame has already been described. One render sees the click; the
    /// next one shows what it did. A control that only exists once the click has happened —
    /// the second half of a confirm, a field that opens under a row — is therefore not in the
    /// tree until that second render.</para>
    ///
    /// <para>That is invisible to a caller who reads the tree between two commands and a
    /// trap for one who does not: the next command looks up a control that is on screen by
    /// every account except the one the runtime has. Rendering the owed frame here is what
    /// makes a command mean "this happened" rather than "this was noticed". The old callback
    /// path had the same guarantee for a different reason — it invoked the handler before the
    /// render rather than inside it.</para>
    /// </summary>
    private void Settle()
    {
        _runtime.EndBuild();
        _runtime.EndBuild();
    }

    private string HandleClick(JsonNode node)
    {
        var id = RequireId(node);
        _runtime.DispatchClick(id);
        Settle();
        return Ok();
    }

    private string HandleClickAt(JsonNode node)
    {
        float x = node["x"]?.GetValue<float>() ?? throw new ArgumentException("click_at requires 'x'");
        float y = node["y"]?.GetValue<float>() ?? throw new ArgumentException("click_at requires 'y'");
        // A press and a release, which is what a click is. Going through the
        // same path as a real one means multi-click, focus and drag state all
        // behave as they do for a user.
        _runtime.ProcessEvents(new[]
        {
            Mouse(BibUiEventKind.MouseDown, MouseButton.Left, x, y),
            Mouse(BibUiEventKind.MouseUp,   MouseButton.Left, x, y),
        });
        Settle();
        return Ok();
    }

    private string HandleRightClickAt(JsonNode node)
    {
        float x = node["x"]?.GetValue<float>() ?? throw new ArgumentException("right_click_at requires 'x'");
        float y = node["y"]?.GetValue<float>() ?? throw new ArgumentException("right_click_at requires 'y'");
        _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseDown, MouseButton.Right, x, y));
        Settle();
        return Ok();
    }

    private string HandleMouseDownAt(JsonNode node)
    {
        float x = node["x"]?.GetValue<float>() ?? throw new ArgumentException("mousedown_at requires 'x'");
        float y = node["y"]?.GetValue<float>() ?? throw new ArgumentException("mousedown_at requires 'y'");
        _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseDown, MouseButton.Left, x, y));
        Settle();
        return Ok();
    }

    private string HandleMouseUp()
    {
        _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseUp, MouseButton.Left, 0f, 0f));
        Settle();
        return Ok();
    }

    /// <summary>
    /// One continuous pointer gesture: press at the first point, move through the rest,
    /// release at the last — with NO render in between.
    ///
    /// <para>This exists because the per-command handlers each build a frame (<see cref="BibuiRuntime.EndBuild"/>),
    /// which means a test-driven drag gets a fresh frame between every move. A real pointer
    /// does not: mouse moves arrive far faster than frames, so build code acting on a drag
    /// is holding state from an older frame. Any app that writes back a captured
    /// snapshot (<c>setState.Set(state with ...)</c>) instead of using
    /// <c>setState.Update</c> is broken under a real mouse and green under a synthetic one —
    /// exactly the bug this command makes reproducible.</para>
    ///
    /// <para><c>points</c> is <c>[[x,y], …]</c>, at least two. <c>render_each</c> (default
    /// false) restores the old per-move render for comparison — a test that passes with it
    /// true and fails with it false has found a stale-closure bug.</para>
    ///
    /// <para>Unlike <c>mousedown_at</c>, this does not dispatch a click: a drag is not a
    /// click, and a real window only clicks when the pointer did not travel.</para>
    /// </summary>
    private string HandleGesture(JsonNode node)
    {
        if (node["points"] is not JsonArray pts || pts.Count < 2)
            return Err("gesture requires 'points': [[x,y], [x,y], ...] with at least two points");
        bool renderEach = node["render_each"]?.GetValue<bool>() ?? false;

        (float X, float Y) At(int i)
        {
            var p = pts[i] as JsonArray ?? throw new ArgumentException($"points[{i}] must be [x, y]");
            return (p[0]!.GetValue<float>(), p[1]!.GetValue<float>());
        }

        var start = At(0);
        _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseDown, MouseButton.Left, start.X, start.Y));
        if (renderEach) _runtime.EndBuild();

        for (int i = 1; i < pts.Count; i++)
        {
            var p = At(i);
            _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseMove, MouseButton.Left, p.X, p.Y));
            if (renderEach) _runtime.EndBuild();
        }

        var end = At(pts.Count - 1);
        _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseUp, MouseButton.Left, end.X, end.Y));
        Settle();
        return Ok();
    }

    private string HandleHover(JsonNode node)
    {
        float x = node["x"]?.GetValue<float>() ?? throw new ArgumentException("hover requires 'x'");
        float y = node["y"]?.GetValue<float>() ?? throw new ArgumentException("hover requires 'y'");
        _runtime.ProcessEvent(Mouse(BibUiEventKind.MouseMove, MouseButton.Unknown, x, y));
        Settle();
        return Ok();
    }

    /// <summary>
    /// Synthesise a real key press: builds the same BibUiEvent the window pump would for this
    /// key and enqueues it, so an app's global shortcut handling (Delete, Ctrl+A, Escape) is
    /// reachable from a test.
    ///
    /// <para>Distinct from <c>key</c>, which only steers an open dropdown and never touches the
    /// event queue — nothing driven through that command can exercise a host's own key handler.</para>
    ///
    /// <para>Names are <see cref="Bibui.Key"/> values (Silk.NET's names) ("Delete", "Backspace", "Escape", "A"), with
    /// the Arrow* spellings accepted as aliases for the bare direction names.</para>
    /// </summary>
    private string HandlePress(JsonNode node)
    {
        var name = node["key"]?.GetValue<string>()
            ?? throw new ArgumentException("press requires 'key' (e.g. Delete, Backspace, Escape, A)");
        bool ctrl  = node["ctrl"]?.GetValue<bool>()  ?? false;
        bool shift = node["shift"]?.GetValue<bool>() ?? false;

        if (!TryParseKey(name, out var key))
            return Err($"Unknown key '{name}'. Use a Bibui.Key name, e.g. Delete or Escape.");

        _runtime.ProcessEvent(BibuiWindow.BibUiEventFromKey(key, ctrl, shift, _runtime.ClipboardGet));
        Settle();
        // And let go of it, in a frame of its own, as a keyboard does. A press that never
        // released was a key no user can type: an app that acted on the release as well as
        // the press — Tab switching a tab over and straight back — passed every scripted case
        // and failed on the first real keystroke.
        _runtime.ProcessEvent(new BibUiEvent { Kind = BibUiEventKind.Release, Key = key });
        Settle();
        return Ok();
    }

    /// <summary>
    /// Type text into whatever holds focus, one character at a time, exactly as a keyboard
    /// would.
    ///
    /// <para>The other half of <c>press</c>. A keyboard reports a printable character on a
    /// different channel from the key that produced it — Silk raises KeyChar separately from
    /// KeyDown, and <see cref="BibuiWindow.BibUiEventFromKey"/> deliberately returns nothing for
    /// printable keys because layout, not scancode, decides what "the 2 key" types. So
    /// <c>press</c> can reach Delete and Escape but can never enter a digit, and a text field
    /// stayed undrivable.</para>
    ///
    /// <para>Distinct from <c>fill</c>, which presses the field first and replaces what it
    /// holds. This only types, through the same per-keystroke path a user does.</para>
    ///
    /// <para>Click the field first; typing with nothing focused reaches no field, which is
    /// not an error, matching what a real keyboard does.</para>
    /// </summary>
    private string HandleType(JsonNode node)
    {
        var text = node["text"]?.GetValue<string>()
            ?? throw new ArgumentException("type requires 'text'");

        foreach (var rune in text.EnumerateRunes())
        {
            // Control characters have no printable event; Enter and Tab are
            // key presses, so route them through press rather than smuggling
            // them in as text.
            if (Rune.IsControl(rune)) continue;

            _runtime.ProcessEvent(new BibUiEvent
            {
                Kind      = BibUiEventKind.Text,
                Key       = Key.Unknown,
                Char      = (char)rune.Value,
                CodePoint = (uint)rune.Value,
            });
        }

        Settle();
        return Ok();
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

    private string HandleScroll(JsonNode node)
    {
        float x = node["x"]?.GetValue<float>() ?? throw new ArgumentException("scroll requires 'x'");
        float y = node["y"]?.GetValue<float>() ?? throw new ArgumentException("scroll requires 'y'");
        float dy = node["dy"]?.GetValue<float>() ?? throw new ArgumentException("scroll requires 'dy'");
        float dx = node["dx"]?.GetValue<float>() ?? 0f;
        _runtime.ProcessEvent(new BibUiEvent
        {
            Kind = BibUiEventKind.Scroll, X = x, Y = y, ScrollX = dx, ScrollY = dy,
        });
        Settle();
        return Ok();
    }

    private string HandleFill(JsonNode node)
    {
        var id    = RequireId(node);
        var value = node["value"]?.GetValue<string>() ?? "";
        _runtime.DispatchFill(id, value);
        Settle();
        return Ok();
    }

    // ── Query handlers ─────────────────────────────────────────────────────

    // A frame first, so a query answers for the frame a person would be looking at.
    private UiNode? Find(JsonNode node)
    {
        var id = RequireId(node);
        _runtime.EndBuild();
        return _runtime.FindById(id);
    }

    private string HandleIsVisible(JsonNode node) => OkResult(Find(node) is not null);

    private string HandleIsDisabled(JsonNode node)
        => OkResult(Find(node) is Box box && (box.Claims & InteractionKinds.Disabled) != 0);

    // A text's own words, or the first text under the node.
    private string HandleGetText(JsonNode node)
    {
        var found = Find(node);
        if (found is null) return Err($"No node with id '{RequireId(node)}'");
        return OkResult(FirstText(found) is { } text ? new string(text.Content) : null);

        static Text? FirstText(UiNode n)
        {
            if (n is Text t) return t;
            for (var c = n.FirstChild; c is not null; c = c.Next == c ? null : c.Next)
                if (FirstText(c) is { } found) return found;
            return null;
        }
    }

    private string HandleScreenshot(JsonNode node)
    {
        var path = ResolveOutputPath(
            node["path"]?.GetValue<string>() ?? throw new ArgumentException("screenshot requires 'path'"));
        if (_draw is null) return Err("This server was started without a renderer to draw screenshots with");
        byte[] png;
        lock (_runtime.FrameLock)
        {
            _runtime.EndBuild();
            png = FramePixels.Png(_runtime, _draw);
        }

        // Written beside the target and renamed over it, so a link put in the
        // target's place after the check is replaced, not written through.
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            stream.Write(png);
        File.Move(temp, path, overwrite: true);
        return Ok();
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

    // ── Recording handlers ─────────────────────────────────────────────────

    private string HandleStartRecording(JsonNode node)
    {
        var outputPath = ResolveOutputPath(node["output"]?.GetValue<string>() ?? "recording.mp4");

        lock (_recordLock)
        {
            if (_recording) return Err("Already recording");
        }

        if (_draw is null) return Err("This server was started without a renderer to record with");
        int width, height;
        lock (_runtime.FrameLock)
        {
            _runtime.EndBuild();
            FramePixels.Bgra(_runtime, _draw, out width, out height);
        }

        lock (_recordLock)
            _recording = true;

        _recordThread = new Thread(() =>
        {
            try
            {
                RecordLoop(width, height, outputPath);
            }
            catch (Exception ex)
            {
                // A recorder failure (ffmpeg died, pipe broke) must never take
                // down the host process from a background thread.
                Console.Error.WriteLine($"[BibuiTestServer] recording failed: {ex.Message}");
                lock (_recordLock) _recording = false;
            }
        })
        {
            IsBackground = true,
            Name = "test-server-record"
        };
        _recordThread.Start();

        return Ok();
    }

    private string HandleStopRecording()
    {
        lock (_recordLock)
        {
            if (!_recording) return Err("Not recording");
            _recording = false;
        }

        // Block until the record thread closes FFmpeg and the file is finalised
        _recordThread?.Join();
        _recordThread = null;

        return Ok();
    }

    // ── Frame capture & encode ─────────────────────────────────────────────

    /// <summary>
    /// ffmpeg reading raw RGBA frames on stdin and writing <paramref name="outputPath"/>.
    /// One argument per element, never a string to be split, so nothing in the
    /// path can add an argument; the path is a full one (it starts with the output
    /// directory), so ffmpeg cannot take it for an option or a protocol either.
    /// </summary>
    internal static ProcessStartInfo FfmpegStartInfo(int width, int height, string outputPath)
    {
        if (!Path.IsPathFullyQualified(outputPath))
            throw new ArgumentException("ffmpeg's output must be a full path", nameof(outputPath));

        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardInput = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
        };
        foreach (var arg in (ReadOnlySpan<string>)[
            "-y", "-loglevel", "quiet",
            "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}", "-framerate", "30",
            "-i", "pipe:0",
            "-vf", "format=yuv420p", "-c:v", "libx264", "-preset", "fast",
            outputPath])
            psi.ArgumentList.Add(arg);
        return psi;
    }

    private void RecordLoop(int width, int height, string outputPath)
    {
        var psi = FfmpegStartInfo(width, height, outputPath);

        using var proc  = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg");
        var       stdin = proc.StandardInput.BaseStream;

        const int frameMs = 1000 / 30;
        while (_recording)
        {
            // The frame the window last built, drawn again here: recording adds
            // no frames of its own, so it does not speed up what it records.
            byte[] bgra;
            int srcW, srcH;
            lock (_runtime.FrameLock) bgra = FramePixels.Bgra(_runtime, _draw!, out srcW, out srcH);
            var rgba = BgraToRgba(bgra, srcW, srcH, width, height);
            stdin.Write(rgba, 0, rgba.Length);
            Thread.Sleep(frameMs);
        }

        stdin.Close();
        proc.WaitForExit();
    }

    /// <summary>
    /// The frame as the RGBA ffmpeg is told to expect, nearest-neighbour scaled
    /// to the recording size when the window has since been resized. Straight
    /// off the frame's own pixels, no PNG round trip.
    /// </summary>
    private static byte[] BgraToRgba(byte[] bgra, int srcW, int srcH, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        if (srcW <= 0 || srcH <= 0 || bgra.Length < srcW * srcH * 4) return rgba;

        for (int y = 0; y < height; y++)
        {
            int sy = (int)((long)y * srcH / height);
            for (int x = 0; x < width; x++)
            {
                int sx = (int)((long)x * srcW / width);
                int s = (sy * srcW + sx) * 4;
                int d = (y * width + x) * 4;
                rgba[d]     = bgra[s + 2];
                rgba[d + 1] = bgra[s + 1];
                rgba[d + 2] = bgra[s];
                rgba[d + 3] = bgra[s + 3];
            }
        }
        return rgba;
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
