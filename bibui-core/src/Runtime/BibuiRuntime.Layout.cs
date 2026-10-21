namespace Bibui.Core.Runtime;

// Each stack is pushed by a Begin and popped by its End; the scope form,
// named without the Begin (Parent, Center, FontFace, ...), is the Begin
// handing back a scope whose Dispose is the End, for a build that says them
// with `using`. Either way they pair up before the build returns (SweepFrame).
public sealed partial class BibuiRuntime
{
    private readonly List<UiNode> _parentStack = [];
    private readonly List<LayoutAxis> _axisStack = [];
    private readonly List<SemanticSize> _sizeXStack = [];
    private readonly List<SemanticSize> _sizeYStack = [];
    private readonly List<FontSize> _fontSizeStack = [];
    private readonly List<(FontSlot Family, FontWeight Weight)> _faceStack = [];
    // One entry per centre begun: the cell BeginCenterAcross made and pushed,
    // or null for one along the parent that made none.
    private readonly List<UiNode?> _centerStack = [];

    // ── Parent ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Boxes acquired with <see cref="BoxFromId"/> until <see cref="EndParent"/>
    /// are linked under <paramref name="box"/>, in the order they are acquired.
    /// Hands <paramref name="box"/> back, for the End to name:
    /// <c>var row = runtime.BeginParent(runtime.Row()); ... runtime.EndParent(row);</c>
    /// </summary>
    public T BeginParent<T>(T box) where T : UiNode
    {
        ArgumentNullException.ThrowIfNull(box);
        _parentStack.Add(box);
        return box;
    }

    /// <summary>
    /// Ends <see cref="BeginParent"/> of <paramref name="box"/>, which must be
    /// the innermost: the parent is named again so an End that pairs with the
    /// wrong Begin says so, instead of ending another box's.
    /// </summary>
    public void EndParent(UiNode box)
    {
        ArgumentNullException.ThrowIfNull(box);
        // The frame's root is the build's own, and EndBuild ends it.
        if (_parentStack.Count == 0 || _parentStack[^1] != box || box == _root)
            throw new InvalidOperationException($"EndParent('{box.Id}') does not end the innermost BeginParent.");
        _parentStack.RemoveAt(_parentStack.Count - 1);
    }

    /// <summary><see cref="BeginParent"/>, ended when the scope is disposed.</summary>
    public ParentScope Parent(UiNode box)
    {
        BeginParent(box);
        return new ParentScope(this, box);
    }

    // ── Rows, columns, spacers ───────────────────────────────────────────────

    /// <summary>
    /// A keyless box that lays its children out top to bottom, linked under the
    /// parent in scope. On an axis no <see cref="BeginSizeX"/> or
    /// <see cref="BeginSizeY"/> is in force, it is as large as its children
    /// (<see cref="Size.Fit"/>); size it otherwise with <see cref="NodeLayout.Sized"/>.
    /// <code>
    /// var column = runtime.BeginParent(runtime.Column());
    /// runtime.Text("Title");
    /// runtime.Button("ok", "OK");
    /// runtime.EndParent(column);
    /// </code>
    /// </summary>
    public Box Column() => Group(LayoutAxis.Y);

    /// <summary>The same as <see cref="Column"/>, laying its children out left to right.</summary>
    public Box Row() => Group(LayoutAxis.X);

    private Box Group(LayoutAxis axis)
    {
        Box box = Box();
        box.ChildLayoutAxis = axis;
        if (_sizeXStack.Count == 0) box.SizeX = Size.Fit;
        if (_sizeYStack.Count == 0) box.SizeY = Size.Fit;
        return box;
    }

    /// <summary>
    /// A keyless spacer <paramref name="along"/> the axis of the parent in
    /// scope, and nothing across it. <c>Spacer(Size.Fill)</c> is a spring: it
    /// takes the room its siblings leave, pushing what follows to the far end,
    /// and gives it up first when they do not fit.
    /// </summary>
    public Box Spacer(SemanticSize along)
    {
        var parent = _parentStack.Count > 0 ? _parentStack[^1]
            : throw new InvalidOperationException("A spacer is along its parent's axis, and there is no parent in scope.");
        var spacer = Box();
        bool alongX = parent.ChildLayoutAxis == LayoutAxis.X;
        spacer.SizeX = alongX ? along : Size.Px(0f);
        spacer.SizeY = alongX ? Size.Px(0f) : along;
        return spacer;
    }

    /// <summary>
    /// <paramref name="ems"/> times the font size in scope (<see cref="BeginFontSize"/>,
    /// or the theme's Md), in logical px: a row height or a gap that follows the text.
    /// Taken when it is called, as the font in scope is then.
    /// </summary>
    public SemanticSize Em(float ems) => Size.Px(ems * SizeInScope().In(_theme));

