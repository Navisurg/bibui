using System.Numerics;

namespace Bibui;

public abstract class UiNode
{
    public string? Id { get; init; }
    /// <summary>
    /// Settable, not init-only: a box from <c>BibuiRuntime.BoxFromId</c> is
    /// retained and re-described each frame. Prefer setting the properties of
    /// the Style already there over assigning a new one — the retained box owns
    /// its instance and clears it in place.
    /// </summary>
    public Style Style { get; set; } = new();

    // ── The tree, as links ──────────────────────────────────────────────────
    // Set when a node is pushed into its parent, and only then: there is one
    // way into the tree. Read by the layout solver.

    /// <summary>The node this one was pushed into, or null.</summary>
    public UiNode? Parent { get; internal set; }

    /// <summary>The previous sibling, or null.</summary>
    public UiNode? Prev { get; internal set; }

    /// <summary>The next sibling, or null.</summary>
    public UiNode? Next { get; internal set; }

    /// <summary>The first node pushed into this one, or null.</summary>
    public UiNode? FirstChild { get; internal set; }

    /// <summary>The last node pushed into this one, or null.</summary>
    public UiNode? LastChild { get; internal set; }

    /// <summary>
    /// Appends <paramref name="child"/> to <paramref name="parent"/>'s children.
    /// A node that already has a parent is moved: it is unlinked from that
    /// parent first, so a node made under the parent in scope (by
    /// <c>runtime.Text</c>, <c>runtime.Box</c>, a component) can be pushed
    /// somewhere else afterwards, as the <c>Arrange</c> expressions do. Pushed
    /// into the parent it is already in, it moves to the end. Pushing a node
    /// into itself or into one of its own descendants throws.
    /// </summary>
    public static void PushIntoParent(UiNode parent, UiNode child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        if (parent == child) throw new InvalidOperationException("A node cannot be pushed into itself.");
        for (UiNode? a = parent.Parent; a is not null; a = a.Parent)
        {
            if (a == child)
                throw new InvalidOperationException(
                    $"A node cannot be pushed into its own descendant: '{Describe(child)}' is an ancestor of '{Describe(parent)}'.");
        }
        if (child.Parent is not null) Unlink(child);
        child.Parent = parent;
        child.Prev = parent.LastChild;
        child.Next = null;
        if (parent.LastChild is not null) parent.LastChild.Next = child;
        else parent.FirstChild = child;
        parent.LastChild = child;
    }

    /// <summary>
    /// Takes <paramref name="node"/> out of its parent's children, mending the
    /// links around it. A link is mended only where it still points at
    /// <paramref name="node"/>: links left from an earlier frame (a parent or
    /// sibling the runtime has since reset and reused) are not the node's to
    /// change.
    /// </summary>
    internal static void Unlink(UiNode node)
    {
        UiNode parent = node.Parent!;
        UiNode? prev = node.Prev, next = node.Next;
        if (prev is not null) { if (prev.Next == node) prev.Next = next; }
        else if (parent.FirstChild == node) parent.FirstChild = next;
        if (next is not null) { if (next.Prev == node) next.Prev = prev; }
        else if (parent.LastChild == node) parent.LastChild = prev;
        node.Parent = null;
        node.Prev = null;
        node.Next = null;
    }

    private static string Describe(UiNode node) => node.Id ?? node.GetType().Name;

    // ── What the build asks for ─────────────────────────────────────────────

    /// <summary>The axis this node lays its children out along.</summary>
    public LayoutAxis ChildLayoutAxis { get; set; }

    /// <summary>The size this node asks for along X. See <see cref="SemanticSize"/>.</summary>
    public SemanticSize SizeX { get; set; }

    /// <summary>The size this node asks for along Y. See <see cref="SemanticSize"/>.</summary>
    public SemanticSize SizeY { get; set; }

    /// <summary>Children are not shrunk to fit along X: they keep their size and run past.</summary>
    public bool AllowOverflowX { get; set; }

    /// <summary>The same as <see cref="AllowOverflowX"/>, along Y.</summary>
    public bool AllowOverflowY { get; set; }

