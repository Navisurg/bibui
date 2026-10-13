using System.Numerics;
using System.Runtime.CompilerServices;

namespace Bibui.Raster.Managed;

// Blurred shapes (shadows) and backdrop blur. Kept apart from Canvas.cs,
// which only calls in here.
//
// These reproduce Skia 2.88's raster shadows exactly: the nine-patch shortcut
// for blurred rects and rrects (draw_nine), the mask bounds and the no-blur
// case of SkMaskFilterBase::filterPath, the A8 mask blends
// (blit_mask_d32_a8, blit_mask_d32_a8_general) and backdrop blur as a surface
// snapshot drawn back through a blur image filter. The fixture baselines are
// drawn by the Skia backend, so the managed backend matches its pixels bit for
// bit to be verified against them.
public sealed unsafe partial class Canvas
{
    private uint[] _snapshot = [];
    private uint[] _blurred = [];

    // ── Blurred shapes ─────────────────────────────────────────────────────

    /// <summary>
    /// A blurred rect: with <see cref="GaussianMaskBlur"/>, an
    /// analytic blur (<see cref="RectBlur"/>) of a small rect stretched as a
    /// nine-patch, cached; otherwise, or when the rect is too small to stretch,
    /// the blurred mask of the whole shape.
    /// </summary>
    private void DrawBlurredRect(Rect rect, in Paint paint)
    {
        if (paint.Style == PaintStyle.Fill && paint.Shader is null && MaskBlur is GaussianMaskBlur blur
            && IsAxisAligned(_s.Matrix) && TryRectNine(blur, MapRect(rect), paint))
            return;
        if (Unblurred(paint, out var plain)) { DrawRect(rect, plain); return; }
        _scratch.Rewind();
        _scratch.AddRect(rect);
        DrawBlurred(_scratch, paint);
    }

    /// <summary>
    /// A blurred rrect: the smallest rrect with the same
    /// corners and room to stretch, box-blurred once (cached) and drawn as a
    /// nine-patch; the whole shape when it is too small for that.
    /// </summary>
    private void DrawBlurredRRect(in RRect rr, in Paint paint)
    {
        if (paint.Style == PaintStyle.Fill && paint.Shader is null && MaskBlur is GaussianMaskBlur blur && IsAxisAligned(_s.Matrix))
        {
            var d = MapRect(rr.Rect);
            float rx = rr.Rx * MathF.Abs(_s.Matrix.M11), ry = rr.Ry * MathF.Abs(_s.Matrix.M22);
            if (TryRRectNine(blur, d, rx, ry, paint)) return;
        }
        if (Unblurred(paint, out var plain)) { DrawRRect(rr, plain); return; }
        _scratch.Rewind();
        _scratch.AddRRect(rr);
        DrawBlurred(_scratch, paint);
    }

    /// <summary>A fill whose sigma is too small to blur (<see cref="GaussianMaskBlur.HasNoBlur"/>) is drawn as the plain shape.</summary>
    private bool Unblurred(in Paint paint, out Paint plain)
    {
        plain = paint;
        plain.MaskBlurSigma = 0;
        return paint.Style == PaintStyle.Fill && MaskBlur is { } mb && mb.Margin(paint.MaskBlurSigma) <= 0;
    }

    /// <summary>
    /// <c>SkBlurMaskFilterImpl::filterRectsToNine</c>: the patch is the
    /// analytic blur of the rect shortened to just past its two blurred edges,
    /// keeping its fractional phase.
    /// </summary>
    private bool TryRectNine(GaussianMaskBlur blur, Rect d, in Paint paint)
    {
        float sigma = paint.MaskBlurSigma;
        if (RectBlur.ProfileSize(sigma) <= 0 || !(d.Width > 0 && d.Height > 0)) return false;
        var outer = RectBlur.Bounds(d, sigma);
        var src = d.RoundOut();
        int smallW = outer.Width - src.Width + 2, smallH = outer.Height - src.Height + 2;
        int cx = smallW, cy = smallH;
        smallW++;
        smallH++;
        int dx = src.Width - smallW, dy = src.Height - smallH;
        if (dx < 0 || dy < 0) return false;
        var small = new Rect(d.Left, d.Top, d.Right - dx, d.Bottom - dy);
        if (small.Width < 2 || small.Height < 2) return false;

        var pb = RectBlur.Bounds(small, sigma);
        var key = new ShadowKey(0, sigma, pb.Width, pb.Height, cx, cy, 0);
        if (!blur.Cache.TryGet(key, out var patch))
        {
            var mask = RectBlur.Mask(small, sigma, out var b);
            patch = new NinePatch(mask, b.Width, b.Height, cx, cy);
            blur.Cache.Add(key, patch);
        }
        DrawNine(patch, outer, paint);
        return true;
    }

