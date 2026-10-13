using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bibui.Platform.Wayland.Protocol;

namespace Bibui.Platform.Wayland;

/// <summary>
/// The offers the compositor has introduced on our data device, the mime types
/// each advertises, and which one is the selection now. Pure bookkeeping over
/// proxy handles: what to destroy is returned, never done here.
///
/// <para>The protocol introduces an offer (<c>data_offer</c>), lists its mime
/// types (<c>offer</c>), then names it the selection or a drag's
/// (<c>selection</c>, <c>enter</c>). A new selection supersedes every offer
/// but the drag's; a drag's offer lives until <c>leave</c> or the next
/// <c>enter</c>.</para>
/// </summary>
internal sealed class SelectionOffers
{
    /// <summary>The mime types we paste, best first. Matched without regard to case.</summary>
    public static readonly string[] TextMimes = ["text/plain;charset=utf-8", "UTF8_STRING", "text/plain"];

    private readonly Dictionary<nint, List<string>> _mimes = [];

    /// <summary>The selection's offer, or 0 when the selection is empty.</summary>
    public nint Current { get; private set; }

    /// <summary>The drag-and-drop offer, or 0. Never pasted from.</summary>
    public nint Drag { get; private set; }

    /// <summary>How many offers are alive (introduced and not yet given back to destroy).</summary>
    public int Count => _mimes.Count;

    public void Introduced(nint offer) => _mimes[offer] = [];

    public void MimeOffered(nint offer, string mime)
    {
        if (_mimes.TryGetValue(offer, out var list) && !list.Contains(mime)) list.Add(mime);
    }

    public IReadOnlyList<string> MimesOf(nint offer) => _mimes.TryGetValue(offer, out var l) ? l : [];

    /// <summary>The selection is now <paramref name="offer"/> (0: empty). Returns the offers to destroy.</summary>
    public List<nint> Selection(nint offer)
    {
        var dead = new List<nint>();
        foreach (var o in _mimes.Keys)
            if (o != offer && o != Drag) dead.Add(o);
        foreach (var o in dead) _mimes.Remove(o);
        // An offer never introduced is not ours to read.
        Current = offer != 0 && _mimes.ContainsKey(offer) ? offer : 0;
        return dead;
    }

    /// <summary>A drag entered with <paramref name="offer"/>. Returns the offers to destroy.</summary>
    public List<nint> Enter(nint offer)
    {
        var dead = Leave();
        Drag = offer != 0 && _mimes.ContainsKey(offer) ? offer : 0;
        return dead;
    }

    /// <summary>The drag left. Returns the offers to destroy.</summary>
    public List<nint> Leave()
    {
        var dead = new List<nint>();
        if (Drag != 0 && Drag != Current && _mimes.Remove(Drag)) dead.Add(Drag);
        Drag = 0;
        return dead;
    }

    /// <summary>Every offer still alive, for teardown; forgets them all.</summary>
    public List<nint> Clear()
    {
        var all = _mimes.Keys.ToList();
        _mimes.Clear();
        Current = Drag = 0;
        return all;
    }

