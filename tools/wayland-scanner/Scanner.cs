using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Bibui.WaylandScanner;

/// <summary>A protocol XML to generate from, and the file its bindings go to.</summary>
/// <param name="Path">The XML.</param>
/// <param name="OutputName">The generated file's name, e.g. <c>xdg-shell.g.cs</c>.</param>
/// <param name="TablesFromLibwayland">
/// True for the core protocol: libwayland-client exports its interface tables,
/// so only the typed wrappers are generated and the tables are looked up.
/// </param>
public sealed record ProtocolSource(string Path, string OutputName, bool TablesFromLibwayland = false);

/// <summary>
/// The C# counterpart of wayland-scanner: protocol XML in, typed bindings out.
///
/// <para>For each protocol it emits one file of typed proxies — a struct per
/// interface with a method per request (since-version checked, the destructor
/// flag set where the XML says so, new_id requests returning the new proxy),
/// an enum per <c>&lt;enum&gt;</c>, and a listener struct of unmanaged
/// function pointers whose builder fills every slot left unset with a no-op,
/// because libwayland calls an event's slot unconditionally. One more file
/// holds the <c>wl_interface</c> tables for every interface not exported by
/// libwayland, allocated first and filled second to resolve the cycles.</para>
///
/// <para>Pure: XML in, file contents out, deterministic. Writing them is the
/// caller's business, so a test can regenerate in memory and diff.</para>
/// </summary>
public static class Scanner
{
    public const string Namespace = "Bibui.Platform.Wayland.Protocol";
    public const string TablesFile = "WaylandInterfaces.g.cs";
    // Types are written unqualified. Every top-level type in the namespace has a
    // protocol prefix (WlSurface, XdgToplevel) or is support code (WlArgument),
    // and members are named from XML names, which carry no prefix, so no member
    // can shadow a type. Enums are always qualified by their interface.

    /// <summary>The protocols bibui's Wayland host speaks, from the system's installed XML.</summary>
    public static readonly IReadOnlyList<ProtocolSource> DefaultSources =
    [
        new("/usr/share/wayland/wayland.xml", "wayland.g.cs", TablesFromLibwayland: true),
        new("/usr/share/wayland-protocols/stable/xdg-shell/xdg-shell.xml", "xdg-shell.g.cs"),
        new("/usr/share/wayland-protocols/unstable/xdg-decoration/xdg-decoration-unstable-v1.xml", "xdg-decoration-unstable-v1.g.cs"),
        new("/usr/share/wayland-protocols/staging/fractional-scale/fractional-scale-v1.xml", "fractional-scale-v1.g.cs"),
        new("/usr/share/wayland-protocols/stable/viewporter/viewporter.xml", "viewporter.g.cs"),
        new("/usr/share/wayland-protocols/staging/cursor-shape/cursor-shape-v1.xml", "cursor-shape-v1.g.cs"),
        new("/usr/share/wayland-protocols/stable/presentation-time/presentation-time.xml", "presentation-time.g.cs"),
    ];

    // ── Model ────────────────────────────────────────────────────────────────

    public sealed record Arg(string Name, string Type, string? Interface, bool AllowNull, string? Enum, string? Summary);
    public sealed record Message(string Name, int Opcode, int Since, bool IsDestructor, IReadOnlyList<Arg> Args, string? Summary);
    public sealed record EnumEntry(string Name, uint Value, string? Summary);
    public sealed record EnumDef(string Name, bool Bitfield, IReadOnlyList<EnumEntry> Entries, string? Summary);
    public sealed record Interface(
        string Name, int Version, IReadOnlyList<Message> Requests, IReadOnlyList<Message> Events,
        IReadOnlyList<EnumDef> Enums, string? Summary, bool TablesFromLibwayland);
    public sealed record Protocol(string Name, ProtocolSource Source, IReadOnlyList<Interface> Interfaces);

    public static Protocol Parse(ProtocolSource source) => Parse(XDocument.Load(source.Path), source);

