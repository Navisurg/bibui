using System.Numerics;

namespace Bibui.Raster;

/// <summary>What a path is built from; each takes a fixed number of arguments (<see cref="VectorPath.ArgCount"/>).</summary>
public enum PathVerb : byte
{
    Move,       // x y
    Line,       // x y
    Quad,       // x1 y1 x2 y2
    Cubic,      // x1 y1 x2 y2 x3 y3
    Arc,        // rx ry rotation largeArc sweep x y: an SVG arc
    AddArc,     // left top right bottom start sweep: an arc of an oval, as a new contour
    Close,
    AddPath,    // another path, transformed: its source and matrix are VectorPath.Added, in order
}

/// <summary>
/// A path as bibui describes it: verbs and their float arguments, owned by no
/// raster backend. A renderer builds its own native path from it, and caches
/// that by <see cref="Id"/> when the path is kept from frame to frame.
///
/// <para><see cref="AddPath"/> keeps its source by reference and its transform
/// with it, so a renderer builds the source natively and then adds it under the
/// transform: an icon is parsed at 24 units and scaled when added, the order the
/// pixels were blessed in.</para>
/// </summary>
public sealed class VectorPath
{
    private static int s_nextId;

    private PathVerb[] _verbs = new PathVerb[8];
    private int _verbCount;
    private float[] _args = new float[16];
    private int _argCount;
    private List<(VectorPath Source, Matrix3x2 Transform)>? _added;

    /// <summary>Process-unique: a renderer's cache key for the native path it builds from this one.</summary>
    public int Id { get; } = Interlocked.Increment(ref s_nextId);

    /// <summary>
    /// Kept by bibui's path cache for frames to come, so a draw list names it
    /// rather than copying it. Set by <c>DrawContext.Path</c>, never changed after.
    /// </summary>
    internal bool Kept { get; init; }

    public ReadOnlySpan<PathVerb> Verbs => _verbs.AsSpan(0, _verbCount);

    public ReadOnlySpan<float> Args => _args.AsSpan(0, _argCount);

    /// <summary>The <paramref name="i"/>-th <see cref="PathVerb.AddPath"/>'s source and transform.</summary>
    public (VectorPath Source, Matrix3x2 Transform) Added(int i) => _added![i];

    public bool IsEmpty => _verbCount == 0;

    public void MoveTo(float x, float y) => Verb(PathVerb.Move, x, y);

    public void LineTo(float x, float y) => Verb(PathVerb.Line, x, y);

    public void QuadTo(float x1, float y1, float x2, float y2) => Verb(PathVerb.Quad, x1, y1, x2, y2);

    public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) => Verb(PathVerb.Cubic, x1, y1, x2, y2, x3, y3);

    /// <summary>An SVG elliptical arc from the current point to (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public void ArcTo(float rx, float ry, float xAxisRotate, bool largeArc, bool sweep, float x, float y)
        => Verb(PathVerb.Arc, rx, ry, xAxisRotate, largeArc ? 1f : 0f, sweep ? 1f : 0f, x, y);

    /// <summary>An arc of the oval in <paramref name="oval"/>, as a new contour; angles in degrees, clockwise from 3 o'clock.</summary>
    public void AddArc(RectF oval, float startAngle, float sweepAngle)
        => Verb(PathVerb.AddArc, oval.Left, oval.Top, oval.Right, oval.Bottom, startAngle, sweepAngle);

    public void Close() => Verb(PathVerb.Close);

    /// <summary>Appends <paramref name="source"/> under <paramref name="transform"/>. The source is kept by reference: change it and this path changes.</summary>
    public void AddPath(VectorPath source, in Matrix3x2 transform)
    {
        ArgumentNullException.ThrowIfNull(source);
        (_added ??= []).Add((source, transform));
        Verb(PathVerb.AddPath);
    }

    /// <summary>Empties the path, keeping its storage, to build another into it.</summary>
    public void Rewind()
    {
        _verbCount = _argCount = 0;
        _added?.Clear();
    }

    /// <summary>The number of arguments <paramref name="verb"/> takes.</summary>
    public static int ArgCount(PathVerb verb) => verb switch
    {
        PathVerb.Move or PathVerb.Line => 2,
        PathVerb.Quad => 4,
        PathVerb.Cubic or PathVerb.AddArc => 6,
        PathVerb.Arc => 7,
        _ => 0,
    };

    /// <summary>
    /// Builds this path into <paramref name="into"/>, a backend's own path,
    /// verb for verb. An added path is built into a path of
    /// <paramref name="makePath"/>'s first, then added under its transform.
    /// </summary>
    public void BuildInto(RasterPath into, Func<RasterPath> makePath)
    {
        ArgumentNullException.ThrowIfNull(into);
        Build(Verbs, Args, into, makePath, this);
    }

    /// <summary>
    /// <see cref="BuildInto"/> over verbs and arguments held elsewhere (a
    /// <see cref="DrawList"/>'s payload); <paramref name="owner"/> supplies the
    /// added paths, if any verb adds one.
    /// </summary>
    public static void Build(ReadOnlySpan<PathVerb> verbs, ReadOnlySpan<float> a, RasterPath into, Func<RasterPath> makePath, VectorPath? owner = null)
    {
        int at = 0, added = 0;
        foreach (var verb in verbs)
        {
            switch (verb)
            {
                case PathVerb.Move: into.MoveTo(a[at], a[at + 1]); break;
                case PathVerb.Line: into.LineTo(a[at], a[at + 1]); break;
                case PathVerb.Quad: into.QuadTo(a[at], a[at + 1], a[at + 2], a[at + 3]); break;
                case PathVerb.Cubic: into.CubicTo(a[at], a[at + 1], a[at + 2], a[at + 3], a[at + 4], a[at + 5]); break;
                case PathVerb.Arc: into.ArcTo(a[at], a[at + 1], a[at + 2], a[at + 3] != 0f, a[at + 4] != 0f, a[at + 5], a[at + 6]); break;
                case PathVerb.AddArc: into.AddArc(new RectF(a[at], a[at + 1], a[at + 2], a[at + 3]), a[at + 4], a[at + 5]); break;
                case PathVerb.Close: into.Close(); break;
                case PathVerb.AddPath:
                {
                    var (source, transform) = owner!.Added(added++);
                    using var built = makePath();
                    source.BuildInto(built, makePath);
                    into.AddPath(built, transform);
                    break;
                }
            }
            at += ArgCount(verb);
        }
    }

    /// <summary>Whether every argument is a finite number.</summary>
    public bool AllFinite
    {
        get
        {
            foreach (float a in Args) if (!float.IsFinite(a)) return false;
            return true;
        }
    }

    /// <summary>Whether it draws anything: some verb other than a move or a close.</summary>
    public bool HasSegment
    {
        get
        {
            foreach (var v in Verbs) if (v is not (PathVerb.Move or PathVerb.Close)) return true;
            return false;
        }
    }

    private void Verb(PathVerb verb, float a0 = 0, float a1 = 0, float a2 = 0, float a3 = 0, float a4 = 0, float a5 = 0, float a6 = 0)
    {
        if (_verbCount == _verbs.Length) Array.Resize(ref _verbs, _verbCount * 2);
        _verbs[_verbCount++] = verb;
        int n = ArgCount(verb);
        if (_argCount + n > _args.Length) Array.Resize(ref _args, Math.Max(_args.Length * 2, _argCount + n));
        Span<float> args = [a0, a1, a2, a3, a4, a5, a6];
        args[..n].CopyTo(_args.AsSpan(_argCount));
        _argCount += n;
    }
}
