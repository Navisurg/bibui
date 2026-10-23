using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bibui.Port;

/// <summary>A request the app refused, or a node the tool could not reach, with the reason.</summary>
public sealed class PortException(string message) : Exception(message);

/// <summary>A node of a <c>test.tree</c> answer, linked to its parent.</summary>
public sealed class PortNode
{
    public string Kind = "";
    public string? Id;
    public string? Text;
    public float X, Y, W, H;
    public string[] Claims = [];
    public PortNode? Parent;
    public List<PortNode> Children = [];

    public bool HasClaim(string claim) => Array.IndexOf(Claims, claim) >= 0;
    public (float X, float Y) Centre => (X + W / 2, Y + H / 2);

    /// <summary>One line: kind, id, rect, claims, text.</summary>
    public override string ToString()
    {
        var line = new StringBuilder(Kind);
        if (Id is not null) line.Append(" #").Append(Id);
        line.Append(CultureInfo.InvariantCulture, $" [{X:0.#},{Y:0.#} {W:0.#}x{H:0.#}]");
        if (Claims.Length > 0) line.Append(' ').AppendJoin(' ', Claims);
        if (Text is not null) line.Append(" ").Append(JsonValue.Create(Text).ToJsonString(Port.Relaxed));
        return line.ToString();
    }
}

/// <summary>
/// A connection to a running app's test port: one line out, one answer back. And
/// what the tool does with a node: find it, wait until it holds still, scroll it
/// into view, click it.
/// </summary>
public sealed class Port : IDisposable
{
    /// <summary>' and non-ASCII text as themselves, as the port writes them.</summary>
    public static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly StreamReader _reader;

    /// <summary>How long a click waits for its node to hold still.</summary>
    public TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public Port(string path)
    {
        _socket.Connect(new UnixDomainSocketEndPoint(path));
        _socket.ReceiveTimeout = 30_000;    // an app that stops answering: fail, not hang
        _reader = new StreamReader(new NetworkStream(_socket), Encoding.UTF8);
    }

    public void Dispose() => _socket.Dispose();

    // ── Finding an app ─────────────────────────────────────────────────────

