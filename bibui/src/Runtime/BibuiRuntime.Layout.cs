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
    /// parent in scope. It is a box like any other: push it to build into it,
    /// and size or style it with the usual helpers.
    /// <code>
    /// var column = runtime.ColumnFit().Padded(EdgeInsets.All(24f));
    /// using (runtime.PushParent(column))
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
    /// </summary>
    public Box Column() => Group(LayoutAxis.Y, null, null);

    /// <summary>The same as <see cref="Column"/>, laying its children out left to right.</summary>
    public Box Row() => Group(LayoutAxis.X, null, null);

    // The sized forms. Each names the rule for the axes it names; an axis it
    // does not name is as large as its children.

    /// <summary>A <see cref="Column"/> as wide and as tall as its children.</summary>
    public Box ColumnFit() => Group(LayoutAxis.Y, Fit, Fit);

    /// <summary>A <see cref="Column"/> as wide as its parent, and as tall as its children.</summary>
    public Box ColumnFillX() => Group(LayoutAxis.Y, Fill, Fit);

    /// <summary>A <see cref="Column"/> as tall as its parent, and as wide as its children.</summary>
    public Box ColumnFillY() => Group(LayoutAxis.Y, Fit, Fill);

    /// <summary>A <see cref="Column"/> as wide and as tall as its parent.</summary>
    public Box ColumnFill() => Group(LayoutAxis.Y, Fill, Fill);

    /// <summary>A <see cref="Column"/> <paramref name="width"/> pixels wide, and as tall as its children.</summary>
    public Box ColumnWidth(float width) => Group(LayoutAxis.Y, SemanticSize.Pixels(width), Fit);

    /// <summary>A <see cref="Column"/> of a fixed size.</summary>
    public Box ColumnPx(float width, float height)
        => Group(LayoutAxis.Y, SemanticSize.Pixels(width), SemanticSize.Pixels(height));

    /// <summary>A <see cref="Row"/> as wide and as tall as its children.</summary>
    public Box RowFit() => Group(LayoutAxis.X, Fit, Fit);

    /// <summary>A <see cref="Row"/> as wide as its parent, and as tall as its children.</summary>
    public Box RowFillX() => Group(LayoutAxis.X, Fill, Fit);

    /// <summary>A <see cref="Row"/> as wide as its parent and <paramref name="height"/> pixels tall: a toolbar, a header.</summary>
    public Box RowFillX(float height) => Group(LayoutAxis.X, Fill, SemanticSize.Pixels(height));

    /// <summary>A <see cref="Row"/> as tall as its parent, and as wide as its children.</summary>
    public Box RowFillY() => Group(LayoutAxis.X, Fit, Fill);

    /// <summary>A <see cref="Row"/> as wide and as tall as its parent.</summary>
    public Box RowFill() => Group(LayoutAxis.X, Fill, Fill);

    /// <summary>A <see cref="Row"/> <paramref name="height"/> pixels tall, and as wide as its children.</summary>
    public Box RowHeight(float height) => Group(LayoutAxis.X, Fit, SemanticSize.Pixels(height));

    /// <summary>A <see cref="Row"/> of a fixed size.</summary>
    public Box RowPx(float width, float height)
        => Group(LayoutAxis.X, SemanticSize.Pixels(width), SemanticSize.Pixels(height));

    private static SemanticSize Fit => SemanticSize.ChildrenSum();
    private static SemanticSize Fill => SemanticSize.ParentPct(1f);

    // A size given here is the box's own; one not given is left as Box() set
    // it, from the size pushed or none.
    private Box Group(LayoutAxis axis, SemanticSize? sizeX, SemanticSize? sizeY)
    {
        Box box = Box();
        box.ChildLayoutAxis = axis;
        if (sizeX is { } x) box.SizeX = x;
        if (sizeY is { } y) box.SizeY = y;
        return box;
    }

    /// <summary>
    /// A spacer that takes whatever room its siblings leave along the axis of
    /// the parent in scope, and gives all of it up first when they do not fit:
    /// what pushes the nodes built after it to the far end.
    /// <c>using (runtime.PushParent(runtime.RowFillX())) { runtime.Text(title); runtime.Spring(); runtime.Button(...); }</c>
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
    /// What is built inside is centred along the parent in scope's own axis: a
    /// spring before it now, and one after it when the scope is disposed. No
    /// box of its own; the contents stay children of the parent. Centred
    /// across a row: <c>using (runtime.PushParent(runtime.ColumnFillY())) using (runtime.Centred()) ...</c>.
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
