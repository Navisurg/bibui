using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// A handle around a <see cref="BibUiEvent"/> with a consume flag. Widgets that
/// handle an event call <see cref="Eat"/>, which hides it from subsequent
/// iterators.
/// </summary>
public sealed class BibUiEventHandle
{
    public BibUiEvent Event { get; }
    public bool Consumed { get; private set; }
    public void Eat() => Consumed = true;
    internal BibUiEventHandle(BibUiEvent ev) { Event = ev; }
}
