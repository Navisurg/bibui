using System.Numerics;
using Bibui.Core;
using Bibui.Core.Theming;
using Bibui.Core.Draw;

namespace Bibui.Core.Draw;

/// <summary>
/// Draws a laid-out tree. Every node's <see cref="UiNode.Rect"/>,
/// <see cref="UiNode.Extent"/> and <see cref="UiNode.ContentSize"/> are final
/// before it is called: it lays nothing out, measures nothing, and writes
/// nothing back to a node. It walks the tree in order and records the frame's
/// draws through <see cref="DrawingContext"/> into a <see cref="DrawList"/>; the
/// dropdown and tooltip roots are linked last, so they land on top.
/// </summary>
internal sealed class Painter
{
    private readonly RenderCaches _caches;
    private readonly Fonts _fonts;

    internal Painter(RenderCaches caches, Fonts fonts)
    {
        _caches = caches;
        _fonts = fonts;
        _drawing = new DrawingContext(fonts, caches);
    }

    // The one recording context, begun again for every frame.
    private readonly DrawingContext _drawing;

    /// <summary>
    /// Whether the frame just painted left anything mid-transition. Reported,
    /// not acted on — scheduling another frame is the host's decision.
    /// </summary>
    internal bool IsAnimating { get; private set; }

    /// <summary>
    /// Records <paramref name="root"/>'s frame into <paramref name="list"/>, from a
    /// <see cref="DrawOpKind.Clear"/> in the theme's background on. Draws nothing:
    /// a renderer executes the list.
    /// </summary>
    internal void Record(DrawList list, UiNode root, SurfaceOptions options, ThemeTokens theme, FrameInput input)
    {
        list.Reset();
        list.Push(DrawOpKind.Clear).Color = DrawingContext.ParseColor(theme.Colors.Background);
        _drawing.Begin(list, options.ScaleFactor, input, theme, options.Width, options.Height);
        Node(root, theme, _drawing.Root);

        // Commit this frame's animations and ask whether any of them are still
        // going. Before EndFrame, which advances the touch clock this reads.
        //
        // Only cells drawn this frame count. One that scrolled off-screen stops
        // having its Next recomputed, so it sits frozen short of Target and
        // would otherwise report "animating" until it evicted — keeping the
        // loop awake for a second after the thing left the screen.
        //
        // Tested on Current, then committed: Current is what this frame put on
        // screen, and Next has not been shown to anyone yet. Testing the
        // committed value instead would stop a frame early, leaving a fade
        // visibly short of where it was asked to end.
        bool animating = false;
        foreach (var entry in _caches.Anims.Entries.Values)
        {
            if (entry.Touched != _caches.Anims.Frame) continue;
            var cell = entry.Value;
            animating |= MathF.Abs(cell.Target - cell.Current) > cell.Epsilon;
            cell.Current = cell.Next;
        }
        IsAnimating = animating;
    }

