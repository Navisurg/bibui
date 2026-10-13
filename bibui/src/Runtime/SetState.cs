namespace Bibui.Runtime;

/// <summary>
/// State setter handed to a stateful build function. Supports two forms:
///
/// <list type="bullet">
///   <item><see cref="Set"/>: replace the boxed state with a new value. This is the
///         normal render-time form invoked from build code acting on what a box reports.</item>
///   <item><see cref="Update"/>: atomically read the latest boxed state, apply an
///         updater, and write the result back. Use this from async continuations to
///         avoid stale-closure clobbering.</item>
/// </list>
///
/// <para>Stale-closure problem: when an async handler captures <c>state</c> at render
/// time and later runs <c>setState.Set(state with { ... })</c> after an <c>await</c>,
/// any field changed during the await is reverted because the captured <c>state</c>
/// is the snapshot from when the handler was built. The visible symptom is a dialog
/// that closes and immediately reopens after an async create flow finishes, or a
/// filter selection that silently reverts. Calling <c>setState.Update(s =&gt; s with { ... })</c>
/// from the async continuation reads the current state at apply time and preserves
/// any concurrent change.</para>
///
/// <para>Both methods are safe to call from any thread.</para>
/// </summary>
public sealed class SetState<T>
{
    private readonly Action<T> _set;
    private readonly Action<Func<T, T>> _update;

    internal SetState(Action<T> set, Action<Func<T, T>> update)
    {
        _set = set;
        _update = update;
    }

    /// <summary>Replaces the boxed state with <paramref name="newState"/>.</summary>
    public void Set(T newState) => _set(newState);

    /// <summary>
    /// Atomically reads the latest boxed state, applies <paramref name="updater"/>, and
    /// writes the result back. Call this from async continuations (post-<c>await</c> code)
    /// so a concurrent state change during the await is preserved instead of being
    /// clobbered by a stale captured snapshot.
    /// </summary>
    public void Update(Func<T, T> updater) => _update(updater);
}
