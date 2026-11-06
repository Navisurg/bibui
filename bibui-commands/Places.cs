namespace Bibui.Commands;

// Places: the ways into the layers' data. The evaluator knows no layer's data structures; it knows
// Places, and each layer answers for its own data through one. A Place is a name, a key type (the layer's own: a text, a number, a record of them), the shape of the
// root value at a key, and a reader: registered once, at startup, by the layer that owns the data, and
// kept. An expression enters a Place with a key (vessels("Case 0", 3)), gets its start at that key
// (Addresses.cs), steps from there by the root's shape, and reads a leaf through the Place's reader. A
// key may come from anywhere (an expression, a fact, another Place's data, code): the Place says only what
// one is and what is found at one. It keeps the keys it was entered by this generation (its KeyTable), and
// a start holds its key's index there, so the reader is handed the layer's own key. Getting data out of
// another layer is building an address in its Place; no layer reaches into another.
//
// A layer declares its Places where it defines them ([Place<K, D>] on the function that finds its data
// at a key, its query), and cmdgen writes the registration: the shape of everything reachable from D,
// the key's fields as the entry's arguments, and the reader, D's along the path from what the query
// gives. The model registers its Place by hand: its nodes are stepped by hooks, and it is entered by its
// own symbols (root, $15), not by name.
//
// The registry itself is a Place, `places`: each Place's name, how it is entered, and what it is.
//
// A Place has a generation: a number that moves whenever what its reader would give at any key may have
// changed (the model's is its published generation). Coarse on purpose: one per Place, not per key, so
// the layer bumps one counter and never tracks which keys a change touched; the cost is that a change
// anywhere in a Place makes everything derived from it stale. Something derived from a value's data (a
// tree drawn from a segmentation's paths) keys on what its address means and its Place's generation
// (Addresses.Hash, Addresses.Gen): the same key and generation is the same result, and a new generation
// is a new key, never an invalidation. A Place given none is taken never to change (a file read once):
// its generation is always 0.

/// <summary>The key of a Place that has one root: entered by its bare name (vessel_cache).</summary>
public readonly record struct NoKey;

/// <summary>A Place's reader, typed: the value at <paramref name="path"/> from its data at <paramref name="key"/>; a number, a text, a count. None for a path its data has not (an element past its last), or data not there (pending).</summary>
public delegate Value PlaceRead<K>(in K key, ReadOnlySpan<Step> path) where K : notnull;

/// <summary>A Place's reader as the address table calls it: its key by its index in the Place's keys.</summary>
public delegate Value AddressRead(int key, ReadOnlySpan<Step> path);

/// <summary>A Place's key's hash, by its index in the Place's keys: what Addresses.Hash hashes for the start.</summary>
public delegate int KeyHash(int key);

/// <summary>A key made from an entry's arguments, evaluated, in the key's field order; false when they make none (too few, of another kind).</summary>
public delegate bool KeyFromArgs<K>(ReadOnlySpan<Value> args, out K key) where K : notnull;

/// <summary>One Place, as registered: what it is called, what its key and data are, and how a leaf of it is read.</summary>
public sealed class PlaceInfo
{
    public required string Name;          // what an expression enters it by: vessels
    public string Description = "";
    public int Id;                        // its index in EvalContext.Places, + 1: what its starts hold
    public EvalType? KeyType;             // its key's shape: the entry's arguments, a member each; null for NoKey, or a Place not entered by name
    public required EvalType Type;        // the root's shape at a key
    public required AddressRead Read;     // a leaf: the key (by index) and the path from it
    public required KeyHash KeyHash;      // a key's hash (by index): for Addresses.Hash
    public required Func<long> Gen;       // its generation: moves when what it reads may have changed; always 0 for a Place that never changes
    public string Signature = "";         // how it is entered, as text: vessels(case: int, index: int) → vessels
}

/// <summary>A Place, typed by its key, and the keys it was entered by this generation: what code that holds it builds starts with (Places.At).</summary>
public readonly record struct Place<K>(int Id, EvalType Type, KeyTable<K> Keys) where K : notnull;

/// <summary>
/// Declares a layer's generation: a static function of the layer's state, on the class
/// whose functions are its Places' queries, giving a number that moves whenever any of its Places' data
/// may have changed. cmdgen gives it to each of the class's Places. A layer without one is taken never to change.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PlaceGenAttribute : Attribute;

/// <summary>
/// Declares a Place: the function it is on is the Place's query, giving its data (<typeparamref name="D"/>)
/// at a key (<typeparamref name="K"/>); none while there is none (pending). Its parameters are the layer's
/// state, then <c>in K key</c> (none for <see cref="NoKey"/>); it returns D (or D?). cmdgen checks the
/// function against K and D, and writes the registration (<c>{Class}Places.Add</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class PlaceAttribute<K, D>(string name, string description) : Attribute where K : notnull
{
    public readonly string Name = name;
    public readonly string Description = description;
}

