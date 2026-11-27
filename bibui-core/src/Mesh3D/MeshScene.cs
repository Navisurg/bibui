using System.Numerics;

namespace Bibui.Core;

/// <summary>
/// Several <see cref="TriangleMesh"/>es drawn together in one <c>runtime.Mesh3D</c>
/// view or <see cref="MeshBuffers"/>: each placed in the world by a transform,
/// each shown or hidden. A mesh's id is the order it was added in, and is what
/// picking and the id buffer answer. The app keeps the scene and changes it
/// between frames.
/// </summary>
public sealed class MeshScene
{
    internal readonly List<TriangleMesh> Meshes = [];
    internal readonly List<Matrix4x4> Transforms = [];
    internal readonly List<bool> Visible = [];

    // Bumped by every change to the scene itself (not to its meshes); what a view's cache compares.
    internal uint Version;

    // Bumped when meshes are added or removed: the faces of the scene as a whole change.
    internal uint TopologyVersion;

    /// <summary>
    /// Adds <paramref name="mesh"/>, placed by <paramref name="transform"/> (its
    /// coordinates to the world's, a row vector times the matrix as
    /// <c>System.Numerics</c> writes it; null is the identity), and returns its id.
    /// </summary>
    public int Add(TriangleMesh mesh, Matrix4x4? transform = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        Meshes.Add(mesh);
        Transforms.Add(transform ?? Matrix4x4.Identity);
        Visible.Add(true);
        Version++;
        TopologyVersion++;
        return Meshes.Count - 1;
    }

    /// <summary>How many meshes the scene holds; ids run from 0 to one less.</summary>
    public int Count => Meshes.Count;

    /// <summary>The mesh with id <paramref name="id"/>.</summary>
    public TriangleMesh this[int id] => Meshes[id];

    /// <summary>Where the mesh with id <paramref name="id"/> is placed: its coordinates to the world's.</summary>
    public Matrix4x4 TransformOf(int id) => Transforms[id];

    /// <summary>Places the mesh with id <paramref name="id"/>: its coordinates to the world's.</summary>
    public void SetTransform(int id, Matrix4x4 transform)
    {
        if (Transforms[id] == transform) return;
        Transforms[id] = transform;
        Version++;
    }

    /// <summary>Whether the mesh with id <paramref name="id"/> is drawn.</summary>
    public bool IsVisible(int id) => Visible[id];

    /// <summary>Shows or hides the mesh with id <paramref name="id"/>. A hidden mesh is not drawn, picked or counted in <see cref="Bounds"/>.</summary>
    public void SetVisible(int id, bool visible)
    {
        if (Visible[id] == visible) return;
        Visible[id] = visible;
        Version++;
    }

    /// <summary>Removes every mesh; ids start at 0 again.</summary>
    public void Clear()
    {
        Meshes.Clear();
        Transforms.Clear();
        Visible.Clear();
        Version++;
        TopologyVersion++;
    }

    /// <summary>The box around the visible meshes, in the world; zero when none is visible.</summary>
    public (Vector3 Min, Vector3 Max) Bounds
    {
        get
        {
            var min = new Vector3(float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity);
            for (int m = 0; m < Meshes.Count; m++)
            {
                if (!Visible[m] || Meshes[m].VertexCount == 0) continue;
                var (lo, hi) = Meshes[m].Bounds;
                // The placed box's centre, and how far it reaches along each world axis:
                // each of the mesh's half-extents carried by the matrix, their sizes summed.
                var t = Transforms[m];
                var centre = Vector3.Transform((lo + hi) * 0.5f, t);
                var half = (hi - lo) * 0.5f;
                var reach = Vector3.Abs(new Vector3(t.M11, t.M12, t.M13)) * half.X
                    + Vector3.Abs(new Vector3(t.M21, t.M22, t.M23)) * half.Y
                    + Vector3.Abs(new Vector3(t.M31, t.M32, t.M33)) * half.Z;
                min = Vector3.Min(min, centre - reach);
                max = Vector3.Max(max, centre + reach);
            }
            return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
        }
    }

    /// <summary>What every mesh's own changes add up to: rises whenever any of them changes.</summary>
    internal ulong ContentStamp
    {
        get
        {
            ulong sum = 0;
            foreach (var mesh in Meshes) sum += mesh.Version;
            return sum;
        }
    }
}
