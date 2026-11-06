using System.Numerics;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Core.Runtime;

/// <summary>
/// An agent driving the app, as a cursor a person watching can follow: where its
/// pointer is, whether a button is held, how many times it has clicked, and whether
/// it is there. Not app state: the pointer is folded from the window events the
/// agent sends (<see cref="FromWindowEvents"/>), and <see cref="Connected"/> is set
/// each frame, from a test port's <c>HasClient</c> or as the app likes.
/// <see cref="AgentCursorUi.Build"/> draws it.
/// </summary>
public struct AgentCursor
{
    /// <summary>The agent's name, on the tag beside the pointer.</summary>
    public string Name;
    /// <summary>The pointer's and the tag's colour; the tag's text is light on it.</summary>
    public UiColor Color;
    /// <summary>Where the agent's pointer is, in logical pixels from the window's top-left.</summary>
    public float X, Y;
    /// <summary>A button is held down.</summary>
    public bool Pressed;
    /// <summary>Presses so far: each new one sets off a ring.</summary>
    public int Clicks;
    /// <summary>Shown while true; it fades out a while after it goes false.</summary>
    public bool Connected;

    /// <summary>A cursor before the agent has pointed anywhere: near the top-left corner.</summary>
    public static AgentCursor Start(string name, UiColor color) => new() { Name = name, Color = color, X = 24, Y = 24 };

    /// <summary>
    /// The cursor after <paramref name="events"/>, a frame of the agent's window
    /// events: a pointer event moves it, a press holds it down and counts a click,
    /// a release lets go; other events leave it.
    /// </summary>
    public static AgentCursor FromWindowEvents(AgentCursor cursor, ReadOnlySpan<WindowEvent> events)
    {
        foreach (ref readonly var ev in events)
        {
            if (ev.Kind is not (WindowEventKind.MouseMove or WindowEventKind.MouseDown or WindowEventKind.MouseUp or WindowEventKind.Scroll)) continue;
            (cursor.X, cursor.Y) = (ev.X, ev.Y);
            if (ev.Kind == WindowEventKind.MouseDown) (cursor.Pressed, cursor.Clicks) = (true, cursor.Clicks + 1);
            if (ev.Kind == WindowEventKind.MouseUp) cursor.Pressed = false;
        }
        return cursor;
    }
}

/// <summary>
/// An <see cref="AgentCursor"/> drawn over the UI: an arrow at the agent's pointer,
/// a tag with its name, a ring growing from the tip on each click. It glides to a
/// new point, shrinks a little while pressed, fades in while connected and out a
/// while after. All of that is the runtime's animations, keyed by the cursor;
/// nothing is kept here but one drawing callback per colour.
/// </summary>
public static class AgentCursorUi
{
    private const float Glide = 0.3f;     // seconds to a new point: short, since the app has the event already
    private const float Drag = 0.04f;     // seconds to a new point while a button is held: a drag is many small moves
    private const float Fade = 0.25f;     // seconds to show or hide
    private const float Linger = 2f;      // seconds shown after Connected goes false: an agent may reconnect for every request
    private const float Ripple = 0.4f;    // seconds a click's ring grows and fades
    private const float PressScale = 0.8f;
    private static readonly UiColor Ink = UiColor.Hex("#FAF9F5");

    // At size 1: the arrow's box, the tag's place from the tip, the ring's radii.
    private static readonly Vector2 ArrowSize = new(14, 21);
    private static readonly Vector2 TagOffset = new(13, 17);
    private const float RingFrom = 4f, RingTo = 18f, RingWidth = 2f;

    // A Draw callback cannot be handed the colour without capturing it, so each
    // colour's arrow is made once and kept; an app has one or two.
    private static readonly Dictionary<UiColor, DrawCallback> Arrows = [];