    // ── Centring ─────────────────────────────────────────────────────────────
    // Along the parent's axis, a fill spacer before what is centred and one
    // after: no box of its own, so the parent's size along its axis must be
    // known. Across it, a cell perpendicular to the parent, as large as it
    // across and as its content along, with the same two spacers inside: so
    // the parent's size across its axis must be known.
    //
    // NOTE(k): centred both ways, the centre along the parent begins first.
    // The other then makes its cell in the parent, as tall (or wide) as it.
    // Begun the other way round, the second centres in the first's cell,
    // which is only as large as its content across, and that is a ring.
    // BeginCenterXY begins them in that order.

    /// <summary>
    /// What is built until <see cref="EndCenter"/> is centred along the axis of
    /// the parent in scope (across a row: left to right). Makes no box; it
    /// stays the parent's children.
    /// </summary>
    public void BeginCenter()
    {
        Spacer(Size.Fill);
        _centerStack.Add(null);
    }

    /// <summary>Ends the innermost <see cref="BeginCenter"/>.</summary>
    public void EndCenter()
    {
        if (PopCenter("EndCenter") is not null)
            throw new InvalidOperationException("EndCenter ends a BeginCenterAcross; end it with EndCenterAcross.");
        Spacer(Size.Fill);
    }

    /// <summary>
    /// What is built until <see cref="EndCenterAcross"/> is centred across the
    /// axis of the parent in scope (in a row: top to bottom), in a cell of its
    /// own that is as large as the parent across and as its content along.
    /// Hands the cell back: a box like any other, to style or to move.
    /// </summary>
    public Box BeginCenterAcross()
    {
        var parent = ParentInScope;
        Box cell = parent.ChildLayoutAxis == LayoutAxis.X
            ? Column().Sized(Size.Fit, Size.Fill)
            : Row().Sized(Size.Fill, Size.Fit);
        BeginParent(cell);
        Spacer(Size.Fill);
        _centerStack.Add(cell);
        return cell;
    }

    /// <summary>Ends the innermost <see cref="BeginCenterAcross"/>.</summary>
    public void EndCenterAcross()
    {
        if (PopCenter("EndCenterAcross") is not { } cell)
            throw new InvalidOperationException("EndCenterAcross ends a BeginCenter; end it with EndCenter.");
        Spacer(Size.Fill);
        EndParent(cell);
    }

    /// <summary>
    /// What is built until <see cref="EndCenterX"/> is centred left to right:
    /// <see cref="BeginCenter"/> in a row, <see cref="BeginCenterAcross"/> in a column.
    /// Centred both ways, begin the one along the parent first: in a row this,
    /// then <see cref="BeginCenterY"/>.
    /// </summary>
    public void BeginCenterX()
    {
        if (ParentInScope.ChildLayoutAxis == LayoutAxis.X) BeginCenter();
        else BeginCenterAcross();
    }

    /// <summary>
    /// What is built until <see cref="EndCenterY"/> is centred top to bottom:
    /// <see cref="BeginCenter"/> in a column, <see cref="BeginCenterAcross"/> in a row.
    /// Centred both ways, begin the one along the parent first: in a column
    /// this, then <see cref="BeginCenterX"/>.
    /// </summary>
    public void BeginCenterY()
    {
        if (ParentInScope.ChildLayoutAxis == LayoutAxis.Y) BeginCenter();
        else BeginCenterAcross();
    }

    /// <summary>
    /// What is built until <see cref="EndCenterXY"/> is centred both ways in
    /// the parent in scope: <see cref="BeginCenter"/> along its axis, then
    /// <see cref="BeginCenterAcross"/> in it, the order that centres both (the
    /// other order centres the second in the first's cell, which has no room).
    /// The parent's size on both axes must be known. Hands the cell back.
    /// </summary>
    public Box BeginCenterXY()
    {
        BeginCenter();
        return BeginCenterAcross();
    }

    /// <summary>Ends the innermost <see cref="BeginCenterXY"/>.</summary>
    public void EndCenterXY()
    {
        EndCenterAcross();
        EndCenter();
    }

    /// <summary>Ends the innermost <see cref="BeginCenterX"/>.</summary>
    public void EndCenterX() => EndCenterEither("EndCenterX");

    /// <summary>Ends the innermost <see cref="BeginCenterY"/>.</summary>
    public void EndCenterY() => EndCenterEither("EndCenterY");

    /// <summary><see cref="BeginCenter"/>, ended when the scope is disposed.</summary>
    public CenterScope Center() { BeginCenter(); return new CenterScope(this, _centerStack.Count); }

