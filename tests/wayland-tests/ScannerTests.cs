using System.Xml.Linq;
using Bibui.WaylandScanner;

namespace Bibui.WaylandTests;

/// <summary>The generator: its wire format, its names, and that the checked-in output is what it makes today.</summary>
internal static class ScannerTests
{
    private const string Synthetic = """
        <protocol name="test">
          <interface name="test_thing" version="4">
            <description summary="a thing"/>
            <request name="destroy" type="destructor"/>
            <request name="bind">
              <arg name="name" type="uint"/>
              <arg name="id" type="new_id"/>
            </request>
            <request name="make_child">
              <arg name="id" type="new_id" interface="test_thing"/>
              <arg name="parent" type="object" interface="test_thing" allow-null="true"/>
              <arg name="label" type="string" allow-null="true"/>
            </request>
            <request name="set_mode" since="3">
              <arg name="mode" type="uint" enum="mode"/>
              <arg name="object" type="object" interface="not_bound_here"/>
              <arg name="at" type="fixed"/>
              <arg name="fd" type="fd"/>
            </request>
            <event name="done" type="destructor">
              <arg name="serial" type="uint"/>
            </event>
            <event name="states" since="2">
              <arg name="states" type="array"/>
              <arg name="mode" type="uint" enum="mode"/>
            </event>
            <enum name="mode" bitfield="true">
              <entry name="none" value="0"/>
              <entry name="90" value="0x10" summary="rotated &lt;90&gt;"/>
            </enum>
          </interface>
        </protocol>
        """;

    private static Scanner.Protocol Parse() =>
        Scanner.Parse(XDocument.Parse(Synthetic), new ProtocolSource("test.xml", "test.g.cs"));

