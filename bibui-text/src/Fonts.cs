using Bibui.Text.FreeType;

namespace Bibui.Text;

/// <summary>
/// Everything about text that is not drawing it: the typefaces, a font's
/// metrics, how wide a text is, its glyphs, and where it breaks into lines at a
/// width. Text is UTF-16 throughout. Layout, the painter and the runtime all ask
/// this one instance, so what is drawn is what was measured: a text's glyphs are
/// walked from per-character tables (<see cref="FontStrike"/>), and a
/// <c>Text</c> layout walked is drawn from that walk.
///
/// <para>It knows nothing about a canvas, and nothing about a raster backend:
/// faces, metrics and measurements come from the FreeType + HarfBuzz engine it
/// owns, so whichever backend draws, layout is the same. Sizes are logical px; a measurement is
/// taken at the physical size, <c>logical × scale</c>, because hinting moves
/// glyph advances with the pixel size. A <see cref="Font"/> carries its scale,
/// so what takes one needs nothing else.</para>
///
/// <para>The runtime hands out the renderer's own instance as
/// <c>runtime.Fonts</c>, and the font a text built at some point would get as
/// <c>runtime.FontInScope()</c>.</para>
/// </summary>
public sealed class Fonts : IDisposable
{
    // The one text engine every configuration measures with, whatever draws:
    // confined to the rendering thread, as Fonts is. Everything below is data;
    // what is done with it is the functions on TextLayer (Fonts/TextLayer.cs).
    internal readonly TextEngine Engine = new();
    internal readonly FontProvider Provider;

    // ── Styles ───────────────────────────────────────────────────────────────

    // A face at a physical size, with what text asks of it. Found by the face's
    // id and the size's bits in one 64-bit key, so a lookup hashes no name.
    // Never evicted: an app sets text in a handful, and their strikes are kept
    // by the engine for its life anyway.
    internal readonly Dictionary<ulong, FontStyle> Styles = new();

    // The style asked for last: a text asks for its style up to five times (its
    // width and line height in layout, once per axis, then its baseline in the
    // painter), and texts next to each other mostly share one, so most asks
    // compare one key and skip the dictionary.
    internal FontStyle? LastStyle;

    // ── This frame's runs ────────────────────────────────────────────────────

    // rad's per-frame run cache, keyed by the Text instead of a hash of its
    // chars: layout walks a text's glyphs and positions into the next free slice
    // of RunGlyphs and RunPoints; the painter copies them out instead of walking
    // again. Each text pushes its runs into Runs, one after another. Emptied when layout
    // starts (TextLayer.RunsBegin), so all of it lives one frame. The arrays grow
    // when a frame needs more room and never shrink, so once the largest frame
    // has been seen they are never allocated again. A RunHandle walked in
    // another frame (its Frame is not RunFrame) names nothing here.
    internal int RunFrame;
    internal ushort[] RunGlyphs = new ushort[4096];
    internal System.Numerics.Vector2[] RunPoints = new System.Numerics.Vector2[4096];   // pen from its run's start: NOTE(positions)
    internal int RunGlyphCount;
    internal TextRun[] Runs = new TextRun[256];
    internal int RunCount;

    // ── Grapheme clusters ────────────────────────────────────────────────────

    // The face a grapheme cluster the text's own face cannot set is set in: the
    // built-in Inter until a platform sets up the system's colour emoji font
    // (UseClusterFont), at start-up. Always a face: with no emoji font, a
    // cluster Inter cannot set is not one this face can either, so it stays in
    // the text's own face as before. Tests set a fixture face here.
    internal FontFace ClusterFace;

    // A cluster's shape, by a 64-bit hash of its chars seeded with the style
    // it is shaped in (TextLayer.ClusterShape): HarfBuzz shapes a cluster once,
    // and every walk after copies it. A fixed table of ClusterCapacity slots,
    // made once: slots are handed out in order (ClusterUsed) until all are,
    // then the least recently used is recycled and its key removed. Clusters[0]
    // is the sentinel of the LRU ring: its NextLru the most recent, its PrevLru
    // the least. ClusterSlots is made at the capacity, so it never grows.
    internal const int ClusterCapacity = 1024;
    internal readonly Dictionary<ulong, int> ClusterSlots = new(ClusterCapacity);
    internal readonly ClusterShape[] Clusters = new ClusterShape[ClusterCapacity + 1];
    internal int ClusterUsed;

    // ── Wraps ────────────────────────────────────────────────────────────────

