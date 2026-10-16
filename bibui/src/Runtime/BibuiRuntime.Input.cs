using Bibui.TextEdit;

namespace Bibui.Runtime;

/// <summary>
/// What input means, once the window has said what happened. The window's job is
/// the OS boundary — turning an OS event into a BibUiEvent — and acting on that
/// meaning is this file's. The list a window produces is the list a test server
/// can hand over, so synthesised and real input take one path.
/// </summary>
public sealed partial class BibuiRuntime
{
    /// <summary>
    /// How this reaches the system clipboard. By default, the clipboard of the
    /// window the runtime runs in (<see cref="WindowClipboard"/>, set by the
    /// platform when it opens), and none (null, nothing written) for a runtime
    /// with no window. A seam because a test asserting on the real clipboard
    /// would clobber whatever the developer had copied, and headless CI may have
    /// none at all.
    /// </summary>
    public Func<string?> ClipboardGet
    {
        get => _clipboardGet ??= () => WindowClipboard.Get?.Invoke();
        set => _clipboardGet = value;
    }

    /// <inheritdoc cref="ClipboardGet"/>
    public Action<string> ClipboardSet
    {
        get => _clipboardSet ??= text => WindowClipboard.Set?.Invoke(text);
        set => _clipboardSet = value;
    }

    // Created once, on first read, so passing ClipboardGet along per key press
    // allocates nothing.
    private Func<string?>? _clipboardGet;
    private Action<string>? _clipboardSet;

    /// <summary>
    /// The clipboard of the window the runtime runs in: set by a platform when it
    /// opens. What the default <see cref="ClipboardGet"/> and
    /// <see cref="ClipboardSet"/> reach; an app that set its own keeps it.
    /// </summary>
    public (Func<string?>? Get, Action<string>? Set) WindowClipboard { get; set; }

    /// <summary>
    /// Acts on everything the window said happened this frame, in order. Called
    /// before the frame is rendered, because input changes what there is to
    /// draw; rendering itself is the caller's business.
    /// </summary>
    public void ProcessEvents(IReadOnlyList<BibUiEvent> events)
    {
        // Under the render lock: BibuiTestServer serves each client on its own
        // thread, and a batch landing mid-build would mutate the tree an app is
        // walking. Re-entrant, so the window pays nothing.
        lock (_renderLock)
        {
            foreach (var ev in events)
            {
                NotePointer(ev);

                // Nothing is taken: every event stays in the queue for the boxes
                // that ask. A pointer event asks for a frame, because hot state
                // follows the pointer.
                EnqueueEvent(ev);
                if (ev.Kind is BibUiEventKind.MouseDown or BibUiEventKind.MouseUp or BibUiEventKind.MouseMove)
                    RequestFrame();
            }
        }
    }

    /// <summary>One event, for a caller that has exactly one.</summary>
    public void ProcessEvent(BibUiEvent ev) => ProcessEvents(new[] { ev });
}
