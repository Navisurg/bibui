using System.Numerics;
using System.Runtime.InteropServices;
using Bibui.Text.FreeType.Native;

namespace Bibui.Text.FreeType;

/// <summary>
/// bibui's text on the system FreeType and HarfBuzz: fonts, strikes (a font at a
/// size), measuring, shaping, the glyph coverage cache, and drawing into pixels.
///
/// <para><b>Parity.</b> The fixture baselines are drawn by the Skia backend, so
/// this engine reproduces SkiaSharp 2.88.8's text exactly: measured widths are
/// bit-identical to <c>SKPaint.MeasureText</c> with bibui's text paint, metrics
/// equal <c>GetFontMetrics</c>, and drawn pixels match at SSIM 1.0000, as a
/// parity suite against SkiaSharp checks. That rests on three things:
/// the FreeType load flags (<see cref="FontStrike"/>), linear advances, and the
/// mask gamma (<see cref="MaskGamma"/>).</para>
///
/// <para><b>Threads.</b> An engine, and every <see cref="Font"/>, <see cref="FontStrike"/>
/// and scratch buffer it owns, is confined to one thread at a time: FreeType faces
/// and HarfBuzz buffers are not safe to use concurrently. Use one engine per
/// rendering thread. What may be shared:</para>
/// <list type="bullet">
/// <item><see cref="FontData"/>: immutable, across threads and engines.</item>
/// <item><see cref="GlyphMask"/>: immutable once returned; a rasterizer worker may
/// read masks while the engine's thread goes on rendering others.</item>
/// <item><see cref="MaskGamma"/> and <see cref="GlyphCompositor"/>: static and stateless.</item>
/// <item><see cref="GlyphRun"/>: a mutable buffer the caller owns; one per thread.</item>
/// </list>
/// <para>Each engine has its own FT_Library, so several engines on several threads
/// are independent.</para>
/// </summary>
public sealed class TextEngine : IDisposable
{
    private IntPtr _ftLibrary;
    private readonly Dictionary<FontData, Font> _fonts = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(Font Font, int SizeBits), FontStrike> _strikes = new();
    private readonly Dictionary<(string Family, int Weight), Font> _registered = new();
    private readonly Dictionary<ulong, List<FontData>> _loadedBytes = new();
    private PlacedGlyph[] _placed = new PlacedGlyph[64];
    private bool _disposed;

    internal IntPtr HbBuffer { get; }
    internal GlyphRun ScratchRun { get; } = new();

    /// <summary>"freetype 2.14.3, harfbuzz 14.4.0": the versions this process loaded.</summary>
    public string NativeVersions { get; }

    /// <summary>The built-in face for weights below 600: embedded Inter Regular.</summary>
    public Font DefaultRegular { get; }

    /// <summary>The built-in face for weights 600 and up: embedded Inter SemiBold.</summary>
    public Font DefaultSemiBold { get; }

    public TextEngine()
    {
        FT.Check(FT.FT_Init_FreeType(out _ftLibrary), "FT_Init_FreeType");
        FT.FT_Library_Version(_ftLibrary, out int maj, out int min, out int pat);
        NativeVersions = $"freetype {maj}.{min}.{pat}, harfbuzz {Marshal.PtrToStringUTF8(HB.hb_version_string())}";
        HbBuffer = HB.hb_buffer_create();
        DefaultRegular = Open(FontData.InterRegular, "", 400);
        DefaultSemiBold = Open(FontData.InterSemiBold, "", 600);
    }

    // ── Fonts ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The font for <paramref name="data"/>, opened once per engine: the same
    /// <see cref="FontData"/> always gives the same <see cref="Font"/> instance, so
    /// fonts can be compared by reference (bibui splits text into runs that way).
    /// </summary>
    public Font Load(FontData data) => Open(data, "", 400);

