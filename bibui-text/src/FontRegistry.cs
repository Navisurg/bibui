using System.Buffers.Binary;
using System.Reflection;

namespace Bibui.Text;

/// <summary>
/// Process-wide registry letting a host app supply its own font files for a named
/// family, each file saying its own weight and slant, so a <c>ThemeTokens</c> can
/// reference a brand font family by name and have it resolve to real bytes instead
/// of only a system-installed-font lookup.
///
/// bibui core never ships a client's brand fonts — it only ever falls back to a
/// system font probe / the built-in Inter resource (see <see cref="FontProvider"/>).
/// A host app embeds its own font files and calls
/// <see cref="Register"/>/<see cref="RegisterFromEmbeddedResource"/> once at startup,
/// before mounting any UI.
///
/// What is registered is the font's bytes, not a typeface: a typeface belongs to a
/// raster backend, and each renderer's backend loads its own from these bytes the
/// first time the family is asked for. A file's weight and slant are two fields of
/// its OS/2 table, read without one.
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
    private static readonly Dictionary<string, List<(int Weight, bool Italic, byte[] Bytes)>> Fonts = new();

    /// <summary>
    /// Registers a family's font files in one call: each file's weight and slant are
    /// read from the file itself (OS/2 usWeightClass, and fsSelection's ITALIC or
    /// OBLIQUE bit). TrueType and OpenType files only, with an OS/2 table; a collection
    /// (.ttc) or WOFF/WOFF2 is refused. A file that is not one throws
    /// <see cref="ArgumentException"/>, and registers none of the call's.
    /// </summary>
    public static void Register(string family, params byte[][] fontFiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        ArgumentNullException.ThrowIfNull(fontFiles);
        var faces = new (int Weight, bool Italic, byte[] Bytes)[fontFiles.Length];
        for (int i = 0; i < fontFiles.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(fontFiles[i], nameof(fontFiles));
            var (weight, italic) = WeightAndSlant(fontFiles[i]) ?? throw new ArgumentException(
                $"Font file {i} for '{family}' is not a TrueType or OpenType file, with an OS/2 table.", nameof(fontFiles));
            faces[i] = (weight, italic, fontFiles[i].ToArray());
        }
        lock (Lock) { foreach (var f in faces) Add(family, f.Weight, f.Italic, f.Bytes); }
    }

    // A file registered again at the same weight and slant replaces the one before.
    private static void Add(string family, int weight, bool italic, byte[] bytes)
    {
        if (!Fonts.TryGetValue(family, out var files)) Fonts[family] = files = [];
        int at = files.FindIndex(f => f.Weight == weight && f.Italic == italic);
        if (at >= 0) files[at] = (weight, italic, bytes);
        else files.Add((weight, italic, bytes));
    }

    // usWeightClass and fsSelection's ITALIC (bit 0) or OBLIQUE (bit 9), from the
    // OS/2 table; null for a file that is not TrueType/OpenType or has no OS/2.
    private static (int Weight, bool Italic)? WeightAndSlant(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) is not (0x00010000 or 0x4F54544F or 0x74727565))
            return null;
        int tables = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        for (int i = 0, at = 12; i < tables && at + 16 <= data.Length; i++, at += 16)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(data[at..]) != 0x4F532F32) continue;
            uint os2 = BinaryPrimitives.ReadUInt32BigEndian(data[(at + 8)..]);
            if (os2 + 64L > data.Length) return null;
            int weight = BinaryPrimitives.ReadUInt16BigEndian(data[((int)os2 + 4)..]);
            int selection = BinaryPrimitives.ReadUInt16BigEndian(data[((int)os2 + 62)..]);
            return (weight, (selection & 0x0201) != 0);
        }
        return null;
    }

    /// <summary>
    /// Registers a family's font files embedded in <paramref name="assembly"/>, by
    /// resource name, as <see cref="Register(string, byte[][])"/> does: each file's
    /// weight and slant are read from the file. Throws if a resource is missing.
    /// </summary>
    public static void RegisterFromEmbeddedResource(string family, Assembly assembly, params string[] resourceNames)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(resourceNames);
        var files = new byte[resourceNames.Length][];
        for (int i = 0; i < files.Length; i++)
        {
            using var stream = assembly.GetManifestResourceStream(resourceNames[i])
                ?? throw new ArgumentException($"Embedded resource '{resourceNames[i]}' not found in {assembly.GetName().Name}.", nameof(resourceNames));
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            files[i] = ms.ToArray();
        }
        Register(family, files);
    }

    /// <summary>Whether any file is registered for <paramref name="family"/>.</summary>
    public static bool IsRegistered(string family)
    {
        lock (Lock) { return Fonts.ContainsKey(family); }
    }

    /// <summary>Clears all registered fonts. Test isolation only — not for app use.</summary>
    public static void Clear()
    {
        lock (Lock) { Fonts.Clear(); }
    }

    /// <summary>
    /// The registered bytes of <paramref name="family"/>'s file nearest
    /// <paramref name="weight"/> in the slant asked for (on a tie, the heavier),
    /// else nearest it in the other slant, so a family is never swapped for Inter
    /// to slant it; null for a family not registered. Never mutated once registered.
    /// </summary>
    internal static byte[]? TryResolve(string family, int weight, bool italic)
    {
        lock (Lock)
        {
            if (!Fonts.TryGetValue(family, out var files)) return null;
            byte[]? best = null;
            int bestScore = int.MaxValue;
            foreach (var f in files)
            {
                int score = (f.Italic == italic ? 0 : 1 << 20) + Math.Abs(f.Weight - weight) * 2 + (f.Weight < weight ? 1 : 0);
                if (score < bestScore) (best, bestScore) = (f.Bytes, score);
            }
            return best;
        }
    }
}
