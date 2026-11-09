using System.Diagnostics;
using Bibui.Platform.Portal;
using Bibui.Platform.Portal.DBus;

namespace Bibui.PortalTests;

/// <summary>
/// The D-Bus wire format. What the file chooser sends is checked against
/// GLib's own encoder (GVariant, GDBusMessage, through PyGObject), which is
/// what the portal itself reads with, so the bytes are not judged by the code
/// that wrote them; the rest is round trips and refusals.
/// </summary>
internal static class WireTests
{
    public static readonly FileChooserOptions Everything = new()
    {
        ParentWindow = "wayland:abc",
        Title = "Attach files",
        AcceptLabel = "Attach",
        Multiple = true,
        Filters =
        [
            new FileFilter("Images", "*.png", "*.JPG") { MimeTypes = ["image/*"] },
            new FileFilter("Notes ✎", "*.md"),
        ],
        CurrentFilter = new FileFilter("Notes ✎", "*.md"),
        CurrentFolder = "/home/a/Mød",
    };

    // The same request, built by GLib.
    private const string Script = """
        import sys, gi
        gi.require_version('Gio', '2.0')
        from gi.repository import Gio, GLib

        def opts():
            return {
                'handle_token': GLib.Variant('s', 'bibui_tok'),
                'accept_label': GLib.Variant('s', 'Attach'),
                'modal': GLib.Variant('b', True),
                'multiple': GLib.Variant('b', True),
                'directory': GLib.Variant('b', False),
                'filters': GLib.Variant('a(sa(us))', [('Images', [(0, '*.png'), (0, '*.JPG'), (1, 'image/*')]), ('Notes ✎', [(0, '*.md')])]),
                'current_filter': GLib.Variant('(sa(us))', ('Notes ✎', [(0, '*.md')])),
                'current_folder': GLib.Variant.new_bytestring('/home/a/Mød'.encode()),
            }

        def open_file():
            return GLib.Variant('(ssa{sv})', ('wayland:abc', 'Attach files', opts()))

        mode = sys.argv[1]
        if mode == 'openfile-body':
            # GVariant's own serialisation is not D-Bus's: the body is taken out of a message.
            m = Gio.DBusMessage.new_method_call('org.freedesktop.portal.Desktop', '/org/freedesktop/portal/desktop',
                                                'org.freedesktop.portal.FileChooser', 'OpenFile')
            m.set_body(open_file())
            m.set_byte_order(Gio.DBusMessageByteOrder.LITTLE_ENDIAN)
            blob = m.to_blob(Gio.DBusCapabilityFlags.NONE)
            print(blob[len(blob) - int.from_bytes(blob[4:8], 'little'):].hex())
        elif mode == 'decode':
            m = Gio.DBusMessage.new_from_blob(bytes.fromhex(sys.argv[2]), Gio.DBusCapabilityFlags.NONE)
            print(m.get_message_type().value_nick, m.get_serial(), m.get_destination(), m.get_path(),
                  m.get_interface(), m.get_member(), m.get_signature(), m.get_body().equal(open_file()))
        elif mode == 'response':
            m = Gio.DBusMessage.new_signal('/org/freedesktop/portal/desktop/request/1_7/tok', 'org.freedesktop.portal.Request', 'Response')
            m.set_sender(':1.2')
            m.set_serial(3)
            m.set_body(GLib.Variant('(ua{sv})', (0, {
                'choices': GLib.Variant('a(ss)', [('encoding', 'utf8')]),
                'uris': GLib.Variant('as', ['file:///home/a/My%20Notes.md', 'file:///tmp/x%E2%9C%8E']),
                'current_filter': GLib.Variant('(sa(us))', ('Notes', [(0, '*.md')])),
            })))
            m.set_byte_order(Gio.DBusMessageByteOrder.BIG_ENDIAN if sys.argv[2] == 'big' else Gio.DBusMessageByteOrder.LITTLE_ENDIAN)
            print(m.to_blob(Gio.DBusCapabilityFlags.NONE).hex())
        """;

