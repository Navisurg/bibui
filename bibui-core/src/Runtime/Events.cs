using System.Collections;
using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

/// <summary>
/// This frame's events that nothing has taken yet, oldest first: what
/// <see cref="BibuiRuntime.UnconsumedEvents"/> gives. A view over the runtime's
/// list, not a copy: <c>foreach</c> over it allocates nothing, and an event
/// eaten during the loop is skipped by every loop after it.
/// </summary>
public readonly struct UnconsumedEventList : IEnumerable<BibUiEvent>
{
    private readonly BibUiEvent? _first;
    internal UnconsumedEventList(BibUiEvent? first) => _first = first;

    public Enumerator GetEnumerator() => new(_first);
    // For LINQ in tests and tools; the struct enumerator above is what foreach uses.
    IEnumerator<BibUiEvent> IEnumerable<BibUiEvent>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<BibUiEvent>
    {
        private BibUiEvent? _next;
        private BibUiEvent? _current;
        internal Enumerator(BibUiEvent? first) { _next = first; _current = null; }

        public readonly BibUiEvent Current => _current!;
        readonly object IEnumerator.Current => _current!;

        public bool MoveNext()
        {
            // Next is read before the event is handed out, so eating it changes nothing here.
            while (_next is { } ev)
            {
                _next = ev.Next;
                if (!ev.Consumed) { _current = ev; return true; }
            }
            return false;
        }

        public void Reset() => throw new NotSupportedException();
        public readonly void Dispose() { }
    }
}