public static class Places
{
    /// <summary>
    /// Registers a Place in <paramref name="ctx"/>: its data's shape, its reader, and how it is entered.
    /// Keyless (NoKey), its name is its root; with a key packer, its name is a function from the key's
    /// fields to the root at that key; with neither, it is entered by its owner's own
    /// symbols (the model's). <paramref name="gen"/> is its generation; none, it never changes. A name
    /// another symbol has is a mistake: EvalContext.Add throws.
    /// </summary>
    public static Place<K> Add<K>(EvalContext ctx, string name, string description, EvalType type, PlaceRead<K> read,
        EvalType? keyType = null, KeyFromArgs<K>? key = null, Func<long>? gen = null) where K : notnull
    {
        var keys = new KeyTable<K>();
        var info = new PlaceInfo
        {
            Name = name, Description = description, Id = ctx.Places.Count + 1, KeyType = keyType, Type = type,
            Read = (k, path) => read(keys.All[k], path),
            KeyHash = k => EqualityComparer<K>.Default.GetHashCode(keys.All[k]),
            Gen = gen ?? Never,
        };
        ctx.Places.Add(info);
        var place = new Place<K>(info.Id, type, keys);
        if (typeof(K) == typeof(NoKey))
        {
            info.Signature = $"{name} → {type.Name}";
            ctx.Add(name, () => At(ctx, place, default(K)!));
        }
        else if (key is not null)
        {
            info.Signature = $"{name}({Arguments(keyType)}) → {type.Name}";
            ctx.Add(name, EvalTypes.Function(name, description,
                (in Value _, ReadOnlySpan<Value> args, Expr.Call _) => key(args, out var k) ? At(ctx, place, k) : Value.None));
        }
        else info.Signature = $"{name}: entered by its own symbols → {type.Name}";
        return place;
    }

    static long Never() => 0;

    /// <summary>The root of <paramref name="place"/> at <paramref name="key"/>: its type, at its start there. Reads nothing.</summary>
    public static Value At<K>(EvalContext ctx, Place<K> place, in K key) where K : notnull =>
        new(place.Type, 0, 0, 0, null) { Address = Start(ctx, place, key) };

    /// <summary>The start of <paramref name="place"/> at <paramref name="key"/>: the key kept in the Place's keys, the start found or made.</summary>
    public static int Start<K>(EvalContext ctx, Place<K> place, in K key) where K : notnull =>
        Addresses.Start(ctx.Addresses, place.Id, Addresses.KeyIndex(ctx.Addresses, place.Keys, key));

    /// <summary>The key of the start <paramref name="address"/> is reached from, when that start is in <paramref name="place"/>; default for any other.</summary>
    public static K? KeyOf<K>(EvalContext ctx, Place<K> place, int address) where K : notnull
    {
        int start = Addresses.StartOf(ctx.Addresses, address);
        ref var e = ref ctx.Addresses.Entries[start];
        return start != 0 && e.Place == place.Id ? place.Keys.All[e.Key] : default;
    }

    // A key's fields as an entry's arguments: case: int, index: int.
    static string Arguments(EvalType? key) =>
        key?.Members is { } members ? string.Join(", ", members.Select(m => $"{m.Name}: {m.Type.Name}")) : "";

    //- the registry, as a Place: places, each its name, how it is entered, and what it is

    public const string ListingName = "places";

    static readonly EvalType ListedType = new()
    {
        Name = "place", Description = "A way into a layer's data: entered by its name, with its key.", Kind = EvalKind.Parts,
        Members = [("name", EvalTypes.Text), ("signature", EvalTypes.Text), ("description", EvalTypes.Text)],
        Fields = ["name", "signature", "description"],
    };
    static readonly EvalType ListingType = new() { Name = "place[]", Description = "Every Place an expression may enter.", Kind = EvalKind.Parts, PartType = ListedType };

    static Places() => ListedType.PartName = i => ListedType.Members![i].Name;

    /// <summary>Registers <c>places</c>: every Place in <paramref name="ctx"/>, itself among them.</summary>
    public static void AddListing(EvalContext ctx) =>
        Add<NoKey>(ctx, ListingName, "Every Place an expression may enter: its name, how it is entered, and what it is.", ListingType,   // its generation: none; Places are registered once, at startup
            (in NoKey _, ReadOnlySpan<Step> path) => path switch
            {
                [{ Kind: StepKind.Count }] => Value.Of((long)ctx.Places.Count),
                [{ Kind: StepKind.Element, At: var i }, { Kind: StepKind.Member, At: var m }] when i < ctx.Places.Count => m switch
                {
                    0 => Value.Of(ctx.Places[(int)i].Name),
                    1 => Value.Of(ctx.Places[(int)i].Signature),
                    2 => Value.Of(ctx.Places[(int)i].Description),
                    _ => Value.None,
                },
                _ => Value.None,
            });
}