    /// <summary>The selection's best text mime type, or null when it offers no text (or there is none).</summary>
    public string? PasteMime()
    {
        if (Current == 0) return null;
        var offered = _mimes[Current];
        foreach (var want in TextMimes)
            foreach (var m in offered)
                if (string.Equals(m, want, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }
}

/// <summary>
/// What <see cref="WaylandClipboard"/> asks of the protocol, by proxy handle.
/// The live one speaks wl_data_device; tests give it a fake, so nothing they
/// do reaches the session's real selection.
/// </summary>
internal interface IClipboardWire
{
    /// <summary>True once a data device exists (a manager and a seat were bound).</summary>
    bool Ready { get; }

    /// <summary>A new wl_data_source offering <paramref name="mimes"/>, listened on.</summary>
    nint CreateSource(IReadOnlyList<string> mimes);

    void SetSelection(nint source, uint serial);
    void DestroySource(nint source);

    /// <summary>wl_data_offer.receive: asks the offer's owner to write <paramref name="mime"/> into <paramref name="fd"/>.</summary>
    void Receive(nint offer, string mime, int fd);

    void DestroyOffer(nint offer);

    /// <summary>Sends what is queued to the compositor.</summary>
    void Flush();

    /// <summary>Dispatches what has arrived, without waiting: a paste's wait keeps answering pings.</summary>
    void Dispatch();
}

/// <summary>
/// The session clipboard, through wl_data_device_manager and
/// wl_data_device, as an <see cref="IWaylandFeature"/>.
///
/// <list type="bullet">
/// <item><b>Copy</b> creates a wl_data_source offering
/// <see cref="SelectionOffers.TextMimes"/> and sets it as the selection with
/// the latest input serial (<see cref="WaylandPlatformHost.LastInputSerial"/>,
/// which the input feature sets). Each <c>send</c> is served on a thread of
/// its own, through a non-blocking fd with a stall bound, so a slow or absent
/// reader never touches the frame loop. The text is encoded once, at copy.</item>
/// <item><b>Paste</b> reads the current selection's offer through a pipe:
/// <c>receive</c>, flush, then read with <see cref="PasteTimeout"/>, dispatching
/// the display between waits. The API is synchronous; this is the bound.</item>
/// <item><b>Our own selection</b>: while our source is the selection (not yet
/// <c>cancelled</c>), paste returns the text we copied without asking the
/// compositor — no round trip through ourselves. <c>cancelled</c> (another
/// client copied) destroys the source and ends that.</item>
/// <item><b>No serial yet</b>: copy cannot set the session's selection without
/// one, so the text stays in-process — pasted inside the app until the
/// session's selection next changes.</item>
/// </list>
///
/// <para>Primary selection (middle-click) is not here; see the follow-up on
/// the board. Drag-and-drop offers are tracked only to be destroyed.</para>
/// </summary>
internal sealed unsafe class WaylandClipboard : IWaylandFeature
{
    /// <summary>How long a paste waits for another client's text.</summary>
    public TimeSpan PasteTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a copy's writer waits for a reader that has taken nothing.</summary>
    public TimeSpan SendStall { get; init; } = TimeSpan.FromSeconds(5);

    private readonly Func<uint?> _serial;
    private readonly IClipboardWire _wire;
    private readonly SelectionOffers _offers = new();

    // Ours: the text we copied, its bytes, and the source that serves it (0
    // when the text never reached the session: no serial, or no device).
    private string? _ownText;
    private byte[]? _ownBytes;
    private nint _ownSource;
    private bool _ownIsLocal;

    /// <summary>Writers still running (for tests: a send finishes on its own thread).</summary>
    private int _sending;
    public int SendsInFlight => Volatile.Read(ref _sending);

    /// <summary>The host's: binds wl_data_device_manager and a wl_seat of its own.</summary>
    public WaylandClipboard(Func<uint?> lastInputSerial, Action dispatch, Action flush)
    {
        _serial = lastInputSerial;
        _wire = new WaylandWire(this, dispatch, flush);
    }

    /// <summary>Tests': over a fake wire.</summary>
    internal WaylandClipboard(IClipboardWire wire, Func<uint?> lastInputSerial)
    {
        _serial = lastInputSerial;
        _wire = wire;
    }

    internal SelectionOffers Offers => _offers;

    /// <summary>True once a data device exists to copy and paste through.</summary>
    public bool HasDevice => _wire.Ready;

    /// <summary>True while the selection is the text we copied.</summary>
    public bool OwnsSelection => _ownText is not null;

    // ── IPlatformHost's side ─────────────────────────────────────────────────

    public string? GetText()
    {
        try
        {
            if (_ownText is not null) return _ownText;
            if (!_wire.Ready) return null;
            return Receive();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"bibui: clipboard read failed: {e.Message}");
            return null;
        }
    }

    public void SetText(string text)
    {
        try
        {
            DropOwn();
            _ownText = text;
            _ownBytes = Encoding.UTF8.GetBytes(text);
            _ownIsLocal = true;
            if (!_wire.Ready || _serial() is not uint serial) return;

            _ownSource = _wire.CreateSource(SelectionOffers.TextMimes);
            _wire.SetSelection(_ownSource, serial);
            _ownIsLocal = false;
            _wire.Flush();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"bibui: clipboard write failed: {e.Message}");
        }
    }

    private string? Receive()
    {
        nint offer = _offers.Current;
        if (_offers.PasteMime() is not { } mime) return null;
        if (!ClipboardPipe.TryCreate(out int readFd, out int writeFd)) return null;
        try
        {
            _wire.Receive(offer, mime, writeFd);
            _wire.Flush();
            // libwayland has sent a duplicate; ours must close, or the read never sees end of file.
            ClipboardPipe.Close(writeFd);
            writeFd = -1;
            var bytes = ClipboardPipe.ReadAll(readFd, PasteTimeout, whileWaiting: _wire.Dispatch);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            ClipboardPipe.Close(writeFd);
            ClipboardPipe.Close(readFd);
        }
    }

