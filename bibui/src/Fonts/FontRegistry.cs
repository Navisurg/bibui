using System.Buffers.Binary;
using System.Reflection;

namespace Bibui;

/// <summary>
/// Process-wide registry letting a host app supply its own font bytes for a named
/// (family, weight) pair, so a <see cref="Bibui.Theming.ThemeTokens"/> can reference a
/// brand font family by name and have it resolve to real bytes instead of only a
/// system-installed-font lookup.
///
/// bibui core never ships a client's brand fonts — it only ever falls back to a
/// system font probe / the built-in Inter resource (see <see cref="FontProvider"/>).
/// A host app embeds its own font files and calls
/// <see cref="Register"/>/<see cref="RegisterFromEmbeddedResource"/> once at startup,
/// before mounting any UI.
///
/// What is registered is the font's bytes, not a typeface: a typeface belongs to a
/// raster backend, and each renderer's backend loads its own from these bytes the
/// first time the family is asked for.
///
/// State is static and process-wide (a single process routinely hosts multiple
/// renderer instances, each with its own backend), guarded by a lock
/// mirroring <c>BibuiRuntime</c>'s existing sync-root convention. <see cref="Register"/>
/// is expected to run once, single-threaded, at startup; <see cref="TryResolve"/> runs
/// on the render hot path and may be called from more than one thread.
/// </summary>
public static class FontRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<(string Family, int Weight), byte[]> Fonts = new();

    /// <summary>
    /// Registers raw font bytes for the given (family, weight) pair. Throws
    /// <see cref="ArgumentException"/> when the bytes are not a font file
    /// (TrueType, OpenType, a collection, or WOFF/WOFF2) by their signature.
    /// </summary>
    public static void Register(string family, int weight, byte[] fontData)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        ArgumentNullException.ThrowIfNull(fontData);

        if (!LooksLikeFont(fontData)) throw new ArgumentException(
            $"Font data for '{family}' weight {weight} is not a TrueType/OpenType/WOFF font.", nameof(fontData));

        // A copy, so a caller reusing its buffer cannot change a registered font.
        var copy = fontData.ToArray();
        lock (Lock) { Fonts[(family, weight)] = copy; }
    }

    // The sfnt version tag (or container signature) every font file opens with.
    // Checked here rather than by parsing, because parsing needs a raster
    // backend and registration happens before there is one.
    private static bool LooksLikeFont(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return false;
        return BinaryPrimitives.ReadUInt32BigEndian(data) switch
        {
            0x00010000 => true, // TrueType
            0x4F54544F => true, // 'OTTO' — OpenType with CFF outlines
            0x74727565 => true, // 'true' — Apple TrueType
            0x74797031 => true, // 'typ1'
            0x74746366 => true, // 'ttcf' — collection
            0x774F4646 => true, // 'wOFF'
            0x774F4632 => true, // 'wOF2'
            _ => false,
        };
    }

    /// <summary>
    /// Registers a font family/weight from an embedded resource in the given assembly
    /// (e.g. <c>Assembly.GetExecutingAssembly()</c> in a host app that embeds its own
    /// <c>.ttf</c> files via <c>&lt;EmbeddedResource Include="Assets/Fonts/*.ttf" /&gt;</c>).
    /// </summary>
    public static void RegisterFromEmbeddedResource(string family, int weight, Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArgumentException($"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}.", nameof(resourceName));
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Register(family, weight, ms.ToArray());
    }

    /// <summary>True if bytes have already been registered for this exact (family, weight) pair.</summary>
    public static bool IsRegistered(string family, int weight)
    {
        lock (Lock) { return Fonts.ContainsKey((family, weight)); }
    }

    /// <summary>Clears all registered fonts. Test isolation only — not for app use.</summary>
    public static void Clear()
    {
        lock (Lock) { Fonts.Clear(); }
    }

    /// <summary>The registered bytes for (family, weight), or null. Never mutated once registered.</summary>
    internal static byte[]? TryResolve(string family, int weight)
    {
        lock (Lock) { return Fonts.TryGetValue((family, weight), out var bytes) ? bytes : null; }
    }
}
