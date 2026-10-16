using Bibui.Raster;

namespace Bibui.Renderer;

/// <summary>
/// Executes a <see cref="DrawList"/> onto an <see cref="IRasterCanvas"/>: each op
/// becomes exactly the canvas calls <see cref="DrawContext"/> made before it
/// recorded instead, so a frame's pixels do not move. Owns the native paths it
/// builds from kept <see cref="VectorPath"/>s, by <see cref="VectorPath.Id"/>, evicted when
/// a path is not drawn for a while; a path from the payload is built into one
/// scratch path, rewound per use.
/// </summary>
internal sealed class DrawListReplay : IDisposable
{
    private readonly IRasterBackend _backend;
    private readonly Func<RasterPath> _makePath;
    private readonly ResourceCache<int, RasterPath> _paths = new(keepFrames: 60);
    private RasterPath? _scratch;

    internal DrawListReplay(IRasterBackend backend)
    {
        _backend = backend;
        _makePath = backend.CreatePath;
    }

    internal void Execute(DrawList list, IRasterCanvas canvas)
    {
        foreach (ref readonly var op in list.Ops)
        {
            switch (op.Kind)
            {
                case DrawOpKind.Clear: canvas.Clear(op.Color); break;
                case DrawOpKind.PushClip: canvas.Save(); canvas.ClipRect(op.Rect); break;
                case DrawOpKind.PushTranslate: canvas.Save(); canvas.Translate(op.A, op.B); break;
                case DrawOpKind.PushScale: canvas.Save(); canvas.Translate(op.A, op.B); canvas.Scale(op.C, op.C); break;
                case DrawOpKind.PushTransform: canvas.Save(); canvas.SetMatrix(op.Matrix * canvas.TotalMatrix); break;
                case DrawOpKind.PushOpacity: canvas.SaveLayerAlpha(op.Alpha); break;
                case DrawOpKind.Pop: canvas.Restore(); break;
                case DrawOpKind.RoundRect: canvas.DrawRoundRect(op.Rect, op.A, op.B, Paint(op, list)); break;
                case DrawOpKind.Line: canvas.DrawLine(op.A, op.B, op.C, op.D, Paint(op, list)); break;
                case DrawOpKind.Path: canvas.DrawPath(Path(op, list), Paint(op, list)); break;
                case DrawOpKind.Points: canvas.DrawPoints(op.Mode, list.Points.Slice(op.Start, op.Count), Paint(op, list)); break;
                case DrawOpKind.Texture: canvas.DrawTexture(op.Texture, op.Rect, Paint(op, list)); break;
                case DrawOpKind.BackdropBlur: canvas.DrawBackdropBlur(op.Region, op.Rect, op.A); break;
                case DrawOpKind.Glyphs:
                    canvas.DrawGlyphs(list.FaceAt(op.Resource), op.C, list.Glyphs.Slice(op.Start, op.Count),
                        list.Points.Slice(op.Start2, op.Count), op.A, op.B, op.Color);
                    break;
            }
        }
    }

    /// <summary>Touch-evicts the native paths no frame has drawn lately; once per frame.</summary>
    internal void EndFrame() => _paths.EndFrame();

    public void Dispose()
    {
        _paths.Dispose();
        _scratch?.Dispose();
    }

    private RasterPath Path(in DrawOp op, DrawList list)
    {
        if (op.Resource >= 0)
        {
            var path = list.PathAt(op.Resource);
            return _paths.FromKey(path.Id, (path, self: this), static p =>
            {
                var native = p.self._backend.CreatePath();
                p.path.BuildInto(native, p.self._makePath);
                return native;
            });
        }
        _scratch ??= _backend.CreatePath();
        _scratch.Rewind();
        VectorPath.Build(list.Verbs.Slice(op.Start, op.Count), list.Floats.Slice(op.Start2, op.Count2), _scratch, _makePath);
        return _scratch;
    }

    // The paint the op recorded, its gradient rebuilt from the payload exactly as
    // DrawContext built it, so the shader and its cache key are the same.
    private static RasterPaint Paint(in DrawOp op, DrawList list)
    {
        RasterShader? shader = null;
        if (op.Shading.Present)
        {
            var s = op.Shading;
            var colors = list.Colors.Slice(s.ColorStart, s.ColorCount);
            var stops = list.Floats.Slice(s.StopStart, s.StopCount);
            shader = s.Type == GradientType.Radial
                ? RasterShader.Radial(s.Start, s.Radius, colors, stops)
                : RasterShader.Linear(s.Start, s.End, colors, stops);
        }
        return new RasterPaint(op.Paint)
        {
            Shader = shader,
            MaskBlurSigma = op.MaskBlurSigma,
            ImageBlurSigma = op.ImageBlurSigma,
            Dash = op.Dashed ? op.Dash : null,
        };
    }
}