    /// <summary>
    /// The font for a font file's bytes, opened once per engine: identical bytes
    /// give the same <see cref="Font"/> instance, so a caller that is handed bytes
    /// every time (bibui's FontRegistry) still gets one face per file. Null when
    /// FreeType cannot parse them.
    /// </summary>
    public Font? Load(ReadOnlySpan<byte> fontFile)
    {
        if (fontFile.IsEmpty) return null;
        ulong key = (ulong)fontFile.Length << 32 ^ (ulong)(uint)Hash(fontFile);
        if (_loadedBytes.TryGetValue(key, out var candidates))
            foreach (var d in candidates)
                if (Same(d, fontFile)) return _fonts[d];
        FontData data = new(fontFile);
        Font font;
        try { font = Load(data); }
        catch (InvalidOperationException) { return null; }
        (candidates ??= _loadedBytes[key] = []).Add(data);
        return font;
    }

    /// <summary>Makes <paramref name="data"/> the face for (<paramref name="family"/>, <paramref name="weight"/>).</summary>
    public Font Register(string family, int weight, FontData data)
    {
        ArgumentException.ThrowIfNullOrEmpty(family);
        var font = Open(data, family, weight);
        _registered[(family, weight)] = font;
        return font;
    }

    /// <summary>
    /// The font for a family and weight, as bibui's FontProvider resolves its
    /// default path: an exact registration, else the built-in Inter, Regular below
    /// 600 and SemiBold from 600 up. Looking up installed families is fontconfig's
    /// job and is not done yet.
    /// </summary>
    public Font Resolve(string? family, int weight)
    {
        if (family is not null && _registered.TryGetValue((family, weight), out var f)) return f;
        return weight >= 600 ? DefaultSemiBold : DefaultRegular;
    }

    private Font Open(FontData data, string family, int weight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_fonts.TryGetValue(data, out var font)) return font;
        font = new Font(this, data, family, weight);
        _fonts[data] = font;
        return font;
    }

    // Faces are closed together, by FT_Done_FreeType in Dispose.
    internal unsafe IntPtr OpenFace(FontData data)
    {
        FT.Check(FT.FT_New_Memory_Face(_ftLibrary, data.Pointer, data.Length, 0, out IntPtr face), "FT_New_Memory_Face");
        return face;
    }

    // ── Strikes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="font"/> at <paramref name="sizePx"/> physical px per em,
    /// made once and kept for the engine's life (with its mask cache).
    /// </summary>
    public FontStrike Strike(Font font, float sizePx)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (font.Engine != this) throw new ArgumentException("The font belongs to another engine.", nameof(font));
        var key = (font, BitConverter.SingleToInt32Bits(sizePx));
        if (!_strikes.TryGetValue(key, out var strike))
            _strikes[key] = strike = new FontStrike(font, sizePx);
        return strike;
    }

    /// <summary>The strike for a family, weight and physical size; see <see cref="Resolve"/>.</summary>
    public FontStrike Strike(string? family, int weight, float sizePx) => Strike(Resolve(family, weight), sizePx);

    /// <summary>Every strike made so far.</summary>
    public IReadOnlyCollection<FontStrike> Strikes => _strikes.Values;

    /// <summary>Drops every cached glyph mask in every strike. Advances and metrics stay.</summary>
    public void ClearMasks()
    {
        foreach (var s in _strikes.Values) s.ClearMasks();
    }

    // ── Measure, metrics ─────────────────────────────────────────────────────

    /// <summary>Ascent, descent and leading in physical px.</summary>
    public FontMetrics Metrics(FontStrike strike) => strike.Metrics;

    // ── Draw ─────────────────────────────────────────────────────────────────

    // ── Lifetime ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var s in _strikes.Values) s.Destroy();
        foreach (var f in _fonts.Values) f.Destroy();
        _strikes.Clear();
        _fonts.Clear();
        HB.hb_buffer_destroy(HbBuffer);
        FT.FT_Done_FreeType(_ftLibrary);   // closes every face the engine opened
        _ftLibrary = 0;
    }

    private static int Hash(ReadOnlySpan<byte> data)
    {
        // Content hash for interning: length plus a sample, cheap even for a large font.
        var h = new HashCode();
        h.AddBytes(data.Length <= 4096 ? data : data[..2048]);
        if (data.Length > 4096) h.AddBytes(data[^2048..]);
        return h.ToHashCode();
    }

    private static unsafe bool Same(FontData d, ReadOnlySpan<byte> bytes)
        => d.Length == bytes.Length && new ReadOnlySpan<byte>(d.Pointer, d.Length).SequenceEqual(bytes);
}