    /// <summary>Out of the flow along X: placed at <see cref="FixedPosition"/> and not counted in its parent's size.</summary>
    public bool FloatingX { get; set; }

    /// <summary>The same as <see cref="FloatingX"/>, along Y.</summary>
    public bool FloatingY { get; set; }

    /// <summary>Where a floating node sits, from its parent's top-left corner.</summary>
    public Vector2 FixedPosition { get; set; }

    // ── What the solver writes ──────────────────────────────────────────────

    /// <summary>The size the solver gave this node. Working state of a solve.</summary>
    public Vector2 FixedSize { get; internal set; }

    /// <summary>
    /// Where this node was laid out last, its top-left corner in logical
    /// pixels. Written by the layout, never by a build function.
    ///
    /// <para>Deliberately a frame behind for whoever asks during a build: a box
    /// asks where it <em>was</em> to answer where the pointer is now, because
    /// where it will be is not known until the frame has been laid out.</para>
    /// </summary>
    public Vector2 Rect { get; set; }

    /// <summary>
    /// How large this node was laid out last, in logical pixels. See
    /// <see cref="Rect"/>. Not <c>Size</c>: that names a component's text size.
    /// </summary>
    public Vector2 Extent { get; set; }

    /// <summary>How far this node's children extend, padding included: what <see cref="Extent"/> would be if it held them all.</summary>
    public Vector2 ContentSize { get; set; }

    /// <summary>How far this node's children are moved up and left, in logical pixels.</summary>
    public Vector2 ViewOffset { get; set; }

    /// <summary>Where <see cref="ViewOffset"/> is heading.</summary>
    public Vector2 TargetViewOffset { get; set; }
}

public sealed class Box : UiNode
{
    /// <summary>
    /// The kinds this box may take when asked about. Declaring is not winning —
    /// a container claiming LeftPress still loses one its card claimed and asked
    /// for first. What it guarantees is that a kind never claimed is never
    /// taken.
    /// </summary>
    public InteractionKinds Claims { get; set; }

    /// <summary>
    /// The box's own drawing, handed the box and a <see cref="DrawContext"/>: what
    /// it draws is recorded into the frame's draw list in tree order, and drawn by
    /// whichever renderer executes the list. It draws in the box's own space:
    /// (0,0) is the box's top-left, and nothing drawn leaves the box. Every path it hands out comes from the
    /// renderer's caches, and every paint resolves against the backend's, so a
    /// custom component costs what a built-in one costs.
    ///
    /// <para>Build state like the rest: set it on the box a build acquired —
    /// a retained one from <c>BibuiRuntime.BoxFromId</c> as readily as a keyless
    /// <c>Box()</c> — and it is cleared when the box is acquired again next
    /// frame, so a box draws only while its build keeps saying so. It runs at
    /// paint, after the box's decoration and children; resolve theme colours
    /// there with <see cref="DrawContext.Resolve(UiColor)"/>, not while
    /// building, so a theme change before the paint is followed.</para>
    /// </summary>
    public DrawCallback? Draw { get; set; }

    /// <summary>
    /// How hovered this box is, eased 0..1. Only means
    /// anything on a box that outlives the frame, i.e. one obtained from
    /// <c>BibuiRuntime.BoxFromId</c>; a box the build function constructed
    /// itself starts at 0 every frame because nothing of it survives to ease.
    /// </summary>
    public float HotT { get; internal set; }

    /// <summary>The same for "pressed".</summary>
    public float ActiveT { get; internal set; }
}

/// <summary>
/// Paints custom content for <paramref name="box"/>, which is handed in for what
/// the drawing may read of it (<see cref="Box.HotT"/>, <see cref="Box.ActiveT"/>,
/// its key). See <see cref="DrawContext"/>.
/// </summary>
public delegate void DrawCallback(Box box, DrawContext ctx);

public sealed class Style
{
    public EdgeInsets Padding { get; set; } = EdgeInsets.Zero;
    public UiColor? BackgroundColor { get; set; }
    public UiColor? BorderColor { get; set; }