    public static Protocol Parse(XDocument doc, ProtocolSource source)
    {
        var root = doc.Root ?? throw new InvalidDataException($"{source.Path}: empty document");
        var interfaces = root.Elements("interface").Select(i => new Interface(
            Attr(i, "name"),
            int.Parse(Attr(i, "version"), CultureInfo.InvariantCulture),
            Messages(i, "request"),
            Messages(i, "event"),
            i.Elements("enum").Select(e => new EnumDef(
                Attr(e, "name"),
                e.Attribute("bitfield")?.Value == "true",
                e.Elements("entry").Select(x => new EnumEntry(Attr(x, "name"), ParseUInt(Attr(x, "value")), x.Attribute("summary")?.Value)).ToList(),
                Summary(e))).ToList(),
            Summary(i),
            source.TablesFromLibwayland)).ToList();
        return new Protocol(Attr(root, "name"), source, interfaces);

        static List<Message> Messages(XElement i, string kind) =>
            i.Elements(kind).Select((m, k) => new Message(
                Attr(m, "name"), k,
                int.Parse(m.Attribute("since")?.Value ?? "1", CultureInfo.InvariantCulture),
                m.Attribute("type")?.Value == "destructor",
                m.Elements("arg").Select(a => new Arg(
                    Attr(a, "name"), Attr(a, "type"), a.Attribute("interface")?.Value,
                    a.Attribute("allow-null")?.Value == "true", a.Attribute("enum")?.Value,
                    a.Attribute("summary")?.Value)).ToList(),
                Summary(m))).ToList();

        static string? Summary(XElement e) => e.Element("description")?.Attribute("summary")?.Value;
    }

    private static string Attr(XElement e, string name) =>
        e.Attribute(name)?.Value ?? throw new InvalidDataException($"<{e.Name}> has no {name}");

    private static uint ParseUInt(string v) => v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? uint.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : uint.Parse(v, CultureInfo.InvariantCulture);

    // ── Wire format: what wayland-scanner puts in a *-protocol.c ──────────────

    private static readonly Dictionary<string, char> Codes = new()
    {
        ["int"] = 'i', ["uint"] = 'u', ["fixed"] = 'f', ["string"] = 's', ["object"] = 'o',
        ["new_id"] = 'n', ["array"] = 'a', ["fd"] = 'h',
    };

    /// <summary>
    /// The signature string and its types array: since-version prefix, '?'
    /// before a nullable argument, and an untyped new_id expanded to "sun"
    /// (interface name, version, id) with three null types.
    /// </summary>
    public static (string Signature, List<string?> Types) Wire(Message m)
    {
        var sig = new StringBuilder(m.Since > 1 ? m.Since.ToString(CultureInfo.InvariantCulture) : "");
        var types = new List<string?>();
        foreach (var a in m.Args)
        {
            if (a.AllowNull) sig.Append('?');
            if (a.Type == "new_id" && a.Interface is null)
            {
                sig.Append("sun");
                types.AddRange([null, null, null]);
                continue;
            }
            sig.Append(Codes.TryGetValue(a.Type, out var c) ? c : throw new InvalidDataException($"unknown arg type {a.Type}"));
            types.Add(a.Type is "object" or "new_id" ? a.Interface : null);
        }
        return (sig.ToString(), types);
    }

    /// <summary>
    /// The tables as text, one line per message — the format
    /// tools/wayland-scanner/check_tables.py emits from the XML and
    /// <c>WlTables.Dump</c> prints from a live table.
    /// </summary>
    public static List<string> ExpectedDump(Interface i)
    {
        var lines = new List<string> { $"interface {i.Name} v{i.Version} methods={i.Requests.Count} events={i.Events.Count}" };
        foreach (var (prefix, list) in new[] { ("r", i.Requests), ("e", i.Events) })
            foreach (var m in list)
            {
                var (sig, types) = Wire(m);
                lines.Add($"  {prefix} {m.Name} '{sig}' [{string.Join(",", types.Select(t => t ?? "-"))}]");
            }
        return lines;
    }

    // ── Generation ───────────────────────────────────────────────────────────

    /// <summary>Every output file, by name, for <paramref name="sources"/>.</summary>
    public static SortedDictionary<string, string> Generate(IReadOnlyList<ProtocolSource> sources)
    {
        var protocols = sources.Select(Parse).ToList();
        return Generate(protocols);
    }

