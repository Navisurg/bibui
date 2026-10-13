namespace Bibui.Platform.Wayland;

/// <summary>
/// Client-side key repeat as a schedule. Wayland repeats nothing for
/// the client: wl_keyboard.repeat_info says how fast and after how long, and
/// the client times it. This is only the arithmetic — which key, when the next
/// repeat is due, how many are due now — so it is tested without a clock; the
/// host arms a timerfd for <see cref="NextDue"/> and polls it beside the
/// display, so a repeat runs on the loop thread like every other event.
///
/// <para>Times are in milliseconds on any monotonic clock.</para>
/// </summary>
internal sealed class KeyRepeat
{
    /// <summary>
    /// The most repeats one wake reports. A loop held up (a long frame, a
    /// stall) does not burst a line of characters when it comes back: the
    /// schedule skips ahead instead.
    /// </summary>
    public const int MaxBurst = 2;

    /// <summary>Repeats per second; 0 turns repeat off. Before repeat_info arrives, GLFW's and most compositors' 25.</summary>
    public int Rate { get; private set; } = 25;

    /// <summary>Milliseconds from the press to the first repeat. Default 600, as for <see cref="Rate"/>.</summary>
    public int Delay { get; private set; } = 600;

    /// <summary>The evdev code repeating, or null when none is.</summary>
    public uint? Key { get; private set; }

    /// <summary>When the next repeat is due, or null when nothing repeats.</summary>
    public double? NextDue { get; private set; }

    private double Interval => 1000.0 / Rate;

    /// <summary>wl_keyboard.repeat_info. Takes effect from the next press.</summary>
    public void SetInfo(int rate, int delay)
    {
        Rate = Math.Max(0, rate);
        Delay = Math.Max(0, delay);
        if (Rate == 0) Stop();
    }

    /// <summary>A repeating key went down at <paramref name="now"/>: it repeats from now + delay, replacing whatever repeated before.</summary>
    public void Press(uint key, double now)
    {
        if (Rate == 0) { Stop(); return; }
        Key = key;
        NextDue = now + Delay;
    }

    /// <summary>A key went up. Stops the repeat when it is the key repeating.</summary>
    public void Release(uint key)
    {
        if (Key == key) Stop();
    }

    public void Stop()
    {
        Key = null;
        NextDue = null;
    }

    /// <summary>
    /// How many repeats are due at <paramref name="now"/> (at most
    /// <see cref="MaxBurst"/>), and moves the schedule past them.
    /// </summary>
    public int Take(double now)
    {
        if (NextDue is not double due || now < due) return 0;
        double interval = Interval;
        int dueCount = 1 + (int)Math.Floor((now - due) / interval);
        if (dueCount > MaxBurst)
        {
            // Behind: report a burst, then pick the beat up again from now.
            NextDue = now + interval;
            return MaxBurst;
        }
        NextDue = due + dueCount * interval;
        return dueCount;
    }
}
