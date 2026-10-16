using Bibui.Platform.Wayland.Protocol;
using Bibui.Runtime.Platform;

namespace Bibui.Platform.Wayland;

/// <summary>
/// A part of the host that binds globals of its own. The registry offers each
/// global to every feature in turn; whichever binds it owns it, listens on it,
/// and destroys it in <see cref="Destroy"/>. Listeners queue what happened
/// through the host's sink and nothing else — the same rule WindowInput keeps:
/// no runtime call from inside the platform's dispatch.
///
/// <para>This is how seat input, the clipboard and HiDPI
/// (fractional scale, viewporter, cursor shape) arrive: as a feature that
/// binds its globals here, not as changes to the event pump.</para>
/// </summary>
internal interface IWaylandFeature
{
    /// <summary>Binds <paramref name="iface"/> if this feature wants it. True when it did.</summary>
    bool TryBind(WlRegistry registry, uint name, string iface, uint version);

    /// <summary>A global went away (an output unplugged, a seat removed).</summary>
    void OnGlobalRemove(uint name) { }

    /// <summary>Destroys whatever it bound. Must cope with having bound nothing.</summary>
    void Destroy();
}
