using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bibui.Commands;

// Tables: the model's data, as rows of records keyed by the node they belong to. The tree says what a node
// is and where it sits (its id, its kind, its links); a table holds what it says (a case's title and
// mode). Kept apart so model code works on typed rows and tree code on the tree: walking, moving and
// removing nodes never touches a row, and a row's type is its kind's alone, not one type for every node.
//
// A row is never changed in place: a write in a new generation is a new version of it, stamped with that
// generation; a write in the same generation replaces it (no one has read it). Each row keeps its last few
// versions in a ring of its own, so a generation the model's log holds reads the row as it saw it, the change output shows what a row was,
// and an agent tells that a row it read has changed since (its generation moved). The oldest version
// falls off the ring: a row written more than History times since a generation no longer answers for it.
//
// A table is keyed by its kind's id type (CaseId: a NodeId only a check makes), so it holds no other
// kind's rows and checks nothing on a write. A kind's row is added when its node is made and removed when
// it is released (Nodes.New, Nodes.Release, through its kind's AddRow and RemoveRow), so a write to an id
// with no row is a stale id, and writes nothing. A table of rows any node may have (TId is NodeId: a node's own view rule) adds
// a row on its first write. Ids are never reused, so a released node's id finds nothing ever after.
//
// Asks are total: a node with no row (none written yet, or released) reads as the table's default row, the
// record's declared defaults, so a command reads, changes and writes without checking first.
//
// Nothing is allocated per write: a version overwrites the oldest slot of its row's ring, a row's ring is
// made once and kept with its entry when the row closes, for the next row to reuse. A row is a record
// struct, held in the ring by value: a version copies the row's own fields, never what they refer to
// (a list in a row is shared by its versions).
//
// Generations are the model's clock (ModelClock), shared by the tree and every table: the generation being
// written, and a count of changes, so a command that wrote a row publishes.

/// <summary>
/// The model's generations, shared by its tree and its tables: <see cref="Gen"/>, the one being written
/// (what a new row is stamped with); <see cref="Changes"/>, counted by every table write, so the model knows
/// a command changed something; <see cref="Live"/>, the tree being written, which says which nodes are alive
/// (a table writes no row for a node that is gone). Moved on by the model when it publishes (Nodes.Fork).
/// </summary>
public sealed class ModelClock
{
    public long Gen;
    public long Changes;
    public ModelStore? Live;
}

/// <summary>Who a node is: an id given once and never reused, so a handle to a node that is gone never names another. The one handle a node has; 0 is none. Written <c>#42</c>.</summary>
[JsonConverter(typeof(NodeIdJson))]
public readonly record struct NodeId(long Value)
{
    public static readonly NodeId None = default;
    public bool IsNone => Value == 0;
    public override string ToString() => $"#{Value}";

    /// <summary>Reads <c>#42</c> (or <c>42</c>); false for anything else.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, out NodeId id)
    {
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        bool ok = long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out long v) && v > 0;
        id = ok ? new(v) : default;
        return ok;
    }
}

