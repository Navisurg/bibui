using System.Numerics;

namespace Bibui.Raster;

/// <summary>What a <see cref="DrawOp"/> does. A push is undone by the <see cref="Pop"/> that matches it.</summary>
public enum DrawOpKind : byte
{
    /// <summary>Fills the whole target with <see cref="DrawOp.Color"/>: the frame's first op.</summary>
    Clear,
    /// <summary>Clips to <see cref="DrawOp.Rect"/>.</summary>
    PushClip,
    /// <summary>
    /// Applies <see cref="DrawOp.Matrix"/> before the current transform: current =
    /// Matrix × current. A translation and a scale are matrices like any other; a
    /// renderer may take the cheaper path for them.
    /// </summary>
    PushTransform,
    /// <summary>Draws what follows into a layer composited at <see cref="DrawOp.Alpha"/>.</summary>
    PushOpacity,
    Pop,
    /// <summary><see cref="DrawOp.Rect"/> with corner radii (A, B), in the op's paint: a plain rect at 0.</summary>
    Rect,
    /// <summary>From (A, B) to (C, D).</summary>
    Line,
    /// <summary>A path: <see cref="DrawList.PathAt"/>(Resource) when Resource ≥ 0, else verbs and args from the payload.</summary>
    Path,
    /// <summary><see cref="DrawOp.Count"/> points from <see cref="DrawList.Points"/>, as <see cref="DrawOp.Mode"/> says.</summary>
    Points,
    /// <summary><see cref="DrawOp.Texture"/> scaled into <see cref="DrawOp.Rect"/>.</summary>
    Texture,
    /// <summary>Blurs what is painted in <see cref="DrawOp.Region"/> by A and draws it back clipped to <see cref="DrawOp.Rect"/>.</summary>
    BackdropBlur,
    /// <summary>Glyphs of <see cref="DrawList.FaceAt"/>(Resource) at size C, at their positions from origin (A, B), in <see cref="DrawOp.Color"/>.</summary>
    Glyphs,
}

/// <summary>A paint's gradient, as values: the colours and stops are ranges of <see cref="DrawList.Colors"/> and <see cref="DrawList.Floats"/>.</summary>
public struct Shading
{
    public bool Present;
    public GradientType Type;
    public Vector2 Start, End;
    public float Radius;
    public int ColorStart, ColorCount;
    public int StopStart, StopCount;
}

/// <summary>
/// One draw, in physical pixels with resolved colours. Fixed-size and holding
/// no references, so a frame is one array of these, reused from frame to frame;
/// each op uses the fields its <see cref="Kind"/> names and leaves the rest.
/// What varies in size lives in the <see cref="DrawList"/>'s payload, which an
/// op indexes by range; what is shared or kept is named by index (paths, faces)
/// or handle (textures).
/// </summary>
public struct DrawOp
{
    public DrawOpKind Kind;
    public PointMode Mode;          // Points
    public byte Alpha;              // PushOpacity
    public Rgba Color;              // Clear, Glyphs
    public RectF Rect;              // Rect, PushClip, Texture, BackdropBlur's clip
    public float A, B, C, D;        // see DrawOpKind
    public Matrix3x2 Matrix;        // PushTransform
    public int Start, Count;        // payload: points, glyphs, path verbs
    public int Start2, Count2;      // payload: glyph positions, path args
    public int Resource;            // DrawList.PathAt or FaceAt; -1 for none
    public TextureHandle Texture;   // Texture
    public PixelRect Region;        // BackdropBlur's read region

    // The paint, for the kinds that take one.
    public PaintSpec Paint;
    public float MaskBlurSigma, ImageBlurSigma;
    public bool Dashed;
    public DashPattern Dash;
    public Shading Shading;
}

/// <summary>
/// A frame's draws, in paint order: what the core records after layout and a
/// renderer executes. Reset when a frame starts recording and valid until the
/// next one does. Renderers read it through spans; only the core writes it.
/// Every array only grows, so a frame recorded after the first allocates nothing.
/// </summary>
public sealed class DrawList
{
    private DrawOp[] _ops = new DrawOp[256];
    private int _opCount;
    private Vector2[] _points = new Vector2[256];
    private int _pointCount;
    private PathVerb[] _verbs = new PathVerb[64];
    private int _verbCount;
    private float[] _floats = new float[256];
    private int _floatCount;
    private Rgba[] _colors = new Rgba[16];
    private int _colorCount;
    private ushort[] _glyphs = new ushort[256];
    private int _glyphCount;