    private void Node(UiNode node, ThemeTokens theme, DrawContext ctx)
    {
        var frame = new Frame(node.Rect.X, node.Rect.Y, node.Extent.X, node.Extent.Y);

        // Style's transform moves what is painted, not where it was laid out:
        // pushed around the node and its subtree, outside the fade, so a
        // transparency layer is composited where the node is drawn.
        bool moved = node.Style.HasTransform;
        if (moved)
        {
            // The style's transform is over logical px; the same map over physical
            // px is down to logical, through it, and back up.
            float s = ctx.Scale;
            ctx.PushTransform(Matrix3x2.CreateScale(1f / s) * node.Style.TransformFor(node.Rect, node.Extent) * Matrix3x2.CreateScale(s));
        }

        // Style.Opacity fades this node and its whole subtree via a transparency layer.
        bool fade = node.Style.Opacity < 1f;
        if (fade) ctx.PushOpacity(node.Style.Opacity);

        // BackdropBlur must run before the decoration paints this node's own fill/border, so it
        // blurs only what's already behind it — never this node's own content.
        if (node.Style.BackdropBlur is { } blurSigma)
            ctx.BlurBackdrop(frame.X, frame.Y, frame.Width, frame.Height, blurSigma);

        // A box knows its own hotness: HotT is stepped after the paint and eases
        // out over seven frames, so this trails the pointer — a frame late
        // arriving, and lit for about a tenth of a second after it leaves.
        float hotT = 0f, activeT = 0f;
        if (node is Box hotBox) (hotT, activeT) = (hotBox.HotT, hotBox.ActiveT);
        DrawDecoration(frame, node.Style, theme, ctx, hotT, activeT);

        switch (node)
        {
            case Text text: DrawText(text, frame, theme, ctx); break;
            case Icon icon: DrawIcon(icon, frame, theme, ctx); break;
            case Spinner spinner: SpinnerPainter.Draw(SpinnerPainter.FromNative(spinner), frame, theme, ctx); break;
        }

        if (node.FirstChild is not null)
        {
            // A box that claims Clip cuts everything under it to its rect,
            // floating descendants too: the clip is pushed around the children
            // and the backend intersects it with any already in force. The same claim
            // cuts their presses (InteractionsFor), so what is drawn and what
            // is pressed agree. Nothing else clips.
            bool clips = node is Box { Claims: var claims } && (claims & InteractionKinds.Clip) != 0;
            if (clips) ctx.PushClip(ctx.Physical(frame.X, frame.Y, frame.Width, frame.Height));
            for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
                Node(child, theme, ctx);
            if (clips) ctx.PopClip();
        }

        if (node is Box { Draw: { } draw } box)
        {
            // A callback draws in its box's space, clipped to it unless it says
            // Unclipped: the painter's translation and clip are pushed around
            // it, and its own pushes sit above them. Whatever it leaves pushed
            // is popped, and a pop past its own does nothing, so it can neither
            // leak state into the next draw nor undo the painter's.
            float s = ctx.Scale;
            bool clipped = (box.Style.Draw & DrawFlags.Unclipped) == 0;
            ctx.PushTransform(Matrix3x2.CreateTranslation(frame.X * s, frame.Y * s));
            var local = ctx.For(frame.Width, frame.Height);
            if (clipped) ctx.PushClip(local.Rect);
            int paints = _drawing.PaintDepth, floor = _drawing.StateFloor;
            _drawing.StateFloor = _drawing.StateDepth;
            draw(box, local);
            while (_drawing.StateDepth > _drawing.StateFloor) local.Pop();
            (_drawing.StateFloor, _drawing.PaintDepth) = (floor, paints);
            if (clipped) ctx.PopClip();
            ctx.PopTransform();
        }

        if (fade) ctx.PopOpacity();
        if (moved) ctx.PopTransform();
    }

    // A text is one line, drawn from the top-left of its rect as it was
    // measured. Nothing is wrapped, aligned or cut here.
    private static void DrawText(Text text, Frame frame, ThemeTokens theme, DrawContext ctx)
    {
        var font = ctx.Fonts.FontOf(text, theme, ctx.Scale);
        ctx.DrawTextTop(text.Content, frame.X, frame.Y, font, (text.Color ?? UiColor.TextPrimary).Resolve(theme.Colors));
    }

    private static void DrawIcon(Icon icon, Frame frame, ThemeTokens theme, DrawContext ctx)
    {
        float size = icon.Size.In(theme);
        float w = MathF.Min(frame.Width, size), h = MathF.Min(frame.Height, size);
        ctx.DrawIcon(icon.Name, frame.X + w / 2f, frame.Y + h / 2f, MathF.Min(w, h),
            DrawContext.Color((icon.Color ?? UiColor.TextPrimary).Resolve(theme.Colors)));
    }

    /// <summary>
    /// Blends toward <paramref name="to"/> in Oklab, so a hover between two
    /// colours passes through the colours between them rather than the grey a
    /// channel-by-channel blend makes of, say, a blue going to an orange.
    /// Exact at both ends: a box at rest or fully hot draws the colour named.
    /// </summary>
    private static Rgba Blend(Rgba from, Rgba to, float t) => ColorMix.Oklab(from, to, t);

