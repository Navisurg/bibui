using System.Numerics;

namespace Bibui.Core;

/// <summary>
/// Points and lines drawn over a <c>runtime.Mesh3D</c> view, projected with its
/// camera, antialiased, and not hidden by the mesh. The app keeps one, and
/// fills it again (<see cref="Clear"/>, then add) when what it shows changes.
/// </summary>
public sealed class MeshOverlay
{
    internal enum Kind : byte { Points, Strip, Lines }

    internal readonly record struct Item(Kind Kind, int Start, int Count, Rgba Color, float Size);

    internal readonly List<Item> Items = [];
    internal Vector3[] Vertices = new Vector3[64];
    internal int VertexCount;

    /// <summary>Removes everything.</summary>
    public void Clear()
    {
        Items.Clear();
        VertexCount = 0;
    }

    /// <summary>Round dots, <paramref name="size"/> logical px across.</summary>
    public void Points(ReadOnlySpan<Vector3> points, Rgba color, float size = 6f) => Add(Kind.Points, points, color, size);

    /// <summary>One polyline through <paramref name="points"/>, <paramref name="width"/> logical px wide.</summary>
    public void LineStrip(ReadOnlySpan<Vector3> points, Rgba color, float width = 2f) => Add(Kind.Strip, points, color, width);

    /// <summary>Separate segments, from each even point to the odd one after it.</summary>
    public void Lines(ReadOnlySpan<Vector3> pairs, Rgba color, float width = 2f) => Add(Kind.Lines, pairs[..(pairs.Length & ~1)], color, width);

    private void Add(Kind kind, ReadOnlySpan<Vector3> points, Rgba color, float size)
    {
        if (points.IsEmpty) return;
        if (VertexCount + points.Length > Vertices.Length)
            Array.Resize(ref Vertices, Math.Max(Vertices.Length * 2, VertexCount + points.Length));
        points.CopyTo(Vertices.AsSpan(VertexCount));
        Items.Add(new Item(kind, VertexCount, points.Length, color, size));
        VertexCount += points.Length;
    }
}
