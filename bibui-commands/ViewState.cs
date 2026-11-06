namespace Bibui.Commands;

// View state: the runtime of a view, kept between frames for the model node that presents it (a panel,
// a tab), and nothing that is a fact: a cursor and a selection, scroll, pan and zoom, a drag in progress.
// Never in the model, the log or a diff, so moving a view never makes a generation; an agent reads it
// through a query the app answers. One table for every kind of view, keyed by node: a view kind keeps its
// own data as its User, so the table needs no field per kind.
//
// Lifetime is by use: a view state lives while the build asks for it each frame, and Evict drops it the
// first frame nothing does, or once its node is gone. Nothing frees it explicitly.

/// <summary>One view's runtime, keyed by the node that presents it.</summary>
public sealed class ViewState
{
    public NodeId Node;           // the node presenting the view: the table's key, and the id its model row is kept by too
    public long LastFrameTouched; // the frame the build last asked for it
    public object? User;          // the view kind's own data: made on first ask (Views.User), of the kind's type; disposed with the state if IDisposable
}

/// <summary>Every view's state, by the node that presents it. The build's thread only.</summary>
public sealed class ViewStates
{
    public readonly Dictionary<NodeId, ViewState> ByNode = new();
}

public static class Views
{
    /// <summary>The view state of <paramref name="node"/>, made if it has none; touched in <paramref name="frame"/>, so <see cref="Evict"/> keeps it.</summary>
    public static ViewState From(ViewStates t, NodeId node, long frame)
    {
        if (!t.ByNode.TryGetValue(node, out var vs)) t.ByNode[node] = vs = new ViewState { Node = node };
        vs.LastFrameTouched = frame;
        return vs;
    }

    /// <summary>The view kind's own data: made the first time it is asked for, the same object after while the view state lives.</summary>
    public static T User<T>(ViewState vs) where T : class, new()
    {
        if (vs.User is not T user) vs.User = user = new T();
        return user;
    }

    /// <summary>
    /// Drops the view states nothing asked for in <paramref name="frame"/>, and those whose node
    /// is gone from <paramref name="tree"/>; a User that holds what must be let go
    /// (<see cref="IDisposable"/>) is disposed with it. Called once a frame, after the build.
    /// </summary>
    public static void Evict(ViewStates t, ModelStore tree, long frame)
    {
        // A dictionary may lose entries while it is enumerated.
        foreach (var (node, vs) in t.ByNode)
            if (vs.LastFrameTouched < frame || !Nodes.Holds(tree, node))
            {
                (vs.User as IDisposable)?.Dispose();
                t.ByNode.Remove(node);
            }
    }
}
