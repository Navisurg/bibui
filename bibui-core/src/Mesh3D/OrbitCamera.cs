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
    /// <summary>The share of <see cref="Distance"/> the near plane is at when <see cref="Near"/> is 0.</summary>
    public const float DefaultNearShare = 1e-3f;

    /// <summary>
    /// How far ahead of the eye, along the view's axis, the near plane is:
    /// what is closer is cut away. 0, the default, is
    /// <see cref="DefaultNearShare"/> of <see cref="Distance"/>.
    /// </summary>
    public float Near { get; init; }

    /// <summary>The near plane's distance in effect: <see cref="Near"/>, or its default.</summary>
    internal float NearPlane => Near > 0f ? Near : DefaultNearShare * MathF.Max(Distance, 1e-6f);

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

    /// <summary>
    /// The camera's axes in the world, OpenCV's: x to the right of the picture,
    /// y down it, z ahead. <c>Vector3.Transform(Vector3.UnitZ, Orientation)</c>
    /// is <see cref="Forward"/>.
    /// </summary>
    public Quaternion Orientation
    {
        get
        {
            Vector3 x = Right, y = -ViewUp, z = Forward;
            // A row vector times this matrix takes camera axes to the world's: its rows are their images.
            return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(new Matrix4x4(
                x.X, x.Y, x.Z, 0f,
                y.X, y.Y, y.Z, 0f,
                z.X, z.Y, z.Z, 0f,
                0f, 0f, 0f, 1f)));
        }
    }

    /// <summary>
    /// The camera at <paramref name="position"/> turned by <paramref name="orientation"/>
    /// (its OpenCV axes, x right, y down, z ahead, to the world's), seeing
    /// <paramref name="fovY"/> degrees top to bottom. The target, what an orbit
    /// turns about, is <paramref name="targetDistance"/> ahead; the camera's own
    /// up is the axis it orbits about.
    /// </summary>
    public static OrbitCamera FromPose(Vector3 position, Quaternion orientation, float fovY, float targetDistance)
    {
        var q = Quaternion.Normalize(orientation);
        var forward = Vector3.Transform(Vector3.UnitZ, q);
        var down = Vector3.Transform(Vector3.UnitY, q);
        return new OrbitCamera(position, position + forward * MathF.Max(targetDistance, 1e-6f), -down, fovY);
    }

    private static Vector3 Perpendicular(Vector3 v) =>
        Vector3.Normalize(MathF.Abs(v.X) < 0.9f ? Vector3.Cross(v, Vector3.UnitX) : Vector3.Cross(v, Vector3.UnitY));
}