    /// <summary>
    /// Layers a state over what is under it. Called only where the state has a
    /// colour, so a box that never asked for a treatment simply never reaches
    /// it and keeps whatever it had.
    ///
    /// Nothing underneath is the state's own colour at zero alpha, not black at
    /// zero alpha. Rgba is straight alpha, so lerping from black would fade a
    /// red border in through dark red instead of holding the hue and gaining
    /// opacity.
    /// </summary>
    private static Rgba Over(Rgba? under, Rgba state, float t) =>
        Blend(under ?? state.WithAlpha(0), state, t);

    /// <summary>
    /// A token's channels, without spelling them as hex on the way. Every box's
    /// decoration comes through here, so a colour given as numbers stays numbers
    /// and one carrying WithAlpha no longer builds a string per frame to say so.
    /// </summary>
    private static Rgba ResolveRgba(UiColor c, ThemeTokens theme)
    {
        var (r, g, b, a) = c.ResolveColor(theme.Colors);
        return new Rgba(r, g, b, a);
    }

    /// <summary>
    /// What anything drawn lands on: the resting colour, the hot one blended
    /// over it by <paramref name="hotT"/>, the active one over that by
    /// <paramref name="activeT"/>. The same code whatever is drawn — the caller
    /// chooses which colours go in, and a colour left out is a state that does
    /// not apply.
    ///
    /// Hot gives way to active only where there is an active colour to give way
    /// to. Without one it keeps the box lit for the whole press, because fading
    /// out with nothing in its place is just going bare.
    /// </summary>
    private static Rgba? Treatment(
        UiColor? resting, UiColor? hover, UiColor? active,
        float hotT, float activeT, ThemeTokens theme)
    {
        if (active is null) activeT = 0f;
        else hotT *= 1f - activeT;

        Rgba? c = null;
        if (resting is { } r) c = ResolveRgba(r, theme);
        if (hover is { } h) c = Over(c, ResolveRgba(h, theme), hotT);
        if (active is { } a && c is not null) c = Over(c, ResolveRgba(a, theme), activeT);

        // Faded all the way out is nothing at all, not an invisible stroke.
        if (c is { A: 0 }) c = null;
        return c;
    }