    /// <summary><c>SkBlurMaskFilterImpl::filterRRectToNine</c>.</summary>
    private bool TryRRectNine(GaussianMaskBlur blur, Rect d, float rx, float ry, in Paint paint)
    {
        float sigma = paint.MaskBlurSigma;
        if (GaussianMaskBlur.HasNoBlur(sigma)) return false;
        int m = blur.Margin(sigma);
        float left = rx + 2 * m, top = ry + 2 * m;
        // Three spare columns: fractional space either side plus one to stretch.
        float tsw = left + left + 3, tsh = top + top + 3;
        if (tsw >= d.Width || tsh >= d.Height) return false;
        int cx = (int)MathF.Ceiling(left) + 1, cy = (int)MathF.Ceiling(top) + 1;

        var key = new ShadowKey(1, sigma, tsw, tsh, rx, ry, (byte)Antialiasing);
        if (!blur.Cache.TryGet(key, out var patch))
        {
            // The small rrect at the origin, antialiased into an A8 mask as
            // the canvas fills it, then blurred in a mask outset by the margin.
            var srcBounds = new IRect(0, 0, (int)MathF.Ceiling(tsw), (int)MathF.Ceiling(tsh));
            var mb = new IRect(-m, -m, srcBounds.Right + m, srcBounds.Bottom + m);
            var cm = RentMask(mb);
            RRectCoverage(cm, new Rect(0, 0, tsw, tsh), rx, ry, srcBounds);
            blur.Blur(cm, sigma);
            patch = new NinePatch(cm.Data.ToArray(), mb.Width, mb.Height, cx, cy);
            _maskPool.Add(cm);
            blur.Cache.Add(key, patch);
        }
        var r = d.RoundOut();
        DrawNine(patch, new IRect(r.Left - m, r.Top - m, r.Right + m, r.Bottom + m), paint);
        return true;
    }

    /// <summary>An rrect's coverage written into a mask, scan-converted as <see cref="FillRRectFast"/> does.</summary>
    private void RRectCoverage(CoverageMask mask, Rect d, float rx, float ry, IRect clip)
    {
        var saved = _s.Clip;
        _s.Clip = clip;
        bool compat = Antialiasing == Antialiasing.BaselineCompatible;
        bool ssaa = compat && !UsesAnalyticRRect(d, rx, ry);
        _raster.Reset(clip, snapY: compat && !ssaa);
        AddRRectEdges(d, rx, ry, reverse: false);
        var op = RasterOp.Plain(FillRule.NonZero, ssaa ? RasterMode.SupersampleSimple : RasterMode.Simple);
        op.MidStart = (int)MathF.Ceiling(d.Top + ry);
        op.MidEnd = (int)MathF.Floor(d.Bottom - ry);
        op.SetSpan(d.Left, d.Right);
        var sink = new MaskSink { Origin = mask.Origin, Stride = mask.Stride };
        op.Run(this, ref sink);
        _s.Clip = saved;
    }

