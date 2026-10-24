namespace Bibui.Platform.Browser;

/// <summary>
/// A canvas's size as bibui sees it. The browser reports the canvas's size in
/// device pixels (CSS size × devicePixelRatio); that is the frame's size, and
/// devicePixelRatio is its scale.
/// </summary>
internal struct CanvasSize
{
    /// <summary>The frame's size: the canvas's, in device pixels, exactly as the browser reported it.</summary>
    public int Width, Height;
    public float Ratio;

    /// <summary>
    /// Takes the size the browser reported. True when it changed, which owes a
    /// frame.
    ///
    /// <para>The frame is drawn at the reported size itself, never at the
    /// logical size scaled back up: that rounds, and at a fractional ratio the
    /// two differ (792 px at 1.25 is 633.6 logical, 634 rounded, and 634 × 1.25
    /// is 793), so a frame made from the logical size would not fit its canvas.</para>
    /// </summary>
    public bool Update(int deviceWidth, int deviceHeight, float ratio)
    {
        if (ratio <= 0f || !float.IsFinite(ratio)) ratio = 1f;
        deviceWidth = Math.Max(1, deviceWidth);
        deviceHeight = Math.Max(1, deviceHeight);
        if (deviceWidth == Width && deviceHeight == Height && ratio == Ratio) return false;
        (Width, Height, Ratio) = (deviceWidth, deviceHeight, ratio);
        return true;
    }
}

/// <summary>
/// The display's refresh rate, measured from <c>requestAnimationFrame</c>, which
/// the browser calls once a refresh but whose rate it does not tell: the
/// shortest gap between consecutive callbacks is one refresh. Measured afresh
/// every 30 callbacks, so a window moved to another display follows it.
/// </summary>
internal struct RefreshClock
{
    private double _lastMs, _shortestMs;
    private int _samples;
    private float _hz;

    /// <summary>The refresh rate in Hz; 60 until measured.</summary>
    public readonly float Hz => _hz > 0f ? _hz : 60f;

    /// <summary>Takes one callback's timestamp (<c>requestAnimationFrame</c>'s, in ms).</summary>
    public void Tick(double nowMs)
    {
        double gap = nowMs - _lastMs;
        _lastMs = nowMs;
        // Consecutive callbacks only: the loop starting again after it stopped is no measure.
        if (gap <= 0 || gap > 100) return;
        _shortestMs = _samples == 0 ? gap : Math.Min(_shortestMs, gap);
        if (++_samples < 30) return;
        _hz = (float)(1000.0 / _shortestMs);
        _samples = 0;
    }
}
