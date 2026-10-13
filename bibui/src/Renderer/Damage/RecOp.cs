using System.Numerics;
using Bibui.Raster;

namespace Bibui.Renderer.Damage;

internal enum OpKind : byte
{
    Clear, Save, Restore, Translate, Scale, SetMatrix, ClipRect, SaveLayer,
    RoundRect, Line, Path, Points, Image, Pixels, BackdropBlur, Text, TextUtf8, GlyphRun, Deferred,
}

/// <summary>One recorded raster call. Fat on purpose: a fixed struct in one array, reused frame to frame.</summary>
internal struct RecOp
{
    public OpKind Kind;
    public bool Draws;          // touches pixels (and is subject to the damage clip)
    public byte Alpha;          // SaveLayer
    public PointMode Mode;      // Points
    public RectF Rect;          // rrect / image dest / clip / backdrop clip
    public float A, B, C, D;    // radii, line ends, text origin, translate, scale, sigma
    public RasterPaint Paint;
    public object? Ref;         // path, image, text, deferred draw
    public RasterFont Font;
    public Rgba Color;
    public int Start, Count, W, H;
    public int Start2;          // GlyphRun: where its positions start in Vecs
    public PixelRect Region;    // backdrop read region
    public Matrix3x2 Matrix;    // SetMatrix
    public int Match;           // SaveLayer: index of its Restore
    public PixelRect Bounds;    // device px every pixel it may touch lies in (SaveLayer: its contents')
    public PixelRect Safe;      // device px a clip may lie wholly inside without cutting an edge of it
    public RectF SafeShape;     // a rounded rect the safe region really is (device px), when SafeRadius > 0
    public float SafeRadius;
    public bool Covers;         // writes every pixel of Safe whatever was under it (an opaque fill, the clear)
}

/// <summary>
/// A box's paint signature. Its own draws are hashed in three parts — before
/// its first child, after its last, and between children (with the children's
/// keys, so a reorder there shows) — because only draws interleaved with
/// children make the children's order matter to the box itself.
/// </summary>
internal struct BoxRec
{
    public ulong Key;
    public ulong Pre, Tail, Inter, Children;
    public bool Interleaved, HasChild;
    public PixelRect Own, Subtree;
    public int Parent, FirstChild, LastChild, NextSibling;
    // Recording scratch.
    public ulong Seg;
    public bool SegHasOps;
    public int ChildCounter;
    public float L, T, R, B;    // own bounds while recording, before rounding
}
