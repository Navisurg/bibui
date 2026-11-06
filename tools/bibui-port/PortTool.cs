using System.Text;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bibui.Port;

/// <summary>
/// bibui-port's commands: each call finds the app's socket, does one thing, prints
/// the answer and exits 0, or prints the reason on stderr and exits 1.
/// </summary>
public static class PortTool
{
    public const string Usage = """
        bibui-port: drive a running bibui app through its test port.

        usage: bibui-port [--app <name> | --socket <path>] <command> [args]

          list                          the running apps' sockets
          tree [--id <id>] [--depth N]  the UI tree as an outline; --json for the answer's JSON
          find <text>                   nodes whose id or text holds the text
          get [path] [--depth N]        the app's model at path (panels/23#7, panels/7, focused),
                                        path@gen for an older generation; JSON, or with --outline
                                        a line per node: its handle and its values beside it, an
                                        editable one in [brackets], and under it (row:) what it
                                        shows on screen: [key] an editable field, text → command a
                                        button; each at <handle>/<key or command>
          log [since]                   the commands that made the model's generations after since
          text                          the app as text, drawn through its lenses (as its screen is,
                                        through theirs): its model, and what it has open; for an
                                        app that answers it
          schema                        what each kind of node in the model is, its keys, and its row
                                        (what a node of it presents and offers); --json for the list
          ops                           the app's commands: what each one does, and what it takes; --json for the list
          click <id> [--right|--middle] scroll into view if hidden, then click at the centre
          hover <id>                    the same, a move without a press
          drag <id> <x> <y>             press at the node, move to x y, release
          drag <x> <y> <x> <y>          the same from a point: to select text, from its first letter to its last
          scroll <id> <steps>           wheel steps over a node; a negative one shows what is below
          move <x> <y>                  move the pointer there, in logical pixels
          type <text>                   type into what has the keyboard
          key <key> [--ctrl --shift --alt --super] [--repeat N]
                                        press and release a key (Enter, Tab, Left, A, ...)
          cmd <name> [key=value...]     an app command, answering what it changed in the model
                                        (--json: as JSON): {"$cmd":name, key:value...}; numbers, true,
                                        false, null and JSON values ({..} [..] "..") as such; an
                                        argument the app takes as text or an expression as written,
                                        after the first '=' ('Value="Renamed"', 'Lens=board(it.case)').
                                        Checked against what the app says it takes first
          eval <expr>                   the app's eval command: an expression evaluated against the
                                        model, answered as text through its lenses (cmd eval expr=...)
          screenshot <path> [--id <id>] the frame on screen as a PNG the app writes; one node's rect with --id

        The model is a tree of nodes, each with a key, maybe a value, and children. A node is
        named by its handle, slot#id ("5#5"): its slot is where it lives, its id who it is. A
        slot is reused once its node is gone, an id never is, so a handle to a gone node
        resolves to nothing, never to another node. Ids increase in the order nodes are made:
        of two nodes, the one with the smaller id was made first. Handles last as long as the
        app runs. A key the schema declares and no command has written yet is shown as its
        zero, marked absent. cmd's argument names match without case (panel=5#5 is Panel).

        A path for get is keys from the root (panels/panel), ids (panels/5) or handles
        (panels/5#5, or a handle alone: 5#5), optionally ending in @gen (panels@3); a path of
        @gen alone is the whole model then.

        cmd answers what it changed in the model, generation by generation: + added (with its
        values), - removed, ~ changed (old → new), ↕ moved; "no change" when nothing did. A
        click, key, type, drag, scroll or hover that changed the model says so the same way.
        With --json, cmd prints the answer as it came: {"from":40,"gen":41,"diff":[...]}.
        A command that answers (eval) prints what it answered after what it changed, and no
        "no change" when it changed nothing.

        With no --app or --socket, the only app running.
        Coordinates are logical pixels from the window's top-left.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        //- options, anywhere; the rest is the command and its arguments
        string? app = null, id = null;
        int depth = int.MaxValue, repeat = 1;
        bool json = false, outline = false;
        string button = "left";
        var modifiers = new List<string>();
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string? Value() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--app" or "--socket": app = Value(); break;
                case "--id": id = Value(); break;
                case "--depth": depth = int.TryParse(Value(), out var d) && d >= 0 ? d : -1; break;
                case "--json": json = true; break;
                case "--outline": outline = true; break;
                case "--repeat": repeat = int.TryParse(Value(), out var r) && r > 0 ? r : -1; break;
                case "--right" or "--middle": button = args[i][2..]; break;
                case "--ctrl" or "--shift" or "--alt" or "--super": modifiers.Add(args[i][2..]); break;
                case "-h" or "--help": rest.Insert(0, "help"); break;
                default: rest.Add(args[i]); break;
            }
        }
        if (depth < 0 || repeat < 0) return Fail(error, "--depth and --repeat take a number above 0.");
        if (rest.Count == 0 || rest[0] == "help") { output.Write(Usage); return rest.Count == 0 ? 1 : 0; }
        var (command, argv) = (rest[0], rest.GetRange(1, rest.Count - 1));
        var needs = command switch
        {
            "find" or "type" => "a text", "click" or "hover" => "an id", "drag" => "an id, x and y", "scroll" => "an id and wheel steps",
            "move" => "x and y", "key" => "a key", "cmd" => "a command's name", "eval" => "an expression", "screenshot" => "a path", _ => null,
        };
        if (needs is not null && argv.Count == 0) return Fail(error, $"{command} takes {needs}.");

        try
        {
            if (command == "list")
            {
                foreach (var (name, pid, path) in Port.Running()) output.WriteLine($"{name}\t{pid}\t{path}");
                return 0;
            }

            using var port = new Port(Port.SocketFor(app));
            return Command(port, command, argv, id, depth, json, outline, button, modifiers, repeat, output, error);
        }
        catch (PortException e) { return Fail(error, e.Message); }
        catch (SocketException e) { return Fail(error, $"The app's socket: {e.Message}"); }
        catch (IOException e) { return Fail(error, $"The app's socket: {e.Message}"); }
        catch (JsonException e) { return Fail(error, $"Not JSON: {e.Message}"); }
    }

    // One command on a connected port: its answer printed, 0; or the reason, 1.
    private static int Command(Port port, string command, List<string> argv, string? id, int depth, bool json, bool outline,
        string button, List<string> modifiers, int repeat, TextWriter output, TextWriter error)
    {
        bool Number(string text, out float value) => float.TryParse(text, CultureInfo.InvariantCulture, out value);
        switch (command)
        {
            case "tree" when json:
                var answer = port.Ask("""{"$cmd":"test.tree"}""")!;
                var subtree = id is null ? answer : FindJson(answer, id) ?? throw new PortException($"No node '{id}' in the tree.");
                output.WriteLine(subtree.ToJsonString(Port.Relaxed));
                return 0;

            case "tree":
                var tree = port.Tree();
                Outline(output, id is null ? tree : Port.Find(tree, id) ?? throw new PortException($"No node '{id}' in the tree."), 0, depth);
                return 0;

            case "find":
                var found = Port.FindAll(port.Tree(), n =>
                    (n.Id?.Contains(argv[0], StringComparison.OrdinalIgnoreCase) ?? false)
                    || (n.Text?.Contains(argv[0], StringComparison.OrdinalIgnoreCase) ?? false));
                foreach (var n in found)
                {
                    // A text's nearest ancestor with an id is what a click names.
                    var owner = n.Parent;
                    while (n.Id is null && owner is not null && owner.Id is null) owner = owner.Parent;
                    output.WriteLine(n.Id is null && owner is not null ? $"{n}  in #{owner.Id}" : n.ToString());
                }
                return found.Count > 0 ? 0 : Fail(error, $"Nothing holds '{argv[0]}'.");

            case "get":
                var get = new JsonObject { ["$cmd"] = "model.get", ["path"] = argv.Count > 0 ? argv[0] : "" };
                if (depth != int.MaxValue) get["depth"] = depth;
                var got = port.Ask(get);
                if (!outline) { output.WriteLine(got?.ToJsonString(Port.Relaxed)); return 0; }
                output.WriteLine($"gen {got?["gen"]}");
                if (got?["ui"] is { } ui) output.WriteLine($"({ui})");
                if (got?["node"] is { } top) ModelOutline(output, top, 0);
                return 0;
            case "text":
                output.Write(port.Ask("""{"$cmd":"model.text"}""")?["text"]?.GetValue<string>());
                return 0;
            case "schema" when json: output.WriteLine(port.Ask("""{"$cmd":"model.schema"}""")?.ToJsonString(Port.Relaxed)); return 0;
            case "schema":
                foreach (var kind in port.Ask("""{"$cmd":"model.schema"}""") as JsonArray ?? [])
                {
                    output.WriteLine($"{kind!["kind"]}  {kind["label"]}");
                    output.WriteLine($"    {kind["description"]}");
                    foreach (var child in kind["children"] as JsonArray ?? [])
                    {
                        var value = child!["ref"] is { } to ? $"{child["value"]} → {to}" : child["value"]!.ToString();
                        output.WriteLine($"    {child["key"]}: {value}{(child["many"]?.GetValue<bool>() == true ? ", many" : "")}  {child["description"]}");
                    }
                    // Its row: what a node of it presents and offers, as declared.
                    if (kind["row"] is JsonArray { Count: > 0 } row)
                        output.WriteLine($"    row: {string.Join("   ", row.Select(e => e!["command"] is { } c ? $"{e["text"]} → {c}" : $"[{e["field"]}]"))}");
                }
                return 0;
            case "log":
                var log = new JsonObject { ["$cmd"] = "model.log" };
                if (argv.Count > 0 && long.TryParse(argv[0], out var since)) log["since"] = since;
                output.WriteLine(port.Ask(log)?.ToJsonString(Port.Relaxed));
                return 0;
            case "ops" when json: output.WriteLine(port.Ask("""{"$cmd":"test.ops"}""")?.ToJsonString(Port.Relaxed)); return 0;
            case "ops":
                foreach (var op in Ops(port))
                {
                    output.WriteLine($"{op["wire"]}  {op["label"]}{(op["readOnly"]?.GetValue<bool>() == true ? "  [read-only]" : "")}");
                    if (op["description"]?.GetValue<string>() is { Length: > 0 } description) output.WriteLine($"    {description}");
                    var required = Required(op);
                    foreach (var (name, arg) in Arguments(op))
                        output.WriteLine($"    {name}: {(Field(arg, "nodeKind") is { } kind ? $"handle → {kind}" : Field(arg, "type") ?? "any")}{(required.Contains(name) ? "" : ", optional")}{(Field(arg, "description") is { } d ? $"  {d}" : "")}");
                }
                return 0;

            case "click":
                var (x, y) = port.Click(argv[0], button).Centre;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"clicked #{argv[0]} at {x:0.#},{y:0.#}"));
                PrintMade(output, port.Made, always: false);
                return 0;

            case "hover":
                port.Hover(argv[0]);
                PrintMade(output, port.Made, always: false);
                return 0;

            case "drag" when argv.Count == 4 && Number(argv[0], out var fx) && Number(argv[1], out var fy):
                if (!Number(argv[2], out var tx) || !Number(argv[3], out var ty)) return Fail(error, "drag takes x y to drag from and x y to drag to.");
                port.Drag(fx, fy, tx, ty);
                PrintMade(output, port.Made, always: false);
                return 0;

            case "drag":
                if (argv.Count < 3 || !Number(argv[1], out var dx) || !Number(argv[2], out var dy))
                    return Fail(error, "drag takes an id, and x and y to drag to.");
                port.Drag(argv[0], dx, dy);
                PrintMade(output, port.Made, always: false);
                return 0;

            case "scroll":
                if (argv.Count < 2 || !Number(argv[1], out var steps)) return Fail(error, "scroll takes an id and wheel steps.");
                port.Scroll(argv[0], steps);
                PrintMade(output, port.Made, always: false);
                return 0;

            case "move":
                if (argv.Count < 2 || !Number(argv[0], out var mx) || !Number(argv[1], out var my))
                    return Fail(error, "move takes x and y, in logical pixels.");
                port.Ask(new JsonObject { ["$cmd"] = "test.events", ["events"] = new JsonArray(new JsonObject { ["kind"] = "mouse_move", ["x"] = mx, ["y"] = my }) });
                return 0;

            case "type":
                port.Type(string.Join(' ', argv));
                PrintMade(output, port.Made, always: false);
                return 0;

            case "key":
                port.Key(argv[0], modifiers, repeat);
                PrintMade(output, port.Made, always: false);
                return 0;

            case "eval" or "cmd":
                // eval is the app's eval command, its expression the rest as typed: a string, not
                // read as a number or JSON as key=value is.
                var cmd = command == "eval"
                    ? new JsonObject { ["$cmd"] = "eval", ["expr"] = JsonValue.Create(string.Join(' ', argv)) }
                    : CmdLine(argv[0], argv.GetRange(1, argv.Count - 1));
                string wire = cmd["$cmd"]!.GetValue<string>();
                // Checked against what the app says the command takes, before it is sent: a
                // reason in the command's own words, not the app's deserializer's. An app that
                // lists no commands is sent it as it is, and answers for itself.
                var ops = Ops(port);
                if (ops.Count > 0)
                {
                    if (ops.FirstOrDefault(o => o["wire"]?.GetValue<string>() == wire) is not { } op)
                        return Fail(error, $"No command '{wire}': bibui-port ops lists the app's commands.");
                    if (command == "cmd") cmd = CmdLine(wire, argv.GetRange(1, argv.Count - 1), verbatim: key => IsText(op, key));   // read again, by what each argument is
                    if (Problem(op, cmd) is { } problem) return Fail(error, problem);
                    cmd = Named(op, cmd);
                }
                // An app that reports its model answers what the command changed in it.
                var made = port.Ask(cmd);
                if (json) { if (made is not null) output.WriteLine(made.ToJsonString(Port.Relaxed)); }
                else PrintMade(output, port.Made, always: true);
                return 0;

            case "screenshot":
                var file = Path.GetFullPath(argv[0]);     // the app writes it, from its own folder
                var shot = new JsonObject { ["$cmd"] = "test.screenshot", ["path"] = file };
                if (id is not null) shot["id"] = id;
                port.Ask(shot);
                output.WriteLine(file);
                return 0;

            default:
                return Fail(error, $"No command '{command}': see bibui-port help.");
        }
    }

    // The app's commands, as test.ops lists them: one entry each.
    private static List<JsonNode> Ops(Port port) =>
        port.Ask("""{"$cmd":"test.ops"}""") is JsonArray list ? [.. list.OfType<JsonNode>()] : [];

    private static IEnumerable<(string Name, JsonNode? Schema)> Arguments(JsonNode op) =>
        op["input"]?["properties"] is JsonObject properties ? properties.Select(p => (p.Key, p.Value)) : [];

    // Whether the argument key (without case) is text or an expression, as its entry says: sent as written.
    private static bool IsText(JsonNode op, string key)
    {
        foreach (var (name, schema) in Arguments(op))
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                return schema?["type"] is JsonValue t ? t.GetValue<string>() == "string"
                    : schema?["type"] is JsonArray ts && ts.Any(x => x?.GetValue<string>() == "string");
        return false;
    }

    private static HashSet<string> Required(JsonNode op) =>
        op["input"]?["required"] is JsonArray required ? [.. required.Select(r => r!.GetValue<string>())] : [];

    // The command with each argument under its name as the entry has it (value=3 is sent as
    // Value): matched without case here, sent as the app reads it, however strictly it does.
    private static JsonObject Named(JsonNode op, JsonObject cmd)
    {
        var names = Arguments(op).Select(a => a.Name).ToList();
        var named = new JsonObject();
        foreach (var (key, value) in cmd)
            named[names.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase)) ?? key] = value?.DeepClone();
        return named;
    }

    // Why the command should not be sent, from its entry, or null: an argument missing or
    // unknown or of the wrong type. Names are matched
    // without case; Named sends them as the entry has them.
    private static string? Problem(JsonNode op, JsonObject cmd)
    {
        string wire = op["wire"]!.GetValue<string>();
        var arguments = Arguments(op).ToDictionary(a => a.Name, a => a.Schema, StringComparer.OrdinalIgnoreCase);
        var given = cmd.Where(p => p.Key != "$cmd").ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var name in Required(op))
            if (!given.ContainsKey(name))
                return $"{wire} needs {name} ({Field(arguments.GetValueOrDefault(name), "type") ?? "any"}){(Field(arguments.GetValueOrDefault(name), "description") is { } d ? $": {d}" : "")}";
        foreach (var (name, value) in given)
        {
            if (!arguments.TryGetValue(name, out var schema))
            {
                if (op["input"]?["additionalProperties"]?.GetValue<bool>() == false)
                    return $"{wire} takes {string.Join(", ", arguments.Keys)}; not {name}.";
                continue;
            }
            var type = Field(schema, "type");
            bool fits = type switch
            {
                "integer" => value is JsonValue v && v.TryGetValue<long>(out _),
                "number" => value is JsonValue n && n.TryGetValue<double>(out _),
                "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
                "string" => value is JsonValue t && t.TryGetValue<string>(out _),
                _ => true,
            };
            if (!fits) return $"{wire}'s {name} is {(type is "integer" ? "an integer" : $"a {type}")}, not {value?.ToJsonString() ?? "null"}.";
        }
        return null;
    }

    // What the requests changed in the model, as lines: "gen 40 → 41", then "+ panel 6#6 in panels
    // 3#3   submits 1" (added, with its values), "- panel 6#6 from panels 3#3" (removed), "~ focused
    // 4#4 in root 1#1: 6#6 → 5#5" (changed), "↕ panel 2#2 in panels 3#3" (moved). An unchanged model
    // says "no change" when always, and nothing otherwise (a hover that did nothing says nothing).
    private static void PrintMade(TextWriter output, List<JsonObject> made, bool always)
    {
        long from = -1, gen = -1;
        var lines = new List<string>();
        var answered = new System.Text.StringBuilder();   // what the commands answered (an eval's value), after what they changed
        foreach (var answer in made)
        {
            if (from < 0) from = answer["from"]?.GetValue<long>() ?? -1;
            gen = answer["gen"]?.GetValue<long>() ?? gen;
            if (answer["error"] is { } why) lines.Add($"  ({why})");
            if (answer["output"] is JsonValue said && said.TryGetValue<string>(out var text)) answered.Append(text);
            foreach (var e in answer["diff"] as JsonArray ?? [])
            {
                string node = $"{e!["key"]} {e["handle"]}", parent = $"{e["parentKey"]} {e["parent"]}";
                lines.Add(e["op"]?.ToString() switch
                {
                    "add" => $"+ {node} in {parent}{Values(e)}",
                    "remove" => $"- {node} from {parent}",
                    "change" => $"~ {e["key"]} in {parent}: {Shown(e["old"])} → {Shown(e["new"])}",   // a value: its own handle says nothing
                    _ => $"↕ {node} in {parent}",
                });
            }
        }
        made.Clear();
        if (from < 0) return;
        if (lines.Count > 0)
        {
            output.WriteLine($"gen {from} → {gen}");
            foreach (var line in lines) output.WriteLine(line);
        }
        else if (always && answered.Length == 0) output.WriteLine($"gen {gen}: no change");
        output.Write(answered.ToString());

        static string Shown(JsonNode? v) => v is null ? "(none)" : v is JsonValue s && s.TryGetValue<string>(out var text) && !text.Contains('#') ? $"\"{text}\"" : v.ToJsonString().Trim('"');

        // An added node's own value and its children's, beside it: "   seed 42".
        static string Values(JsonNode e)
        {
            var parts = new List<string>();
            if (e["value"] is { } v) parts.Add(Shown(v));
            foreach (var c in e["children"] as JsonArray ?? [])
                parts.Add(c!["value"] is { } cv ? $"{c["key"]} {Shown(cv)}" : $"{c["key"]} {c["handle"]}");
            return parts.Count == 0 ? "" : "   " + string.Join("   ", parts);
        }
    }

    // A model.get answer as an outline: a line per node that holds other nodes, with its handle
    // (what commands take) and its plain values beside it (panel 5#5   submits 1). Handles read
    // "→ 5#5", keys nothing has written "(absent)", a handle to a gone node "(gone)", children
    // past --depth "… N more".
    private static void ModelOutline(TextWriter output, JsonNode node, int level, string place = "")
    {
        var line = new StringBuilder(new string(' ', 2 * level));
        line.Append(Plain(node) ?? $"{node["key"]} {node["handle"]}{place}");
        var nested = new List<JsonNode>();
        foreach (var child in node["children"] as JsonArray ?? [])
        {
            if (Plain(child!) is { } plain) line.Append("   ").Append(plain);
            else nested.Add(child!);
        }
        if (node["children"] is JsonValue more) line.Append($"   … {more} more");
        output.WriteLine(line);
        // Its row: what it shows, left to right: [key] a field typed into, key a field shown, text →
        // command a button; each at <handle>/<key or command>.
        if (node["row"] is JsonArray { Count: > 0 } row)
            output.WriteLine($"{new string(' ', 2 * level)}  row: {string.Join("   ", row.Select(e => e!["command"] is { } command ? $"{e["text"]} → {command}"
                : e["editable"]?.GetValue<bool>() == true ? $"[{e["field"]}]" : $"{e["field"]}"))}   (at {node["handle"]}/<key or command>)");
        // A node among others of its key (a list) says its place: "(2 of 3)".
        foreach (var child in nested)
        {
            int of = nested.Count(c => c["key"]?.ToString() == child["key"]?.ToString());
            int at = nested.Where(c => c["key"]?.ToString() == child["key"]?.ToString()).ToList().IndexOf(child) + 1;
            ModelOutline(output, child, level + 1, of > 1 ? $" ({at} of {of})" : "");
        }

        // A node with a value and no children, or an absent one, as "key value"; null otherwise.
        static string? Plain(JsonNode n)
        {
            if (n["children"] is not null) return null;
            bool absent = n["absent"]?.GetValue<bool>() == true;
            var value = n["value"] switch
            {
                null => null,
                var v when n["kind"]?.ToString() == "handle" => $"→ {v}{(n["gone"]?.GetValue<bool>() == true ? " (gone)" : "")}",
                JsonValue v when v.TryGetValue<string>(out var text) => $"\"{text}\"",
                var v => v.ToJsonString(),
            };
            if (value is null && !absent) return null;
            var key = n["editable"]?.GetValue<bool>() == true ? $"[{n["key"]}]" : $"{n["key"]}";   // [key]: editable
            return $"{key}{(value is null ? "" : " " + value)}{(absent ? " (absent)" : "")}";
        }
    }

    // A field of an argument's schema. A schema need not be an object: an argument whose type
    // writes its own JSON (a handle, "23#7") has the schema true, which takes anything.
    private static string? Field(JsonNode? schema, string name) => schema is JsonObject o ? o[name]?.ToString() : null;

    private static int Fail(TextWriter error, string reason)
    {
        error.WriteLine(reason);
        return 1;
    }

    private static void Outline(TextWriter output, PortNode node, int level, int depth)
    {
        output.Write(new string(' ', 2 * level));
        output.WriteLine(node);
        if (level < depth)
            foreach (var child in node.Children) Outline(output, child, level + 1, depth);
    }

    private static JsonNode? FindJson(JsonNode node, string id)
    {
        if (node["id"]?.GetValue<string>() == id) return node;
        if (node["children"] is JsonArray children)
            foreach (var child in children)
                if (FindJson(child!, id) is { } found) return found;
        return null;
    }

    /// <summary>
    /// {"$cmd":name, key:value...} from key=value arguments: a number, true, false,
    /// null, or a JSON object, array or string as such; anything else a string. An argument
    /// <paramref name="verbatim"/> says is text (or an expression, whose JSON is its text) is
    /// the string after the first '=' as written, whatever it looks like.
    /// </summary>
    public static JsonObject CmdLine(string name, IReadOnlyList<string> pairs, Func<string, bool>? verbatim = null)
    {
        var cmd = new JsonObject { ["$cmd"] = name };
        foreach (var pair in pairs)
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) throw new PortException($"'{pair}' is not key=value.");
            var (key, value) = (pair[..eq], pair[(eq + 1)..]);
            bool asJson = verbatim?.Invoke(key) != true
                && (value is "true" or "false" or "null"
                    || (value.Length > 0 && (char.IsAsciiDigit(value[0]) || value[0] == '-') && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    || value.StartsWith('{') || value.StartsWith('[') || value.StartsWith('"'));
            cmd[key] = asJson ? JsonNode.Parse(value) : JsonValue.Create(value);
        }
        return cmd;
    }
}
