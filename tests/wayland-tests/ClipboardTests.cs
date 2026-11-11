using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bibui.Core.Runtime.Platform;
using Bibui.Platform.Wayland;
using Bibui.Platform.Wayland.Protocol;

namespace Bibui.WaylandTests;

/// <summary>
/// The clipboard without the session's selection: offer and mime
/// bookkeeping as state, the pipe reader and writer on real pipes, and the
/// clipboard over a fake wire — so set_selection never reaches the
/// compositor. One live probe binds wl_data_device_manager and a seat, makes
/// the data device and tears it down; it sets no selection and reads none.
/// </summary>
internal static unsafe class ClipboardTests
{
    [DllImport("libc")] private static extern int dup(int fd);
    [DllImport("libc")] private static extern int close(int fd);
    [DllImport("libc")] private static extern nint write(int fd, byte* buf, nuint count);

    private static void Write(int fd, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        fixed (byte* p = b) Assert.Eq((nint)b.Length, write(fd, p, (nuint)b.Length), "write");
    }

    private static (int r, int w) Pipe()
    {
        Assert.True(ClipboardPipe.TryCreate(out int r, out int w), "pipe2");
        return (r, w);
    }

    /// <summary>
    /// The protocol as the clipboard sees it, recorded. <see cref="Remote"/>
    /// plays the client that owns the selection: it gets a duplicate of the
    /// pipe's write end, as libwayland gives the compositor one.
    /// </summary>
    private sealed class FakeWire : IClipboardWire
    {
        public bool Ready { get; set; } = true;
        public readonly List<string> Log = [];
        public readonly List<nint> DestroyedOffers = [], DestroyedSources = [];
        public int Receives, Dispatches;
        public Action<string, int>? Remote; // (mime, fd it must close)
        private nint _next = 0x1000;

        public nint CreateSource(IReadOnlyList<string> mimes)
        {
            var s = _next++;
            Log.Add($"source {s:x} [{string.Join(",", mimes)}]");
            return s;
        }

        public void SetSelection(nint source, uint serial) => Log.Add($"set_selection {source:x} {serial}");
        public void DestroySource(nint source) => DestroyedSources.Add(source);
        public void DestroyOffer(nint offer) => DestroyedOffers.Add(offer);
        public void Accept(nint offer, uint serial, string? mime) => Log.Add($"accept {offer:x} {serial} {mime ?? "null"}");
        public void SetActions(nint offer) => Log.Add($"set_actions {offer:x} copy");
        public void Finish(nint offer) => Log.Add($"finish {offer:x}");
        public void Flush() { }
        public void Dispatch() => Dispatches++;

        public void Receive(nint offer, string mime, int fd)
        {
            Receives++;
            Log.Add($"receive {offer:x} {mime}");
            int mine = dup(fd);
            if (Remote is { } r) r(mime, mine);
            else close(mine);
        }
    }

    private static void Offer(WaylandClipboard c, nint offer, params string[] mimes)
    {
        c.OnDataOffer(offer);
        foreach (var m in mimes) c.OnOfferMime(offer, m);
    }

    private static void WaitSends(WaylandClipboard c)
    {
        var until = Stopwatch.StartNew();
        while (c.SendsInFlight > 0 && until.ElapsedMilliseconds < 5000) Thread.Sleep(5);
        Assert.Eq(0, c.SendsInFlight, "writers finished");
    }

    private static string Describe(WindowEvent e) => e.Kind switch
    {
        WindowEventKind.FileDragOver => $"over {e.X},{e.Y}",
        WindowEventKind.FilesDropped => $"dropped {e.X},{e.Y} [{string.Join("|", e.Paths!)}]",
        WindowEventKind.FileDragLeave => "leave",
        _ => e.Kind.ToString(),
    };