    /// <summary><see cref="BeginCenterAcross"/>, ended when the scope is disposed.</summary>
    public CenterScope CenterAcross() { BeginCenterAcross(); return new CenterScope(this, _centerStack.Count); }

    /// <summary><see cref="BeginCenterX"/>, ended when the scope is disposed.</summary>
    public CenterScope CenterX() { BeginCenterX(); return new CenterScope(this, _centerStack.Count); }

    /// <summary><see cref="BeginCenterY"/>, ended when the scope is disposed.</summary>
    public CenterScope CenterY() { BeginCenterY(); return new CenterScope(this, _centerStack.Count); }

    /// <summary><see cref="BeginCenterXY"/>, ended when the scope is disposed.</summary>
    public CenterScope CenterXY() { BeginCenterXY(); return new CenterScope(this, _centerStack.Count, both: true); }

    // X and Y chose along or across when they began; the entry says which.
    private void EndCenterEither(string end)
    {
        if (PopCenter(end) is { } cell)
        {
            Spacer(Size.Fill);
            EndParent(cell);
        }
        else Spacer(Size.Fill);
    }

    private UiNode? PopCenter(string end)
    {
        if (_centerStack.Count == 0) throw new InvalidOperationException($"{end} without a BeginCenter.");
        var cell = _centerStack[^1];
        _centerStack.RemoveAt(_centerStack.Count - 1);
        return cell;
    }

    // ── Axis ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boxes acquired with <see cref="BoxFromId"/> until <see cref="EndAxis"/>
    /// lay their children out along <paramref name="axis"/>.
    /// </summary>
    public void BeginAxis(LayoutAxis axis) => _axisStack.Add(axis);

    /// <summary>Ends the innermost <see cref="BeginAxis"/>.</summary>
    public void EndAxis() => Pop(_axisStack, "EndAxis without a BeginAxis.");

    /// <summary><see cref="BeginAxis"/>, ended when the scope is disposed.</summary>
    public AxisScope Axis(LayoutAxis axis)
    {
        BeginAxis(axis);
        return new AxisScope(this, _axisStack.Count);
    }

    // ── Sizes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Boxes acquired with <see cref="BoxFromId"/> until <see cref="EndSizeX"/>
    /// are <paramref name="size"/> along X, unless the build says otherwise.
    /// </summary>
    public void BeginSizeX(SemanticSize size) => _sizeXStack.Add(size);

    /// <summary>Ends the innermost <see cref="BeginSizeX"/>.</summary>
    public void EndSizeX() => Pop(_sizeXStack, "EndSizeX without a BeginSizeX.");

    /// <summary>The same as <see cref="BeginSizeX"/>, along Y.</summary>
    public void BeginSizeY(SemanticSize size) => _sizeYStack.Add(size);

    /// <summary>Ends the innermost <see cref="BeginSizeY"/>.</summary>
    public void EndSizeY() => Pop(_sizeYStack, "EndSizeY without a BeginSizeY.");

    /// <summary><see cref="BeginSizeX"/>, ended when the scope is disposed.</summary>
    public SizeScope SizeX(SemanticSize size)
    {
        BeginSizeX(size);
        return new SizeScope(this, LayoutAxis.X, _sizeXStack.Count);
    }

    /// <summary><see cref="BeginSizeY"/>, ended when the scope is disposed.</summary>
    public SizeScope SizeY(SemanticSize size)
    {
        BeginSizeY(size);
        return new SizeScope(this, LayoutAxis.Y, _sizeYStack.Count);
    }

    // ── Fonts ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Texts and icons built until <see cref="EndFontSize"/> are set in
    /// <paramref name="size"/>, unless they are given a size of their own.
    /// Nested ones stack: the innermost is the one in force.
    /// </summary>
    public void BeginFontSize(FontSize size) => _fontSizeStack.Add(size);

    /// <summary>Ends the innermost <see cref="BeginFontSize"/>.</summary>
    public void EndFontSize() => Pop(_fontSizeStack, "EndFontSize without a BeginFontSize.");

    /// <summary><see cref="BeginFontSize"/>, ended when the scope is disposed.</summary>
    public FontSizeScope FontSize(FontSize size)
    {
        BeginFontSize(size);
        return new FontSizeScope(this, _fontSizeStack.Count);
    }

    /// <summary>
    /// Texts built until <see cref="EndFontFace"/> are set in <paramref name="family"/>
    /// at <paramref name="weight"/>. Either left out is the one already in scope,
    /// so <c>BeginFontFace(weight: FontWeight.Bold)</c> makes what is inside bold
    /// in whatever family it was. A text's own family and weight still win
    /// (<see cref="Text"/>).
    /// </summary>
    public void BeginFontFace(FontSlot? family = null, FontWeight? weight = null)
    {
        var outer = FaceInScope();
        _faceStack.Add((family ?? outer.Family, weight ?? outer.Weight));
    }

