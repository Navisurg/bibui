using System.Numerics;

namespace Bibui.Raster.Managed;

// How the canvas strokes and dashes — the dash first, then the
// hairline test or the stroker, then a fill of the outline. The order and the
// decisions are Skia 2.88's (SkDraw::drawPath, SkDraw::drawPoints,
// ComputeResScaleForStroking), reproduced exactly so a stroke's pixels match
// the fixture baselines the Skia backend drew. Kept out of Canvas.cs so the
// core file stays the core's.
public sealed unsafe partial class Canvas
{
    private readonly Path _dashed = new();
    private readonly Path _dashScratch = new();
    private readonly Dasher _dasher = new();
    private readonly List<(Rect Rect, bool AsPath)> _dashRects = [];

    /// <summary>
    /// The larger of the transform's
    /// two axis scales, which sets how finely the stroker and the dasher follow
    /// curves.
    /// </summary>
    internal static float ResScale(in Matrix3x2 m)
    {
        float sx = CurveMath.Length(m.M11, m.M12);
        float sy = CurveMath.Length(m.M21, m.M22);
        if (float.IsFinite(sx) && float.IsFinite(sy))
        {
            float scale = MathF.Max(sx, sy);
            if (scale > 0) return scale;
        }
        return 1;
    }

    /// <summary>The device clip, outset a pixel and mapped back to local coordinates (<c>computeConservativeLocalClipBounds</c>).</summary>
    private Rect? LocalClipBounds()
    {
        if (_s.Clip.IsEmpty || !Matrix3x2.Invert(_s.Matrix, out var inv)) return null;
        var c = _s.Clip;
        return MapBounds(new Rect(c.Left - 1, c.Top - 1, c.Right + 1, c.Bottom + 1), inv);
    }

    private static Rect MapBounds(Rect r, in Matrix3x2 m)
    {
        var a = Vector2.Transform(new Vector2(r.Left, r.Top), m);
        var b = Vector2.Transform(new Vector2(r.Right, r.Top), m);
        var c = Vector2.Transform(new Vector2(r.Right, r.Bottom), m);
        var d = Vector2.Transform(new Vector2(r.Left, r.Bottom), m);
        return new Rect(
            MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
            MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)), MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
    }

    /// <summary>
    /// Strokes <paramref name="path"/>:
    /// dashes it if the paint has a dash, draws it as a hairline if the stroke
    /// is at most a device pixel wide, and otherwise fills the stroker's
    /// outline nonzero.
    /// </summary>
    private void StrokePathCore(Path path, in Paint paint)
    {
        var src = path;
        float coverage = 1;
        bool hair = paint.Style == PaintStyle.Stroke && TreatAsHairline(paint, _s.Matrix, out coverage);

        // Only strokes are dashed (not stroke-and-fill).
        if (paint.Dash is { } dash && paint.Style == PaintStyle.Stroke)
        {
            var dp = paint;
            if (hair) dp.StrokeWidth = 0;
            _dashed.Rewind();
            switch (_dasher.Apply(path, dash, dp, ResScale(_s.Matrix), LocalClipBounds(), _dashed))
            {
                case DashResult.Filled:
                {
                    var fill = paint;
                    fill.Style = PaintStyle.Fill;
                    fill.Dash = null;
                    _dashed.FillRule = FillRule.NonZero;
                    FillPathCore(_dashed, fill, exact: true);
                    return;
                }
                case DashResult.Path:
                    if (_dashed.IsEmpty) return;
                    src = _dashed;
                    break;
            }
        }

        if (hair)
        {
            HairPath(src, paint, coverage);
            return;
        }
        if (!StrokeOutline(src, paint)) return;
        var f = paint;
        f.Style = PaintStyle.Fill;
        f.Dash = null;
        FillPathCore(_stroked, f, exact: true);
    }

    /// <summary>The stroker's outline of <paramref name="src"/> (undashed) into <c>_stroked</c>; false if it is empty.</summary>
    private bool StrokeOutline(Path src, in Paint paint)
    {
        if (Stroker is null)
            throw new NotSupportedException(
                "A stroke wider than one device pixel (or dashed, or with round caps) needs an IPathStroker; Canvas.Stroker is null.");
        _stroked.Rewind();
        var p = paint;
        p.Dash = null;
        Stroker.Stroke(src, p, ResScale(_s.Matrix), _stroked);
        _stroked.FillRule = FillRule.NonZero;
        return !_stroked.IsEmpty;
    }

    /// <summary>A blurred stroke: the outline (dashed first, if dashed), blurred as a fill.</summary>
    private void DrawBlurredStroke(Path path, in Paint paint)
    {
        var src = path;
        if (paint.Dash is { } dash && paint.Style == PaintStyle.Stroke)
        {
            _dashed.Rewind();
            var r = _dasher.Apply(path, dash, paint, ResScale(_s.Matrix), LocalClipBounds(), _dashed);
            if (r == DashResult.Filled)
            {
                var fill = paint;
                fill.Style = PaintStyle.Fill;
                fill.Dash = null;
                DrawBlurred(_dashed, fill);
                return;
            }
            if (r == DashResult.Path) src = _dashed;
        }
        var p = paint;
        if (!(p.StrokeWidth > 0)) p.StrokeWidth = 1f / ResScale(_s.Matrix); // a blurred hairline: one device pixel
        if (!StrokeOutline(src, p)) return;
        var f = paint;
        f.Style = PaintStyle.Fill;
        f.Dash = null;
        DrawBlurred(_stroked, f);
    }

    /// <summary>
    /// A dashed straight line by the rect shortcut: when
    /// <see cref="Dasher.AsRects"/> applies, each dash is a filled rect (a
    /// partial end dash a filled rect path). False when it does not apply and
    /// the line is to be stroked as a path.
    /// </summary>
    private bool DashedLineAsRects(float x0, float y0, float x1, float y1, DashPattern dash, in Paint paint)
    {
        if (paint.MaskBlurSigma > 0) return false;
        var m = _s.Matrix;
        bool rectStaysRect = (m.M12 == 0 && m.M21 == 0) || (m.M11 == 0 && m.M22 == 0);
        if (!rectStaysRect || !Matrix3x2.Invert(m, out var inv)) return false;
        var c = _s.Clip;
        // cull_line outsets the device bounds by the (local) stroke radius, then maps them back.
        float radius = paint.StrokeWidth * 0.5f;
        if (radius == 0) radius = 1;
        if (paint.Join == LineJoin.Miter) radius *= paint.MiterLimit > 0 ? paint.MiterLimit : 4f;
        var cull = MapBounds(new Rect(c.Left - radius, c.Top - radius, c.Right + radius, c.Bottom + radius), inv);
        if (Dasher.AsRects(new Vector2(x0, y0), new Vector2(x1, y1), dash, paint, rectStaysRect, cull, _dashRects) < 0) return false;
        var fill = paint;
        fill.Style = PaintStyle.Fill;
        fill.Dash = null;
        foreach (var (r, asPath) in _dashRects)
        {
            if (asPath)
            {
                _dashScratch.Rewind();
                _dashScratch.AddRect(r);
                FillPathCore(_dashScratch, fill, exact: true);
            }
            else DrawRect(r, fill);
        }
        return true;
    }
}
