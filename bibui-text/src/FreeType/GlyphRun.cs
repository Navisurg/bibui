namespace Bibui.Text.FreeType;

/// <summary>
/// Shaped text in one strike: glyph ids, per-glyph advances and offsets, and the
/// UTF-16 index each glyph came from. A reusable buffer: shaping into it clears it
/// first, and it grows but never shrinks, so a caller that keeps one per thread
/// shapes without allocating.
///
/// <para>Positions are not stored absolute. Glyph <c>i</c> sits at
/// <c>pen_i + XOffsets[i]</c>, where <c>pen_0 = originX</c> and
/// <c>pen_{i+1} = pen_i + Advances[i]</c> accumulated in float <i>from the origin</i>.
/// The order matters: a run positioned relative to zero and then translated
/// rounds differently and lands glyphs on a different quarter pixel.
/// <see cref="FontStrike.Place"/> and <see cref="GetPositions"/> do it this way.</para>
///
/// <para>Owned by the caller; not thread-safe.</para>
/// </summary>
public sealed class GlyphRun
{
    private uint[] _glyphs = new uint[32];
    private float[] _advances = new float[32];
    private float[] _xOffsets = new float[32];
    private float[] _yOffsets = new float[32];
    private int[] _clusters = new int[32];

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
    /// <summary>The UTF-16 index in the shaped text where each glyph's cluster starts.</summary>
    public ReadOnlySpan<int> Clusters => _clusters.AsSpan(0, Count);

    /// <summary>
    /// Total advance, summed from zero in glyph order: what <c>SKPaint.MeasureText</c>
    /// returns for the same text.
    /// </summary>
    public float Width
    {
        get
        {
            float w = 0f;
            foreach (float a in Advances) w += a;
            return w;
        }
    }

    /// <summary>Glyph positions for a run drawn at (<paramref name="originX"/>, <paramref name="baselineY"/>).</summary>
    public void GetPositions(float originX, float baselineY, Span<float> xs, Span<float> ys)
    {
        float pen = originX;
        for (int i = 0; i < Count; i++)
        {
            xs[i] = pen + _xOffsets[i];
            ys[i] = baselineY + _yOffsets[i];
            pen += _advances[i];
        }
    }

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
        Array.Resize(ref _clusters, cap);
    }

    internal void Add(uint glyph, float advance, float xOffset, float yOffset, int cluster)
    {
        if (Count == _glyphs.Length) Reserve(Count + 1);
        _glyphs[Count] = glyph;
        _advances[Count] = advance;
        _xOffsets[Count] = xOffset;
        _yOffsets[Count] = yOffset;
        _clusters[Count] = cluster;
        Count++;
    }
}
