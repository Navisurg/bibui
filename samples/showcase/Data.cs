namespace Showcase;

// Everything Zenith shows is invented: the site, the targets, their names and
// the telemetry. Positions are made up too, but they move across the sky the
// way real ones would, so the dome turns as the night goes on.

internal enum TargetKind { Galaxy, Nebula, Cluster, Remnant, Variable, Comet }

internal enum TargetStatus { Imaging, Queued, Hold, Done }

internal sealed record Target(
    string Id,
    string Name,
    string Code,
    TargetKind Kind,
    double RaHours,
    double DecDeg,
    float Magnitude,
    int PlannedMin,
    int DoneMin,
    string Filter,
    TargetStatus Status,
    int Priority);

internal static class Data
{
    public const string SiteName = "Alto Vigía";
    public const double LatitudeDeg = -24.6;

    public static readonly Target[] Targets =
    [
        new("lantern",  "Lantern Nebula",   "ZC 1142", TargetKind.Nebula,   5.58, -5.4,  4.0f, 180, 126, "Hα · OIII", TargetStatus.Imaging, 1),
        new("kestrel",  "Kestrel Galaxy",   "ZC 0311", TargetKind.Galaxy,   0.71, -37.7, 8.1f, 240,  60, "LRGB",      TargetStatus.Queued,  1),
        new("ember",    "Ember Ring",       "ZC 2207", TargetKind.Nebula,   3.62, -26.3, 9.3f, 120, 120, "OIII",      TargetStatus.Done,    2),
        new("harrow",   "Harrow Cluster",   "ZC 0915", TargetKind.Cluster,  7.85, -38.5, 5.8f,  60,  12, "RGB",       TargetStatus.Queued,  2),
        new("tamsin",   "Tamsin's Veil",    "ZC 1780", TargetKind.Remnant,  8.55, -45.2, 7.0f, 300,  75, "Hα · SII",  TargetStatus.Queued,  1),
        new("oriel",    "Oriel Spiral",     "ZC 0044", TargetKind.Galaxy,   1.55, -29.4, 9.9f, 210,   0, "L",         TargetStatus.Hold,    3),
        new("wren",     "Wren Globular",    "ZC 0127", TargetKind.Cluster,  0.42, -72.1, 4.1f,  45,  45, "RGB",       TargetStatus.Done,    3),
        new("cinder",   "Cinder Arc",       "ZC 2381", TargetKind.Remnant,  6.12, -20.6, 8.6f, 150,  30, "Hα",        TargetStatus.Queued,  2),
        new("mirelle",  "Mirelle Cloud",    "ZC 0552", TargetKind.Nebula,   5.25, -69.8, 1.5f,  90,  10, "Hα · OIII", TargetStatus.Queued,  2),
        new("sable",    "Sable Knot",       "ZC 3310", TargetKind.Galaxy,   4.05, -54.9, 10.4f, 360,  0, "L",         TargetStatus.Hold,    3),
        new("quill",    "Quill Comet",      "ZC C/26", TargetKind.Comet,    2.95, -12.2, 7.6f,  40,  18, "RGB",       TargetStatus.Imaging, 1),
        new("halden",   "Halden Pair",      "ZC 1406", TargetKind.Variable, 6.85, -50.6, 6.4f,  30,   8, "V",         TargetStatus.Queued,  2),
        new("ashfall",  "Ashfall Remnant",  "ZC 2902", TargetKind.Remnant,  5.68, -67.4, 9.8f, 270,  95, "OIII",      TargetStatus.Queued,  2),
        new("lumen",    "Lumen Twins",      "ZC 0788", TargetKind.Galaxy,   3.37, -35.4, 8.9f, 180,  44, "LRGB",      TargetStatus.Queued,  3),
    ];

    public static string KindName(TargetKind k) => k switch
    {
        TargetKind.Galaxy => "Galaxy",
        TargetKind.Nebula => "Nebula",
        TargetKind.Cluster => "Cluster",
        TargetKind.Remnant => "Remnant",
        TargetKind.Variable => "Variable",
        _ => "Comet",
    };

    public static string StatusName(TargetStatus s) => s switch
    {
        TargetStatus.Imaging => "Imaging",
        TargetStatus.Queued => "Queued",
        TargetStatus.Hold => "Weather hold",
        _ => "Complete",
    };

    // ── The sky ──────────────────────────────────────────────────────────────

    /// <summary>Local sidereal time in hours, <paramref name="clock"/> seconds into the night.</summary>
    public static double Lst(double clock) => (4.2 + clock / 3600.0 * 1.00274) % 24.0;

    /// <summary>Altitude and azimuth in degrees of a body at (ra, dec), at sidereal time <paramref name="lst"/>.</summary>
    public static (double Alt, double Az) AltAz(double raHours, double decDeg, double lst)
    {
        double ha = (lst - raHours) * 15.0 * Math.PI / 180.0;
        double dec = decDeg * Math.PI / 180.0, lat = LatitudeDeg * Math.PI / 180.0;
        double sinAlt = Math.Sin(dec) * Math.Sin(lat) + Math.Cos(dec) * Math.Cos(lat) * Math.Cos(ha);
        double alt = Math.Asin(Math.Clamp(sinAlt, -1, 1));
        double cosAz = (Math.Sin(dec) - Math.Sin(alt) * Math.Sin(lat)) / (Math.Cos(alt) * Math.Cos(lat));
        double az = Math.Acos(Math.Clamp(cosAz, -1, 1));
        if (Math.Sin(ha) > 0) az = 2 * Math.PI - az;
        return (alt * 180.0 / Math.PI, az * 180.0 / Math.PI);
    }

