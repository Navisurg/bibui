using System.Runtime.InteropServices;

namespace Bibui.FreeType;

/// <summary>
/// The bytes of one font file, copied once into native memory that never moves.
/// FreeType and HarfBuzz both read faces straight from these bytes without
/// copying them, so they must outlive every face made from them: a
/// <see cref="TextEngine"/> keeps a reference to each one it has opened.
///
/// <para>Immutable, and safe to share between threads and between engines. The
/// memory is released by the finalizer once nothing refers to it any more.</para>
/// </summary>
public sealed unsafe class FontData
{
    private readonly byte* _bytes;

    /// <summary>Length in bytes.</summary>
    public int Length { get; }

    /// <summary>A name for diagnostics, e.g. "Inter-Regular".</summary>
    public string Name { get; }

    internal byte* Pointer => _bytes;

    /// <summary>Copies <paramref name="fontFile"/> (a TrueType/OpenType file) into native memory.</summary>
    public FontData(ReadOnlySpan<byte> fontFile, string name = "")
    {
        if (fontFile.IsEmpty) throw new ArgumentException("Font data is empty.", nameof(fontFile));
        Length = fontFile.Length;
        Name = name;
        _bytes = (byte*)NativeMemory.Alloc((nuint)fontFile.Length);
        fontFile.CopyTo(new Span<byte>(_bytes, fontFile.Length));
        GC.AddMemoryPressure(fontFile.Length);
    }

    ~FontData()
    {
        NativeMemory.Free(_bytes);
        GC.RemoveMemoryPressure(Length);
    }

    /// <summary>Reads a font from an embedded resource of <paramref name="assembly"/>.</summary>
    public static FontData FromEmbeddedResource(System.Reflection.Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new ArgumentException($"Embedded resource '{resourceName}' not found in {assembly.GetName().Name}.", nameof(resourceName));
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return new FontData(bytes, Path.GetFileNameWithoutExtension(resourceName));
    }

    private static FontData? s_interRegular, s_interSemiBold;

    /// <summary>The embedded Inter Regular, the same bytes bibui embeds. Loaded once per process.</summary>
    public static FontData InterRegular => s_interRegular ?? Interlocked.CompareExchange(ref s_interRegular,
        FromEmbeddedResource(typeof(FontData).Assembly, "Bibui.Assets.Fonts.Inter-Regular.ttf"), null) ?? s_interRegular!;

    /// <summary>The embedded Inter SemiBold, which bibui draws every weight from 600 up with.</summary>
    public static FontData InterSemiBold => s_interSemiBold ?? Interlocked.CompareExchange(ref s_interSemiBold,
        FromEmbeddedResource(typeof(FontData).Assembly, "Bibui.Assets.Fonts.Inter-SemiBold.ttf"), null) ?? s_interSemiBold!;

    public override string ToString() => Name.Length > 0 ? Name : $"FontData({Length} bytes)";
}