    /// <summary>
    /// Which of the theme's standard treatments this node draws. The explicit
    /// colours win where they are set, so a node can take the standard hover
    /// without giving up a resting colour of its own.
    /// </summary>
    public DrawFlags Draw { get; set; }
    /// <summary>
    /// Background color applied instead of <see cref="BackgroundColor"/> while this node is
    /// hovered, eased by <see cref="Box.HotT"/>. Only a box that asks goes hot.
    /// Null = no hover-specific background (default).
    /// </summary>
    public UiColor? HoverBackgroundColor { get; set; }
    /// <summary>Border color applied instead of <see cref="BorderColor"/> while this node is hovered.
    /// Same tracking requirement as <see cref="HoverBackgroundColor"/>. Null = no hover-specific border.</summary>
    public UiColor? HoverBorderColor { get; set; }
    public float BorderWidth { get; set; } = 1f;
    public float CornerRadius { get; set; }

    /// <summary>Group opacity in 0..1 applied to this node and its entire subtree. 1 = fully opaque (default).</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>Declarative multi-stop gradient fill for the node's background. Takes precedence over
    /// <see cref="BackgroundColor"/> when set, and — unlike a box that draws itself — still lays out children on top.</summary>
    public Gradient? BackgroundGradient { get; set; }

    /// <summary>Soft drop shadow / elevation painted behind the node's rounded-rect fill. Null = none (default).</summary>
    public BoxShadow? Shadow { get; set; }

    /// <summary>
    /// Per-edge border overrides, painted in addition to (or instead of, per edge) the uniform
    /// <see cref="BorderColor"/>/<see cref="BorderWidth"/>. Null = that edge uses the uniform
    /// border (or no border, if <see cref="BorderColor"/> is also null). Used for affordances
    /// like a left-only nav active-indicator bar or a bottom-only tab underline.
    /// </summary>
    public EdgeBorder? BorderLeft { get; set; }
    public EdgeBorder? BorderTop { get; set; }
    public EdgeBorder? BorderRight { get; set; }
    public EdgeBorder? BorderBottom { get; set; }

    /// <summary>
    /// Gaussian blur sigma (logical px) applied to the region already painted beneath this node
    /// before its own fill/border/children paint on top — a translucent "frosted glass" HUD chip.
    /// Null (default) = no blur. It composites over what is already painted, so it needs the
    /// surface: a context without one draws no blur.
    /// </summary>
    public float? BackdropBlur { get; set; }

    // ── Transform ───────────────────────────────────────────────────────────
    // Applied when the node is painted and when a pointer is tested against
    // it, never when it is laid out: a row sliding in, a card scaling up on
    // hover, moves nothing around it. The order is scale and rotate about
    // TransformOrigin, then translate — the order CSS applies
    // `translate(…) rotate(…) scale(…)` in, read right to left.
    //
    // Kept off to one side and made on first use, because almost no node has
    // one: a Style is allocated for every node a build makes, and four more
    // fields on each would be paid for by every frame of every app.

    private sealed class TransformParts
    {
        public Vector2 Translate;
        public Vector2 Scale = Vector2.One;
        public float Rotate;
        public Vector2 Origin = new(0.5f, 0.5f);
    }

    private TransformParts? _transform;
    private TransformParts Parts => _transform ??= new TransformParts();

    /// <summary>
    /// Moves this node and its subtree by this many logical px when it is
    /// painted, without moving its layout slot. Zero (default) is no move.
    /// </summary>
    public Vector2 Translate
    {
        get => _transform?.Translate ?? Vector2.Zero;
        set { if (_transform is not null || value != Vector2.Zero) Parts.Translate = value; }
    }

    /// <summary>
    /// Scales this node and its subtree about <see cref="TransformOrigin"/>
    /// when it is painted. (1, 1) (default) is its own size.
    /// </summary>
    public Vector2 Scale
    {
        get => _transform?.Scale ?? Vector2.One;
        set { if (_transform is not null || value != Vector2.One) Parts.Scale = value; }
    }

