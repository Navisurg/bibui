using System.Buffers.Binary;
using System.IO.Hashing;

namespace Bibui.Commands;

// Addresses: where a value is, within a Place (Places.cs), as data, never as an object. A row of the
// table is a start (a Place, and a key into it: the root there) or a step from another row (a member of
// its type, an element of it, or its count). Interned: a row is its parent row and one step, and the
// same step from the same row, or the same Place and key, is the same row. So a value carries one int,
// equal addresses are equal ints, and comparing or hashing an address never walks its path.
//
// A start does not hold its key: its Place keeps the keys used this generation (KeyTable), and the start
// holds the key's index there. So a key is the layer's own, whatever it is (a text, a handle, a record
// of both), only the Place knows its type, and the table's rows stay one fixed-size struct.
//
// The table is cleared when the model's generation changes (Addresses.Begin), keeping its capacity; the
// Places stay, and each Place's keys are dropped the first time it is entered after. NOTE(k): an address
// is valid only in the generation it was made in. What outlives that (a cache, a view state) keys on what
// the address means (Addresses.Hash), never on its id.
//
// Reading: a value's type describes its shape (EvalType.Members, PartType), so the evaluator steps
// through it by itself, adding a row per step and reading nothing; only a leaf (a number, a text, a
// count) is read, by walking up to the start and handing its key and the path to its Place's reader,
// which is the layer's own code. So stepping into a large structure costs rows, not reads, and a layer
// is asked only for the leaves an expression uses. The table in use is the current context's
// (Eval.Current), selected for the evaluation: hooks are given values, not contexts.

/// <summary>What a step from an address is: a member of its type, by its index in the type's Members; an element, by its index; its count.</summary>
public enum StepKind : byte { Member, Element, Count }

/// <summary>One step from an address.</summary>
public readonly record struct Step(StepKind Kind, long At);

/// <summary>One row: a start's (Place set: the Place, and its key's index in the Place's keys), or a step's (Parent and Step).</summary>
public struct AddressEntry
{
    public int Place;         // a start's: its Place's id; 0 for a step
    public int Key;           // a start's: its key's index in the Place's keys this generation
    public int Parent;        // a step's: the row it is taken from
    public Step Step;         // a step's
}

/// <summary>
/// The addresses of one generation. The evaluator's: filled as it steps, read by whoever reads a leaf, on
/// its one thread.
/// </summary>
public sealed class AddressTable
{
    public AddressEntry[] Entries = new AddressEntry[256];
    public int Count = 1;                                                // 0 is no address
    public readonly Dictionary<(int Parent, Step Step), int> Children = new();
    public readonly Dictionary<(int Place, int Key), int> Starts = new();
    public long Gen = long.MinValue;                                     // the generation its addresses are of
    public Step[] Path = new Step[16];                                   // a read's path, collected from the leaf up: reused
    public byte[] Hashed = new byte[256];                                // what Addresses.Hash hashes, written field by field: reused
}

/// <summary>
/// A Place's keys this generation: each key entered by, once, by its index. Kept by the Place (Places.Add),
/// cleared the first time the Place is entered in another generation than the address table's last.
/// </summary>
public sealed class KeyTable<K> where K : notnull
{
    public readonly Dictionary<K, int> ByKey = new();   // a key → its index
    public readonly List<K> All = new();                 // an index → its key: what the Place's reader is given
    public long Gen = long.MinValue;                     // the generation its keys are of
}

public static class Addresses
{
    /// <summary>Starts generation <paramref name="gen"/>: when it is another than the table's, its addresses are dropped (its capacity kept).</summary>
    public static void Begin(AddressTable t, long gen)
    {
        if (t.Gen == gen) return;
        t.Gen = gen;
        t.Children.Clear();
        t.Starts.Clear();
        t.Count = 1;
    }

    /// <summary>The index of <paramref name="key"/> in <paramref name="keys"/>: found, or added. Clears the keys of an older generation first.</summary>
    public static int KeyIndex<K>(AddressTable t, KeyTable<K> keys, in K key) where K : notnull
    {
        if (keys.Gen != t.Gen)
        {
            keys.ByKey.Clear();
            keys.All.Clear();
            keys.Gen = t.Gen;
        }
        if (keys.ByKey.TryGetValue(key, out int at)) return at;
        keys.ByKey[key] = keys.All.Count;
        keys.All.Add(key);
        return keys.All.Count - 1;
    }