    public static void Register(Harness t)
    {
        t.Run("wire: the OpenFile body is GLib's, byte for byte", () =>
        {
            var ours = Convert.ToHexString(FileChooser.EncodeOpenFile(Everything, "bibui_tok").Written).ToLowerInvariant();
            Assert.Eq(Python("openfile-body"), ours, "OpenFile body");
        });

        t.Run("wire: GLib reads our OpenFile call whole", () =>
        {
            var call = DBusMessage.Call("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                "org.freedesktop.portal.FileChooser", "OpenFile", "ssa{sv}", FileChooser.EncodeOpenFile(Everything, "bibui_tok"));
            call.Serial = 7;
            Assert.Eq("method-call 7 org.freedesktop.portal.Desktop /org/freedesktop/portal/desktop org.freedesktop.portal.FileChooser OpenFile ssa{sv} True",
                Python("decode", Convert.ToHexString(call.Encode())), "GLib's reading");
        });

        foreach (var order in new[] { "big", "little" })
            t.Run($"wire: a {order}-endian Response from GLib reads back as paths", () =>
            {
                var m = DBusMessage.Decode(Convert.FromHexString(Python("response", order)));
                Assert.Eq(DBusMessageType.Signal, m.Type, "type");
                Assert.Eq(order == "big", m.BigEndian, "byte order");
                Assert.Eq("/org/freedesktop/portal/desktop/request/1_7/tok", m.Path, "path");
                Assert.Eq("org.freedesktop.portal.Request", m.Interface, "interface");
                Assert.Eq("Response", m.Member, "member");
                Assert.Eq(":1.2", m.Sender, "sender");
                Assert.Eq(3u, m.Serial, "serial");
                var r = FileChooser.ReadResponse(m);
                Assert.Eq(FileChooserOutcome.Chosen, r.Outcome, "outcome");
                Assert.Eq("/home/a/My Notes.md|/tmp/x✎", string.Join("|", r.Paths), "paths");
                Assert.Eq(null, r.Reason, "reason");
            });

        t.Run("wire: OpenFile's options read back as written", () =>
        {
            var body = FileChooser.EncodeOpenFile(Everything, "bibui_tok").ToArray();
            var v = new DBusReader(body, 0, body.Length, false).ReadAll("ssa{sv}");
            Assert.Eq("wayland:abc", (string)v[0], "parent");
            Assert.Eq("Attach files", (string)v[1], "title");
            var o = (Dictionary<object, object>)v[2];
            Assert.Eq("handle_token,accept_label,modal,multiple,directory,filters,current_filter,current_folder",
                string.Join(",", o.Keys), "keys, in order");
            Assert.Eq("bibui_tok", (string)((DBusVariant)o["handle_token"]).Value, "token");
            Assert.Eq(true, (bool)((DBusVariant)o["multiple"]).Value, "multiple");
            Assert.Eq(false, (bool)((DBusVariant)o["directory"]).Value, "directory");
            var filters = (DBusVariant)o["filters"];
            Assert.Eq("a(sa(us))", filters.Signature, "filters' type");
            var images = (object[])((List<object>)filters.Value)[0];
            Assert.Eq("Images", (string)images[0], "filter name");
            Assert.Eq("0:*.png 0:*.JPG 1:image/*",
                string.Join(" ", ((List<object>)images[1]).Cast<object[]>().Select(p => $"{p[0]}:{p[1]}")), "globs, then MIME types");
            var folder = (List<object>)((DBusVariant)o["current_folder"]).Value;
            Assert.Eq("/home/a/Mød\0", System.Text.Encoding.UTF8.GetString(folder.Cast<byte>().ToArray()), "folder, NUL-terminated");
        });

        t.Run("wire: unset options are left out", () =>
        {
            var body = FileChooser.EncodeOpenFile(new FileChooserOptions { Directory = true }, "bibui_t").ToArray();
            var o = (Dictionary<object, object>)new DBusReader(body, 0, body.Length, false).ReadAll("ssa{sv}")[2];
            Assert.Eq("handle_token,modal,multiple,directory", string.Join(",", o.Keys), "keys");
            Assert.Eq(true, (bool)((DBusVariant)o["directory"]).Value, "directory");
        });

        t.Run("wire: a NUL in an option is refused before anything is sent", () =>
        {
            Assert.Throws<ArgumentException>(() => FileChooser.ChooseAsync(new FileChooserOptions { Title = "a\0b" }, null, default));
            Assert.Throws<ArgumentException>(() => FileChooser.ChooseAsync(new FileChooserOptions { CurrentFolder = "/a\0" }, null, default));
        });

        t.Run("wire: an empty array still pads to its element", () =>
        {
            var w = new DBusWriter();
            var a = w.BeginArray(8);
            w.EndArray(a);
            w.Byte(9);
            // The length, 0, then padding to 8 that the length does not count, then the byte.
            Assert.Eq("000000000000000009", Convert.ToHexString(w.Written), "bytes");
            var r = new DBusReader(w.ToArray(), 0, w.Length, false);
            var v = r.ReadAll("a(sa(us))y");
            Assert.Eq(0, ((List<object>)v[0]).Count, "elements");
            Assert.Eq((byte)9, (byte)v[1], "the byte after");
            Assert.True(r.AtEnd, "read to the end");
        });

        t.Run("wire: every type the client reads round-trips", () =>
        {
            var w = new DBusWriter();
            w.Byte(200);
            w.Bool(true);
            w.Int32(-5);
            w.UInt32(7);
            w.String("hé");
            w.String("/a/b");
            w.Signature("a{sv}");
            w.Strings(["x", "", "z"]);
            w.Bytes([1, 2, 3]);
            var d = w.BeginArray(8);
            w.BeginStruct(); w.String("n"); w.Variant("u"); w.UInt32(9);
            w.BeginStruct(); w.String("v"); w.Variant("v"); w.Variant("s"); w.String("inner");
            w.EndArray(d);
            var bytes = w.ToArray();
            var r = new DBusReader(bytes, 0, bytes.Length, false);
            var v = r.ReadAll("ybiusogasaya{sv}");
            Assert.Eq((byte)200, (byte)v[0], "y");
            Assert.Eq(true, (bool)v[1], "b");
            Assert.Eq(-5, (int)v[2], "i");
            Assert.Eq(7u, (uint)v[3], "u");
            Assert.Eq("hé", (string)v[4], "s");
            Assert.Eq("/a/b", (string)v[5], "o");
            Assert.Eq("a{sv}", (string)v[6], "g");
            Assert.Eq("x||z", string.Join("|", ((List<object>)v[7]).Cast<string>()), "as");
            Assert.Eq("1,2,3", string.Join(",", ((List<object>)v[8]).Cast<byte>()), "ay");
            var map = (Dictionary<object, object>)v[9];
            Assert.Eq(9u, (uint)((DBusVariant)map["n"]).Value, "a{sv} u");
            var nested = (DBusVariant)((DBusVariant)map["v"]).Value;
            Assert.Eq("inner", (string)nested.Value, "variant in a variant");
            Assert.True(r.AtEnd, "read to the end");
        });

        t.Run("wire: a complete type's length", () =>
        {
            Assert.Eq(1, DBusReader.TypeLength("s", 0), "s");
            Assert.Eq(5, DBusReader.TypeLength("a{sv}", 0), "a{sv}");
            Assert.Eq(9, DBusReader.TypeLength("a(sa(us))", 0), "a(sa(us))");
            Assert.Eq(3, DBusReader.TypeLength("aasu", 0), "aas");
            Assert.Eq(8, DBusReader.TypeLength("u(sa(us))", 1), "(sa(us)) after u");
        });

        t.Run("wire: malformed messages are refused, not trusted", () =>
        {
            var good = Hello();
            Assert.Eq(good.Length, DBusMessage.LengthFromPrefix(good), "length from the prefix");
            Assert.Eq("Hello", DBusMessage.Decode(good).Member, "decodes");

            Assert.Throws<DBusFormatException>(() => DBusMessage.Decode(good[..^1]));             // shorter than it says
            var order = (byte[])good.Clone(); order[0] = (byte)'x';
            Assert.Throws<DBusFormatException>(() => DBusMessage.Decode(order));                  // no such byte order
            var version = (byte[])good.Clone(); version[3] = 2;
            Assert.Throws<DBusFormatException>(() => DBusMessage.Decode(version));                // protocol version
            var type = (byte[])good.Clone(); type[1] = 9;
            Assert.Throws<DBusFormatException>(() => DBusMessage.Decode(type));                   // message type

            byte[] padding = [7, 1, 0, 0, 0];                                                      // y then u, padding not zero
            Assert.Throws<DBusFormatException>(() => new DBusReader(padding, 0, 5, false).ReadAll("yu"));
            byte[] boolean = [2, 0, 0, 0];
            Assert.Throws<DBusFormatException>(() => new DBusReader(boolean, 0, 4, false).ReadAll("b"));
            byte[] longArray = [0xff, 0, 0, 0, 1];                                                 // 255 bytes said, 1 there
            Assert.Throws<DBusFormatException>(() => new DBusReader(longArray, 0, 5, false).ReadAll("ay"));
            byte[] unknown = [0];
            Assert.Throws<DBusFormatException>(() => new DBusReader(unknown, 0, 1, false).ReadAll("z"));
            byte[] unterminated = [1, 0, 0, 0, (byte)'a', (byte)'b'];
            Assert.Throws<DBusFormatException>(() => new DBusReader(unterminated, 0, 6, false).ReadAll("s"));

            // A variant holding a variant holding … 100 deep.
            var deep = new DBusWriter();
            for (int i = 0; i < 100; i++) deep.Variant("v");
            deep.Variant("y");
            deep.Byte(1);
            var d = deep.ToArray();
            Assert.Throws<DBusFormatException>(() => new DBusReader(d, 0, d.Length, false).ReadAll("v"));
        });
    }

    private static byte[] Hello()
    {
        var m = DBusMessage.Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "Hello");
        m.Serial = 1;
        return m.Encode();
    }

    private static string? _script;

    /// <summary>Runs the GLib script in <paramref name="mode"/>; SKIP when python3 or PyGObject is missing.</summary>
    private static string Python(params string[] args)
    {
        if (_script is null)
        {
            _script = Path.Combine(Path.GetTempPath(), $"bibui-portal-gio-{Environment.ProcessId}.py");
            File.WriteAllText(_script, Script);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(_script);
        }
        Process p;
        try
        {
            p = Process.Start(new ProcessStartInfo("python3", [_script, .. args])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
        }
        catch (System.ComponentModel.Win32Exception) { throw new SkipException("python3 is not installed"); }
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            if (stderr.Contains("No module named 'gi'") || stderr.Contains("Namespace Gio not available"))
                throw new SkipException("python3 has no PyGObject with Gio");
            throw new Exception($"python3 failed:\n{stderr}");
        }
        return stdout.Result.Trim();
    }
}
