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