    /// <summary>
    /// A shape's coverage blurred and painted: the mask covers the path's bounds outset by ½ px and
    /// then by the blur's margin, trimmed to the clip plus at most 128 px of
    /// margin. A sigma with no margin draws the shape unblurred.
    /// </summary>
    private void DrawBlurred(Path path, in Paint paint)
    {
        if (MaskBlur is null)
            throw new NotSupportedException("A blurred draw (MaskBlurSigma) needs an IMaskBlur.");
        float sigma = paint.MaskBlurSigma;
        int margin = MaskBlur.Margin(sigma);
        var plain = paint;
        plain.MaskBlurSigma = 0;
        if (paint.Style != PaintStyle.Fill)
        {
            // The stroke's outline, which comes back here as a fill.
            DrawBlurredStroke(path, paint);
            return;
        }
        if (margin <= 0)
        {
            FillPathCore(path, plain, exact: true);
            return;
        }
        var dev = DeviceBounds(path);
        if (dev.IsEmpty) return;
        var bounds = dev.Outset(0.5f, 0.5f).RoundOut();
        int slop = Math.Min(margin, 128);
        var c = _s.Clip;
        bounds = bounds.Intersect(new IRect(c.Left - slop, c.Top - slop, c.Right + slop, c.Bottom + slop));
        if (bounds.IsEmpty) return;
        var mask = RentMask(new IRect(bounds.Left - margin, bounds.Top - margin, bounds.Right + margin, bounds.Bottom + margin));
        var sink = new MaskSink { Origin = mask.Origin, Stride = mask.Stride };
        var mode = LoadPath(path, bounds, exact: true);
        if (!_raster.IsEmpty)
        {
            if (mode == RasterMode.Supersample) _raster.FillSupersampled(ref sink, path.FillRule);
            else _raster.Fill(ref sink, path.FillRule, exact: mode == RasterMode.Exact);
        }
        MaskBlur.Blur(mask, sigma);
        if (paint.Shader is null)
        {
            var b = mask.Bounds.Intersect(_s.Clip);
            if (!b.IsEmpty)
            {
                _s.Layer?.Touch(b);
                BlitA8(mask.Data, 0, mask.Stride, mask.Bounds, b, paint.Color);
            }
        }
        else DrawMask(mask, plain);
        _maskPool.Add(mask);
    }

    /// <summary>
    /// A nine-patch drawn: the patch's corners at the corners of
    /// <paramref name="outer"/>, its centre row and column stretched along the
    /// sides, the middle filled. Corners and sides blend as A8 masks
    /// (<see cref="BlitA8"/>), the top and bottom as antialiased spans, the
    /// middle as a rect.
    /// </summary>
    private void DrawNine(NinePatch p, IRect outer, in Paint paint)
    {
        var clip = outer.Intersect(_s.Clip);
        if (clip.IsEmpty) return;
        uint pm = PmFor(paint.Color);
        if (pm == 0) return;
        _s.Layer?.Touch(clip);
        int w = p.Width, h = p.Height, cx = p.CenterX, cy = p.CenterY;
        var m = p.Mask;
        int rw = w - cx - 1, bh = h - cy - 1;

        BlitA8(m, 0, w, new IRect(outer.Left, outer.Top, outer.Left + cx, outer.Top + cy), clip, paint.Color);
        BlitA8(m, cx + 1, w, new IRect(outer.Right - rw, outer.Top, outer.Right, outer.Top + cy), clip, paint.Color);
        BlitA8(m, (cy + 1) * w, w, new IRect(outer.Left, outer.Bottom - bh, outer.Left + cx, outer.Bottom), clip, paint.Color);
        BlitA8(m, (cy + 1) * w + cx + 1, w, new IRect(outer.Right - rw, outer.Bottom - bh, outer.Right, outer.Bottom), clip, paint.Color);

        var inner = new IRect(outer.Left + cx, outer.Top + cy, outer.Right + (cx + 1 - w), outer.Bottom + (cy + 1 - h));
        var t = Target();
        var mask = _s.Mask;
        var sink = new SolidSink
        {
            Origin = t.Origin, Stride = t.Stride, Pm = pm,
            Mask = mask is null ? null : mask.Origin, MaskStride = mask?.Stride ?? 0,
        };
        var mid = inner.Intersect(clip);
        for (int y = mid.Top; y < mid.Bottom; y++) sink.Blit(y, mid.Left, mid.Width, 255);

        var top = new IRect(inner.Left, outer.Top, inner.Right, inner.Top).Intersect(clip);
        for (int y = top.Top; y < top.Bottom; y++)
        {
            byte a = m[(y - outer.Top) * w + cx];
            if (a != 0) sink.Blit(y, top.Left, top.Width, a);
        }
        var bottom = new IRect(inner.Left, inner.Bottom, inner.Right, outer.Bottom).Intersect(clip);
        for (int y = bottom.Top; y < bottom.Bottom; y++)
        {
            byte a = m[(h - (outer.Bottom - y)) * w + cx];
            if (a != 0) sink.Blit(y, bottom.Left, bottom.Width, a);
        }
        // The sides repeat one row of the patch all the way down.
        var leftSide = new IRect(outer.Left, inner.Top, inner.Left, inner.Bottom);
        BlitA8(m, cy * w, 0, leftSide, clip, paint.Color);
        var rightSide = new IRect(inner.Right, inner.Top, outer.Right, inner.Bottom);
        BlitA8(m, cy * w + (w - outer.Right + inner.Right), 0, rightSide, clip, paint.Color);
    }

