using System.Diagnostics;
using Bibui.Platform.Portal;
using Bibui.Platform.Portal.DBus;

namespace Bibui.PortalTests;

/// <summary>
/// The file chooser end to end against <see cref="FakeBus"/>: what it sends,
/// each way a request can end, and every way of there being no portal, none
/// of which may throw. One live probe asks the session bus's portal for its
/// version and adds a match rule; neither opens a dialog.
/// </summary>
internal static class ChooserTests
{
    private static readonly FileChooserOptions Attach = new() { Title = "Attach files", Multiple = true };

    /// <summary>A <c>Response(code, {uris})</c> signal's body; the caller stamps the header.</summary>
    public static DBusMessage Response(uint code, params string[] uris)
    {
        var w = new DBusWriter();
        w.UInt32(code);
        var d = w.BeginArray(8);
        if (uris.Length > 0)
        {
            w.BeginStruct();
            w.String("uris");
            w.Variant("as");
            w.Strings(uris);
        }
        w.EndArray(d);
        return new DBusMessage
        {
            Type = DBusMessageType.Signal,
            Path = "/org/freedesktop/portal/desktop/request/1_7/t",
            Interface = "org.freedesktop.portal.Request",
            Member = "Response",
            Signature = "ua{sv}",
            Body = w.ToArray(),
        };
    }

    // The handle the client expects for this OpenFile call.
    private static string HandleOf(DBusMessage openFile)
    {
        var options = (Dictionary<object, object>)openFile.ReadBody()[2];
        return FileChooser.RequestPath(FakeBus.ClientName, (string)((DBusVariant)options["handle_token"]).Value);
    }

    /// <summary>A portal that answers OpenFile or SaveFile with the expected handle, and then <paramref name="then"/>.</summary>
    private static Func<DBusMessage, Task> Portal(FakeBus bus, Func<DBusMessage, string, Task>? then = null) => async m =>
    {
        if (m.Member is not ("OpenFile" or "SaveFile")) return;
        var handle = HandleOf(m);
        await bus.Reply(m, "o", w => w.String(handle));
        if (then is not null) await then(m, handle);
    };

    public static void Register(Harness t)
    {
        t.Run("chooser: the request handle is the portal's", () =>
        {
            Assert.Eq("/org/freedesktop/portal/desktop/request/1_42/bibui_x", FileChooser.RequestPath(":1.42", "bibui_x"), "handle");
        });

        t.Run("chooser: a choice comes back as local paths", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = async m =>
            {
                // The Response and the reply in one write, the Response first: it
                // reaches the chooser before it knows the handle, as one sent
                // right after the reply can, and must not be lost.
                var handle = HandleOf(m);
                await bus.SendRaw([.. bus.ResponseBytes(handle, 0, FakeBus.PortalName, "file:///home/a/My%20Notes.md", "file:///tmp/b"),
                    .. bus.ReplyBytes(m, "o", w => w.String(handle))]);
            };
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Eq(FileChooserOutcome.Chosen, r.Outcome, $"outcome ({r.Reason})");
            Assert.Eq("/home/a/My Notes.md|/tmp/b", string.Join("|", r.Paths), "paths");

            Assert.Eq($"AUTH EXTERNAL {Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(Uid().ToString()))}", bus.AuthLine.TrimStart('\0'), "AUTH as this user");
            Assert.True(bus.AuthLine.StartsWith('\0'), "a NUL byte first");
            var open = bus.Received.Single(m => m.Member == "OpenFile");
            Assert.Eq("org.freedesktop.portal.Desktop", open.Destination, "destination");
            Assert.Eq("/org/freedesktop/portal/desktop", open.Path, "path");
            Assert.Eq("org.freedesktop.portal.FileChooser", open.Interface, "interface");
            Assert.Eq("ssa{sv}", open.Signature, "signature");
            Assert.Eq("Attach files", (string)open.ReadBody()[1], "title");
            var rule = bus.Rules.Single();
            Assert.Contains(rule, "sender='org.freedesktop.portal.Desktop'");
            Assert.Contains(rule, $"path='{HandleOf(open)}'");
            // Subscribed before asking: AddMatch is sent before OpenFile.
            Assert.True(bus.Received.FindIndex(m => m.Member == "AddMatch") < bus.Received.FindIndex(m => m.Member == "OpenFile"), "AddMatch first");
        });