    private static void DrawDecoration(
        Frame frame, Style style, ThemeTokens theme, DrawContext ctx, float hotT, float activeT)
    {
        // A treatment is the theme's, so it goes only where the theme's resting
        // colour went: HotEffects on a box that painted its own background is
        // asking for a hover edge, not for its fill to be recoloured underneath
        // it. An explicit hover colour is the caller's own and needs no flag.
        bool edgeIsTheme = (style.Draw & DrawFlags.Border) != 0;
        bool fillIsTheme = (style.Draw & DrawFlags.Background) != 0;
        bool hotEffects = (style.Draw & DrawFlags.HotEffects) != 0;
        bool activeEffects = (style.Draw & DrawFlags.ActiveEffects) != 0;

        // The edge. A flag names the theme's colour; an explicit one replaces it.
        UiColor? edge = style.BorderColor;
        if (edge is null && edgeIsTheme) edge = UiColor.Border;

        UiColor? hotEdge = style.HoverBorderColor;
        if (hotEdge is null && edgeIsTheme && hotEffects) hotEdge = UiColor.BorderHover;

        UiColor? heldEdge = null;
        if (edgeIsTheme && activeEffects) heldEdge = UiColor.Accent;

        Rgba? border = Treatment(edge, hotEdge, heldEdge, hotT, activeT, theme);

        // The fill, said the same way.
        UiColor? fill = style.BackgroundColor;
        if (fill is null && fillIsTheme) fill = UiColor.SurfaceRaised;

        UiColor? hotFill = style.HoverBackgroundColor;
        if (hotFill is null && fillIsTheme && hotEffects) hotFill = UiColor.SurfaceRaisedHover;

        UiColor? heldFill = null;
        if (fillIsTheme && activeEffects) heldFill = UiColor.SurfaceRaisedActive;

        Rgba? background = Treatment(fill, hotFill, heldFill, hotT, activeT, theme);

        bool hasFill = background is not null || style.BackgroundGradient is not null;
        bool hasEdgeBorders = style.BorderLeft is not null || style.BorderTop is not null
            || style.BorderRight is not null || style.BorderBottom is not null;
        if (!hasFill && border is null && style.Shadow is null && !hasEdgeBorders)
            return;

        // Shadow paints first, behind the fill.
        if (style.Shadow is { } shadow)
        {
            ctx.DrawShadow(
                frame.X, frame.Y, frame.Width, frame.Height, style.CornerRadius,
                shadow.OffsetX, shadow.OffsetY, shadow.Blur, shadow.Spread,
                ResolveRgba(shadow.Color, theme));
        }

        // Straight to the typed overloads: the blend already produced colours,
        // and going back through hex to re-parse them would undo the point.
        float? strokeWidth = border is null ? null : style.BorderWidth;

        if (style.BackgroundGradient is { } gradient && gradient.Stops.Count > 0)
        {
            // Stops stay hex: DrawContext's gradient overload takes them that way,
            // and a gradient is drawn far too rarely to be worth widening it for.
            var stops = new (float Stop, string Color)[gradient.Stops.Count];
            for (int i = 0; i < gradient.Stops.Count; i++)
                stops[i] = (gradient.Stops[i].Offset, gradient.Stops[i].Color.Resolve(theme.Colors));

            ctx.DrawRRectGradient(
                frame.X, frame.Y, frame.Width, frame.Height, style.CornerRadius,
                stops, gradient.Start, gradient.End, border, strokeWidth, gradient.Type);
        }
        else if (background is not null || border is not null)
        {
            ctx.DrawRRect(
                frame.X, frame.Y, frame.Width, frame.Height, style.CornerRadius,
                fillColor: background,
                strokeColor: border,
                strokeWidth: strokeWidth);
        }

        // Per-edge borders paint on top of the uniform fill/border, e.g. a nav item's left
        // active-indicator bar or a tab's bottom underline.
        DrawEdgeBorder(ctx, theme, style.BorderLeft, EdgeSide.Left, frame);
        DrawEdgeBorder(ctx, theme, style.BorderTop, EdgeSide.Top, frame);
        DrawEdgeBorder(ctx, theme, style.BorderRight, EdgeSide.Right, frame);
        DrawEdgeBorder(ctx, theme, style.BorderBottom, EdgeSide.Bottom, frame);
    }

    private enum EdgeSide { Left, Top, Right, Bottom }

    private static void DrawEdgeBorder(DrawContext ctx, ThemeTokens theme, EdgeBorder? edge, EdgeSide side, Frame frame)
    {
        if (edge is null) return;
        Rgba color = ResolveRgba(edge.Color, theme);
        bool dashed = edge.LineStyle == BorderLineStyle.Dashed;
        float w = edge.Width;
        float halfW = w / 2f;

        switch (side)
        {
            case EdgeSide.Left:
                ctx.DrawEdge(frame.X + halfW, frame.Y, frame.X + halfW, frame.Y + frame.Height, w, color, dashed);
                break;
            case EdgeSide.Top:
                ctx.DrawEdge(frame.X, frame.Y + halfW, frame.X + frame.Width, frame.Y + halfW, w, color, dashed);
                break;
            case EdgeSide.Right:
                ctx.DrawEdge(frame.X + frame.Width - halfW, frame.Y, frame.X + frame.Width - halfW, frame.Y + frame.Height, w, color, dashed);
                break;
            case EdgeSide.Bottom:
                ctx.DrawEdge(frame.X, frame.Y + frame.Height - halfW, frame.X + frame.Width, frame.Y + frame.Height - halfW, w, color, dashed);
                break;
        }
    }
}