    /// <summary>
    /// A point on the galactic band, <paramref name="theta"/> degrees along it:
    /// a great circle tilted 62° to the equator, so it arcs across the dome.
    /// </summary>
    public static (double RaHours, double DecDeg) Band(double theta)
    {
        double t = theta * Math.PI / 180.0, inc = 62.0 * Math.PI / 180.0;
        double x = Math.Cos(t), y = Math.Sin(t) * Math.Cos(inc), z = Math.Sin(t) * Math.Sin(inc);
        double ra = (Math.Atan2(y, x) * 12.0 / Math.PI + 24.0 + 6.4) % 24.0;
        return (ra, Math.Asin(z) * 180.0 / Math.PI);
    }

    /// <summary>A star of the backdrop: where it is, how bright, and a tint.</summary>
    public readonly record struct Star(double RaHours, double DecDeg, float Brightness, byte Tint);

    public static readonly Star[] Stars = MakeStars(1500);

    private static Star[] MakeStars(int n)
    {
        var rng = new Random(20261013);
        var stars = new Star[n];
        for (int i = 0; i < n; i++)
        {
            // Uniform over the sphere, and a third of them crowded into a band —
            // the galaxy's plane, slanting across the dome.
            double ra = rng.NextDouble() * 24.0;
            double dec = Math.Asin(rng.NextDouble() * 2 - 1) * 180.0 / Math.PI;
            if (i % 4 == 0)
            {
                var (bra, bdec) = Band(rng.NextDouble() * 360.0);
                ra = bra;
                dec = Math.Clamp(bdec + (rng.NextDouble() + rng.NextDouble() - 1.0) * 14.0, -89.0, 89.0);
            }
            float b = (float)Math.Pow(rng.NextDouble(), 2.6);
            stars[i] = new Star(ra, dec, b, (byte)rng.Next(0, 4));
        }
        return stars;
    }

    // ── Telemetry ────────────────────────────────────────────────────────────
    // Smooth, deterministic noise: the same clock always gives the same
    // reading, so a headless render is the same picture every time.

    private static double Noise(double t, double seed)
        => 0.5 * Math.Sin(t * 0.9 + seed) + 0.3 * Math.Sin(t * 2.3 + seed * 1.7) + 0.2 * Math.Sin(t * 5.1 + seed * 0.3);

    /// <summary>Seeing, arcseconds (lower is better).</summary>
    public static float Seeing(double minutes) => (float)(0.86 + 0.16 * Noise(minutes / 9.0, 1.3) + 0.05 * Math.Sin(minutes / 2.0));

    /// <summary>Camera sensor temperature, °C, held near its −20 set point.</summary>
    public static float SensorTemp(double minutes) => (float)(-19.8 + 0.35 * Noise(minutes / 6.0, 4.1));

    /// <summary>Data throughput, GB per hour.</summary>
    public static float Throughput(double minutes) => (float)Math.Max(0.2, 1.75 + 0.55 * Noise(minutes / 4.0, 2.2) + 0.25 * Math.Sin(minutes / 1.3));

    /// <summary>Sky brightness, magnitudes per square arcsecond (higher is darker).</summary>
    public static float SkyBrightness(double minutes) => (float)(21.42 + 0.12 * Noise(minutes / 12.0, 0.6));

    /// <summary>Wind, km/h.</summary>
    public static float Wind(double minutes) => (float)(11.0 + 4.0 * Noise(minutes / 5.0, 3.3));

    /// <summary>Relative humidity, %.</summary>
    public static float Humidity(double minutes) => (float)(18.0 + 3.0 * Noise(minutes / 15.0, 7.7));

    // ── Observing log ────────────────────────────────────────────────────────

    /// <summary>The night's journal: what the Journal page shows above the selected target's log.</summary>
    public const string Night = """
        # Night 284 · Alto Vigía

        Clear from dusk, a thin crescent moon set at **23:40**. The dome opened at 22:05 after a
        twenty-minute flush; mirror and air were within **0.4 °C** of each other by 22:50.

        ## Timeline

        1. **22:12** first light on *Wren Globular*, focus V-curve fitted in four steps
        2. **23:58** meridian flip on *Ember Ring*, guiding re-acquired in 41 s
        3. **01:10** second dome flush; seeing dropped from 1.1″ to **0.82″**
        4. **01:48** started the long Hα run on *Lantern Nebula*

        ## Sequencer

        ```
        target  lantern   ZC 1142
        filter  Ha 300s x 24, OIII 300s x 12
        dither  every 3 subs, 8 px
        guide   settle 0.4" within 30 s
        ```

        > The east wall wind gusted to 19 km/h around 00:30. No subs lost; two flagged for review.

        See the [weather log](zenith://weather/284) and the [calibration plan](zenith://calibration).

        ---
        """;

    public static string Log(Target t, string utc) => $$"""
        ## {{t.Name}}

        **{{t.Code}}** · {{KindName(t.Kind)}} · mag {{t.Magnitude:0.0}} · filters `{{t.Filter}}`

        > Seeing settled below **0.9″** after the dome flush at 01:10. Guiding RMS holds at 0.31″.

        | Sub | Filter | Exposure | FWHM |
        |---|---|---:|---:|
        | 41 | Hα | 300 s | 2.1 px |
        | 42 | Hα | 300 s | 2.0 px |
        | 43 | OIII | 300 s | 2.3 px |

        - [x] Focus run at {{utc}} UTC, V-curve minimum 18 412
        - [x] Flats queued for dawn
        - [ ] Re-frame 4′ east to keep the faint outer shell
        - [ ] ~~Swap to binning 2×2~~ not needed tonight

        Next: hand over to **Kestrel Galaxy** once {{t.Name}} drops below 35°.
        """;
}