        t.Run("chooser: OpenAsync returns the paths, or null", async () =>
        {
            await using (var bus = new FakeBus())
            {
                bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 0, FakeBus.PortalName, "file:///x")));
                var paths = await FileChooser.OpenAsync(Attach, bus.Address, default);
                Assert.Eq("/x", string.Join("|", paths ?? ["(null)"]), "chosen");
            }
            await using (var bus = new FakeBus())
            {
                bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 1, FakeBus.PortalName)));
                Assert.Eq(null, await FileChooser.OpenAsync(Attach, bus.Address, default), "cancelled");
            }
            Assert.Eq(null, await FileChooser.OpenAsync(Attach, null, default), "no bus");
        });

        t.Run("chooser: a save asks SaveFile and comes back as one local path", async () =>
        {
            await using (var bus = new FakeBus())
            {
                bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 0, FakeBus.PortalName, "file:///home/a/M%C3%B8de%20notes.md")));
                var r = await FileChooser.ChooseSaveAsync(new SaveFileOptions { Title = "Save the notes", CurrentName = "notes.md" }, bus.Address, default)
                    .WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Eq(FileChooserOutcome.Chosen, r.Outcome, $"outcome ({r.Reason})");
                Assert.Eq("/home/a/Møde notes.md", string.Join("|", r.Paths), "path");
                var save = bus.Received.Single(m => m.Interface == "org.freedesktop.portal.FileChooser");
                Assert.Eq("SaveFile", save.Member, "method");
                Assert.Eq("ssa{sv}", save.Signature, "signature");
                Assert.Eq("Save the notes", (string)save.ReadBody()[1], "title");
                Assert.Contains(bus.Rules.Single(), $"path='{HandleOf(save)}'");
            }
            await using (var bus = new FakeBus())
            {
                bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 0, FakeBus.PortalName, "file:///tmp/out.md")));
                Assert.Eq("/tmp/out.md", await FileChooser.SaveAsync(new SaveFileOptions(), bus.Address, default), "SaveAsync, chosen");
            }
            await using (var bus = new FakeBus())
            {
                bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 1, FakeBus.PortalName)));
                Assert.Eq(null, await FileChooser.SaveAsync(new SaveFileOptions(), bus.Address, default), "SaveAsync, cancelled");
            }
            Assert.Eq(null, await FileChooser.SaveAsync(new SaveFileOptions(), null, default), "SaveAsync, no bus");
        });

        t.Run("chooser: closing the dialog is Cancelled", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 1, FakeBus.PortalName)));
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default);
            Assert.Eq(FileChooserOutcome.Cancelled, r.Outcome, "outcome");
            Assert.Eq(0, r.Paths.Count, "no paths");
        });

        t.Run("chooser: a request the portal ends otherwise is Failed", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = Portal(bus, (_, h) => bus.SendRaw(bus.ResponseBytes(h, 2, FakeBus.PortalName)));
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default);
            Assert.Eq(FileChooserOutcome.Failed, r.Outcome, "outcome");
        });

        t.Run("chooser: a Response from anyone but the portal is ignored", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = Portal(bus, async (_, h) =>
            {
                await bus.SendRaw(bus.ResponseBytes(h, 0, ":1.99", "file:///evil"));
                await bus.SendRaw(bus.ResponseBytes(h + "x", 0, FakeBus.PortalName, "file:///other-request"));
                await bus.SendRaw(bus.ResponseBytes(h, 0, FakeBus.PortalName, "file:///good"));
            });
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Eq("/good", string.Join("|", r.Paths), "the portal's answer, for this request");
        });

        t.Run("chooser: a portal that names its own handle is followed there", async () =>
        {
            await using var bus = new FakeBus();
            const string own = "/org/freedesktop/portal/desktop/request/old/42";
            bus.Portal = async m =>
            {
                await bus.Reply(m, "o", w => w.String(own));
                // Sent once the second match is in place, as a bus would only
                // deliver it then; off the bus's loop, which has that AddMatch to read.
                _ = Task.Run(async () =>
                {
                    while (bus.Rules.Count < 2) await Task.Delay(5);
                    await bus.SendRaw(bus.ResponseBytes(own, 0, FakeBus.PortalName, "file:///old"));
                });
            };
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Eq("/old", string.Join("|", r.Paths), "paths");
            Assert.Contains(bus.Rules[1], $"path='{own}'");
        });

        t.Run("chooser: no portal on the bus is Unavailable, not an exception", async () =>
        {
            await using var bus = new FakeBus(); // no Portal: ServiceUnknown, as dbus-broker says it
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default);
            Assert.Eq(FileChooserOutcome.Unavailable, r.Outcome, "outcome");
            Assert.Contains(r.Reason ?? "", "org.freedesktop.DBus.Error.ServiceUnknown");
        });

        t.Run("chooser: a portal that refuses is Failed, with its words", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = m => bus.Error(m, "org.freedesktop.portal.Error.NotAllowed", "no dialogs today");
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default);
            Assert.Eq(FileChooserOutcome.Failed, r.Outcome, "outcome");
            Assert.Contains(r.Reason ?? "", "no dialogs today");
        });

        t.Run("chooser: no session bus is Unavailable, not an exception", async () =>
        {
            var none = await FileChooser.ChooseAsync(Attach, null, default);
            Assert.Eq(FileChooserOutcome.Unavailable, none.Outcome, "no address");
            Assert.Contains(none.Reason ?? "", "no session bus");

            var dir = Directory.CreateTempSubdirectory("bibui-portal-none-").FullName;
            try
            {
                var gone = await FileChooser.ChooseAsync(Attach, $"unix:path={dir}/bus", default);
                Assert.Eq(FileChooserOutcome.Unavailable, gone.Outcome, "a socket nobody listens on");
                Assert.Contains(gone.Reason ?? "", "cannot reach the session bus");
            }
            finally { Directory.Delete(dir, true); }

            var tcp = await FileChooser.ChooseAsync(Attach, "tcp:host=localhost,port=1", default);
            Assert.Eq(FileChooserOutcome.Unavailable, tcp.Outcome, "an address with no Unix socket");
            Assert.Eq(null, await FileChooser.VersionAsync(null, default), "no version without a bus");
        });

        t.Run("chooser: a bus that refuses authentication is Unavailable", async () =>
        {
            await using var bus = new FakeBus { AuthReply = "REJECTED EXTERNAL" };
            var r = await FileChooser.ChooseAsync(Attach, bus.Address, default);
            Assert.Eq(FileChooserOutcome.Unavailable, r.Outcome, "outcome");
            Assert.Contains(r.Reason ?? "", "REJECTED");
        });

        t.Run("chooser: a portal that never answers is Unavailable after the timeout", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = _ => Task.CompletedTask;
            var was = FileChooser.CallTimeout;
            FileChooser.CallTimeout = TimeSpan.FromMilliseconds(200);
            try
            {
                var r = await FileChooser.ChooseAsync(Attach, bus.Address, default);
                Assert.Eq(FileChooserOutcome.Unavailable, r.Outcome, "outcome");
                Assert.Contains(r.Reason ?? "", "did not answer in time");
            }
            finally { FileChooser.CallTimeout = was; }
        });

        t.Run("chooser: the dialog holds no thread, and the bus going away ends it", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = Portal(bus);
            var clock = Stopwatch.StartNew();
            var task = FileChooser.ChooseAsync(Attach, bus.Address, default);
            Assert.True(clock.ElapsedMilliseconds < 1000, $"ChooseAsync returned at once ({clock.ElapsedMilliseconds} ms)");
            await bus.FirstPortalCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.True(!task.IsCompleted, "still open while the dialog is");
            bus.Kill();
            var r = await task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Eq(FileChooserOutcome.Failed, r.Outcome, "outcome");
            Assert.Contains(r.Reason ?? "", "closed");
        });

        t.Run("chooser: cancelling closes the dialog and throws OperationCanceledException", async () =>
        {
            await using var bus = new FakeBus();
            var closed = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            bus.Portal = async m =>
            {
                if (m.Member == "OpenFile") await Portal(bus)(m);
                else if (m.Member == "Close") { closed.TrySetResult(m); await bus.Reply(m, "", null); }
            };
            using var cts = new CancellationTokenSource();
            var task = FileChooser.ChooseAsync(Attach, bus.Address, cts.Token);
            var open = await bus.FirstPortalCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            var close = await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Eq(HandleOf(open), close.Path, "Close on the request's handle");
            Assert.Eq("org.freedesktop.portal.Request", close.Interface, "interface");
        });

        t.Run("chooser: the version comes from the portal's property", async () =>
        {
            await using var bus = new FakeBus();
            bus.Portal = m =>
            {
                var args = m.ReadBody();
                Assert.Eq("org.freedesktop.DBus.Properties.Get", $"{m.Interface}.{m.Member}", "call");
                Assert.Eq("org.freedesktop.portal.FileChooser/version", $"{args[0]}/{args[1]}", "property");
                return bus.Reply(m, "v", w => { w.Variant("u"); w.UInt32(4); });
            };
            Assert.Eq(4u, await FileChooser.VersionAsync(bus.Address, default), "version");
        });

        t.Run("chooser: live, the session bus's portal says its version (no dialog)", async () =>
        {
            var address = SessionBus.Address() ?? throw new SkipException("no session bus");
            var version = await FileChooser.VersionAsync(address, default) ?? throw new SkipException("no file chooser portal on the session bus");
            Assert.True(version >= 1, $"version {version}");
            // The bus accepts the match rule the chooser subscribes with.
            await using var bus = await DBusConnection.ConnectAsync(address, default);
            Assert.True(bus.UniqueName.StartsWith(':'), $"unique name {bus.UniqueName}");
            await FileChooser.AddMatchAsync(bus, FileChooser.RequestPath(bus.UniqueName, "bibui_probe"), default);
        });
    }

    [System.Runtime.InteropServices.DllImport("libc")] private static extern uint getuid();

    private static uint Uid() => getuid();
}