    /// <summary>
    /// An A8 coverage block in a solid colour, blended through the clip.
    /// <paramref name="origin"/> indexes the coverage of device pixel
    /// (<paramref name="dev"/>.Left, <paramref name="dev"/>.Top); a
    /// <paramref name="stride"/> of 0 repeats one row.
    /// </summary>
    private void BlitA8(ReadOnlySpan<byte> cov, int origin, int stride, IRect dev, IRect clip, uint argb)
    {
        var r = dev.Intersect(clip);
        if (r.IsEmpty) return;
        uint pm = PmFor(argb);
        var t = Target();
        var mask = _s.Mask;
        fixed (byte* c = cov)
        {
            for (int y = r.Top; y < r.Bottom; y++)
            {
                byte* src = c + origin + (y - dev.Top) * stride + (r.Left - dev.Left);
                uint* d = t.Origin + (long)y * t.Stride + r.Left;
                byte* mrow = mask is null ? null : mask.Origin + (long)y * mask.Stride + r.Left;
                BlendA8Row(d, src, mrow, r.Width, pm);
            }
        }
    }

    /// <summary>
    /// One row of <see cref="BlendA8"/>, a vector of pixels at a time where it
    /// can be. Coverage 0 leaves a pixel as it is (the blend is the identity
    /// there), so runs of empty coverage are skipped, not special-cased.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void BlendA8Row(uint* d, byte* src, byte* m, int len, uint pm)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int nb = Vector<byte>.Count, q = Vector<uint>.Count;
            var rbm = new Vector<uint>(0x00FF00FFu);
            var agm = new Vector<uint>(0xFF00FF00u);
            var prb = new Vector<uint>(pm & 0x00FF00FFu);
            var pag = new Vector<uint>((pm >> 8) & 0x00FF00FFu);
            var k256 = new Vector<uint>(256u);
            var r128 = new Vector<uint>(128u);
            for (; i <= len - nb; i += nb)
            {
                var kb = Vector.Load(src + i);
                if (kb == Vector<byte>.Zero) continue;
                Vector.Widen(kb, out var kl, out var kh);
                Vector.Widen(kl, out var k0, out var k1);
                Vector.Widen(kh, out var k2, out var k3);
                if (m != null)
                {
                    Vector.Widen(Vector.Load(m + i), out var ml, out var mh);
                    Vector.Widen(ml, out var m0, out var m1);
                    Vector.Widen(mh, out var m2, out var m3);
                    k0 = Div255(k0 * m0 + r128); k1 = Div255(k1 * m1 + r128);
                    k2 = Div255(k2 * m2 + r128); k3 = Div255(k3 * m3 + r128);
                }
                Blend(d + i, k0); Blend(d + i + q, k1); Blend(d + i + 2 * q, k2); Blend(d + i + 3 * q, k3);
            }

            void Blend(uint* p, Vector<uint> k)
            {
                var scale = k + Vector<uint>.One;
                var left = (Vector.ShiftRightLogical(prb * scale, 8) & rbm) | (pag * scale & agm);
                var inv = k256 - Vector.ShiftRightLogical(left, 24);
                var dv = Vector.Load(p);
                var dd = (Vector.ShiftRightLogical((dv & rbm) * inv, 8) & rbm) | ((Vector.ShiftRightLogical(dv, 8) & rbm) * inv & agm);
                (left + dd).Store(p);
            }

