namespace Bibui.Text.FreeType;

/// <summary>
/// Shaped text in one strike (<see cref="FontStrike.Shape"/>): glyph ids and
/// per-glyph advances and offsets. A reusable buffer: shaping into it clears it
/// first, and it grows but never shrinks, so a caller that keeps one per thread
/// shapes without allocating. Glyph <c>i</c> sits at <c>pen_i + XOffsets[i]</c>,
/// the pen moving by each glyph's advance.
///
/// <para>Owned by the caller; not thread-safe.</para>
/// </summary>
public sealed class GlyphRun
{
    private uint[] _glyphs = new uint[32];
    private float[] _advances = new float[32];
    private float[] _xOffsets = new float[32];
    private float[] _yOffsets = new float[32];

    /// <summary>The strike the glyph ids and advances belong to.</summary>
    public FontStrike? Strike { get; internal set; }

    public int Count { get; private set; }

    public ReadOnlySpan<uint> Glyphs => _glyphs.AsSpan(0, Count);
    /// <summary>Pen advance after each glyph, physical px.</summary>
    public ReadOnlySpan<float> Advances => _advances.AsSpan(0, Count);
    /// <summary>X offset of each glyph from its pen position, physical px.</summary>
    public ReadOnlySpan<float> XOffsets => _xOffsets.AsSpan(0, Count);
    /// <summary>Y offset of each glyph from the baseline, physical px, y down.</summary>
    public ReadOnlySpan<float> YOffsets => _yOffsets.AsSpan(0, Count);

    public void Clear()
    {
        Count = 0;
        Strike = null;
    }

    internal void Reserve(int n)
    {
        if (_glyphs.Length >= n) return;
        int cap = Math.Max(n, _glyphs.Length * 2);
        Array.Resize(ref _glyphs, cap);
        Array.Resize(ref _advances, cap);
        Array.Resize(ref _xOffsets, cap);
        Array.Resize(ref _yOffsets, cap);
    }

    internal void Add(uint glyph, float advance, float xOffset, float yOffset)
    {
        if (Count == _glyphs.Length) Reserve(Count + 1);
        _glyphs[Count] = glyph;
        _advances[Count] = advance;
        _xOffsets[Count] = xOffset;
        _yOffsets[Count] = yOffset;
        Count++;
    }
}
