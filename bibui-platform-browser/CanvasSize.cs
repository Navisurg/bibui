using Bibui.Runtime;

namespace Bibui.Platform.Browser;

/// <summary>
/// A canvas's size as bibui sees it. The browser reports the canvas's size in
/// device pixels (CSS size × devicePixelRatio); that is the frame's size, and
/// devicePixelRatio is the runtime's scale.
/// </summary>
internal struct CanvasSize
{
    /// <summary>The frame's size: the canvas's, in device pixels, exactly as the browser reported it.</summary>
    public int Width, Height;
    public float Ratio;

    /// <summary>
    /// Takes the size the browser reported, and resizes the runtime's surface
    /// when it changed: logical size is the device size over the ratio. True
    /// when it changed, which owes a frame.
    ///
    /// <para>The frame is drawn at the reported size itself, never at the
    /// logical size scaled back up: that rounds, and at a fractional ratio the
    /// two differ (792 px at 1.25 is 633.6 logical, 634 rounded, and 634 × 1.25
    /// is 793), so a frame made from the logical size would not fit its canvas.</para>
    /// </summary>
    public bool Update(BibuiRuntime runtime, int deviceWidth, int deviceHeight, float ratio)
    {
        if (ratio <= 0f || !float.IsFinite(ratio)) ratio = 1f;
        deviceWidth = Math.Max(1, deviceWidth);
        deviceHeight = Math.Max(1, deviceHeight);
        if (deviceWidth == Width && deviceHeight == Height && ratio == Ratio) return false;
        (Width, Height, Ratio) = (deviceWidth, deviceHeight, ratio);
        runtime.ResizeSurface(deviceWidth, deviceHeight, ratio);
        return true;
    }
}

/// <summary>The time a frame steps animations by, as the desktop windows measure it.</summary>
internal struct FrameClock
{
    private double _lastMs;

    /// <summary>The longest step one frame takes: a tab the browser stopped drawing comes back without a jump.</summary>
    public const float MaxDt = 0.25f;

    /// <summary>
    /// <see cref="BibuiRuntime.Dt"/> for a frame at <paramref name="nowMs"/>
    /// (requestAnimationFrame's timestamp): the time since the last frame, or
    /// one frame at <paramref name="targetHz"/> after a wait for input (nothing
    /// was owed), and for the first frame.
    /// </summary>
    public float Step(double nowMs, bool waited, float targetHz)
    {
        float oneFrame = 1f / (targetHz > 0f ? targetHz : 60f);
        float dt = waited || _lastMs == 0 || nowMs <= _lastMs ? oneFrame : (float)((nowMs - _lastMs) / 1000.0);
        _lastMs = nowMs;
        return Math.Min(dt, MaxDt);
    }
}