    public static void Register(Harness t)
    {
        // ── URI lists ────────────────────────────────────────────────────────

        t.Run("uri list: file URIs become paths, percent-decoded as UTF-8, in order, each once", () =>
        {
            var list = "# dragged from a file manager\r\n"
                     + "file:///home/u/My%20Pictures/sk%C3%A6rm.png\r\n"
                     + "file://localhost/tmp/a%25b.txt\r\n"
                     + "file://box/srv/c.txt\r\n"            // this machine, by name
                     + "\r\n"
                     + "file:///home/u/My%20Pictures/sk%C3%A6rm.png\r\n" // again
                     + "file:/opt/single-slash\n"             // a bare LF ends a line too
                     + "file:///with%20query?x=1#frag";
            Assert.Eq("/home/u/My Pictures/skærm.png|/tmp/a%b.txt|/srv/c.txt|/opt/single-slash|/with query",
                      string.Join("|", UriList.Paths(Encoding.UTF8.GetBytes(list), hostName: "box")), "paths");
        });

        t.Run("uri list: what is not a local file is left out", () =>
        {
            var list = "https://example.org/x.png\r\n"
                     + "file://elsewhere/home/u/x\r\n"        // another host's file
                     + "file:///bad%00nul\r\n"                // a NUL is no path
                     + "file:///latin1-%E6\r\n"               // not UTF-8: no string names it
                     + "file:relative\r\n"
                     + "FILE:///ok\r\n";                      // the scheme without regard to case
            Assert.Eq("/ok", string.Join("|", UriList.Paths(Encoding.UTF8.GetBytes(list), hostName: "box")), "paths");
        });

        t.Run("uri list: a % without two hex digits stands for itself; a character for its UTF-8", () =>
        {
            Assert.Eq("/a%zz/100%", UriList.PercentDecode("/a%zz/100%"), "bare %");
            Assert.Eq("/ø/✓/😀", UriList.PercentDecode("/%C3%B8/✓/%F0%9F%98%80"), "decoded and literal agree");
            Assert.Eq("/plain", UriList.PercentDecode("/plain"), "nothing to decode");
            Assert.True(UriList.PercentDecode("/%C3") is null, "half a character is not UTF-8");
        });

        // ── Offers and mime types ────────────────────────────────────────────

        t.Run("clipboard offers: the best text mime is pasted, matched without case", () =>
        {
            var o = new SelectionOffers();
            o.Introduced(1);
            o.MimeOffered(1, "text/html");
            o.MimeOffered(1, "text/plain");
            o.MimeOffered(1, "TEXT/PLAIN;CHARSET=UTF-8");
            o.MimeOffered(1, "text/plain"); // repeated: listed once
            Assert.Eq(3, o.MimesOf(1).Count, "mimes");
            Assert.Eq(0, o.Selection(1).Count, "nothing superseded");
            Assert.Eq((nint)1, o.Current, "current");
            Assert.Eq("TEXT/PLAIN;CHARSET=UTF-8", o.PasteMime(), "utf-8 first");

            o.Introduced(2);
            o.MimeOffered(2, "UTF8_STRING");
            o.MimeOffered(2, "text/plain");
            o.Selection(2);
            Assert.Eq("UTF8_STRING", o.PasteMime(), "UTF8_STRING over text/plain");
        });

        t.Run("clipboard offers: a new selection supersedes the old; empty selection pastes nothing", () =>
        {
            var o = new SelectionOffers();
            o.Introduced(1); o.MimeOffered(1, "text/plain"); o.Selection(1);
            o.Introduced(2); o.MimeOffered(2, "image/png");
            Assert.Eq("1", string.Join(",", o.Selection(2)), "the old selection is destroyed");
            Assert.True(o.PasteMime() is null, "no text in an image-only offer");
            Assert.Eq("2", string.Join(",", o.Selection(0)), "an empty selection supersedes too");
            Assert.Eq((nint)0, o.Current, "empty");
            Assert.True(o.PasteMime() is null, "nothing to paste");
            Assert.Eq(0, o.Count, "no offer left alive");
            o.Selection(99);
            Assert.Eq((nint)0, o.Current, "an offer never introduced is not selectable");
        });

        t.Run("clipboard offers: a drag's offer survives a selection and dies on leave", () =>
        {
            var o = new SelectionOffers();
            o.Introduced(5); o.MimeOffered(5, "text/plain");
            Assert.Eq(0, o.Enter(5).Count, "enter");
            o.Introduced(6); o.MimeOffered(6, "text/plain");
            Assert.Eq(0, o.Selection(6).Count, "the drag offer is not superseded by a selection");
            Assert.Eq((nint)6, o.Current, "selection");
            Assert.Eq("5", string.Join(",", o.Leave()), "leave destroys the drag's offer");
            Assert.Eq(1, o.Count, "the selection lives on");
            o.Introduced(7);
            o.Enter(7);
            o.Introduced(8);
            Assert.Eq("7", string.Join(",", o.Enter(8)), "a second enter replaces the first drag");
            Assert.Eq("6,8", string.Join(",", o.Clear().Order()), "teardown returns every live offer");
        });

        t.Run("clipboard offers: a drag carrying files names its uri-list; the drop takes its offer", () =>
        {
            var o = new SelectionOffers();
            o.Introduced(1); o.MimeOffered(1, "text/plain"); o.Enter(1);
            Assert.True(o.DropMime() is null, "a drag of text carries no files");
            o.Introduced(2); o.MimeOffered(2, "x-special/gnome-copied-files"); o.MimeOffered(2, "Text/URI-List");
            Assert.Eq("1", string.Join(",", o.Enter(2)), "the earlier drag's offer goes");
            Assert.Eq("Text/URI-List", o.DropMime(), "the offer's own spelling");
            Assert.Eq((nint)2, o.TakeDrag(), "taken");
            Assert.Eq((nint)0, o.Drag, "no drag after");
            Assert.Eq(0, o.Leave().Count, "so a leave destroys nothing: the taker does");
            Assert.Eq(0, o.Count, "and it is not tracked");
        });

        // ── The pipe ─────────────────────────────────────────────────────────

        t.Run("clipboard pipe: reads to end of file", () =>
        {
            var (r, w) = Pipe();
            Write(w, "hello");
            ClipboardPipe.Close(w);
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromSeconds(1));
            ClipboardPipe.Close(r);
            Assert.Eq("hello", got is null ? null : Encoding.UTF8.GetString(got), "text");
        });

