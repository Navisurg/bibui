using Bibui.Core.TextEdit;

namespace Bibui.Core.Runtime;

// Focus: the one box the keyboard goes to, by its id. It is decided during a
// build for the next frame and fixed for the whole of a frame, so every ask in
// a frame gets the same answer. A box that claims the keyboard takes it with a
// left press on it; the focused box keeps it by asking again, in a frame with
// no press. So a box no longer built, or a press that lands anywhere else,
// ends it with nothing to clear, and a press on another such box moves it. A
// component that ends an edit itself (Enter, Escape) gives it up with
// Focus(""). "" is no box: it matches no id.
public sealed partial class BibuiRuntime
{
    private string _focused = "";       // the box with focus this frame
    private string _focusedNext = "";   // next frame's, decided during the build: "" unless a box takes or keeps it
    private string _focusedLast = "";   // last frame's: a box focused now and not then has just gained it

    /// <summary>
    /// Gives focus to the box named <paramref name="id"/> from the next frame;
    /// <c>""</c> gives it to none. For what the app knows and the pointer does
    /// not: a shortcut that sends the keys to a search field, a palette that
    /// opens with its query focused. A box claiming
    /// <see cref="InteractionKinds.Keyboard"/> takes focus by itself on a press.
    /// </summary>
    public void Focus(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_focusedNext == id) return;
        _focusedNext = id;
        RequestFrame();
    }

    /// <summary>Whether the box named <paramref name="id"/> has focus this frame: for a look drawn before the box asks (an input's outline).</summary>
    public bool IsFocused(string id) => id.Length > 0 && id == _focused;

    // A box claiming the keyboard asked: whether it has focus, and whether it
    // takes it (a press on it) or keeps it (it has it, nothing else has taken
    // it, and no press landed this frame, wherever the box that took the
    // press was built).
    private void AskFocus(string id, ref Interaction interaction)
    {
        interaction.Focused = id == _focused;
        interaction.FocusGained = interaction.Focused && id != _focusedLast;
        if (interaction.Pressed) _focusedNext = id;
        else if (interaction.Focused && _focusedNext.Length == 0 && !PressedThisFrame()) _focusedNext = id;
    }

    private bool PressedThisFrame()
    {
        for (var ev = _firstEvent; ev is not null; ev = ev.Next)
            if (ev.Kind == BibUiEventKind.MouseDown) return true;
        return false;
    }

    // After the build: what was decided becomes the frame's focus.
    private void EndFocus()
    {
        if (_focusedNext != _focused) RequestFrame();
        (_focusedLast, _focused, _focusedNext) = (_focused, _focusedNext, "");
    }
}