    /// <summary>The start of the Place <paramref name="place"/> at the key with index <paramref name="key"/> in its keys: found, or made.</summary>
    public static int Start(AddressTable t, int place, int key)
    {
        if (t.Starts.TryGetValue((place, key), out int found)) return found;
        int address = New(t);
        t.Entries[address] = new AddressEntry { Place = place, Key = key };
        t.Starts[(place, key)] = address;
        return address;
    }

    /// <summary>The address one step from <paramref name="parent"/>: found, or made.</summary>
    public static int Child(AddressTable t, int parent, Step step)
    {
        if (t.Children.TryGetValue((parent, step), out int found)) return found;
        int address = New(t);
        t.Entries[address] = new AddressEntry { Parent = parent, Step = step };
        t.Children[(parent, step)] = address;
        return address;
    }

    /// <summary>The start <paramref name="address"/> is reached from: itself, for a start; 0 for no address.</summary>
    public static int StartOf(AddressTable t, int address)
    {
        while (address != 0 && t.Entries[address].Place == 0) address = t.Entries[address].Parent;
        return address;
    }

    /// <summary>
    /// What is at <paramref name="address"/>, a leaf: its Place's reader, given the key and the steps from
    /// the start. None for no address.
    /// </summary>
    public static Value Read(EvalContext ctx, int address)
    {
        var t = ctx.Addresses;
        int depth = 0;
        int at = address;
        while (at != 0 && t.Entries[at].Place == 0)
        {
            if (depth == t.Path.Length) Array.Resize(ref t.Path, depth * 2);
            t.Path[depth++] = t.Entries[at].Step;
            at = t.Entries[at].Parent;
        }
        if (at == 0) return Value.None;
        var path = t.Path.AsSpan(0, depth);
        path.Reverse();
        ref var start = ref t.Entries[at];
        return ctx.Places[start.Place - 1].Read(start.Key, path);
    }

    /// <summary>
    /// What <paramref name="address"/> means, as a number that is the same in every generation: its Place,
    /// its key (as its Place hashes it) and its steps, hashed (XxHash3, 64 bits); 0 for no address. What a
    /// cache or a view state keys on to find an address again after the table is cleared: content, never
    /// the row's id, which is reused each generation. In-process only: a key's hash (a text's) is the
    /// process's.
    /// </summary>
    public static ulong Hash(EvalContext ctx, int address)
    {
        // Its steps from the leaf up, each its kind and its At; then its Place and its key's hash. Written
        // field by field, never as a struct's bytes: a struct's padding is not part of its meaning.
        var t = ctx.Addresses;
        int n = 0;
        int at = address;
        while (at != 0 && t.Entries[at].Place == 0)
        {
            Room(t, n + 9);
            t.Hashed[n] = (byte)t.Entries[at].Step.Kind;
            BinaryPrimitives.WriteInt64LittleEndian(t.Hashed.AsSpan(n + 1), t.Entries[at].Step.At);
            n += 9;
            at = t.Entries[at].Parent;
        }
        if (at == 0) return 0;
        ref var start = ref t.Entries[at];
        Room(t, n + 8);
        BinaryPrimitives.WriteInt32LittleEndian(t.Hashed.AsSpan(n), start.Place);
        BinaryPrimitives.WriteInt32LittleEndian(t.Hashed.AsSpan(n + 4), ctx.Places[start.Place - 1].KeyHash(start.Key));
        n += 8;
        return XxHash3.HashToUInt64(t.Hashed.AsSpan(0, n));
    }

    /// <summary>
    /// The generation of the Place <paramref name="address"/> is in (its start's): with
    /// Hash, what something derived from the data there keys on, so it is made again once that data may
    /// have changed. 0 for no address.
    /// </summary>
    public static long Gen(EvalContext ctx, int address)
    {
        int start = StartOf(ctx.Addresses, address);
        return start == 0 ? 0 : ctx.Places[ctx.Addresses.Entries[start].Place - 1].Gen();
    }

    // Hashed holds at least n bytes.
    static void Room(AddressTable t, int n)
    {
        if (n > t.Hashed.Length) Array.Resize(ref t.Hashed, Math.Max(n, t.Hashed.Length * 2));
    }

    // A new row, its id.
    static int New(AddressTable t)
    {
        if (t.Count == t.Entries.Length) Array.Resize(ref t.Entries, t.Count * 2);
        return t.Count++;
    }
}
