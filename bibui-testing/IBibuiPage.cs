
namespace Bibui.Testing;

/// <summary>
/// Playwright-style semantic test API for a bibui runtime.
///
/// Tests read the tree the runtime laid out and painted, and drive it by
/// dispatching events through the runtime's event system rather than the OS. This mirrors how Playwright uses Chrome
/// DevTools Protocol to talk to the browser's DOM.
///
/// Usage:
///   page.Click("inc");     // a press and release at the box named "inc"
///   page.GetText("label");
/// </summary>
public interface IBibuiPage : IDisposable
{
    // ── Tree query ────────────────────────────────────────────────────────────

    /// <summary>
    /// The node with the given Id in the tree just rendered, or null. Read what
    /// is needed from it; the next frame resets and reuses it.
    /// </summary>
    UiNode? FindById(string id);

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Clicks the box with the given Id: a press and a release at its centre.
    /// No-op if no box that claims a press carries the name.
    /// </summary>
    void Click(string id);

    /// <summary>Double-click, which on text selects a word.</summary>
    void DoubleClick(string id);

    /// <summary>Triple-click, which on text selects a line.</summary>
    void TripleClick(string id);

    /// <summary>Right-click, which on text opens the text context menu there.</summary>
    void RightClick(string id);

    /// <summary>
    /// Replaces the whole text of the field with the given Id, the way a
    /// person would: presses it, then selects all and types the value.
    /// </summary>
    void Fill(string inputId, string value);

    // ── State inspection ──────────────────────────────────────────────────────

    /// <summary>Returns true if the box with the given Id claims Disabled.</summary>
    bool IsDisabled(string id);

    /// <summary>Returns true if a node with the given Id is in the tree just rendered.</summary>
    bool IsVisible(string id);

    /// <summary>
    /// The words of a text, or of the first text under the node — a button's
    /// label. Null when there is none.
    /// </summary>
    string? GetText(string id);

    /// <summary>The logical-pixel rect of the node with the given Id.</summary>
    System.Drawing.RectangleF GetBounds(string id);

    // ── Async helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="condition"/> up to <paramref name="timeout"/>,
    /// checking every <paramref name="pollInterval"/>. Throws if the condition
    /// is not satisfied within the timeout.
    /// </summary>
    void WaitFor(Func<bool> condition, TimeSpan timeout, TimeSpan? pollInterval = null, string? message = null);

    // ── Keyboard ───────────────────────────────────────────────────

    /// <summary>
    /// One key, with modifiers, as the window would report it. Says what the
    /// per-key helpers cannot: Delete, Home and End, shift-extends-selection,
    /// ctrl-jumps-a-word, Ctrl+A.
    /// </summary>
    void Press(Key key, bool ctrl = false, bool shift = false);

    /// <summary>
    /// The clipboard this page's copy and paste use. Not the system one — a
    /// test can seed it and read it back without touching the real clipboard
    /// or needing one to exist.
    /// </summary>
    string Clipboard { get; set; }

    /// <summary>
    /// Types one character, as the keyboard would. It reaches the field the
    /// application has focused, which reads it when it asks.
    /// </summary>
    void TypeChar(char ch);

    /// <summary>Presses Backspace.</summary>
    void PressBackspace();

    /// <summary>Presses the left arrow.</summary>
    void PressArrowLeft();

    /// <summary>Presses the right arrow.</summary>
    void PressArrowRight();

    // ── Visual ────────────────────────────────────────────────────────────────

    /// <summary>Returns the current render as a PNG byte array.</summary>
    byte[] Screenshot();

}
