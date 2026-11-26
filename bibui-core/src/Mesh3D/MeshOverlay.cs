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

/// <summary>How a <c>runtime.Mesh3D</c> view draws its mesh. Keep one and change it between frames.</summary>
public sealed class Mesh3DOptions
{
    /// <summary>The first camera, and the one a double-click goes back to. Null: the mesh fitted along <see cref="ViewDirection"/>.</summary>
    public OrbitCamera? Home { get; set; }

    /// <summary>Without a <see cref="Home"/>: the fitted camera's up.</summary>
    public Vector3 Up { get; set; } = Vector3.UnitY;

    /// <summary>Without a <see cref="Home"/>: the way the fitted camera looks.</summary>
    public Vector3 ViewDirection { get; set; } = -Vector3.UnitZ;

    /// <summary>Draws every edge over the faces.</summary>
    public bool Wireframe { get; set; }

    public Rgba WireframeColor { get; set; } = new(0, 0, 0, 110);

    /// <summary>Light everywhere, 0..1; the headlight gives the rest.</summary>
    public float Ambient { get; set; } = 0.25f;

    /// <summary>Behind the mesh. Null: the box shows through.</summary>
    public Rgba? Background { get; set; }

    /// <summary>False: the view takes no input.</summary>
    public bool Interactive { get; set; } = true;

    /// <summary>Points and lines over the mesh.</summary>
    public MeshOverlay? Overlay { get; set; }
}
