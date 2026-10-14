using Bibui.Renderer;

namespace Bibui.Runtime;

public sealed partial class BibuiRuntime
{
    // Keyless leaves are reused by position: the n-th call of a kind in a build
    // gets the n-th instance, and the pools rewind when the frame ends. A leaf
    // has no identity, so nothing about it needs to be the same instance as
    // last frame's — only to not be allocated again.
    private readonly NodePool<Text> _texts = new(static () => new Text());
    private readonly NodePool<Icon> _icons = new(static () => new Icon());
    private readonly NodePool<Spinner> _spinners = new(static () => new Spinner());
    private readonly NodePool<Box> _boxes = new(static () => new Box());

    private readonly ResourceCache<string, Text> _retainedTexts = new(keepFrames: 60);

    /// <summary>
    /// A text showing <paramref name="text"/>, linked under the parent
    /// in scope and sized by the size stacks — on an axis with nothing pushed,
    /// by its own text. Set in <paramref name="fontSize"/>, or the size in
    /// scope, and in <paramref name="family"/>, or the family in scope, at the
    /// weight in scope (<see cref="PushFontSize"/>, <see cref="PushFontFace"/>);
    /// <paramref name="textColor"/>, or the primary text colour.
    ///
    /// <para>The text is copied into the node, so a string, a slice of a
    /// <see cref="BibuiStr"/> or a buffer on the stack may all be passed, and
    /// changed as soon as the call returns.</para>
    ///
    /// <para>It has no identity: a keyless text is one of a pool reused frame
    /// to frame. Use <see cref="TextFromId"/> for one that needs to be found
    /// again.</para>
    /// </summary>
    public Text Text(ReadOnlySpan<char> text, FontSize? fontSize = null, UiColor? textColor = null,
        FontSlot? family = null)
    {
        lock (_syncRoot)
            return DescribeText(_texts.Next(), text, fontSize, textColor, family);
    }

    /// <summary>
    /// What this runtime measures and draws text with: its renderer's own
    /// <see cref="Bibui.Fonts"/>, so a string measured here is the width layout
    /// gives a text with that content, from the same cache.
    /// </summary>
    public Fonts Fonts => _renderer.Fonts;

    /// <summary>
    /// The font a <see cref="Text"/> built at this point would be set
    /// in: <paramref name="size"/>, or the size pushed with <see cref="PushFontSize"/>,
    /// or <see cref="ComponentSize.Md"/>; in <paramref name="family"/>, or the
    /// family pushed with <see cref="PushFontFace"/>; at the weight pushed with
    /// it; at the surface's scale. Hand it to <see cref="Fonts"/> to measure
    /// what such a text would show.
    /// </summary>
    public Font FontInScope(FontSize? size = null, FontSlot? family = null)
    {
        lock (_syncRoot)
        {
            var face = FaceInScope();
            return Fonts.FontOf(size ?? SizeInScope(), family ?? face.Family, face.Weight, _theme, _surface.ScaleFactor);
        }
    }

    /// <summary>
    /// The same as <see cref="Text"/>, retained under <paramref name="id"/>
    /// like <see cref="BoxFromId"/>, for a text that needs identity: something
    /// to find it by, or to animate.
    /// </summary>
    public Text TextFromId(string id, ReadOnlySpan<char> text, FontSize? fontSize = null,
        UiColor? textColor = null, FontSlot? family = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        lock (_syncRoot)
            return DescribeText(_retainedTexts.FromKey(id, id, static key => new Text { Id = key }),
                text, fontSize, textColor, family);
    }

    /// <summary>
    /// The icon named <paramref name="name"/>, linked under the parent in
    /// scope: a square <paramref name="size"/> px on a side, or the size pushed
    /// with <see cref="PushFontSize"/>, or Md — so an icon beside a label is the
    /// label's size. It is as large as its <see cref="Icon.Size"/>, which may be
    /// changed on the icon that came back; the size stacks do not reach it.
    /// The name is one of bibui's icons or one the app registered with
    /// <see cref="Bibui.Icons.Register"/>. A name neither has
    /// (<see cref="Bibui.Icon.IsKnown"/>) throws in a debug build; a release
    /// build draws its first letter.
    /// </summary>
    public Icon Icon(string name, float? size = null)
    {
        ArgumentNullException.ThrowIfNull(name);
#if DEBUG
        if (!Bibui.Icon.IsKnown(name))
            throw new ArgumentException($"There is no icon called '{name}' (see Icon.IsKnown; an app adds its own with Icons.Register at start-up).", nameof(name));
#endif
        lock (_syncRoot)
        {
            var icon = _icons.Next();
            ResetNode(icon);
            icon.SizeX = icon.SizeY = SemanticSize.Text();
            LinkUnder(icon, parent: null);
            icon.ResetIcon();
            icon.Name = name;
            icon.Size = size is { } pixels ? FontSize.Px(pixels) : SizeInScope();
            return icon;
        }
    }