    /// <summary>
    /// The running apps' sockets, oldest first: <c>&lt;name&gt;-&lt;pid&gt;.sock</c> in the
    /// port's folder, whose process is still there (a crashed app leaves its socket).
    /// </summary>
    public static List<(string Name, int Pid, string Path)> Running(string? folder = null)
    {
        if (folder is null)
        {
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            folder = Path.Combine(string.IsNullOrEmpty(runtimeDir) ? Path.GetTempPath() : runtimeDir, "bibui");
        }
        var found = new List<(string Name, int Pid, string Path)>();
        if (!Directory.Exists(folder)) return found;
        foreach (var path in Directory.EnumerateFiles(folder, "*.sock").OrderBy(File.GetLastWriteTimeUtc))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            int dash = stem.LastIndexOf('-');
            if (dash > 0 && int.TryParse(stem.AsSpan(dash + 1), out var pid) && Alive(pid)) found.Add((stem[..dash], pid, path));
        }
        return found;
    }

    /// <summary>The socket for <paramref name="app"/>: a path, an app's name (its newest socket), or null for the only app running.</summary>
    public static string SocketFor(string? app)
    {
        if (app is not null && (app.Contains('/') || app.EndsWith(".sock", StringComparison.Ordinal))) return Path.GetFullPath(app);
        var running = Running();
        var named = app is null ? running : running.FindAll(s => s.Name == app);
        if (named.Count == 1 || (app is not null && named.Count > 1)) return named[^1].Path;
        if (named.Count == 0)
            throw new PortException(app is null ? "No app with a test port is running."
                : $"No app named '{app}' is running" + (running.Count > 0 ? $"; running: {string.Join(", ", running.Select(s => s.Name))}." : "."));
        throw new PortException($"Several apps are running ({string.Join(", ", named.Select(s => $"{s.Name} {s.Pid}"))}): name one with --app.");
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ── Lines ──────────────────────────────────────────────────────────────

    /// <summary>One request line out, its answer line back, as sent.</summary>
    public string Send(string line)
    {
        _socket.Send(Encoding.UTF8.GetBytes(line + "\n"));
        return _reader.ReadLine() ?? throw new PortException("The app closed the connection.");
    }

    /// <summary>One request; its answer's <c>result</c> (null when it has none), or the app's reason thrown.</summary>
    public JsonNode? Ask(string line)
    {
        var answer = JsonNode.Parse(Send(line))!;
        if (answer["ok"]?.GetValue<bool>() != true) throw new PortException(answer["error"]?.GetValue<string>() ?? "Refused.");
        return answer["result"];
    }

    public JsonNode? Ask(JsonObject request) => Ask(request.ToJsonString(Relaxed));

    // ── The tree ───────────────────────────────────────────────────────────

    public PortNode Tree() => Parse(Ask("""{"$cmd":"test.tree"}""")!);

    /// <summary>The tree in a <c>test.tree</c> answer's <c>result</c>.</summary>
    public static PortNode Parse(JsonNode e, PortNode? parent = null)
    {
        float Num(string name) => e[name]?.GetValue<float>() ?? 0f;
        var node = new PortNode
        {
            Kind = e["kind"]?.GetValue<string>() ?? "node", Id = e["id"]?.GetValue<string>(), Text = e["text"]?.GetValue<string>(),
            X = Num("x"), Y = Num("y"), W = Num("w"), H = Num("h"),
            Claims = e["claims"] is JsonArray claims ? claims.Select(c => c!.GetValue<string>()).ToArray() : [],
            Parent = parent,
        };
        if (e["children"] is JsonArray children)
            foreach (var child in children) node.Children.Add(Parse(child!, node));
        return node;
    }

    /// <summary>The node with this id, in tree order, or null.</summary>
    public static PortNode? Find(PortNode node, string id)
    {
        if (node.Id == id) return node;
        foreach (var child in node.Children)
            if (Find(child, id) is { } found) return found;
        return null;
    }

    /// <summary>Every node match holds for, in tree order.</summary>
    public static List<PortNode> FindAll(PortNode node, Func<PortNode, bool> match, List<PortNode>? found = null)
    {
        found ??= [];
        if (match(node)) found.Add(node);
        foreach (var child in node.Children) FindAll(child, match, found);
        return found;
    }

    /// <summary>The innermost ancestor claiming Clip that has the node's centre outside it, or null: the node is in view.</summary>
    public static PortNode? Hiding(PortNode node)
    {
        var (x, y) = node.Centre;
        for (var a = node.Parent; a is not null; a = a.Parent)
            if (a.HasClaim("Clip") && !(x >= a.X && x < a.X + a.W && y >= a.Y && y < a.Y + a.H)) return a;
        return null;
    }

    // ── Input ──────────────────────────────────────────────────────────────
    // Each event is one request, so one frame, answered once what it set off has run.

    /// <summary>
    /// Clicks the node with this id with <paramref name="button"/> (left, right or
    /// middle), in view and still, at its centre: a move, a press and a release.
    /// </summary>
    public PortNode Click(string id, string button = "left")
    {
        var node = InView(id);
        var (x, y) = node.Centre;
        Ask(Events(Pointer("mouse_move", x, y)));
        Ask(Events(Pointer("mouse_down", x, y, button)));
        Ask(Events(Pointer("mouse_up", x, y, button)));
        return node;
    }

    /// <summary>Moves the pointer onto the node with this id, in view and still, without a press.</summary>
    public PortNode Hover(string id)
    {
        var node = InView(id);
        var (x, y) = node.Centre;
        Ask(Events(Pointer("mouse_move", x, y)));
        return node;
    }

    /// <summary>
    /// Drags from the node with this id's centre to (x, y): a press, moves a few
    /// pixels apart, a release, as a hand drags.
    /// </summary>
    public PortNode Drag(string id, float toX, float toY)
    {
        var node = InView(id);
        var (x, y) = node.Centre;
        Drag(x, y, toX, toY);
        return node;
    }

    /// <summary>Drags from (x, y) to (toX, toY): to select text, say, from its first letter to its last.</summary>
    public void Drag(float x, float y, float toX, float toY)
    {
        Ask(Events(Pointer("mouse_move", x, y)));
        Ask(Events(Pointer("mouse_down", x, y, "left")));
        int steps = Math.Clamp((int)(MathF.Sqrt((toX - x) * (toX - x) + (toY - y) * (toY - y)) / 12f), 1, 40);
        for (int i = 1; i <= steps; i++)
            Ask(Events(Pointer("mouse_move", x + (toX - x) * i / steps, y + (toY - y) * i / steps)));
        Ask(Events(Pointer("mouse_up", toX, toY, "left")));
    }

    /// <summary>Wheel steps over the node with this id: a negative <paramref name="dy"/> shows what is below.</summary>
    public PortNode Scroll(string id, float dy)
    {
        var node = Still(id);
        var (x, y) = node.Centre;
        Ask(Events(new JsonObject { ["kind"] = "scroll", ["x"] = x, ["y"] = y, ["dy"] = dy }));
        return node;
    }

    /// <summary>The text typed into what has the keyboard, a character a frame.</summary>
    public void Type(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            Ask(Events(new JsonObject { ["kind"] = "key_char", ["char"] = rune.ToString() }));
    }

    /// <summary>A key pressed and released <paramref name="times"/> times, with modifiers (ctrl, shift, alt, super).</summary>
    public void Key(string key, IReadOnlyList<string> modifiers, int times = 1)
    {
        for (int i = 0; i < times; i++)
            foreach (var kind in (ReadOnlySpan<string>)["key_down", "key_up"])
            {
                var mods = new JsonArray();
                foreach (var m in modifiers) mods.Add((JsonNode)JsonValue.Create(m));
                Ask(Events(new JsonObject { ["kind"] = kind, ["key"] = key, ["modifiers"] = mods }));
            }
    }

    /// <summary>Waits until nothing in the app is moving: refused after <paramref name="timeout"/> seconds.</summary>
    public void Idle(double timeout) => Ask(new JsonObject { ["$cmd"] = "test.idle", ["timeout"] = timeout });

    // The node with this id, once it holds still and, if a box claiming Clip hides
    // it, scrolled into view.
    private PortNode InView(string id)
    {
        var node = Still(id);
        for (int tries = 0; Hiding(node) is { } hiding; tries++)
        {
            if (tries == 20) throw new PortException($"'{id}' did not come into view.");
            node = ScrollTowards(node, hiding);
        }
        return node;
    }

    private static JsonObject Pointer(string kind, float x, float y, string? button = null)
    {
        var ev = new JsonObject { ["kind"] = kind, ["x"] = x, ["y"] = y };
        if (button is not null) ev["button"] = button;
        return ev;
    }

    // The node once two trees in a row have it in the same place: each tree is a
    // frame, so a node that is still is not being eased anywhere.
    private PortNode Still(string id)
    {
        var deadline = DateTime.UtcNow + Timeout;
        var last = Find(Tree(), id) ?? throw new PortException($"No node '{id}' in the tree.");
        while (true)
        {
            var node = Find(Tree(), id) ?? throw new PortException($"'{id}' left the tree.");
            if ((node.X, node.Y, node.W, node.H) == (last.X, last.Y, last.W, last.H)) return node;
            if (DateTime.UtcNow > deadline) throw new PortException($"'{id}' did not hold still.");
            last = node;
        }
    }

    // Wheels the box that scrolls the node towards it, by the distance from the
    // node's centre to the hiding box's: a wheel step moves a view 32 logical
    // pixels, a negative dy moving the content up and a positive dx moving it left.
    private PortNode ScrollTowards(PortNode node, PortNode hiding)
    {
        var scroller = Scrolls(hiding) ? hiding : null;
        for (var a = node.Parent; scroller is null && a is not null; a = a.Parent)
            if (Scrolls(a)) scroller = a;
        if (scroller is null) throw new PortException($"'{node.Id}' is out of view under '{hiding.Id ?? hiding.Kind}', and nothing scrolls it.");

        var ((x, y), (hx, hy), (sx, sy)) = (node.Centre, hiding.Centre, scroller.Centre);
        var wheel = new JsonObject { ["kind"] = "scroll", ["x"] = sx, ["y"] = sy };
        wheel["dy"] = scroller.HasClaim("ViewScrollY") ? (hy - y) / 32f : 0f;
        if (scroller.HasClaim("ViewScrollX")) wheel["dx"] = (x - hx) / 32f;
        Ask(Events(wheel));

        var moved = Still(node.Id!);
        if ((moved.X, moved.Y) == (node.X, node.Y))
            throw new PortException($"'{node.Id}' is out of view under '{hiding.Id ?? hiding.Kind}', and scrolling '{scroller.Id ?? scroller.Kind}' does not move it.");
        return moved;

        static bool Scrolls(PortNode n) => n.HasClaim("ViewScrollY") || n.HasClaim("ViewScrollX");
    }

    // A test.events request of one event: one frame.
    private static JsonObject Events(JsonObject ev) => new() { ["$cmd"] = "test.events", ["events"] = new JsonArray(ev) };
}