            static Vector<uint> Div255(Vector<uint> t) => Vector.ShiftRightLogical(t + Vector.ShiftRightLogical(t, 8), 8);
        }
        for (; i < len; i++)
        {
            uint k = src[i];
            if (k == 0) continue;
            if (m != null)
            {
                uint mv = m[i];
                if (mv != 255) k = PixelMath.MulDiv255Round(k, mv);
                if (k == 0) continue;
            }
            d[i] = BlendA8(pm, d[i], k);
        }
    }

    /// <summary>
    /// The A8 mask blend shadows get whatever the colour — opaque, black or
    /// translucent: s·aa + d·(1 − sa·aa),
    /// each product approximated as (x·(y + 1)) >> 8.
    /// </summary>
    private static uint BlendA8(uint s, uint d, uint k)
    {
        uint left = PixelMath.MulQ(s, k + 1);
        return left + PixelMath.MulQ(d, 256 - (left >> 24));
    }

    /// <summary>SkPMSrcOver across a row — src + dst·(256 − src.a)/256 per channel, truncating — a vector at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void SrcOverRow(uint* d, ReadOnlySpan<uint> src)
    {
        int i = 0;
        fixed (uint* s = src)
        {
            if (Vector.IsHardwareAccelerated)
            {
                int n = Vector<uint>.Count;
                var rbm = new Vector<uint>(0x00FF00FFu);
                var agm = new Vector<uint>(0xFF00FF00u);
                var k256 = new Vector<uint>(256u);
                for (; i <= src.Length - n; i += n)
                {
                    var sv = Vector.Load(s + i);
                    var dv = Vector.Load(d + i);
                    var inv = k256 - Vector.ShiftRightLogical(sv, 24);
                    var dd = (Vector.ShiftRightLogical((dv & rbm) * inv, 8) & rbm) | ((Vector.ShiftRightLogical(dv, 8) & rbm) * inv & agm);
                    (sv + dd).Store(d + i);
                }
            }
            for (; i < src.Length; i++) d[i] = PixelMath.SrcOver(s[i], d[i]);
        }
    }

    // ── Backdrop blur ──────────────────────────────────────────────────────

    /// <summary>
    /// Frosted glass: blurs what the surface already holds in
    /// <paramref name="region"/> (device pixels) by a Gaussian of
    /// <paramref name="sigma"/> and draws it back, moved by
    /// (<paramref name="dx"/>, <paramref name="dy"/>), inside <paramref name="clip"/>
    /// (device px, snapped as an aliased clip) and the current clip: a snapshot
    /// of the surface drawn back blurred (<see cref="ImageBlur"/>). Pixels outside the
    /// region count as transparent, so a caller pads the region by about 2σ.
    /// The snapshot is of the surface, not of an open layer.
    /// </summary>
    public void DrawBackdropBlur(IRect region, Rect clip, float sigma, int dx = 0, int dy = 0)
    {
        region = region.Intersect(_surface.Bounds);
        if (region.IsEmpty || !(sigma > 0)) return;
        int spread = ImageBlur.Spread(sigma);
        var reach = new IRect(region.Left - spread + dx, region.Top - spread + dy, region.Right + spread + dx, region.Bottom + spread + dy);
        var target = _s.Clip.Intersect(clip.Round()).Intersect(reach);
        if (target.IsEmpty) return;
        var dst = new IRect(target.Left - dx, target.Top - dy, target.Right - dx, target.Bottom - dy);

        int sn = region.Width * region.Height, dn = dst.Width * dst.Height;
        if (_snapshot.Length < sn) _snapshot = new uint[sn];
        if (_blurred.Length < dn) _blurred = new uint[dn];
        _surface.ReadPixels(region, _snapshot.AsSpan(0, sn));
        ImageBlur.Blur(_snapshot.AsSpan(0, sn), region.Width, region, _blurred.AsSpan(0, dn), dst, sigma);
        dst = target;

        // Drawn back as a sprite: src-over (SkPMSrcOver), through any coverage clip.
        _s.Layer?.Touch(dst);
        var t = Target();
        var mask = _s.Mask;
        for (int y = dst.Top; y < dst.Bottom; y++)
        {
            var s = _blurred.AsSpan((y - dst.Top) * dst.Width, dst.Width);
            uint* d = t.Origin + (long)y * t.Stride + dst.Left;
            byte* m = mask is null ? null : mask.Origin + (long)y * mask.Stride + dst.Left;
            if (m == null)
            {
                SrcOverRow(d, s);
                continue;
            }
            for (int i = 0; i < s.Length; i++)
            {
                uint p = s[i];
                if (p == 0 || m[i] == 0) continue;
                d[i] = m[i] == 255 ? PixelMath.SrcOver(p, d[i]) : PixelMath.BlendARGB32(p, d[i], m[i]);
            }
        }
    }
}