    /// <summary>
    /// A spinner <paramref name="diameter"/> px across, linked under the parent
    /// in scope. Its size is said here, once, as an icon's is.
    /// </summary>
    public Spinner Spinner(float diameter = Bibui.Renderer.Components.SpinnerRenderer.DefaultDiameter)
    {
        lock (_syncRoot)
        {
            var spinner = _spinners.Next();
            ResetNode(spinner);
            spinner.SizeX = spinner.SizeY = SemanticSize.Pixels(diameter);
            LinkUnder(spinner, parent: null);
            spinner.ResetSpinner();
            spinner.Diameter = diameter;
            return spinner;
        }
    }

    /// <summary>
    /// A box with no identity, linked under the parent in scope and laid out
    /// along the axis in scope, sized by the size stacks or by the caller —
    /// a box has no size of its own, so one with none said throws. For what
    /// claims nothing and is found by nothing: a pill, a group, a gap.
    /// </summary>
    public Box Box()
    {
        lock (_syncRoot)
        {
            var box = _boxes.Next();
            ResetForBuild(box);
            box.Claims = InteractionKinds.None;
            box.ChildLayoutAxis = _axisStack.Count > 0 ? _axisStack[^1] : LayoutAxis.X;
            if (_sizeXStack.Count > 0) box.SizeX = _sizeXStack[^1];
            if (_sizeYStack.Count > 0) box.SizeY = _sizeYStack[^1];
            LinkUnder(box, parent: null);
            return box;
        }
    }

    /// <summary>
    /// The parent in scope: the node the last <see cref="PushParent"/> pushed,
    /// which what is built now is linked under. For a builder that describes
    /// the contents of a box its caller acquired and pushed. Throws when
    /// nothing is pushed.
    /// </summary>
    public UiNode ParentInScope
    {
        get
        {
            lock (_syncRoot)
                return _parentStack.Count > 0 ? _parentStack[^1]
                    : throw new InvalidOperationException("Nothing is pushed: there is no parent in scope.");
        }
    }

    /// <summary>
    /// A gap of <paramref name="pixels"/> along the axis of the parent in
    /// scope, and nothing across it: a keyless spacer, as
    /// <see cref="Arrange.Gap"/> pushes, without naming the parent and taken
    /// from this runtime's pool rather than allocated.
    /// </summary>
    public Box Gap(float pixels)
    {
        lock (_syncRoot)
        {
            var parent = _parentStack.Count > 0 ? _parentStack[^1]
                : throw new InvalidOperationException("A gap is along its parent's axis, and there is no parent in scope.");
            var gap = Box();
            bool along = parent.ChildLayoutAxis == LayoutAxis.X;
            gap.SizeX = along ? SemanticSize.Pixels(pixels) : SemanticSize.Pixels(0f);
            gap.SizeY = along ? SemanticSize.Pixels(0f) : SemanticSize.Pixels(pixels);
            return gap;
        }
    }

    // A text is the one node sized by what it holds: on an axis with nothing
    // pushed, it is as large as its text.
    private Text DescribeText(Text text, ReadOnlySpan<char> content, FontSize? fontSize, UiColor? textColor,
        FontSlot? family)
    {
        ResetNode(text);
        text.SizeX = _sizeXStack.Count > 0 ? _sizeXStack[^1] : SemanticSize.Text();
        text.SizeY = _sizeYStack.Count > 0 ? _sizeYStack[^1] : SemanticSize.Text();
        LinkUnder(text, parent: null);
        text.SetContent(content);
        text.Size = fontSize ?? SizeInScope();
        text.Color = textColor;
        var face = FaceInScope();
        text.Weight = face.Weight;
        text.Family = family ?? face.Family;
        return text;
    }

    // The family and weight pushed with PushFontFace, or the body family at its
    // regular weight. Under _syncRoot.
    private (FontSlot Family, FontWeight Weight) FaceInScope() =>
        _faceStack.Count > 0 ? _faceStack[^1] : (FontSlot.Body, FontWeight.Regular);

    // The size pushed with PushFontSize, or Md. Under _syncRoot.
    private FontSize SizeInScope() => _fontSizeStack.Count > 0 ? _fontSizeStack[^1] : ComponentSize.Md;

    private void RewindLeaves()
    {
        _texts.Rewind();
        _icons.Rewind();
        _spinners.Rewind();
        _boxes.Rewind();
        _retainedTexts.EndFrame();
    }

    /// <summary>Instances of one kind, handed out in order and taken back all at once.</summary>
    private sealed class NodePool<T>(Func<T> create) where T : UiNode
    {
        private readonly List<T> _items = [];
        private int _used;

        public T Next()
        {
            if (_used == _items.Count) _items.Add(create());
            return _items[_used++];
        }

        public void Rewind() => _used = 0;
    }
}