        t.Run("clipboard pipe: partial reads are joined, and the wait dispatches", () =>
        {
            var (r, w) = Pipe();
            var writer = new Thread(() =>
            {
                for (int i = 0; i < 8; i++) { Write(w, $"part{i};"); Thread.Sleep(15); }
                ClipboardPipe.Close(w);
            });
            writer.Start();
            int waits = 0;
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromSeconds(2), whileWaiting: () => waits++);
            writer.Join();
            ClipboardPipe.Close(r);
            Assert.Eq("part0;part1;part2;part3;part4;part5;part6;part7;", Encoding.UTF8.GetString(got!), "joined");
            Assert.True(waits > 0, "dispatched while waiting");
        });

        t.Run("clipboard pipe: a source that never writes times out, bounded", () =>
        {
            var (r, w) = Pipe();
            var sw = Stopwatch.StartNew();
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromMilliseconds(150));
            long ms = sw.ElapsedMilliseconds;
            ClipboardPipe.Close(w);
            ClipboardPipe.Close(r);
            Assert.True(got is null, "null on timeout");
            Assert.True(ms is >= 140 and < 1000, $"timed out after {ms} ms");
        });

        t.Run("clipboard pipe: a partial write then a stall is a timeout, not half the text", () =>
        {
            var (r, w) = Pipe();
            Write(w, "half");
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromMilliseconds(100));
            ClipboardPipe.Close(w);
            ClipboardPipe.Close(r);
            Assert.True(got is null, "null");
        });

        t.Run("clipboard pipe: multi-MiB UTF-8 round trip through the writer and reader", () =>
        {
            var text = new StringBuilder();
            var rng = new Random(13);
            while (text.Length < 4 << 20) text.Append("æøå ✓ line ").Append(rng.Next()).Append('\n');
            var bytes = Encoding.UTF8.GetBytes(text.ToString());
            Assert.True(bytes.Length > 4 << 20, "large");
            var (r, w) = Pipe();
            bool ok = false;
            var writer = new Thread(() => { ok = ClipboardPipe.WriteAll(w, bytes, TimeSpan.FromSeconds(5)); ClipboardPipe.Close(w); });
            writer.Start();
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromSeconds(10));
            writer.Join();
            ClipboardPipe.Close(r);
            Assert.True(ok, "writer finished");
            Assert.True(got is not null && got.AsSpan().SequenceEqual(bytes), "bytes equal");
        });

        t.Run("clipboard pipe: more than the limit is refused", () =>
        {
            var (r, w) = Pipe();
            var writer = new Thread(() => { ClipboardPipe.WriteAll(w, new byte[200_000], TimeSpan.FromSeconds(2)); ClipboardPipe.Close(w); });
            writer.Start();
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromSeconds(2), maxBytes: 100_000);
            ClipboardPipe.Close(r); // the writer sees EPIPE and stops
            writer.Join();
            Assert.True(got is null, "null over the limit");
        });

        t.Run("clipboard pipe: the writer gives up on a closed reader (EPIPE) and on a stalled one", () =>
        {
            var (r, w) = Pipe();
            ClipboardPipe.Close(r);
            var sw = Stopwatch.StartNew();
            Assert.True(!ClipboardPipe.WriteAll(w, new byte[10], TimeSpan.FromSeconds(5)), "EPIPE is a failure");
            Assert.True(sw.ElapsedMilliseconds < 1000, "at once");
            ClipboardPipe.Close(w);

            (r, w) = Pipe();
            sw.Restart();
            // A pipe holds 64 KiB; nobody reads, so the rest of 1 MiB never goes.
            Assert.True(!ClipboardPipe.WriteAll(w, new byte[1 << 20], TimeSpan.FromMilliseconds(100)), "stall is a failure");
            long ms = sw.ElapsedMilliseconds;
            Assert.True(ms is >= 90 and < 2000, $"gave up after {ms} ms");
            ClipboardPipe.Close(w);
            ClipboardPipe.Close(r);
        });

        // ── The clipboard over a fake wire ───────────────────────────────────

        t.Run("clipboard: copy offers the text mimes and sets the selection with the input serial", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 42u);
            c.SetText("hi");
            Assert.Eq("source 1000 [text/plain;charset=utf-8,UTF8_STRING,text/plain] | set_selection 1000 42",
                      string.Join(" | ", wire.Log), "requests");
            Assert.True(c.OwnsSelection, "owns");
        });

        t.Run("clipboard: pasting our own selection returns the text with no receive", () =>
        {
            var wire = new FakeWire { Remote = (_, fd) => throw new Exception("received from ourselves") };
            var c = new WaylandClipboard(wire, () => 1u);
            c.SetText("ünïcode ✓");
            // The compositor announces our own source back to us as an offer.
            Offer(c, 0x50, SelectionOffers.TextMimes);
            c.OnSelection(0x50);
            Assert.Eq("ünïcode ✓", c.GetText(), "own text");
            Assert.Eq(0, wire.Receives, "no receive");
        });

        t.Run("clipboard: a send is served off the loop, in UTF-8, and its fd closed", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 1u);
            c.SetText("grüße");
            var (r, w) = Pipe();
            c.OnSend(0x1000, "text/plain;charset=utf-8", w); // the clipboard owns w now
            var got = ClipboardPipe.ReadAll(r, TimeSpan.FromSeconds(2)); // end of file: the writer closed w
            ClipboardPipe.Close(r);
            WaitSends(c);
            Assert.Eq("grüße", got is null ? null : Encoding.UTF8.GetString(got), "sent");

            (r, w) = Pipe();
            c.OnSend(0x999, "text/plain", w); // a source that is not ours (any more)
            got = ClipboardPipe.ReadAll(r, TimeSpan.FromSeconds(1));
            ClipboardPipe.Close(r);
            Assert.Eq(0, got?.Length ?? -1, "closed at once, nothing written");
        });

        t.Run("clipboard: a reader that never reads stalls only its writer", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 1u) { SendStall = TimeSpan.FromMilliseconds(100) };
            c.SetText(new string('x', 1 << 20));
            var (r, w) = Pipe();
            var sw = Stopwatch.StartNew();
            c.OnSend(0x1000, "text/plain", w);
            Assert.True(sw.ElapsedMilliseconds < 50, "OnSend returns at once");
            WaitSends(c);
            ClipboardPipe.Close(r);
        });

        t.Run("clipboard: cancelled ends our selection, and paste then reads the other client", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 1u);
            c.SetText("ours");
            c.OnCancelled(0x1000);
            Assert.True(!c.OwnsSelection, "no longer ours");
            Assert.Eq("1000", string.Join(",", wire.DestroyedSources.Select(s => s.ToString("x"))), "source destroyed once");

            wire.Remote = (mime, fd) => new Thread(() =>
            {
                Thread.Sleep(40);
                Write(fd, "the");
                Thread.Sleep(20);
                Write(fd, "irs");
                close(fd);
            }).Start();
            Offer(c, 0x60, "text/html", "text/plain", "UTF8_STRING");
            c.OnSelection(0x60);
            Assert.Eq("theirs", c.GetText(), "pasted");
            Assert.Eq("receive 60 UTF8_STRING", wire.Log[^1], "asked for the best text mime");
            Assert.True(wire.Dispatches > 0, "dispatched while waiting");
        });

        t.Run("clipboard: a paste the source never answers is null after the timeout", () =>
        {
            var held = new List<int>();
            var wire = new FakeWire { Remote = (_, fd) => held.Add(fd) };
            var c = new WaylandClipboard(wire, () => null) { PasteTimeout = TimeSpan.FromMilliseconds(120) };
            Offer(c, 0x70, "text/plain");
            c.OnSelection(0x70);
            var sw = Stopwatch.StartNew();
            Assert.True(c.GetText() is null, "null");
            Assert.True(sw.ElapsedMilliseconds is >= 110 and < 1000, $"after {sw.ElapsedMilliseconds} ms");
            foreach (var fd in held) close(fd);
        });

        t.Run("clipboard: no text offered, or nothing selected, pastes null without a receive", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 1u);
            Assert.True(c.GetText() is null, "empty selection");
            Offer(c, 0x80, "image/png");
            c.OnSelection(0x80);
            Assert.True(c.GetText() is null, "image only");
            Assert.Eq(0, wire.Receives, "no receive");
        });

        t.Run("clipboard: without a serial a copy stays in-process until the selection changes", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => null);
            c.SetText("local");
            Assert.Eq(0, wire.Log.Count, "nothing sent to the compositor");
            Assert.Eq("local", c.GetText(), "pasted in-process");
            Offer(c, 0x90, "text/plain");
            wire.Remote = (_, fd) => { Write(fd, "session"); close(fd); };
            c.OnSelection(0x90);
            Assert.Eq("session", c.GetText(), "the session's selection wins once it changes");
        });

        t.Run("clipboard: before a data device, copy is in-process and paste of nothing is null", () =>
        {
            var wire = new FakeWire { Ready = false };
            var c = new WaylandClipboard(wire, () => 5u);
            Assert.True(c.GetText() is null, "null");
            c.SetText("x");
            Assert.Eq("x", c.GetText(), "in-process");
            Assert.Eq(0, wire.Log.Count, "no requests");
        });

        t.Run("clipboard: a second copy destroys the first source; teardown destroys what is left", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 3u);
            c.SetText("a");
            c.SetText("b");
            Assert.Eq("1000", string.Join(",", wire.DestroyedSources.Select(s => s.ToString("x"))), "first source");
            Assert.Eq("b", c.GetText(), "latest");
            Offer(c, 0xa0, "text/plain");
            c.OnSelection(0xa0);
            c.Destroy();
            Assert.Eq("1000,1001", string.Join(",", wire.DestroyedSources.Select(s => s.ToString("x"))), "both sources");
            Assert.Eq("a0", string.Join(",", wire.DestroyedOffers.Select(s => s.ToString("x"))), "the offer");
            Assert.True(c.GetText() is null && !c.OwnsSelection, "nothing left");
        });

        // ── Files dropped, over a fake wire ──────────────────────────────────

        t.Run("drop: a drag carrying files is accepted as a copy and reported as it enters, moves and leaves", () =>
        {
            var wire = new FakeWire();
            var events = new List<WindowEvent>();
            var c = new WaylandClipboard(wire, () => 1u, events.Add);
            Offer(c, 0xc0, "text/uri-list", "text/plain");
            c.OnEnter(0xc0, 77, 10.5f, 20f);
            c.OnMotion(30f, 40.25f);
            c.OnLeave();
            Assert.Eq("set_actions c0 copy | accept c0 77 text/uri-list", string.Join(" | ", wire.Log), "requests");
            Assert.Eq("over 10.5,20 | over 30,40.25 | leave", string.Join(" | ", events.Select(Describe)), "events");
            Assert.Eq("c0", string.Join(",", wire.DestroyedOffers.Select(o => o.ToString("x"))), "the offer goes on leave");
        });

        t.Run("drop: a drag of anything else is refused and says nothing", () =>
        {
            var wire = new FakeWire();
            var events = new List<WindowEvent>();
            var c = new WaylandClipboard(wire, () => 1u, events.Add);
            Offer(c, 0xc1, "text/plain");
            c.OnEnter(0xc1, 5, 1f, 1f);
            c.OnMotion(2f, 2f);
            c.OnDrop();
            c.OnLeave();
            c.ServeDrops();
            Assert.Eq("accept c1 5 null", string.Join(" | ", wire.Log), "refused, no actions offered");
            Assert.Eq(0, events.Count, "no events");
            Assert.Eq(0, wire.Receives, "nothing read");
        });

        t.Run("drop: the list is read after dispatch, its paths reported where the drag was, then finished", () =>
        {
            var wire = new FakeWire
            {
                Remote = (mime, fd) => new Thread(() =>
                {
                    Thread.Sleep(30);
                    Write(fd, "file:///home/u/a%20b.png\r\nhttps://x.org/c\r\nfile:///tmp/d.txt\r\n");
                    close(fd);
                }).Start(),
            };
            var events = new List<WindowEvent>();
            var c = new WaylandClipboard(wire, () => 1u, events.Add);
            Offer(c, 0xd0, "text/uri-list");
            c.OnEnter(0xd0, 9, 5f, 6f);
            c.OnMotion(50f, 60f);
            c.OnDrop();
            c.OnLeave();   // a compositor follows a drop with a leave
            Assert.Eq(0, wire.Receives, "nothing read inside dispatch");
            Assert.True(c.DropsPending, "the drop waits for the host");
            Assert.Eq(0, wire.DestroyedOffers.Count, "and the leave left its offer alone");
            c.ServeDrops();
            Assert.True(!c.DropsPending, "served");
            Assert.Eq("over 5,6 | over 50,60 | dropped 50,60 [/home/u/a b.png|/tmp/d.txt]",
                      string.Join(" | ", events.Select(Describe)), "events: no leave after the drop");
            Assert.Eq("receive d0 text/uri-list | finish d0", string.Join(" | ", wire.Log.Skip(2)), "read, then finished");
            Assert.Eq("d0", string.Join(",", wire.DestroyedOffers.Select(o => o.ToString("x"))), "and destroyed once");
            Assert.True(wire.Dispatches > 0, "the wait dispatched");
        });

        t.Run("drop: a list that never comes, or names no file, ends the drag as a leave", () =>
        {
            var held = new List<int>();
            var wire = new FakeWire { Remote = (_, fd) => held.Add(fd) };
            var events = new List<WindowEvent>();
            var c = new WaylandClipboard(wire, () => 1u, events.Add) { PasteTimeout = TimeSpan.FromMilliseconds(80) };
            Offer(c, 0xe0, "text/uri-list");
            c.OnEnter(0xe0, 1, 1f, 1f);
            c.OnDrop();
            c.ServeDrops();
            foreach (var fd in held) close(fd);
            wire.Remote = (_, fd) => { Write(fd, "https://only.example/web\r\n"); close(fd); };
            Offer(c, 0xe1, "text/uri-list");
            c.OnEnter(0xe1, 2, 3f, 3f);
            c.OnDrop();
            c.ServeDrops();
            Assert.Eq("over 1,1 | leave | over 3,3 | leave", string.Join(" | ", events.Select(Describe)), "a target lit for it goes out");
            Assert.Eq("e0,e1", string.Join(",", wire.DestroyedOffers.Select(o => o.ToString("x"))), "both offers destroyed");
        });

        t.Run("drop: teardown destroys a drop still waiting to be read", () =>
        {
            var wire = new FakeWire();
            var c = new WaylandClipboard(wire, () => 1u);
            Offer(c, 0xf0, "text/uri-list");
            c.OnEnter(0xf0, 1, 0f, 0f);
            c.OnDrop();
            c.Destroy();
            Assert.Eq("f0", string.Join(",", wire.DestroyedOffers.Select(o => o.ToString("x"))), "destroyed");
            Assert.True(!c.DropsPending, "nothing left to serve");
        });

        // ── Live, with no surface and no selection ───────────────────────────

        t.Run("clipboard live probe: bind the data device manager and a seat, make the device, tear down", () =>
        {
            if (!WaylandWindow.IsSupported) throw new SkipException("no Wayland session");
            Probe();
        });
    }

    private static WaylandClipboard? _probe;
    private static int _probeBound;

    [UnmanagedCallersOnly]
    private static void OnGlobal(void* data, WlRegistry registry, uint name, byte* iface, uint version)
    {
        var i = WlUtf8.Read(iface)!;
        if (i is WlDataDeviceManager.InterfaceName or WlSeat.InterfaceName)
        {
            _probe!.TryBind(registry, name, i, version);
            _probeBound++;
        }
    }

    private static void Probe()
    {
        var display = WaylandNative.wl_display_connect(null);
        Assert.True(display != 0, "connect");
        var listener = WlRegistry.Listener.Create(new() { Global = &OnGlobal });
        try
        {
            // Serial null: a copy could not reach the session's selection even
            // if this test made one, which it does not.
            _probe = new WaylandClipboard(() => null, () => WaylandNative.wl_display_dispatch_pending(display),
                                          () => WaylandNative.wl_display_flush(display), _ => { });
            _probeBound = 0;
            var registry = new WlDisplay(display).GetRegistry();
            registry.AddListener(listener);
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "roundtrip");
            if (_probeBound < 2) throw new SkipException("the compositor has no wl_data_device_manager or wl_seat");
            Assert.True(_probe.HasDevice, "wl_data_device made on our own seat proxy");
            // The compositor would send a selection only on keyboard focus, which a client with no surface never has.
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "the compositor accepted the data device");
            _probe.Destroy();
            registry.DestroyProxy();
            Assert.True(WaylandNative.wl_display_roundtrip(display) >= 0, "clean teardown");
        }
        finally
        {
            _probe = null;
            WaylandNative.wl_display_disconnect(display);
            WlRegistry.Listener.Free(listener);
        }
    }
}
