namespace Bibui.Raster;

/// <summary>
/// A canvas that records a frame to be drawn later, onto a real canvas,
/// instead of drawing it now — what a damage-tracked frame paints onto. Code
/// that needs the real canvas itself (a Skia custom draw reaching for
/// <c>SKCanvas</c>) defers its draw here; it then runs at replay, under the
/// same transform and clip it would have run under now.
/// </summary>
public interface IRecordingCanvas : IRasterCanvas
{
    /// <summary>
    /// Runs <paramref name="draw"/> on the canvas the frame is replayed onto.
    /// Nothing about what it draws is known, so the frame it is in is painted
    /// whole.
    /// </summary>
    void Defer(Action<IRasterCanvas> draw);
}