/// <summary>A <see cref="NodeId"/> in JSON: the string <c>"#42"</c>.</summary>
public sealed class NodeIdJson : JsonConverter<NodeId>
{
    public override NodeId Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && NodeId.TryParse(reader.GetString(), out var id) ? id
        : reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out long n) && n > 0 ? new(n)
        : throw new JsonException("A node id is a string like \"#42\".");

    public override void Write(Utf8JsonWriter writer, NodeId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>A row as it was written, and the generation it was written in.</summary>
public struct Version<T>
{
    public T Row;
    public long Gen;
}

/// <summary>One row of a table: whose (its id), its last History versions as a ring, and how many it has had.</summary>
public struct RowEntry<T, TId>
{
    public TId Id;
    public Version<T>[] Ring;     // History long: the newest at (Written - 1) % History; the next one written overwrites Written % History
    public long Written;          // versions pushed, ever
}

/// <summary>The fields of two rows of one record that differ, each its key and both values (cmdgen's, per record).</summary>
public delegate void RowDiff<T>(in T before, in T after, List<(string Field, Value Before, Value After)> into);

/// <summary>
/// The rows of one kind, by its id type <typeparamref name="TId"/> (CaseId; NodeId for rows any node may have),
/// each with its last <see cref="History"/> versions. Entries dense ([0, Count)): a closed one swapped with
/// the last. Written only by a command (inside Model.Run), read by anyone on the model's thread.
/// </summary>
public sealed class Table<T, TId> where TId : struct
{
    public const int History = 8;

    public required ModelClock Clock;                          // the generation being written; its change count
    public required T Default;                                 // what a node with no row reads as: the record's declared defaults
    public RowEntry<T, TId>[] Entries = new RowEntry<T, TId>[16];
    public int Count;
    readonly Dictionary<TId, int> index = new();               // an id with a row → its entry

    // A table of rows any node may have adds a row on its first write; a kind's own adds it with its node.
    static readonly bool AddsOnWrite = typeof(TId) == typeof(NodeId);

    //- now

    /// <summary><paramref name="id"/>'s row: its newest version; the default for none.</summary>
    public T FromId(TId id) => index.TryGetValue(id, out int e) ? Newest(ref Entries[e]).Row : Default;

    /// <summary>The generation <paramref name="id"/>'s newest version was written in; 0 for no row. A reader that read it at another generation read what is stale.</summary>
    public long GenFromId(TId id) => index.TryGetValue(id, out int e) ? Newest(ref Entries[e]).Gen : 0;

    //- history

    /// <summary>
    /// <paramref name="id"/>'s row as generation <paramref name="gen"/> saw it: the newest version written at or
    /// before it; the default for no row then. False when <paramref name="gen"/> is older than its ring keeps:
    /// <paramref name="row"/> is then the default, and not what it was.
    /// </summary>
    public bool FromIdAt(TId id, long gen, out T row)
    {
        row = Default;
        if (!index.TryGetValue(id, out int e)) return true;
        ref var entry = ref Entries[e];
        long kept = Math.Min(entry.Written, History);
        for (long k = 0; k < kept; k++)   // newest first
        {
            ref var v = ref entry.Ring[(int)((entry.Written - 1 - k) % History)];
            if (v.Gen <= gen) { row = v.Row; return true; }
        }
        return entry.Written <= History;   // before its first version: no row then; else before its ring
    }

    //- writing: in a command

    /// <summary>A new node's row: <paramref name="row"/>, its first version; nothing when it has one already. What making a node of the kind does (Nodes.New).</summary>
    public void AddRow(TId id, in T row)
    {
        if (index.ContainsKey(id)) return;
        if (Count == Entries.Length) Array.Resize(ref Entries, Count * 2);
        int e = Count++;
        ref var entry = ref Entries[e];
        entry.Id = id;
        entry.Ring ??= new Version<T>[History];   // a closed row's ring, reused
        entry.Ring[0] = new() { Row = row, Gen = Clock.Gen };
        entry.Written = 1;
        index[id] = e;
        Clock.Changes++;
    }

    /// <summary>
    /// Writes <paramref name="row"/> as <paramref name="id"/>'s newest: equal to it, nothing; written in this
    /// generation, replaced; else a new version, the oldest falling off the ring. An id with no row is a
    /// stale one (its node released): nothing is written; in a table of rows any node may have, the row is added.
    /// </summary>
    public void SetRow(TId id, in T row)
    {
        if (!index.TryGetValue(id, out int e))
        {
            if (AddsOnWrite && !EqualityComparer<T>.Default.Equals(Default, row)) AddRow(id, row);
            return;
        }
        ref var entry = ref Entries[e];
        ref var newest = ref Newest(ref entry);
        if (EqualityComparer<T>.Default.Equals(newest.Row, row)) return;   // nothing changed: its generation stays
        Clock.Changes++;
        if (newest.Gen == Clock.Gen) { newest.Row = row; return; }
        entry.Ring[(int)(entry.Written % History)] = new() { Row = row, Gen = Clock.Gen };
        entry.Written++;
    }

    /// <summary><paramref name="id"/>'s row ends: its entry is freed (the last moved into its place), its ring kept for the next row. What releasing its node does (Nodes.Release); a command removes a row its node keeps without (a node's own view rule).</summary>
    public void RemoveRow(TId id)
    {
        if (!index.Remove(id, out int e)) return;
        int last = --Count;
        var closed = Entries[e];
        Array.Clear(closed.Ring);   // let go of what its versions refer to
        if (e != last)
        {
            Entries[e] = Entries[last];
            index[Entries[e].Id] = e;
        }
        Entries[last] = new() { Ring = closed.Ring };   // kept for the next row
        Clock.Changes++;
    }

    //- every row

    /// <summary>Every row: <c>foreach (var (id, row, gen) in table)</c>, each its newest version and its generation.</summary>
    public Enumerator GetEnumerator() => new(this);

    public struct Enumerator(Table<T, TId> table)
    {
        int at = -1;
        public bool MoveNext() => ++at < table.Count;
        public readonly (TId Id, T Row, long Gen) Current
        {
            get { ref var v = ref Newest(ref table.Entries[at]); return (table.Entries[at].Id, v.Row, v.Gen); }
        }
    }

    static ref Version<T> Newest(ref RowEntry<T, TId> entry) => ref entry.Ring[(int)((entry.Written - 1) % History)];
}