    public static void Register(Harness t)
    {
        t.Run("scanner: signatures carry since-prefix, '?' and the 3-slot untyped new_id", () =>
        {
            var i = Parse().Interfaces[0];
            Assert.Eq("", Scanner.Wire(i.Requests[0]).Signature, "destroy");
            Assert.Eq("usun", Scanner.Wire(i.Requests[1]).Signature, "bind");
            Assert.Eq("n?o?s", Scanner.Wire(i.Requests[2]).Signature, "make_child");
            Assert.Eq("3uofh", Scanner.Wire(i.Requests[3]).Signature, "set_mode");
            Assert.Eq("2au", Scanner.Wire(i.Events[1]).Signature, "states");
            Assert.Eq("-,-,-,-", string.Join(",", Scanner.Wire(i.Requests[1]).Types.Select(x => x ?? "-")), "bind types");
            Assert.Eq("test_thing,test_thing,-", string.Join(",", Scanner.Wire(i.Requests[2]).Types.Select(x => x ?? "-")), "make_child types");
        });

        t.Run("scanner: expected dump is check_tables.py's format", () =>
        {
            var lines = Scanner.ExpectedDump(Parse().Interfaces[0]);
            Assert.Eq("interface test_thing v4 methods=4 events=2", lines[0]);
            Assert.Eq("  r set_mode '3uofh' [-,not_bound_here,-,-]", lines[4]);
            Assert.Eq("  e done 'u' [-]", lines[5]);
        });

        t.Run("scanner: requests are typed, version-gated and flag destructors", () =>
        {
            var code = Scanner.Generate([Parse()])["test.g.cs"];
            Assert.Contains(code, "internal readonly unsafe partial record struct TestThing(nint Handle) : IWlProxy<TestThing>");
            Assert.Contains(code, "public const uint InterfaceVersion = 4;");
            // destroy: the destructor flag.
            Assert.Contains(code, "WaylandNative.wl_proxy_marshal_array_flags(Handle, 0, null, v, WaylandNative.WL_MARSHAL_FLAG_DESTROY, a);");
            // untyped new_id: generic over the proxy type, interface name + version + id slots.
            Assert.Contains(code, "public T Bind<T>(uint name, uint version) where T : struct, IWlProxy<T>");
            Assert.Contains(code, "a[1].S = (nint)T.Interface->Name;");
            // typed new_id returns the new proxy; nullable string and object.
            Assert.Contains(code, "public TestThing MakeChild(TestThing parent, string? label)");
            Assert.Contains(code, "WlUtf8.Free(labelUtf8);");
            // since 3: gated before anything is marshalled; enum-typed; stub interface is untyped; keyword escaped.
            Assert.Contains(code, "public const uint SetModeSince = 3;");
            Assert.Contains(code, "if (v < 3) throw WaylandNative.TooOld(\"test_thing.set_mode\", 3, v);");
            Assert.Contains(code, "public void SetMode(TestThing.Mode mode, nint object_, WlFixed at, int fd)");
        });

        t.Run("scanner: listeners are function pointers with a default for every slot", () =>
        {
            var code = Scanner.Generate([Parse()])["test.g.cs"];
            Assert.Contains(code, "public delegate* unmanaged<void*, TestThing, uint, void> Done;");
            Assert.Contains(code, "public delegate* unmanaged<void*, TestThing, WlArray*, TestThing.Mode, void> States;");
            Assert.Contains(code, "public const uint StatesSince = 2;");
            Assert.Contains(code, "if (l->Done == null) l->Done = &Defaults.Done;");
            Assert.Contains(code, "if (l->States == null) l->States = &Defaults.States;");
            // A destructor event's default still destroys the proxy, or it leaks.
            Assert.Contains(code, "public static void Done(void* data, TestThing self, uint serial) => self.DestroyProxy();");
            Assert.Contains(code, "public static void States(void* data, TestThing self, WlArray* states, TestThing.Mode mode) { }");
        });

        t.Run("scanner: enums keep values, escape names, mark bitfields", () =>
        {
            var code = Scanner.Generate([Parse()])["test.g.cs"];
            Assert.Contains(code, "[global::System.Flags]\n    public enum Mode : uint");
            Assert.Contains(code, "_90 = 16,");
            Assert.Contains(code, "/// <summary>rotated &lt;90&gt;</summary>");
        });

        t.Run("scanner: tables file allocates, stubs foreign interfaces, then fills", () =>
        {
            var code = Scanner.Generate([Parse()])[Scanner.TablesFile];
            Assert.Contains(code, "public static readonly WlInterface* test_thing = WlTables.New();");
            Assert.Contains(code, "public static readonly WlInterface* not_bound_here = WlTables.Stub(\"not_bound_here\");");
            Assert.Contains(code, "WlTables.Message(&r[1], \"bind\", \"usun\", 0, 0, 0, 0);");
            Assert.Contains(code, "WlTables.Message(&r[3], \"set_mode\", \"3uofh\", 0, (nint)not_bound_here, 0, 0);");
            Assert.Contains(code, "WlTables.Define(test_thing, \"test_thing\", 4, r, 4, e, 2);");
        });

        t.Run("scanner: output is deterministic", () =>
        {
            var a = Scanner.Generate([Parse()]);
            var b = Scanner.Generate([Parse()]);
            Assert.True(a.Keys.SequenceEqual(b.Keys) && a.All(kv => b[kv.Key] == kv.Value), "two runs differ");
        });

        t.Run("scanner: regenerating from the installed XML reproduces the checked-in bindings", () =>
        {
            RequireXml();
            var dir = Path.Combine(RepoRoot(), "bibui-platform-wayland", "Protocol", "Generated");
            var files = Scanner.Generate(Scanner.DefaultSources);
            var onDisk = Directory.GetFiles(dir, "*.g.cs").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
            Assert.Eq(string.Join(" ", files.Keys), string.Join(" ", onDisk), "generated file set");
            foreach (var (name, text) in files)
                Assert.True(File.ReadAllText(Path.Combine(dir, name)) == text,
                    $"{name} differs from a fresh generation: run `dotnet run --project tools/wayland-scanner -- generate`");
        });
    }

    public static void RequireXml()
    {
        var missing = Scanner.DefaultSources.FirstOrDefault(s => !File.Exists(s.Path));
        if (missing is not null) throw new SkipException($"{missing.Path} is not installed");
    }

    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) return d.FullName;
        throw new InvalidOperationException("not inside the bibui repository");
    }
}