    public static SortedDictionary<string, string> Generate(IReadOnlyList<Protocol> protocols)
    {
        var all = protocols.SelectMany(p => p.Interfaces).ToDictionary(i => i.Name);
        var ctx = new Context(all);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in protocols)
            files[p.Source.OutputName] = ProtocolFile(p, ctx);
        files[TablesFile] = Tables(protocols, ctx);
        return files;
    }

    private sealed class Context(Dictionary<string, Interface> all)
    {
        public readonly Dictionary<string, Interface> All = all;

        /// <summary>The typed proxy for an interface, or null when it is only referenced (a stub).</summary>
        public string? Proxy(string? iface) => iface is not null && All.ContainsKey(iface) ? Pascal(iface) : null;

        /// <summary>The C# type of an enum reference ("format" or "wl_output.transform"), or null if it cannot be resolved.</summary>
        public string? EnumType(Interface owner, string? reference)
        {
            if (reference is null) return null;
            var (ifaceName, enumName) = reference.Contains('.')
                ? (reference[..reference.IndexOf('.')], reference[(reference.IndexOf('.') + 1)..])
                : (owner.Name, reference);
            if (!All.TryGetValue(ifaceName, out var target)) return null;
            var e = target.Enums.FirstOrDefault(x => x.Name == enumName);
            return e is null ? null : Pascal(target.Name) + "." + EnumName(target, e);
        }
    }

    private static string Header(string source) =>
        "// <auto-generated>\n" +
        $"//   Generated by tools/wayland-scanner from {source}.\n" +
        "//   Do not edit. Regenerate with: dotnet run --project tools/wayland-scanner -- generate\n" +
        "// </auto-generated>\n\n" +
        "#nullable enable\n" +
        "#pragma warning disable CS1591 // public members of generated bindings carry the XML's summaries only where it has one\n\n" +
        "using System.Runtime.CompilerServices;\n" +
        "using System.Runtime.InteropServices;\n\n" +
        $"namespace {Namespace};\n";

    private static string ProtocolFile(Protocol p, Context ctx)
    {
        var w = new Writer();
        w.Raw(Header(p.Source.Path));
        foreach (var i in p.Interfaces)
        {
            w.Line();
            Proxy(w, i, ctx);
        }
        return w.ToString();
    }

    private static void Proxy(Writer w, Interface i, Context ctx)
    {
        string type = Pascal(i.Name);
        CheckNames(i);

        Doc(w, i.Summary);
        w.Line($"internal readonly unsafe partial record struct {type}(nint Handle) : IWlProxy<{type}>");
        w.Open();
        w.Line($"public const string InterfaceName = \"{i.Name}\";");
        w.Line($"public const uint InterfaceVersion = {i.Version};");
        w.Line();
        w.Line($"public static WlInterface* Interface => WaylandInterfaces.{i.Name};");
        w.Line($"static uint IWlProxy<{type}>.MaxVersion => InterfaceVersion;");
        w.Line($"public static {type} FromHandle(nint handle) => new(handle);");
        w.Line();
        w.Line("public bool IsNull => Handle == 0;");
        w.Line();
        w.Line("// Not the record's default, which would read Version — a native call — on a possibly dead proxy.");
        w.Line("public override string ToString() => $\"{InterfaceName}@{Handle:x}\";");
        w.Line();
        w.Line("/// <summary>The version this object was created at.</summary>");
        w.Line($"public uint Version => WaylandNative.wl_proxy_get_version(Handle);");
        w.Line();
        w.Line("/// <summary>Frees the client-side proxy without telling the compositor (wl_proxy_destroy).</summary>");
        w.Line($"public void DestroyProxy() => WaylandNative.wl_proxy_destroy(Handle);");

        if (i.Events.Count > 0)
        {
            w.Line();
            w.Line("/// <summary>Sets the listener; <paramref name=\"listener\"/> must outlive the object (see <see cref=\"Listener.Create\"/>).</summary>");
            w.Line($"public int AddListener(Listener* listener, void* data = null) => WaylandNative.wl_proxy_add_listener(Handle, listener, data);");
        }

        foreach (var m in i.Requests) Request(w, i, m, ctx);
        foreach (var e in i.Enums) Enum(w, i, e);
        if (i.Events.Count > 0) Listener(w, i, ctx);

        w.Close();
    }

    private static readonly HashSet<string> Reserved =
    [
        "Handle", "IsNull", "Version", "DestroyProxy", "AddListener", "Interface", "InterfaceName",
        "InterfaceVersion", "FromHandle", "Listener", "Defaults", "Equals", "GetHashCode", "ToString", "Deconstruct",
    ];

    private static void CheckNames(Interface i)
    {
        foreach (var m in i.Requests)
            if (Reserved.Contains(Pascal(m.Name)))
                throw new InvalidDataException($"{i.Name}.{m.Name}: request name collides with a generated member");
        foreach (var e in i.Events)
            if (Pascal(e.Name) is "Create" or "Free")
                throw new InvalidDataException($"{i.Name}.{e.Name}: event name collides with a listener member");
    }

    private static string EnumName(Interface owner, EnumDef e)
    {
        var n = Pascal(e.Name);
        return owner.Requests.Any(r => Pascal(r.Name) == n) || Reserved.Contains(n) ? n + "Enum" : n;
    }

    private static void Request(Writer w, Interface i, Message m, Context ctx)
    {
        string name = Pascal(m.Name);
        var newId = m.Args.FirstOrDefault(a => a.Type == "new_id");
        bool untypedNewId = newId is not null && newId.Interface is null;
        string? returnProxy = newId?.Interface is { } ni ? ctx.Proxy(ni) ?? "nint" : null;

        var parameters = new List<string>();
        var fills = new List<string>();
        var strings = new List<string>();
        int slot = 0;
        foreach (var a in m.Args)
        {
            string p = Ident(a.Name);
            switch (a.Type)
            {
                case "new_id" when a.Interface is null:
                    parameters.Add("uint version");
                    fills.Add($"a[{slot++}].S = (nint)T.Interface->Name;");
                    fills.Add($"a[{slot++}].U = version;");
                    fills.Add($"a[{slot++}].O = 0;");
                    break;
                case "new_id":
                    fills.Add($"a[{slot++}].O = 0;");
                    break;
                case "int":
                    parameters.Add($"int {p}");
                    fills.Add($"a[{slot++}].I = {p};");
                    break;
                case "uint":
                    var et = ctx.EnumType(i, a.Enum);
                    parameters.Add($"{et ?? "uint"} {p}");
                    fills.Add(et is null ? $"a[{slot++}].U = {p};" : $"a[{slot++}].U = (uint){p};");
                    break;
                case "fixed":
                    parameters.Add($"WlFixed {p}");
                    fills.Add($"a[{slot++}].F = {p}.Raw;");
                    break;
                case "string":
                    parameters.Add($"string{(a.AllowNull ? "?" : "")} {p}");
                    strings.Add(p);
                    fills.Add($"a[{slot++}].S = (nint){p}Utf8;");
                    break;
                case "object":
                    var proxy = ctx.Proxy(a.Interface);
                    parameters.Add($"{proxy ?? "nint"} {p}");
                    fills.Add($"a[{slot++}].O = {p}{(proxy is null ? "" : ".Handle")};");
                    break;
                case "array":
                    parameters.Add($"WlArray* {p}");
                    fills.Add($"a[{slot++}].A = (nint){p};");
                    break;
                case "fd":
                    parameters.Add($"int {p}");
                    fills.Add($"a[{slot++}].H = {p};");
                    break;
                default:
                    throw new InvalidDataException($"{i.Name}.{m.Name}: unknown arg type {a.Type}");
            }
        }

        w.Line();
        w.Line($"public const uint {name}Opcode = {m.Opcode};");
        if (m.Since > 1) w.Line($"public const uint {name}Since = {m.Since};");
        Doc(w, m.Summary, m.Since, m.IsDestructor ? "Destroys the object." : null);

        string ret = untypedNewId ? "T" : returnProxy ?? "void";
        string generic = untypedNewId ? $"<T>" : "";
        w.Line($"public {ret} {name}{generic}({string.Join(", ", parameters)})" +
               (untypedNewId ? $" where T : struct, IWlProxy<T>" : ""));
        w.Open();
        w.Line($"uint v = WaylandNative.wl_proxy_get_version(Handle);");
        if (m.Since > 1)
            w.Line($"if (v < {m.Since}) throw WaylandNative.TooOld(\"{i.Name}.{m.Name}\", {m.Since}, v);");
        w.Line($"WlArgument* a = stackalloc WlArgument[{Math.Max(slot, 1)}];");
        foreach (var s in strings) w.Line($"byte* {s}Utf8 = WlUtf8.Alloc({s});");
        if (strings.Count > 0) { w.Line("try"); w.Open(); }
        foreach (var f in fills) w.Line(f);

        string iface = untypedNewId ? "T.Interface" : newId?.Interface is { } t ? $"WaylandInterfaces.{t}" : "null";
        string version = untypedNewId ? "version" : "v";
        string flags = m.IsDestructor ? $"WaylandNative.WL_MARSHAL_FLAG_DESTROY" : "0";
        string call = $"WaylandNative.wl_proxy_marshal_array_flags(Handle, {m.Opcode}, {iface}, {version}, {flags}, a)";
        if (untypedNewId) w.Line($"return T.FromHandle({call});");
        else if (returnProxy == "nint") w.Line($"return {call};");
        else if (returnProxy is not null) w.Line($"return new {returnProxy}({call});");
        else w.Line($"{call};");

        if (strings.Count > 0)
        {
            w.Close();
            w.Line("finally");
            w.Open();
            foreach (var s in strings) w.Line($"WlUtf8.Free({s}Utf8);");
            w.Close();
        }
        w.Close();
    }

    private static void Enum(Writer w, Interface i, EnumDef e)
    {
        w.Line();
        Doc(w, e.Summary);
        if (e.Bitfield) w.Line("[global::System.Flags]");
        w.Line($"public enum {EnumName(i, e)} : uint");
        w.Open();
        foreach (var x in e.Entries)
        {
            Doc(w, x.Summary);
            w.Line($"{EnumEntryName(x.Name)} = {x.Value},");
        }
        w.Close();
    }

    private static string EventParam(Interface i, Arg a, Context ctx) => a.Type switch
    {
        "int" => "int",
        "uint" => ctx.EnumType(i, a.Enum) ?? "uint",
        "fixed" => "WlFixed",
        "string" => "byte*",
        "object" or "new_id" => ctx.Proxy(a.Interface) ?? "nint",
        "array" => "WlArray*",
        "fd" => "int",
        _ => throw new InvalidDataException($"{i.Name}: unknown arg type {a.Type}"),
    };

    private static void Listener(Writer w, Interface i, Context ctx)
    {
        string self = Pascal(i.Name);
        var sigs = i.Events.Select(e =>
            (Event: e, Name: Pascal(e.Name),
             Params: new[] { "void*", self }.Concat(e.Args.Select(a => EventParam(i, a, ctx))).ToList())).ToList();

        w.Line();
        w.Line($"/// <summary>");
        w.Line($"/// struct {i.Name}_listener: one unmanaged function pointer per event, in opcode order.");
        w.Line($"/// Each handler is <c>[UnmanagedCallersOnly]</c> and gets the listener's data pointer and the");
        w.Line($"/// proxy first. A destructor event's handler must destroy the proxy (<see cref=\"DestroyProxy\"/>).");
        w.Line($"/// </summary>");
        w.Line("[StructLayout(LayoutKind.Sequential)]");
        w.Line("public struct Listener");
        w.Open();
        foreach (var (e, name, ps) in sigs)
        {
            Doc(w, e.Summary, e.Since, e.IsDestructor ? "Destructor event: the handler destroys the proxy." : null);
            w.Line($"public delegate* unmanaged<{string.Join(", ", ps)}, void> {name};");
            if (e.Since > 1) w.Line($"public const uint {name}Since = {e.Since};");
        }
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// Copies <paramref name=\"handlers\"/> into native memory, filling every slot left null with a");
        w.Line("/// handler that does nothing (or, for a destructor event, only destroys the proxy): libwayland");
        w.Line("/// calls an event's slot without checking it. The copy lives until <see cref=\"Free\"/>.");
        w.Line("/// </summary>");
        w.Line("public static Listener* Create(in Listener handlers)");
        w.Open();
        w.Line("var l = (Listener*)NativeMemory.Alloc((nuint)sizeof(Listener));");
        w.Line("*l = handlers;");
        foreach (var (_, name, _) in sigs)
            w.Line($"if (l->{name} == null) l->{name} = &Defaults.{name};");
        w.Line("return l;");
        w.Close();
        w.Line();
        w.Line("public static void Free(Listener* listener) => NativeMemory.Free(listener);");
        w.Close();

        w.Line();
        w.Line("/// <summary>The handlers <see cref=\"Listener.Create\"/> puts in a slot left empty.</summary>");
        w.Line("public static class Defaults");
        w.Open();
        foreach (var (e, name, ps) in sigs)
        {
            var named = ps.Select((t, k) => k switch { 0 => $"{t} data", 1 => $"{t} self", _ => $"{t} {Ident(e.Args[k - 2].Name)}" });
            w.Line("[UnmanagedCallersOnly]");
            w.Line($"public static void {name}({string.Join(", ", named)})" + (e.IsDestructor ? " => self.DestroyProxy();" : " { }"));
        }
        w.Close();
    }

    // ── Tables ───────────────────────────────────────────────────────────────

    private static string Tables(IReadOnlyList<Protocol> protocols, Context ctx)
    {
        var interfaces = protocols.SelectMany(p => p.Interfaces).ToList();
        var referenced = interfaces
            .SelectMany(i => i.Requests.Concat(i.Events))
            .SelectMany(m => m.Args)
            .Select(a => a.Interface)
            .OfType<string>()
            .Where(n => !ctx.All.ContainsKey(n))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var w = new Writer();
        w.Raw(Header(string.Join(", ", protocols.Select(p => Path.GetFileName(p.Source.Path)))));
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// The wl_interface table of every interface the bindings name. Core tables are libwayland-client's");
        w.Line("/// own exports; the rest are built here, allocated first and filled in the static constructor so");
        w.Line("/// they can refer to each other in cycles. Referenced-only interfaces are name-only stubs.");
        w.Line("/// </summary>");
        w.Line("internal static unsafe partial class WaylandInterfaces");
        w.Open();
        foreach (var i in interfaces)
            w.Line(i.TablesFromLibwayland
                ? $"public static readonly WlInterface* {i.Name} = WaylandNative.CoreInterface(\"{i.Name}\");"
                : $"public static readonly WlInterface* {i.Name} = WlTables.New();");
        if (referenced.Count > 0)
        {
            w.Line();
            w.Line("// Referenced as an argument type only, from protocols not bound here.");
            foreach (var n in referenced)
                w.Line($"public static readonly WlInterface* {n} = WlTables.Stub(\"{n}\");");
        }

        w.Line();
        w.Line("/// <summary>Every table with contents (not the stubs), in generation order: what a table check dumps.</summary>");
        w.Line("public static readonly (string Name, nint Table, bool FromLibwayland)[] All;");
        w.Line();
        w.Line("static WaylandInterfaces()");
        w.Open();
        bool first = true;
        foreach (var i in interfaces.Where(i => !i.TablesFromLibwayland))
        {
            if (!first) w.Line();
            first = false;
            w.Line($"// {i.Name}");
            w.Open();
            foreach (var (var, list) in new[] { ("r", i.Requests), ("e", i.Events) })
            {
                w.Line($"var {var} = WlTables.Messages({list.Count});");
                foreach (var m in list)
                {
                    var (sig, types) = Wire(m);
                    var typeArgs = types.Select(t => t is null ? "0" : $"(nint){t}");
                    w.Line($"WlTables.Message(&{var}[{m.Opcode}], \"{m.Name}\", \"{sig}\"{string.Concat(typeArgs.Select(t => ", " + t))});");
                }
            }
            w.Line($"WlTables.Define({i.Name}, \"{i.Name}\", {i.Version}, r, {i.Requests.Count}, e, {i.Events.Count});");
            w.Close();
        }
        w.Line();
        w.Line("All =");
        w.Line("[");
        foreach (var i in interfaces)
            w.Line($"    (\"{i.Name}\", (nint){i.Name}, {(i.TablesFromLibwayland ? "true" : "false")}),");
        w.Line("];");
        w.Close();
        w.Close();
        return w.ToString();
    }

    // ── Names ────────────────────────────────────────────────────────────────

    public static string Pascal(string snake)
    {
        var sb = new StringBuilder();
        foreach (var part in snake.Split('_', StringSplitOptions.RemoveEmptyEntries))
            sb.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        return sb.ToString();
    }

    private static string EnumEntryName(string name)
    {
        var p = Pascal(name);
        return p.Length == 0 || char.IsAsciiDigit(p[0]) ? "_" + p : p;
    }

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        // Locals the generated bodies declare.
        "a", "v", "l", "data", "self", "version",
    ];

    /// <summary>A camelCase parameter name that is not a keyword or a generated local.</summary>
    public static string Ident(string snake)
    {
        var p = Pascal(snake);
        var c = char.ToLowerInvariant(p[0]) + p[1..];
        return Keywords.Contains(c) ? c + "_" : c;
    }

    private static void Doc(Writer w, string? summary, int since = 1, string? extra = null)
    {
        if (summary is not null) w.Line($"/// <summary>{Escape(summary.Trim())}</summary>");
        var remarks = new List<string>();
        if (extra is not null) remarks.Add(extra);
        if (since > 1) remarks.Add($"Since version {since}.");
        if (remarks.Count > 0) w.Line($"/// <remarks>{string.Join(" ", remarks)}</remarks>");
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\n", " ");

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private int _indent;
        public void Raw(string s) => _sb.Append(s);
        public void Line(string s = "")
        {
            if (s.Length > 0) _sb.Append(' ', _indent * 4).Append(s);
            _sb.Append('\n');
        }
        public void Open() { Line("{"); _indent++; }
        public void Close() { _indent--; Line("}"); }
        public override string ToString() => _sb.ToString();
    }
}