    // A text's lines at a width (TextLayer.LinesFromText), kept until they go
    // KeepWrapFrames frames unasked; then the node goes on the free list, its
    // lines' array with it, and the next miss fills it, so once the free nodes
    // cover the working set a wrap allocates nothing.
    // NOTE(k): lines handed out are good until the frame ends (WrapsEvict); held
    // past that, they may be some other wrap's.
    internal const ulong KeepWrapFrames = 60;
    internal readonly Dictionary<WrapKey, WrapNode> Wraps = [];
    internal readonly List<WrapKey> StaleWraps = [];   // TextLayer.WrapsEvict's scratch
    internal ulong WrapFrame;
    internal WrapNode? FirstFreeWrap;                  // evicted nodes, their arrays kept, linked by NextFree

    /// <summary>Fonts on a text engine of their own, which the renderer owns with them.</summary>
    internal Fonts()
    {
        Provider = new FontProvider(Engine);
        ClusterFace = TextLayer.FaceBuiltIn(this, 400);
    }

    public void Dispose()
    {
        Engine.Dispose();
    }

    // ── Public: setting up ───────────────────────────────────────────────────

    /// <summary>
    /// Sets the face grapheme clusters this text's own faces cannot set are set
    /// in: the system's colour emoji font, as its bytes. A platform calls it once,
    /// at start-up, with the font however it gets it (on Linux,
    /// <see cref="Fontconfig.UseColorEmoji"/>). Bytes FreeType cannot open leave
    /// the face as it was; with none set, clusters stay in the built-in Inter.
    /// </summary>
    public void UseClusterFont(Bibui.Text.FreeType.FontData font) => TextLayer.ClusterFaceFromData(this, font);

    // ── Public: measuring in a Font ──────────────────────────────────────────

    /// <summary>
    /// The height of one line of text in <paramref name="font"/>, in logical px:
    /// the font's ascent to its descent, with no leading. What layout gives a
    /// one-line text.
    /// </summary>
    public float LineHeight(in Font font) => TextLayer.LineBoxFromFont(this, font);

    /// <summary>
    /// The distance from one line's top to the next's in <paramref name="font"/>,
    /// in logical px: what layout puts between the lines of a wrapped text. At
    /// least <see cref="LineHeight"/>; the difference is the leading.
    /// </summary>
    public float LineAdvance(in Font font) => TextLayer.LineAdvanceFromSize(font.Size);

}

/// <summary>
/// Where a text too wide for its width is broken: between words, or anywhere,
/// for text where words do not matter — a log, a path, a hash.
/// </summary>
public enum TextWrap { Words, Characters }

/// <summary>Vertical extents of a font at a size, in physical px. Ascent is negative (above the baseline).</summary>
public readonly record struct TextMetrics(float Ascent, float Descent, float Leading);

/// <summary>
/// A resolved font: its size in logical px, the face it is set in, and the
/// surface scale it is measured at. Everything <see cref="Fonts"/> needs to
/// measure in it, so nothing else has to be passed alongside.
///
/// <para>A font comes from the theme, never from a family's name:
/// <c>runtime.FontInScope()</c> gives the one a text built there would get. So
/// text is set only in faces the theme provides.</para>
/// </summary>
public readonly record struct Font
{
    internal Font(float size, FontFace face, float scale) => (Size, Face, Scale) = (size, face, scale);

    /// <summary>Its size in logical px.</summary>
    public float Size { get; }

    /// <summary>The face it is set in: the theme's family at the weight asked for, or the built-in Inter.</summary>
    public FontFace Face { get; }

    /// <summary>The surface scale it is measured at: logical px times this is physical px.</summary>
    public float Scale { get; }
}

/// <summary>
/// One face at one physical size, and what text asks of it: the strike that
/// measures and shapes, and its metrics. <see cref="TextLayer.StyleFromFaceSize"/>
/// keeps one per (face, size), under <see cref="Key"/>.
/// </summary>
public sealed class FontStyle
{
    public ulong Key;              // the face's id << 32 | the size's bits: what a cache key under this style holds of it
    public FontFace Face = null!;
    public FontStrike Strike = null!;
    public TextMetrics Metrics;
}

/// <summary>
/// One run of a <c>Text</c>: glyphs in one style, laid from the pen at
/// <see cref="X"/>. Layout's walk pushes a text's runs one after another into
/// <see cref="Fonts.Runs"/>, so a text's runs and their glyphs are each one
/// slice; the text's width is its last run's X + Width.
/// </summary>
internal struct TextRun
{
    public FontStyle Style;
    public int GlyphStart, GlyphCount;   // into Fonts.RunGlyphs / RunPoints
    public float X;                      // the pen at the run's start, physical px from the text's start
    public float Width;                  // how far its glyphs advance the pen, physical px
}

