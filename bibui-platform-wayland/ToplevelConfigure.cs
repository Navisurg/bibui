using Bibui.Platform.Wayland.Protocol;

namespace Bibui.Platform.Wayland;

/// <summary>What one configure sequence asks for, once resolved.</summary>
/// <param name="Width">Logical width to draw at.</param>
/// <param name="Height">Logical height to draw at.</param>
/// <param name="Serial">The serial to ack before committing a frame of this size.</param>
/// <param name="States">The toplevel's states (activated, maximized, resizing…).</param>
internal readonly record struct ResolvedConfigure(int Width, int Height, uint Serial, ToplevelStates States);

/// <summary>xdg_toplevel states as flags — the protocol sends them as an array.</summary>
[Flags]
internal enum ToplevelStates
{
    None = 0,
    Maximized = 1 << 0,
    Fullscreen = 1 << 1,
    Resizing = 1 << 2,
    Activated = 1 << 3,
    TiledLeft = 1 << 4,
    TiledRight = 1 << 5,
    TiledTop = 1 << 6,
    TiledBottom = 1 << 7,
    Suspended = 1 << 8,
}

/// <summary>
/// The xdg-shell configure handshake, as state.
///
/// <para>The compositor sends <c>xdg_toplevel.configure</c> (size and states)
/// and then <c>xdg_surface.configure</c> (a serial) to say the sequence is
/// complete. Nothing of it applies until the client acks that serial and
/// commits a buffer drawn for it. Several sequences can arrive before the
/// client gets round to them; only the latest matters, and acking it acks the
/// ones before.</para>
///
/// <para>A width or height of zero means "you choose": the client keeps the
/// size it has (or, the first time, the size it asked for). Whatever is chosen
/// is held to the minimum size the toplevel declared.</para>
///
/// <para>Pure, so the handshake is tested without a compositor.</para>
/// </summary>
internal sealed class ToplevelConfigure
{
    private int _toplevelW, _toplevelH;
    private ToplevelStates _toplevelStates;
    private uint? _serial;

    /// <summary>True once the first sequence has completed — nothing may be attached before.</summary>
    public bool EverConfigured { get; private set; }

    /// <summary>A completed sequence is waiting to be resolved and acked.</summary>
    public bool HasPending => _serial.HasValue;

    /// <summary>The latest bounds the compositor suggested (configure_bounds), 0 when none.</summary>
    public (int Width, int Height) Bounds { get; private set; }

    public void OnToplevelConfigure(int width, int height, ReadOnlySpan<uint> states)
    {
        _toplevelW = Math.Max(0, width);
        _toplevelH = Math.Max(0, height);
        _toplevelStates = ToStates(states);
    }

    public void OnConfigureBounds(int width, int height) => Bounds = (width, height);

    public void OnSurfaceConfigure(uint serial)
    {
        _serial = serial;
        EverConfigured = true;
    }

    /// <summary>
    /// Resolves the pending sequence against the size the client has now and
    /// its minimum, and clears it. Call only when <see cref="HasPending"/>.
    /// </summary>
    public ResolvedConfigure Take(int currentWidth, int currentHeight, int minWidth, int minHeight)
    {
        var serial = _serial ?? throw new InvalidOperationException("no configure is pending");
        _serial = null;
        int w = _toplevelW > 0 ? _toplevelW : currentWidth;
        int h = _toplevelH > 0 ? _toplevelH : currentHeight;
        return new ResolvedConfigure(Math.Max(Math.Max(w, minWidth), 1), Math.Max(Math.Max(h, minHeight), 1), serial, _toplevelStates);
    }

    private static ToplevelStates ToStates(ReadOnlySpan<uint> states)
    {
        var s = ToplevelStates.None;
        foreach (var raw in states)
            s |= (XdgToplevel.State)raw switch
            {
                XdgToplevel.State.Maximized => ToplevelStates.Maximized,
                XdgToplevel.State.Fullscreen => ToplevelStates.Fullscreen,
                XdgToplevel.State.Resizing => ToplevelStates.Resizing,
                XdgToplevel.State.Activated => ToplevelStates.Activated,
                XdgToplevel.State.TiledLeft => ToplevelStates.TiledLeft,
                XdgToplevel.State.TiledRight => ToplevelStates.TiledRight,
                XdgToplevel.State.TiledTop => ToplevelStates.TiledTop,
                XdgToplevel.State.TiledBottom => ToplevelStates.TiledBottom,
                XdgToplevel.State.Suspended => ToplevelStates.Suspended,
                _ => ToplevelStates.None,
            };
        return s;
    }
}
