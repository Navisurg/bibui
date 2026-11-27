using System.Numerics;

namespace Bibui.Core;

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

    /// <summary>
    /// Null: a headlight from the eye that does not weaken with distance, the
    /// default. Set: a point light at the eye that falls off with the square of
    /// the distance; see <see cref="CameraLight"/>.
    /// </summary>
    public CameraLight? Light { get; set; }

    /// <summary>Behind the mesh. Null: the box shows through.</summary>
    public Rgba? Background { get; set; }

    /// <summary>False: the view takes no input.</summary>
    public bool Interactive { get; set; } = true;

    /// <summary>Points and lines over the mesh.</summary>
    public MeshOverlay? Overlay { get; set; }

    /// <summary>
    /// Overlay points behind the mesh are not drawn, and a line breaks at them.
    /// Tested at the points, with a tolerance of 1 % of their depth so a track on
    /// the surface stays; a segment between two visible points is drawn whole.
    /// </summary>
    public bool OverlayHiddenByMesh { get; set; }

    /// <summary>
    /// Samples a pixel across, 1 to 4: 2 draws the mesh at twice the size each way
    /// and averages, smoothing its outline and the wireframe for four times the
    /// rasterising. 1, the default, takes one sample a pixel.
    /// </summary>
    public int Supersample { get; set; } = 1;

    /// <summary>False: the mesh is not drawn, only the overlay (and the background).</summary>
    public bool ShowMesh { get; set; } = true;
}