    /// <summary>Ends the innermost <see cref="BeginFontFace"/>.</summary>
    public void EndFontFace() => Pop(_faceStack, "EndFontFace without a BeginFontFace.");

    /// <summary><see cref="BeginFontFace"/>, ended when the scope is disposed.</summary>
    public FontFaceScope FontFace(FontSlot? family = null, FontWeight? weight = null)
    {
        BeginFontFace(family, weight);
        return new FontFaceScope(this, _faceStack.Count);
    }

    private static void Pop<T>(List<T> stack, string unmatched)
    {
        if (stack.Count == 0) throw new InvalidOperationException(unmatched);
        stack.RemoveAt(stack.Count - 1);
    }

    // ── Scopes ───────────────────────────────────────────────────────────────
    // Each calls its End, after checking it is the innermost of its stack, so
    // a scope disposed out of order says so instead of ending another's
    // (EndParent checks that itself).

    /// <summary>
    /// Ends <see cref="Parent"/> when disposed. A <c>default</c> scope
    /// began nothing and ends nothing, for a parent that is only sometimes there.
    /// </summary>
    public readonly struct ParentScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly UiNode _box;

        internal ParentScope(BibuiRuntime runtime, UiNode box)
        {
            _runtime = runtime;
            _box = box;
        }

        public void Dispose()
        {
            if (_runtime is null) return;
            _runtime.EndParent(_box);
        }
    }

    /// <summary>Ends <see cref="SizeX"/> or <see cref="SizeY"/> when disposed.</summary>
    public readonly struct SizeScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly LayoutAxis _axis;
        private readonly int _depth;

        internal SizeScope(BibuiRuntime runtime, LayoutAxis axis, int depth)
        {
            _runtime = runtime;
            _axis = axis;
            _depth = depth;
        }

        public void Dispose()
        {
            var stack = _axis == LayoutAxis.X ? _runtime._sizeXStack : _runtime._sizeYStack;
            if (stack.Count != _depth)
                throw new InvalidOperationException("A size scope was disposed out of order.");
            if (_axis == LayoutAxis.X) _runtime.EndSizeX();
            else _runtime.EndSizeY();
        }
    }

    /// <summary>Ends <see cref="FontSize"/> when disposed.</summary>
    public readonly struct FontSizeScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly int _depth;

        internal FontSizeScope(BibuiRuntime runtime, int depth)
        {
            _runtime = runtime;
            _depth = depth;
        }

        public void Dispose()
        {
            if (_runtime._fontSizeStack.Count != _depth)
                throw new InvalidOperationException("A FontSize scope was disposed out of order.");
            _runtime.EndFontSize();
        }
    }

    /// <summary>Ends <see cref="FontFace"/> when disposed.</summary>
    public readonly struct FontFaceScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly int _depth;

        internal FontFaceScope(BibuiRuntime runtime, int depth)
        {
            _runtime = runtime;
            _depth = depth;
        }

        public void Dispose()
        {
            if (_runtime._faceStack.Count != _depth)
                throw new InvalidOperationException("A FontFace scope was disposed out of order.");
            _runtime.EndFontFace();
        }
    }

    /// <summary>Ends a <see cref="Center"/>, <see cref="CenterAcross"/>, <see cref="CenterX"/>, <see cref="CenterY"/> or <see cref="CenterXY"/> when disposed.</summary>
    public readonly struct CenterScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly int _depth;
        private readonly bool _both;   // a CenterXY scope: two entries, the cell's on top

        internal CenterScope(BibuiRuntime runtime, int depth, bool both = false)
        {
            _runtime = runtime;
            _depth = depth;
            _both = both;
        }

        public void Dispose()
        {
            if (_runtime._centerStack.Count != _depth)
                throw new InvalidOperationException("A centre scope was disposed out of order.");
            if (_both) _runtime.EndCenterXY();
            else _runtime.EndCenterEither("A centre scope");
        }
    }

    /// <summary>Ends <see cref="Axis"/> when disposed.</summary>
    public readonly struct AxisScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly int _depth;

        internal AxisScope(BibuiRuntime runtime, int depth)
        {
            _runtime = runtime;
            _depth = depth;
        }

        public void Dispose()
        {
            if (_runtime._axisStack.Count != _depth)
                throw new InvalidOperationException("An Axis scope was disposed out of order.");
            _runtime.EndAxis();
        }
    }
}