    private void DropOwn()
    {
        if (_ownSource != 0) _wire.DestroySource(_ownSource);
        _ownSource = 0;
        _ownText = null;
        _ownBytes = null;
        _ownIsLocal = false;
    }

    // ── Events, by handle (the listeners below, or a test) ───────────────────

    internal void OnDataOffer(nint offer) => _offers.Introduced(offer);

    internal void OnOfferMime(nint offer, string mime) => _offers.MimeOffered(offer, mime);

    internal void OnSelection(nint offer)
    {
        foreach (var dead in _offers.Selection(offer)) _wire.DestroyOffer(dead);
        // Text kept in-process stands until the session's selection changes.
        if (_ownIsLocal) DropOwn();
    }

    internal void OnEnter(nint offer)
    {
        foreach (var dead in _offers.Enter(offer)) _wire.DestroyOffer(dead);
    }

    internal void OnLeave()
    {
        foreach (var dead in _offers.Leave()) _wire.DestroyOffer(dead);
    }

    /// <summary>
    /// A reader wants our text in <paramref name="fd"/>, which is ours to
    /// close. Served from a thread so a reader that is slow, or never reads,
    /// stalls only that thread, and only for <see cref="SendStall"/>.
    /// </summary>
    internal void OnSend(nint source, string mime, int fd)
    {
        if (source != _ownSource || _ownBytes is not { } bytes)
        {
            ClipboardPipe.Close(fd);
            return;
        }
        var stall = SendStall;
        Interlocked.Increment(ref _sending);
        var writer = new Thread(() =>
        {
            try { ClipboardPipe.WriteAll(fd, bytes, stall); }
            finally
            {
                ClipboardPipe.Close(fd);
                Interlocked.Decrement(ref _sending);
            }
        })
        { IsBackground = true, Name = "bibui clipboard send" };
        writer.Start();
    }

    /// <summary>Another client took the selection: our source is done with.</summary>
    internal void OnCancelled(nint source)
    {
        if (source == _ownSource)
        {
            _ownSource = 0; // destroyed just below, not again by DropOwn
            DropOwn();
        }
        _wire.DestroySource(source);
    }

    // ── IWaylandFeature ──────────────────────────────────────────────────────

    public bool TryBind(WlRegistry registry, uint name, string iface, uint version) =>
        _wire is WaylandWire w && w.TryBind(registry, name, iface, version);

    public void OnGlobalRemove(uint name)
    {
        if (_wire is WaylandWire w && w.SeatName == name) Destroy();
    }

    public void Destroy()
    {
        foreach (var o in _offers.Clear()) _wire.DestroyOffer(o);
        if (_ownSource != 0) _wire.DestroySource(_ownSource);
        _ownSource = 0;
        _ownText = null;
        _ownBytes = null;
        _ownIsLocal = false;
        if (_wire is WaylandWire w) w.Destroy();
    }

    // ── The live wire ────────────────────────────────────────────────────────

    /// <summary>
    /// wl_data_device_manager, a wl_seat of the clipboard's own (a second proxy
    /// for the seat input binds is fine), and the data device on them. Its
    /// listeners queue nothing for the runtime: they update the bookkeeping
    /// and serve sends, which is all the protocol needs of them.
    /// </summary>
    private sealed class WaylandWire(WaylandClipboard owner, Action dispatch, Action flush) : IClipboardWire
    {
        private WlDataDeviceManager _manager;
        private WlSeat _seat;
        private WlDataDevice _device;
        private GCHandle _self;

        public uint SeatName { get; private set; }

        public bool Ready => !_device.IsNull;

        public bool TryBind(WlRegistry registry, uint name, string iface, uint version)
        {
            switch (iface)
            {
                case WlDataDeviceManager.InterfaceName when _manager.IsNull:
                    // 3: the drag actions; nothing here needs 4's release.
                    _manager = registry.Bind<WlDataDeviceManager>(name, Math.Min(version, 3u));
                    break;
                case WlSeat.InterfaceName when _seat.IsNull:
                    // Version 1, no listener: only a handle for get_data_device.
                    // Its events (capabilities) are dropped by libwayland.
                    _seat = registry.Bind<WlSeat>(name, 1);
                    SeatName = name;
                    break;
                default:
                    return false;
            }
            if (!_manager.IsNull && !_seat.IsNull && _device.IsNull)
            {
                if (!_self.IsAllocated) _self = GCHandle.Alloc(owner);
                _device = _manager.GetDataDevice(_seat);
                _device.AddListener(DeviceListener, (void*)GCHandle.ToIntPtr(_self));
            }
            // The seat is input's too: claim it only so far as binding our own proxy.
            return iface != WlSeat.InterfaceName;
        }

