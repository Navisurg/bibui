using System.ComponentModel;
using Bibui.Commands;

// A vessel segmentation, faked: what case-viewer's worker computes from one NMF mode. Kept in a cache,
// keyed by what it is computed from and asked for by key. A miss records the key and answers pending;
// a step after the frame (a worker, in case-viewer) makes what was asked for, so a read never blocks.
//
// To the evaluator, two Places, declared here ([Place]) and registered by cmdgen's VesselLayerPlaces.Add
// where the cache is made:
//
//   vessels(source, index) a segmentation, by its key: its paths and what they add up to; none until made
//   vessel_cache           the cache: every segmentation asked for, its key and result, and the runs
//
// Asking happens when a leaf is read (count(vessels("Case 0", 3).paths)), never when the address is built. It
// knows nothing of the model: whoever reads a segmentation builds its key from the facts it has (the
// board's case panel passes its title as the source: vessels(it.title, it.mode.index)).

[Description("What a segmentation is computed from: the source it is segmented from, by name, and the index of an NMF mode of it.")]
public readonly record struct VesselKey(string Source, long Index);

[Description("A vessel segmentation of one NMF mode: its paths, and what they add up to.")]
public sealed class Vessels
{
    public long Segments;
    public double VolumeMl;
    public List<VesselPath> Paths = [];   // in the order they branch: a path's parent comes before it
}

[Description("One vessel path: a run between two branch points.")]
public readonly record struct VesselPath(
    long Index,         // its place in Paths
    long Parent,        // the path it branches from; -1 for a root
    double LengthMm,
    double RadiusMm);

[Description("A segmentation asked for: what it is computed from, and its result once made (none while pending).")]
public sealed class VesselEntry
{
    public VesselKey Key;
    public Vessels? Made;   // null while pending: Produce's to set
}

[Description("The vessel cache: every segmentation asked for, in the order asked, and how many have been run.")]
public sealed class VesselCache
{
    public readonly List<VesselEntry> Entries = new();          // in the order asked, only ever added to
    public readonly Dictionary<VesselKey, int> ByKey = new();    // a key's entry: its index in Entries. Not stepped by the evaluator: vessels(key) is the way in by key
    public int Runs;                                             // segmentations run, ever: none runs twice
    public long Gen;                                             // moves when an entry is asked for or made: its Places' generation (VesselLayer.Gen)
}

static class VesselLayer
{
    //- its Places: each a query, finding its data at a key (cmdgen writes their registration, VesselLayerPlaces)

    [Place<VesselKey, Vessels>("vessels", "A vessel segmentation, by what it is computed from (a source, by name, and the index of an NMF mode of it); none until it is made.")]
    public static Vessels? Segmentation(VesselCache c, in VesselKey key) => c.Entries[Ask(c, key)].Made;

    [Place<NoKey, VesselCache>("vessel_cache", "Every segmentation asked for, in the order asked, and how many have been run.")]
    public static VesselCache Cache(VesselCache c) => c;

    [PlaceGen]
    public static long Gen(VesselCache c) => c.Gen;

    // The index in Entries of a key's segmentation: asked for the first time it is (made after the frame).
    public static int Ask(VesselCache c, in VesselKey key)
    {
        if (c.ByKey.TryGetValue(key, out int at)) return at;
        c.ByKey[key] = c.Entries.Count;
        c.Entries.Add(new VesselEntry { Key = key });
        c.Gen++;   // the cache's listing has another entry
        return c.Entries.Count - 1;
    }

    // Makes what was asked for: in case-viewer, a worker's, on its own thread. A segmentation's paths
    // branch from earlier ones, so a path's parent comes before it; what they add up to is theirs.
    // Returns how many it made.
    public static int Produce(VesselCache c)
    {
        int made = 0;
        foreach (var s in c.Entries)
        {
            if (s.Made is not null) continue;
            var seed = System.IO.Hashing.XxHash3.HashToUInt64(System.Runtime.InteropServices.MemoryMarshal.AsBytes(s.Key.Source.AsSpan()));
            var rng = new Random(unchecked((int)seed * 7919 + (int)s.Key.Index));   // the same each run, from the key (string and HashCode hashes are seeded per process)
            var vessels = new Vessels { Segments = 20 + rng.Next(60) };
            double volume = 0;
            for (int i = 0; i < vessels.Segments; i++)
            {
                double length = Math.Round(4 + rng.NextDouble() * 26, 1), radius = Math.Round(0.3 + rng.NextDouble() * 1.7, 2);
                vessels.Paths.Add(new VesselPath(i, i == 0 ? -1 : rng.Next(i), length, radius));
                volume += Math.PI * radius * radius * length / 1000;   // mm³ to ml
            }
            vessels.VolumeMl = Math.Round(volume, 2);
            s.Made = vessels;
            c.Runs++;
            c.Gen++;
            made++;
        }
        return made;
    }

    // The ways a segmentation is read: what the tree lens's by says of each path (it the path), a number
    // shaded blue to red, a condition lit or faint; none, read plain. Only data: the tree lens evaluates
    // them and colours by what they gave. A reading added here is offered in a case's panel.
    static readonly Expr Radius = Exprs.It.Get(VesselPathFields.RadiusMm), Length = Exprs.It.Get(VesselPathFields.LengthMm);
    public static readonly Expr?[] Interpretations =
    [
        null,                                                                       // plain
        Radius,                                                                     // thick red, thin blue
        Length,                                                                     // long red, short blue
        Radius.Op(BinaryOp.Greater, Exprs.Lit(1.0)),                                // the thick ones
        Exprs.It.Get(VesselPathFields.Parent).Op(BinaryOp.Less, Exprs.Lit(0))
            .Op(BinaryOp.Or, Exprs.It.Get(VesselPathFields.Index).Op(BinaryOp.Less, Exprs.Lit(5))),   // the trunk and its first branches
        Length.Op(BinaryOp.Greater, Exprs.Lit(15.0)).Op(BinaryOp.And, Radius.Op(BinaryOp.Less, Exprs.Lit(1.0))),   // long and thin
    ];
}