    /// <summary>Rotates this node and its subtree about <see cref="TransformOrigin"/>, in degrees clockwise. 0 (default) is upright.</summary>
    public float Rotate
    {
        get => _transform?.Rotate ?? 0f;
        set { if (_transform is not null || value != 0f) Parts.Rotate = value; }
    }

    /// <summary>
    /// The point <see cref="Scale"/> and <see cref="Rotate"/> turn about, as a
    /// fraction of the node's size: (0.5, 0.5) (default) is its centre,
    /// (0, 0) its top-left corner.
    /// </summary>
    public Vector2 TransformOrigin
    {
        get => _transform?.Origin ?? new Vector2(0.5f, 0.5f);
        set { if (_transform is not null || value != new Vector2(0.5f, 0.5f)) Parts.Origin = value; }
    }

    /// <summary>Whether any of the transform properties moves the node from where it was laid out.</summary>
    internal bool HasTransform => _transform is { } t
        && (t.Translate != Vector2.Zero || t.Scale != Vector2.One || t.Rotate != 0f);

    /// <summary>
    /// The transform as a matrix over logical px, for a node laid out at
    /// <paramref name="rect"/> with size <paramref name="extent"/>.
    /// </summary>
    internal Matrix3x2 TransformFor(Vector2 rect, Vector2 extent)
    {
        var t = Parts;
        Vector2 origin = rect + extent * t.Origin;
        var m = Matrix3x2.CreateScale(t.Scale, origin);
        if (t.Rotate != 0f) m *= Matrix3x2.CreateRotation(t.Rotate * MathF.PI / 180f, origin);
        if (t.Translate != Vector2.Zero) m *= Matrix3x2.CreateTranslation(t.Translate);
        return m;
    }

    /// <summary>
    /// Restores every property to the value a freshly constructed Style has, in
    /// place — so a retained box can reuse its Style rather than be handed a new
    /// one each frame. Some defaults are not zero (BorderWidth, Opacity), so
    /// this is a restore, not a wipe.
    ///
    /// Ownership follows: a Style reached by a retained box is cleared by that
    /// box every frame, so it must not be shared with anything that expects to
    /// keep it.
    ///
    /// A property added above and forgotten here is caught by
    /// "Style.Clear: restores every property" in the runtime tests.
    /// </summary>
    public void Clear()
    {
        Padding = EdgeInsets.Zero;
        BackgroundColor = null;
        BorderColor = null;
        Draw = DrawFlags.None;
        HoverBackgroundColor = null;
        HoverBorderColor = null;
        BorderWidth = 1f;
        CornerRadius = 0f;
        Opacity = 1f;
        BackgroundGradient = null;
        Shadow = null;
        BorderLeft = null;
        BorderTop = null;
        BorderRight = null;
        BorderBottom = null;
        BackdropBlur = null;
        // Reset in place, so a retained box animating a transform keeps the one it made.
        if (_transform is { } t)
        {
            t.Translate = Vector2.Zero;
            t.Scale = Vector2.One;
            t.Rotate = 0f;
            t.Origin = new Vector2(0.5f, 0.5f);
        }
    }
}

/// <summary>A single edge's border width + color, for <see cref="Style.BorderLeft"/> etc.</summary>
public sealed class EdgeBorder
{
    public float Width { get; init; } = 1f;
    public UiColor Color { get; init; } = "#000000";
    public BorderLineStyle LineStyle { get; init; } = BorderLineStyle.Solid;

    public EdgeBorder() { }

    public EdgeBorder(float width, UiColor color, BorderLineStyle lineStyle = BorderLineStyle.Solid)
    {
        Width = width;
        Color = color;
        LineStyle = lineStyle;
    }
}

public enum BorderLineStyle { Solid, Dashed }

/// <summary>A single gradient color stop: an offset (0..1 along the gradient axis) and a color.</summary>
public readonly record struct ColorStop(float Offset, UiColor Color);

/// <summary>Gradient shape. <see cref="Radial"/> reinterprets <see cref="Gradient.Start"/>/
/// <see cref="Gradient.End"/> as a center point and a point defining the radius.</summary>
public enum GradientType { Linear, Radial }