        public nint CreateSource(IReadOnlyList<string> mimes)
        {
            var source = _manager.CreateDataSource();
            source.AddListener(SourceListener, (void*)GCHandle.ToIntPtr(_self));
            foreach (var m in mimes) source.Offer(m);
            return source.Handle;
        }

        public void SetSelection(nint source, uint serial) => _device.SetSelection(new WlDataSource(source), serial);

        public void DestroySource(nint source) => new WlDataSource(source).Destroy();

        public void Receive(nint offer, string mime, int fd) => new WlDataOffer(offer).Receive(mime, fd);

        public void DestroyOffer(nint offer) => new WlDataOffer(offer).Destroy();

        public void Flush() => flush();

        public void Dispatch() => dispatch();

        public void Destroy()
        {
            if (!_device.IsNull)
            {
                if (_device.Version >= WlDataDevice.ReleaseSince) _device.Release();
                else _device.DestroyProxy();
            }
            if (!_manager.IsNull) _manager.DestroyProxy();
            if (!_seat.IsNull) _seat.DestroyProxy();
            _device = default;
            _manager = default;
            _seat = default;
            SeatName = 0;
            if (_self.IsAllocated) _self.Free();
        }
    }

    // ── Listeners ────────────────────────────────────────────────────────────
    // The data pointer is a GCHandle to the clipboard. A failure here is the
    // clipboard's alone: it is swallowed rather than taking the window down.

    private static WaylandClipboard Of(void* data) => (WaylandClipboard)GCHandle.FromIntPtr((nint)data).Target!;

    private static readonly WlDataDevice.Listener* DeviceListener = WlDataDevice.Listener.Create(new()
    {
        DataOffer = &OnDeviceDataOffer, Enter = &OnDeviceEnter, Leave = &OnDeviceLeave, Selection = &OnDeviceSelection,
    });
    private static readonly WlDataOffer.Listener* OfferListener = WlDataOffer.Listener.Create(new() { Offer = &OnOfferOffer });
    private static readonly WlDataSource.Listener* SourceListener =
        WlDataSource.Listener.Create(new() { Send = &OnSourceSend, Cancelled = &OnSourceCancelled });

    private static void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) { Debug.WriteLine($"bibui: clipboard event failed: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnDeviceDataOffer(void* data, WlDataDevice device, WlDataOffer offer)
    {
        // Listened on at once: its mime types follow straight after.
        offer.AddListener(OfferListener, data);
        var c = Of(data);
        Guard(() => c.OnDataOffer(offer.Handle));
    }

    [UnmanagedCallersOnly]
    private static void OnOfferOffer(void* data, WlDataOffer offer, byte* mime)
    {
        var c = Of(data);
        var m = WlUtf8.Read(mime);
        if (m is not null) Guard(() => c.OnOfferMime(offer.Handle, m));
    }

    [UnmanagedCallersOnly]
    private static void OnDeviceSelection(void* data, WlDataDevice device, WlDataOffer offer)
    {
        var c = Of(data);
        Guard(() => c.OnSelection(offer.Handle));
    }

    [UnmanagedCallersOnly]
    private static void OnDeviceEnter(void* data, WlDataDevice device, uint serial, WlSurface surface, WlFixed x, WlFixed y, WlDataOffer offer)
    {
        var c = Of(data);
        Guard(() => c.OnEnter(offer.Handle));
    }

    [UnmanagedCallersOnly]
    private static void OnDeviceLeave(void* data, WlDataDevice device)
    {
        var c = Of(data);
        Guard(c.OnLeave);
    }

    [UnmanagedCallersOnly]
    private static void OnSourceSend(void* data, WlDataSource source, byte* mime, int fd)
    {
        var c = Of(data);
        var m = WlUtf8.Read(mime) ?? "";
        bool served = false;
        Guard(() => { c.OnSend(source.Handle, m, fd); served = true; });
        if (!served) ClipboardPipe.Close(fd);
    }

    [UnmanagedCallersOnly]
    private static void OnSourceCancelled(void* data, WlDataSource source)
    {
        var c = Of(data);
        Guard(() => c.OnCancelled(source.Handle));
    }
}
