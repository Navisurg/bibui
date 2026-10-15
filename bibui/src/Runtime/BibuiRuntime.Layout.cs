namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    private readonly List<UiNode> _parentStack = [];
    private readonly List<LayoutAxis> _axisStack = [];
    private readonly List<SemanticSize> _sizeXStack = [];
    private readonly List<SemanticSize> _sizeYStack = [];
    private readonly List<FontSize> _fontSizeStack = [];
    private readonly List<(FontSlot Family, FontWeight Weight)> _faceStack = [];

    /// <summary>
    /// Boxes acquired with <see cref="BoxFromId"/> until the scope is disposed
    /// are linked under <paramref name="box"/>, in the order they are acquired.
    /// </summary>
    public ParentScope PushParent(UiNode box)
    {
        ArgumentNullException.ThrowIfNull(box);
        lock (_syncRoot) _parentStack.Add(box);
        return new ParentScope(this, box);
    }

    /// <summary>
    /// A keyless box that lays its children out top to bottom, linked under the
    /// parent in scope and pushed as the parent until the scope is disposed, so
    /// what is built inside goes into it, in order:
    /// <code>
    /// using (runtime.ColumnFit())
    /// {
    ///     runtime.Text("Title");
    ///     runtime.Button("ok", "OK");
    /// }
    /// </code>
    /// Sized like <see cref="Box"/>: by the sizes pushed with
    /// <see cref="PushSizeX"/>/<see cref="PushSizeY"/>, and with none pushed, not
    /// at all, which layout refuses. Usually one of the sized forms says it:
    /// <see cref="ColumnFit"/>, <see cref="ColumnFillX"/>, <see cref="ColumnFillY"/>,
    /// <see cref="ColumnFill"/>, <see cref="ColumnWidth"/>, <see cref="ColumnPx"/>.
    /// Style the box through the scope: <c>column.Box.Padded(...)</c>.
    /// </summary>
    public BoxScope Column() => PushGroup(LayoutAxis.Y, null, null);

    /// <summary>The same as <see cref="Column"/>, laying its children out left to right.</summary>
    public BoxScope Row() => PushGroup(LayoutAxis.X, null, null);

    // The sized forms. Each names the rule for the axes it names; an axis it
    // does not name is as large as its children.

    /// <summary>A <see cref="Column"/> as wide and as tall as its children.</summary>
    public BoxScope ColumnFit() => PushGroup(LayoutAxis.Y, Fit, Fit);

    /// <summary>A <see cref="Column"/> as wide as its parent, and as tall as its children.</summary>
    public BoxScope ColumnFillX() => PushGroup(LayoutAxis.Y, Fill, Fit);

    /// <summary>A <see cref="Column"/> as tall as its parent, and as wide as its children.</summary>
    public BoxScope ColumnFillY() => PushGroup(LayoutAxis.Y, Fit, Fill);

    /// <summary>A <see cref="Column"/> as wide and as tall as its parent.</summary>
    public BoxScope ColumnFill() => PushGroup(LayoutAxis.Y, Fill, Fill);

    /// <summary>A <see cref="Column"/> <paramref name="width"/> pixels wide, and as tall as its children.</summary>
    public BoxScope ColumnWidth(float width) => PushGroup(LayoutAxis.Y, SemanticSize.Pixels(width), Fit);

    /// <summary>A <see cref="Column"/> of a fixed size.</summary>
    public BoxScope ColumnPx(float width, float height)
        => PushGroup(LayoutAxis.Y, SemanticSize.Pixels(width), SemanticSize.Pixels(height));

    /// <summary>A <see cref="Row"/> as wide and as tall as its children.</summary>
    public BoxScope RowFit() => PushGroup(LayoutAxis.X, Fit, Fit);

    /// <summary>A <see cref="Row"/> as wide as its parent, and as tall as its children.</summary>
    public BoxScope RowFillX() => PushGroup(LayoutAxis.X, Fill, Fit);

    /// <summary>A <see cref="Row"/> as wide as its parent and <paramref name="height"/> pixels tall: a toolbar, a header.</summary>
    public BoxScope RowFillX(float height) => PushGroup(LayoutAxis.X, Fill, SemanticSize.Pixels(height));

    /// <summary>A <see cref="Row"/> as tall as its parent, and as wide as its children.</summary>
    public BoxScope RowFillY() => PushGroup(LayoutAxis.X, Fit, Fill);

    /// <summary>A <see cref="Row"/> as wide and as tall as its parent.</summary>
    public BoxScope RowFill() => PushGroup(LayoutAxis.X, Fill, Fill);

    /// <summary>A <see cref="Row"/> <paramref name="height"/> pixels tall, and as wide as its children.</summary>
    public BoxScope RowHeight(float height) => PushGroup(LayoutAxis.X, Fit, SemanticSize.Pixels(height));

    /// <summary>A <see cref="Row"/> of a fixed size.</summary>
    public BoxScope RowPx(float width, float height)
        => PushGroup(LayoutAxis.X, SemanticSize.Pixels(width), SemanticSize.Pixels(height));

    private static SemanticSize Fit => SemanticSize.ChildrenSum();
    private static SemanticSize Fill => SemanticSize.ParentPct(1f);

    // A size given here is the box's own; one not given is left as Box() set
    // it, from the size pushed or none.
    private BoxScope PushGroup(LayoutAxis axis, SemanticSize? sizeX, SemanticSize? sizeY, bool closingSpring = false)
    {
        Box box = Box();
        box.ChildLayoutAxis = axis;
        if (sizeX is { } x) box.SizeX = x;
        if (sizeY is { } y) box.SizeY = y;
        var pushed = PushParent(box);
        if (closingSpring) Spring();
        return new BoxScope(this, box, pushed, closingSpring);
    }

    /// <summary>
    /// A spacer that takes whatever room its siblings leave along the axis of
    /// the parent in scope, and gives all of it up first when they do not fit:
    /// what pushes the nodes built after it to the far end.
    /// <c>using (runtime.Row()) { runtime.Text(title); runtime.Spring(); runtime.Button(...); }</c>
    /// </summary>
    public Box Spring()
    {
        lock (_syncRoot)
        {
            var parent = _parentStack.Count > 0 ? _parentStack[^1]
                : throw new InvalidOperationException("A spring is along its parent's axis, and there is no parent in scope.");
            var spring = Box();
            bool along = parent.ChildLayoutAxis == LayoutAxis.X;
            spring.SizeX = along ? SemanticSize.ParentPct(1f, 0f) : SemanticSize.Pixels(0f);
            spring.SizeY = along ? SemanticSize.Pixels(0f) : SemanticSize.ParentPct(1f, 0f);
            return spring;
        }
    }

    /// <summary>
    /// What is built inside is centred across the parent in scope's width: a
    /// row as wide as the parent and as tall as what it holds, with a spring
    /// before and after it. <c>using (runtime.CentredX()) runtime.Text("Empty");</c>
    /// </summary>
    public BoxScope CentredX()
        => PushGroup(LayoutAxis.X, Fill, Fit, closingSpring: true);

    /// <summary>
    /// What is built inside is centred across the parent in scope's height: a
    /// column as tall as the parent and as wide as what it holds, with a spring
    /// before and after it.
    /// </summary>
    public BoxScope CentredY()
        => PushGroup(LayoutAxis.Y, Fit, Fill, closingSpring: true);

    /// <summary>
    /// What is built inside is centred along the parent in scope's own axis: a
    /// spring before it now, and one after it when the scope is disposed. No
    /// box of its own; the contents stay children of the parent.
    /// </summary>
    public CentredScope Centred()
    {
        Spring();
        return new CentredScope(this);
    }

    /// <summary>
    /// Boxes acquired with <see cref="BoxFromId"/> until the scope is disposed
    /// lay their children out along <paramref name="axis"/>.
    /// </summary>
    public AxisScope PushAxis(LayoutAxis axis)
    {
        int depth;
        lock (_syncRoot)
        {
            _axisStack.Add(axis);
            depth = _axisStack.Count;
        }
        return new AxisScope(this, depth);
    }

    /// <summary>
    /// Boxes acquired with <see cref="BoxFromId"/> until the scope is disposed
    /// are <paramref name="size"/> along X, unless the build says otherwise.
    /// </summary>
    public SizeScope PushSizeX(SemanticSize size) => PushSize(_sizeXStack, size);

    /// <summary>The same as <see cref="PushSizeX"/>, along Y.</summary>
    public SizeScope PushSizeY(SemanticSize size) => PushSize(_sizeYStack, size);

    /// <summary>
    /// Texts and icons built until the scope is disposed are set in
    /// <paramref name="size"/>, unless they are given a size of their own.
    /// Nested pushes stack: the innermost is the one in force.
    /// </summary>
    public FontSizeScope PushFontSize(FontSize size)
    {
        int depth;
        lock (_syncRoot)
        {
            _fontSizeStack.Add(size);
            depth = _fontSizeStack.Count;
        }
        return new FontSizeScope(this, depth);
    }

    /// <summary>
    /// Texts built until the scope is disposed are set in <paramref name="family"/>
    /// at <paramref name="weight"/>. Either left out is the one already in scope,
    /// so <c>PushFontFace(weight: FontWeight.Bold)</c> makes what is inside bold
    /// in whatever family it was. A text's own family still wins
    /// (<see cref="Text"/>); its weight is only ever the one in scope.
    /// </summary>
    public FontFaceScope PushFontFace(FontSlot? family = null, FontWeight? weight = null)
    {
        int depth;
        lock (_syncRoot)
        {
            var outer = FaceInScope();
            _faceStack.Add((family ?? outer.Family, weight ?? outer.Weight));
            depth = _faceStack.Count;
        }
        return new FontFaceScope(this, depth);
    }

    private SizeScope PushSize(List<SemanticSize> stack, SemanticSize size)
    {
        int depth;
        lock (_syncRoot)
        {
            stack.Add(size);
            depth = stack.Count;
        }
        return new SizeScope(this, stack, depth);
    }

    /// <summary>
    /// Pops <see cref="PushParent"/> when disposed. A <c>default</c> scope
    /// pushed nothing and pops nothing, for a parent that is only sometimes there.
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
            lock (_runtime._syncRoot)
            {
                var stack = _runtime._parentStack;
                if (stack.Count == 0 || stack[^1] != _box)
                    throw new InvalidOperationException($"PushParent('{_box.Id}') was disposed out of order.");
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>Closes <see cref="Centred"/> with its second spring when disposed.</summary>
    public readonly struct CentredScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;

        internal CentredScope(BibuiRuntime runtime) => _runtime = runtime;

        public void Dispose() => _runtime.Spring();
    }

    /// <summary>
    /// The box <see cref="Column"/>, <see cref="Row"/>, <see cref="CentredX"/>
    /// or <see cref="CentredY"/> made, pushed as the
    /// parent until this is disposed. It stands for the box where a node is
    /// wanted, so a build can return it: <c>using (var column = runtime.Column())
    /// { ...; return column; }</c>.
    /// </summary>
    public readonly struct BoxScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly ParentScope _pushed;
        private readonly bool _closingSpring;

        internal BoxScope(BibuiRuntime runtime, Box box, ParentScope pushed, bool closingSpring)
        {
            _runtime = runtime;
            Box = box;
            _pushed = pushed;
            _closingSpring = closingSpring;
        }

        /// <summary>The column or row, to size or style further.</summary>
        public Box Box { get; }

        /// <summary>Stops building into the box (closing a centred one with its second spring).</summary>
        public void Dispose()
        {
            if (_closingSpring) _runtime.Spring();
            _pushed.Dispose();
        }

        public static implicit operator Box(BoxScope scope) => scope.Box;
        public static implicit operator UiNode(BoxScope scope) => scope.Box;
        public static implicit operator Arrange.Item(BoxScope scope) => scope.Box;
    }

    /// <summary>Pops <see cref="PushSizeX"/> or <see cref="PushSizeY"/> when disposed.</summary>
    public readonly struct SizeScope : IDisposable
    {
        private readonly BibuiRuntime _runtime;
        private readonly List<SemanticSize> _stack;
        private readonly int _depth;

        internal SizeScope(BibuiRuntime runtime, List<SemanticSize> stack, int depth)
        {
            _runtime = runtime;
            _stack = stack;
            _depth = depth;
        }

        public void Dispose()
        {
            lock (_runtime._syncRoot)
            {
                if (_stack.Count != _depth)
                    throw new InvalidOperationException("A size scope was disposed out of order.");
                _stack.RemoveAt(_stack.Count - 1);
            }
        }
    }

    /// <summary>Pops <see cref="PushFontSize"/> when disposed.</summary>
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
            lock (_runtime._syncRoot)
            {
                var stack = _runtime._fontSizeStack;
                if (stack.Count != _depth)
                    throw new InvalidOperationException("PushFontSize was disposed out of order.");
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>Pops <see cref="PushFontFace"/> when disposed.</summary>
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
            lock (_runtime._syncRoot)
            {
                var stack = _runtime._faceStack;
                if (stack.Count != _depth)
                    throw new InvalidOperationException("PushFontFace was disposed out of order.");
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>Pops <see cref="PushAxis"/> when disposed.</summary>
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
            lock (_runtime._syncRoot)
            {
                var stack = _runtime._axisStack;
                if (stack.Count != _depth)
                    throw new InvalidOperationException("PushAxis was disposed out of order.");
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }
}