    /// <summary>
    /// Builds <paramref name="cursor"/> under the parent in scope: build it last, at
    /// the root, so it is drawn over everything. It claims nothing, so a click at its
    /// tip reaches what is under it. <paramref name="size"/> scales the pointer and
    /// its ring, not the tag: 1 is an arrow 21 logical pixels tall.
    ///
    /// <para>Returns whether it has settled: glided to the pointer, and done showing
    /// the press and the click. A loop that holds a test port's answers until then
    /// (<c>AnswerCmds(settled: …)</c>) makes the agent's next event wait for the
    /// person watching to see this one.</para>
    /// </summary>
    public static bool Build(BibuiRuntime runtime, in AgentCursor cursor, float size = 1f)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        //- the animations, asked for every frame so they are kept while hidden
        var to = new Vector2(cursor.X, cursor.Y);
        var at = runtime.Animate("agent-cursor", "at", to, Transition.Timed(cursor.Pressed ? Drag : Glide));
        float shown = cursor.Connected ? 1f : 0f;
        var fade = cursor.Connected ? Transition.Timed(Fade) : Transition.Timed(Fade).WithDelay(Linger);
        float opacity = runtime.Animate("agent-cursor", "opacity", shown, fade);
        float pressed = cursor.Pressed ? 1f : 0f;
        float press = runtime.Animate("agent-cursor", "press", pressed, Transition.Timed(0.08f));
        // The ring: an eased count of clicks, the last one's progress its fraction.
        float clicks = runtime.Animate("agent-cursor", "clicks", cursor.Clicks, Transition.Timed(Ripple));
        float ripple = 1f - (cursor.Clicks - clicks);
        bool settled = at == to && press == pressed && clicks == cursor.Clicks;
        if (opacity == 0f) return settled;

        //- a click's ring, growing out from the tip and fading
        if (ripple < 1f)
        {
            float r = size * (RingFrom + (RingTo - RingFrom) * ripple);
            var ring = runtime.Box();
            (ring.FloatingX, ring.FloatingY, ring.FixedPosition) = (true, true, at - new Vector2(r));
            (ring.SizeX, ring.SizeY) = (SemanticSize.Pixels(2 * r), SemanticSize.Pixels(2 * r));
            ring.Style.CornerRadius = r;
            ring.Style.BorderColor = cursor.Color;
            ring.Style.BorderWidth = size * RingWidth;
            ring.Style.Opacity = opacity * (1f - ripple);
        }

        //- the arrow, its tip at the pointer, a little smaller while pressed
        var arrow = runtime.Box();
        var extent = ArrowSize * size * (1f - (1f - PressScale) * press);
        (arrow.FloatingX, arrow.FloatingY, arrow.FixedPosition) = (true, true, at);
        (arrow.SizeX, arrow.SizeY) = (SemanticSize.Pixels(extent.X), SemanticSize.Pixels(extent.Y));
        arrow.Style.Opacity = opacity;
        if (!Arrows.TryGetValue(cursor.Color, out var draw)) Arrows[cursor.Color] = draw = ArrowIn(cursor.Color);
        arrow.Draw = draw;

        //- the tag naming the agent, at its usual size, beside the arrow
        var tag = runtime.Box();
        (tag.FloatingX, tag.FloatingY, tag.FixedPosition) = (true, true, at + size * TagOffset);
        tag.SizeX = tag.SizeY = SemanticSize.ChildrenSum();
        tag.Style.Padding = EdgeInsets.Symmetric(horizontal: 8, vertical: 3);
        tag.Style.CornerRadius = 6f;
        tag.Style.BackgroundColor = cursor.Color;
        tag.Style.Opacity = opacity;
        using (runtime.Parent(tag))
            runtime.Text(cursor.Name, ComponentSize.Sm, Ink);

        return settled;
    }

    // An arrow pointer filling its box, tip at the top-left, in color with a light
    // outline. A Draw draws in physical pixels, so the box's rect carries both the
    // size asked for and the screen's scale.
    private static DrawCallback ArrowIn(UiColor color) => (_, ctx) =>
    {
        float k = ctx.Rect.Width / ArrowSize.X;
        var path = ctx.ScratchPath();
        path.MoveTo(0, 0);
        path.LineTo(0, 16 * k);
        path.LineTo(4 * k, 12.5f * k);
        path.LineTo(7 * k, 19 * k);
        path.LineTo(9.5f * k, 18 * k);
        path.LineTo(6.5f * k, 11.5f * k);
        path.LineTo(12 * k, 11.5f * k);
        path.Close();
        ctx.DrawPath(path, new Paint { Fill = ctx.Resolve(color), Stroke = ctx.Resolve(Ink), StrokeWidth = 1.2f * k });
    };
}
