namespace Bibui.Core;

/// <summary>
/// A point light at the eye of a <c>runtime.Mesh3D</c> view, as an endoscope's
/// own light is: a surface point at distance r, its normal at θ to the way
/// back to the eye, α off the view's axis, is lit
/// <c>Ambient + Intensity · cos θ · cosⁿ α / r²</c> (n = <see cref="SpotExponent"/>)
/// times its colour, each channel clamped at 1. Computed for every pixel.
/// </summary>
/// <param name="Intensity">The light a surface square to the axis gets at distance 1, in the scene's units: 2500 lights a surface 50 mm off fully in millimetres.</param>
/// <param name="SpotExponent">0 lights every direction alike; larger narrows the beam to a cosⁿ lobe about the view's axis.</param>
public readonly record struct CameraLight(float Intensity, float SpotExponent = 0f);
