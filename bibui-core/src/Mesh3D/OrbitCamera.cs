using System.Numerics;

namespace Bibui.Core;

/// <summary>
/// Where a 3D view looks from and at: the eye, the target it orbits, the
/// world's up (the axis it orbits about) and the vertical field of view in
/// degrees. <c>(0, 1, 0)</c> is up for y-up data; <c>(0, -1, 0)</c> for OpenCV
/// camera axes (x right, y down, z forward), in which an eye at the origin
/// looking at a target on +z is the recording camera's own view. A value: each
/// move returns a new camera.
/// </summary>
public readonly record struct OrbitCamera(Vector3 Eye, Vector3 Target, Vector3 Up, float FovY = 45f)
{
    /// <summary>How far the eye is from the target.</summary>
    public float Distance => Vector3.Distance(Eye, Target);

    /// <summary>The way the eye looks, unit length.</summary>
    public Vector3 Forward => Vector3.Normalize(Target - Eye);

    /// <summary>The view's right, unit length: forward × up.</summary>
    public Vector3 Right
    {
        get
        {
            var r = Vector3.Cross(Forward, Vector3.Normalize(Up));
            // Looking straight along up: any right will do, one that holds still.
            return r.LengthSquared() > 1e-10f ? Vector3.Normalize(r) : Perpendicular(Forward);
        }
    }

    /// <summary>The view's up, unit length, square to forward and right.</summary>
    public Vector3 ViewUp => Vector3.Cross(Right, Forward);

    /// <summary>
    /// Turns the eye round the target: <paramref name="yawDegrees"/> about
    /// <see cref="Up"/>, then <paramref name="pitchDegrees"/> about the view's
    /// right, positive toward up. The pitch stops 1° short of either pole.
    /// </summary>
    public OrbitCamera Orbit(float yawDegrees, float pitchDegrees)
    {
        var up = Vector3.Normalize(Up);
        var offset = Eye - Target;
        float dist = offset.Length();
        if (dist <= 0f) return this;
        offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(up, yawDegrees * MathF.PI / 180f));
        // The angle from up now, and the one asked for, kept off the poles.
        float now = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(offset), up), -1f, 1f)) * 180f / MathF.PI;
        float want = Math.Clamp(now - pitchDegrees, 1f, 179f);
        var right = Vector3.Cross(-offset, up);
        if (right.LengthSquared() > 1e-12f && want != now)
            offset = Vector3.Transform(offset, Quaternion.CreateFromAxisAngle(Vector3.Normalize(right), (want - now) * MathF.PI / 180f));
        return this with { Eye = Target + offset };
    }

    /// <summary>Moves eye and target together, <paramref name="right"/> along the view's right and <paramref name="up"/> along its up, in world units.</summary>
    public OrbitCamera Pan(float right, float up)
    {
        var d = Right * right + ViewUp * up;
        return this with { Eye = Eye + d, Target = Target + d };
    }

    /// <summary>The eye <paramref name="factor"/> times as far from the target, on the same line.</summary>
    public OrbitCamera Zoom(float factor)
    {
        if (!(factor > 0f)) return this;
        return this with { Eye = Target + (Eye - Target) * factor };
    }

    /// <summary>
    /// A camera looking along <paramref name="viewDirection"/> at the centre of
    /// the box from <paramref name="min"/> to <paramref name="max"/>, far enough
    /// back that the box's bounding sphere fits the view.
    /// </summary>
    public static OrbitCamera Fit(Vector3 min, Vector3 max, Vector3 viewDirection, Vector3 up, float fovY = 45f)
    {
        var centre = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 1e-3f);
        var dir = viewDirection.LengthSquared() > 0f ? Vector3.Normalize(viewDirection) : -Vector3.UnitZ;
        float dist = radius / MathF.Sin(fovY * 0.5f * MathF.PI / 180f) * 1.05f;
        return new OrbitCamera(centre - dir * dist, centre, up, fovY);
    }

    private static Vector3 Perpendicular(Vector3 v) =>
        Vector3.Normalize(MathF.Abs(v.X) < 0.9f ? Vector3.Cross(v, Vector3.UnitX) : Vector3.Cross(v, Vector3.UnitY));
}