    // Shared things an op names by index; cleared per frame, storage kept.
    private readonly List<VectorPath> _paths = [];
    private readonly List<FontFace> _faces = [];
    private readonly Dictionary<int, int> _faceIndex = [];

    public ReadOnlySpan<DrawOp> Ops => _ops.AsSpan(0, _opCount);
    public ReadOnlySpan<Vector2> Points => _points.AsSpan(0, _pointCount);
    public ReadOnlySpan<PathVerb> Verbs => _verbs.AsSpan(0, _verbCount);
    public ReadOnlySpan<float> Floats => _floats.AsSpan(0, _floatCount);
    public ReadOnlySpan<Rgba> Colors => _colors.AsSpan(0, _colorCount);
    public ReadOnlySpan<ushort> Glyphs => _glyphs.AsSpan(0, _glyphCount);

    /// <summary>A kept path an op names: its <see cref="VectorPath.Id"/> is the renderer's cache key.</summary>
    public VectorPath PathAt(int index) => _paths[index];

    /// <summary>The face a glyph op draws.</summary>
    public FontFace FaceAt(int index) => _faces[index];

    // ── Recording, the core's side ───────────────────────────────────────────

    internal void Reset()
    {
        _opCount = _pointCount = _verbCount = _floatCount = _colorCount = _glyphCount = 0;
        _paths.Clear();
        _faces.Clear();
        _faceIndex.Clear();
    }

    internal ref DrawOp Push(DrawOpKind kind)
    {
        if (_opCount == _ops.Length) Array.Resize(ref _ops, _opCount * 2);
        ref var op = ref _ops[_opCount++];
        op = default;
        op.Kind = kind;
        op.Resource = -1;
        return ref op;
    }

    internal int AddPoints(ReadOnlySpan<Vector2> points) => Append(ref _points, ref _pointCount, points);
    internal int AddGlyphs(ReadOnlySpan<ushort> glyphs) => Append(ref _glyphs, ref _glyphCount, glyphs);
    internal int AddVerbs(ReadOnlySpan<PathVerb> verbs) => Append(ref _verbs, ref _verbCount, verbs);
    internal int AddFloats(ReadOnlySpan<float> floats) => Append(ref _floats, ref _floatCount, floats);
    internal int AddColors(ReadOnlySpan<Rgba> colors) => Append(ref _colors, ref _colorCount, colors);

    internal int AddPath(VectorPath path)
    {
        _paths.Add(path);
        return _paths.Count - 1;
    }

    internal int AddFace(FontFace face)
    {
        if (_faceIndex.TryGetValue(face.Id, out int index)) return index;
        _faces.Add(face);
        return _faceIndex[face.Id] = _faces.Count - 1;
    }

    /// <summary>Puts <paramref name="paint"/> on <paramref name="op"/>, its gradient's colours and stops into the payload.</summary>
    internal void SetPaint(ref DrawOp op, in RasterPaint paint)
    {
        op.Paint = paint.Spec;
        op.MaskBlurSigma = paint.MaskBlurSigma;
        op.ImageBlurSigma = paint.ImageBlurSigma;
        if (paint.Dash is { } dash) (op.Dashed, op.Dash) = (true, dash);
        if (paint.Shader is { } s)
        {
            op.Shading = new Shading
            {
                Present = true,
                Type = s.Type,
                Start = s.Start,
                End = s.End,
                Radius = s.Radius,
                ColorStart = AddColors(s.Colors),
                ColorCount = s.Colors.Length,
                StopStart = AddFloats(s.Stops),
                StopCount = s.Stops?.Length ?? 0,
            };
        }
    }

    private static int Append<T>(ref T[] array, ref int count, ReadOnlySpan<T> items)
    {
        if (count + items.Length > array.Length) Array.Resize(ref array, Math.Max(array.Length * 2, count + items.Length));
        items.CopyTo(array.AsSpan(count));
        int at = count;
        count += items.Length;
        return at;
    }
}