/// <summary>
/// A handle to a text's runs in <see cref="Fonts.Runs"/>: <c>Count</c> records
/// from <c>First</c>, walked in frame <c>Frame</c>, which is the handle's
/// generation. Good while <c>Frame</c> is <see cref="Fonts.RunFrame"/> and it has
/// runs; the zero handle is no runs. Whoever lays a text out keeps its handle
/// with it, as the <c>Text</c> node does.
/// </summary>
public readonly struct RunHandle
{
    internal readonly int First, Count;   // a slice of Fonts.Runs
    internal readonly int Frame;          // the Fonts.RunFrame they were walked in

    internal RunHandle(int first, int count, int frame) => (First, Count, Frame) = (first, count, frame);
}

/// <summary>
/// A grapheme cluster's shape in one style, kept by <see cref="Fonts.Clusters"/>:
/// its glyphs, each placed from the cluster's pen (HarfBuzz's advances before
/// it plus its offset), and how far it moves the pen. The font's ligature makes
/// nearly every emoji one glyph; a shape of more than eight is not kept.
/// </summary>
internal struct ClusterShape
{
    public ulong Hash;              // its key, to remove it from Fonts.ClusterSlots when its slot is recycled
    public int NextLru, PrevLru;    // its neighbours in the LRU ring
    public int GlyphCount;
    public ClusterGlyphs Glyphs;
    public ClusterPoints Points;    // each glyph from the cluster's pen, physical px, y down
    public float Advance;           // the cluster's width at the style's size, physical px
}

[System.Runtime.CompilerServices.InlineArray(8)]
internal struct ClusterGlyphs { private ushort _glyph; }

[System.Runtime.CompilerServices.InlineArray(8)]
internal struct ClusterPoints { private System.Numerics.Vector2 _point; }

/// <summary>
/// One step of measuring a text that is not the fast path's (<see cref="TextLayer.GlyphNext"/>):
/// a grapheme cluster the cluster face sets, or one code point in the text's face.
/// Its advance in physical px, the style it is set in, and its glyphs: one
/// (<see cref="Glyph"/>), or a cluster's, kept in the cluster cache
/// (<see cref="Slot"/>) or, too long to keep, in its engine's scratch run until
/// the next shaping (<see cref="Slot"/> 0).
/// </summary>
public readonly struct GlyphStep
{
    /// <summary>Where the step starts: the char asked about, or before it, when a mark makes one cluster of chars already measured (1️⃣).</summary>
    public readonly int Start;
    /// <summary>Where the next step starts.</summary>
    public readonly int Next;
    /// <summary>How far it moves the pen, physical px.</summary>
    public readonly float Advance;
    /// <summary>The style it is set in: the text's own, or the cluster face's at the same size.</summary>
    public readonly FontStyle Style;
    /// <summary>Whether it is a cluster set in the cluster face.</summary>
    public readonly bool ClusterFace;
    /// <summary>A text-face step's glyph.</summary>
    public readonly ushort Glyph;
    /// <summary>A cluster's slot in the cluster cache; 0 when it was too long to keep.</summary>
    public readonly int Slot;

    internal GlyphStep(int start, int next, float advance, FontStyle style, bool clusterFace, ushort glyph, int slot)
        => (Start, Next, Advance, Style, ClusterFace, Glyph, Slot) = (start, next, advance, style, clusterFace, glyph, slot);
}

/// <summary>
/// A kept wrap: a text's lines at a width, and when they were last asked for. In
/// Fonts.Wraps while kept; on Fonts' free list, by NextFree, once evicted, its
/// array kept for the next miss.
/// </summary>
internal sealed class WrapNode
{
    public TextLine[] Lines = new TextLine[64];   // [0, Count) are the wrap's: grown, never shrunk
    public int Count;
    public ulong Touched;                         // the Fonts.WrapFrame it was last asked for
    public WrapNode? NextFree;                    // the next free node, while this one is free
}

/// <summary>
/// One line of a text broken at a width: the chars [<see cref="Start"/>,
/// <see cref="End"/>) of the text, and where the line after it starts
/// (<see cref="Next"/>), past the space it broke at or the line break. The last
/// line is the one that ends at the text's end; its Next is its End.
/// </summary>
public readonly record struct TextLine(int Start, int End, int Next);