/// <summary>
/// A declarative multi-stop gradient, usable as <see cref="Style.BackgroundGradient"/>.
/// For <see cref="GradientType.Linear"/> (default), <see cref="Start"/>/<see cref="End"/> are two
/// points in normalized rect coordinates (0..1) giving the gradient's direction: (0.5,0)→(0.5,1)
/// is top→bottom. For <see cref="GradientType.Radial"/>, <see cref="Start"/> is the center and
/// <see cref="End"/> is a point on the circle's edge — the radius is the distance between them.
/// </summary>
public sealed class Gradient
{
    public IReadOnlyList<ColorStop> Stops { get; init; } = [];
    public (float X, float Y) Start { get; init; } = (0.5f, 0f);
    public (float X, float Y) End { get; init; } = (0.5f, 1f);
    public GradientType Type { get; init; } = GradientType.Linear;

    /// <summary>Top→bottom gradient through the given stops.</summary>
    public static Gradient Vertical(params ColorStop[] stops)
        => new() { Stops = stops, Start = (0.5f, 0f), End = (0.5f, 1f) };

    /// <summary>Left→right gradient through the given stops.</summary>
    public static Gradient Horizontal(params ColorStop[] stops)
        => new() { Stops = stops, Start = (0f, 0.5f), End = (1f, 0.5f) };

    /// <summary>Top-left→bottom-right diagonal gradient through the given stops.</summary>
    public static Gradient Diagonal(params ColorStop[] stops)
        => new() { Stops = stops, Start = (0f, 0f), End = (1f, 1f) };

    /// <summary>Two-color top→bottom convenience.</summary>
    public static Gradient Linear(UiColor from, UiColor to)
        => Vertical(new ColorStop(0f, from), new ColorStop(1f, to));

    /// <summary>
    /// Radial gradient centered at <paramref name="center"/> (normalized rect coords, 0..1),
    /// reaching <paramref name="edge"/> at its outermost stop — e.g. a card thumbnail's warm
    /// glow: <c>Gradient.Radial((0.34f, 0.24f), (1.2f, 1.0f), ...)</c> for an off-center glow
    /// that overshoots the rect (matching CSS <c>radial-gradient(120% 100% at 34% 24%, ...)</c>).
    /// </summary>
    public static Gradient Radial((float X, float Y) center, (float X, float Y) edge, params ColorStop[] stops)
        => new() { Stops = stops, Start = center, End = edge, Type = GradientType.Radial };
}

/// <summary>
/// A soft drop shadow painted behind a node's rounded-rect fill. Offsets and blur are in logical
/// pixels; <see cref="Spread"/> expands the shadow rect on all sides. <see cref="Color"/> may carry
/// alpha (e.g. "#00000066") or be a theme token.
/// </summary>
public sealed class BoxShadow
{
    public float OffsetX { get; init; }
    public float OffsetY { get; init; } = 8f;
    public float Blur { get; init; } = 24f;
    public float Spread { get; init; }
    public UiColor Color { get; init; } = "#00000066";

    /// <summary>A subtle neutral elevation shadow that lifts a surface off a dark background.</summary>
    public static BoxShadow Elevation(float y = 10f, float blur = 30f, string color = "#00000073")
        => new() { OffsetY = y, Blur = blur, Color = color };

    /// <summary>A colored glow/bloom centered under the node (e.g. an accent halo behind a play button).</summary>
    public static BoxShadow Glow(UiColor color, float blur = 40f, float spread = 2f)
        => new() { OffsetX = 0f, OffsetY = 0f, Blur = blur, Spread = spread, Color = color };
}

public readonly record struct EdgeInsets(float Top, float Right, float Bottom, float Left)
{
    public static EdgeInsets Zero => new(0f, 0f, 0f, 0f);
    public static EdgeInsets All(float value) => new(value, value, value, value);
    public static EdgeInsets Symmetric(float horizontal, float vertical) => new(vertical, horizontal, vertical, horizontal);
}
