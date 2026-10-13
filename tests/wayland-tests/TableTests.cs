using System.Diagnostics;
using Bibui.Platform.Wayland.Protocol;
using Bibui.WaylandScanner;

namespace Bibui.WaylandTests;

/// <summary>
/// The live tables: what libwayland actually marshals with, read back out of
/// native memory and compared to the XML — by the generator's own reading,
/// and by tools/wayland-scanner/check_tables.py, which shares no code with it.
/// </summary>
internal static unsafe class TableTests
{
    public static List<string> DumpAll()
    {
        var lines = new List<string>();
        foreach (var (_, table, _) in WaylandInterfaces.All) lines.AddRange(WlTables.Dump((WlInterface*)table));
        return lines;
    }

    private static void RequireLibwayland()
    {
        if (!WaylandNative.IsAvailable) throw new SkipException($"{WaylandNative.Library} cannot be loaded");
    }

    public static void Register(Harness t)
    {
        t.Run("tables: every live table matches the XML (generator's reading)", () =>
        {
            RequireLibwayland();
            ScannerTests.RequireXml();
            var expected = Scanner.DefaultSources.SelectMany(s => Scanner.Parse(s).Interfaces)
                .ToDictionary(i => i.Name, Scanner.ExpectedDump);
            Assert.Eq(expected.Count, WaylandInterfaces.All.Length, "table count");
            foreach (var (name, table, _) in WaylandInterfaces.All)
            {
                var live = WlTables.Dump((WlInterface*)table);
                var want = expected[name];
                Assert.True(live.SequenceEqual(want),
                    $"{name} differs:\n  live: {string.Join("\n        ", live.Except(want))}\n  xml:  {string.Join("\n        ", want.Except(live))}");
            }
        });

        t.Run("tables: check_tables.py agrees with every live table", () =>
        {
            RequireLibwayland();
            ScannerTests.RequireXml();
            var script = Path.Combine(ScannerTests.RepoRoot(), "tools", "wayland-scanner", "check_tables.py");
            var dump = Path.Combine(Path.GetTempPath(), $"bibui-wayland-tables-{Environment.ProcessId}.txt");
            File.WriteAllLines(dump, DumpAll());
            try
            {
                Process p;
                try
                {
                    p = Process.Start(new ProcessStartInfo("python3", [script, dump])
                    { RedirectStandardOutput = true, UseShellExecute = false })!;
                }
                catch (System.ComponentModel.Win32Exception) { throw new SkipException("python3 is not installed"); }
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0 && output.TrimEnd().EndsWith("ok", StringComparison.Ordinal), output);
            }
            finally { File.Delete(dump); }
        });

        t.Run("tables: core tables are libwayland's own, the rest are ours", () =>
        {
            RequireLibwayland();
            ScannerTests.RequireXml();
            foreach (var (name, table, fromLib) in WaylandInterfaces.All)
            {
                Assert.Eq(fromLib, name.StartsWith("wl_", StringComparison.Ordinal), $"{name} source");
                Assert.True(table != 0, $"{name} is null");
            }
            Assert.Eq((nint)WaylandNative.CoreInterface("wl_surface"), (nint)WlSurface.Interface, "wl_surface");
            Assert.Eq("zwp_tablet_tool_v2", WlUtf8.Read(WaylandInterfaces.zwp_tablet_tool_v2->Name), "stub name");
        });

        t.Run("tables: generated wrapper versions are the tables' versions", () =>
        {
            RequireLibwayland();
            ScannerTests.RequireXml();
            Assert.Eq((int)WlSurface.InterfaceVersion, WlSurface.Interface->Version, "wl_surface");
            Assert.Eq((int)WlCompositor.InterfaceVersion, WlCompositor.Interface->Version, "wl_compositor");
            Assert.Eq((int)XdgToplevel.InterfaceVersion, XdgToplevel.Interface->Version, "xdg_toplevel");
            Assert.Eq((int)WpFractionalScaleV1.InterfaceVersion, WpFractionalScaleV1.Interface->Version, "wp_fractional_scale_v1");
        });

        t.Run("listener: Create fills every slot the caller left empty", () =>
        {
            var cb = WlCallback.Listener.Create(default);
            var top = XdgToplevel.Listener.Create(new XdgToplevel.Listener { Close = &OnClose });
            try
            {
                Assert.True(cb->Done != null, "wl_callback.done");
                Assert.True(top->Configure != null && top->ConfigureBounds != null && top->WmCapabilities != null, "xdg_toplevel defaults");
                Assert.True((nint)top->Close == (nint)(delegate* unmanaged<void*, XdgToplevel, void>)&OnClose, "caller's handler kept");
                Assert.Eq(4 * sizeof(nint), sizeof(XdgToplevel.Listener), "one pointer per event, C layout");
            }
            finally
            {
                WlCallback.Listener.Free(cb);
                XdgToplevel.Listener.Free(top);
            }
        });

        t.Run("proxy: a wrapper is one pointer wide, so it crosses the ABI as the pointer", () =>
        {
            Assert.Eq(sizeof(nint), sizeof(WlSurface), "WlSurface");
            Assert.Eq(sizeof(nint), sizeof(XdgToplevel), "XdgToplevel");
            Assert.Eq(8, sizeof(WlArgument), "wl_argument");
            Assert.Eq("wl_surface@1f", new WlSurface(0x1f).ToString(), "ToString touches no native code");
        });
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnClose(void* data, XdgToplevel self) { }
}
