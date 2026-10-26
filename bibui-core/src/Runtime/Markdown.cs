using System.Runtime.CompilerServices;
using Bibui.Core;
using Bibui.Core.TextEdit;
using static Bibui.Core.Runtime.Markdown;

namespace Bibui.Core.Runtime;

/// <summary>
/// What <see cref="BibuiRuntime.Markdown(string, BibuiStr, float?, ComponentSize)"/>
/// built: the column holding the document, the destination of a link clicked
/// this frame, and the source offset of a task box clicked this frame.
/// </summary>
public readonly record struct MarkdownWidget(Box Node, string? ClickedLink, int ToggledTask)
{
    /// <summary>A link was clicked: <see cref="ClickedLink"/> is where it goes.</summary>
    public bool LinkClicked => ClickedLink is not null;

    /// <summary>A task box was clicked: hand <see cref="ToggledTask"/> to <see cref="Markdown.ToggleTask"/>.</summary>
    public bool TaskClicked => ToggledTask >= 0;

    public static implicit operator UiNode(MarkdownWidget widget) => widget.Node;

    /// <summary>So it goes straight into <c>Row(...)</c> or <c>Column(...)</c>.</summary>
}

/// <summary>
/// A read-only Markdown viewer, described as boxes and texts: no renderer of its
/// own, and nothing the painter does not already draw.
///
/// <para>The source is parsed by <see cref="MarkdownDocument"/> once and kept
/// per runtime and id until it changes, so a frame costs a comparison and
/// the build. Each paragraph is broken into lines here, across its runs —
/// a bold word, a code span and plain text wrap as one paragraph — at the width
/// the viewer had last frame, as <see cref="TextFieldExtensions.MultilineTextField"/> does. A line
/// is a row of texts, one per stretch of a single style.</para>
///
/// <para>It owns no state. A link is a box that takes a press; the destination
/// of the one clicked comes back in <see cref="MarkdownWidget.ClickedLink"/>. A
/// task box reports its click the same way, as the offset in the source of the
/// character between its brackets, for the caller to rewrite with
/// <see cref="ToggleTask"/> if it wants to.</para>
///
/// <para>Italics: the type system has no oblique face yet, so <c>*emphasis*</c>
/// is set in the body face. Strong, code, strikethrough and links are shown.</para>
/// </summary>
public static class Markdown
{
    internal const float CodePadX = 4f;          // either side of a code span
    internal const float CodeRadius = 4f;
    internal const float BlockPad = 12f;          // inside a code block
    internal const float QuotePad = 12f;          // between the rule and the text
    internal const float CellPadX = 10f, TightCellPadX = 6f, CellPadY = 6f;
    internal const float TightGap = 4f;           // between the items of a tight list
    internal const float TaskBox = 14f;

    // Where a line through text sits, as a fraction of its line box: the box is
    // ascent to descent, so the baseline is about four fifths down.
    internal const float UnderlineAt = 0.88f, StrikeAt = 0.56f;


    internal static readonly ConditionalWeakTable<BibuiRuntime, Cache> Caches = new();

    // Whether a run fits its room, with the hundredth of a pixel the layout's own fit test allows.
    internal static bool Fits(float width, float room) => width <= room + 0.01f;



    /// <summary>
    /// Ticks or unticks the task item whose box sits at <paramref name="offset"/>
    /// in <paramref name="source"/> — a <see cref="MarkdownWidget.ToggledTask"/>.
    /// For the caller to apply to its own source; the viewer writes nothing.
    /// </summary>
    public static void ToggleTask(BibuiStr source, int offset)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (offset < 0 || offset >= source.Length) return;
        char mark = source.Str[offset];
        if (mark is not (' ' or 'x' or 'X')) return;
        source.ReplaceRange((offset, offset + 1), mark == ' ' ? "x" : " ");
    }

    internal static MarkdownWidget Build(BibuiRuntime runtime, string id, Cache cache, Entry entry, float? width,
        ComponentSize textSize)
    {
        Box root = runtime.BoxFromId(id);
        root.ChildLayoutAxis = LayoutAxis.Y;
        root.SizeX = width is { } w ? SemanticSize.Pixels(w) : SemanticSize.ParentPct(1f);
        root.SizeY = SemanticSize.ChildrenSum();

        // Broken at last frame's width; before the first layout, at the surface's.
        float room = width ?? (root.Extent.X > 0f ? root.Extent.X : runtime.SurfaceWidth / MathF.Max(1f, runtime.ScaleFactor));

        var writer = cache.Writer;
        writer.Begin(runtime, entry, textSize);
        using (runtime.Parent(root))
        using (runtime.FontFace(FontSlot.Body, FontWeight.Regular))
        using (runtime.SizeX(SemanticSize.Text()))
        using (runtime.SizeY(SemanticSize.Text()))
            writer.Blocks(entry.Document!.Root.Children, room, writer.Body, tight: false);
        var result = new MarkdownWidget(root, writer.ClickedLink, writer.ToggledTask);
        writer.End();
        return result;
    }

    // ── Per runtime ─────────────────────────────────────────────────────────

    internal sealed class Cache
    {
        private const int MaxEntries = 64;
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private long _clock;

        public readonly Writer Writer = new();

        public Entry Entry(string id)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                if (_entries.Count >= MaxEntries) EvictOldest();
                entry = new Entry(id);
                _entries.Add(id, entry);
            }
            entry.Used = ++_clock;
            return entry;
        }

        private void EvictOldest()
        {
            string? oldest = null;
            long used = long.MaxValue;
            foreach (var (key, value) in _entries)
                if (value.Used < used) (oldest, used) = (key, value.Used);
            if (oldest is not null) _entries.Remove(oldest);
        }
    }

    internal sealed class Entry(string id)
    {
        private readonly List<string> _ids = [];

        // The source last parsed, refilled in place when it changes: an edit
        // copies into it, and allocates only when the source outgrows it.
        public readonly BibuiStr Source = new(0);
        // Whether Document was parsed from Source, not handed in by the caller.
        public bool Parsed;
        public MarkdownDocument? Document;
        public long Used;

        // The n-th retained box of a build — a link piece, a task box, a code
        // block — named once and kept, so a frame allocates no ids.
        public string IdAt(int n)
        {
            while (_ids.Count <= n) _ids.Add($"{id}/{_ids.Count}");
            return _ids[n];
        }
    }

    // How a run of inline text is set, before its own style.
    internal readonly record struct Look(FontSize Size, FontSlot Family, bool Bold, UiColor Color);

    internal struct Frag
    {
        public int Run;
        public int Start, Len;
        public float Width;
        public bool Space, Break;
    }

    internal struct Line
    {
        public int Start, End;    // in Writer._placed
        public float Width;
    }

    // ── The build ───────────────────────────────────────────────────────────

    internal sealed class Writer
    {
        private readonly List<Frag> _frags = [];
        private readonly List<Frag> _placed = [];
        private readonly List<Line> _lines = [];
        private readonly Font[] _fonts = new Font[4];   // body, body bold, mono, mono bold
        private BibuiRuntime _runtime = null!;
        private Entry _entry = null!;
        private MarkdownDocument _doc = null!;
        private ComponentSize _textSize;
        private int _nextId, _listDepth;
        private float _breakSlack;   // how far a word may run into a cell's padding before it is broken

        // Line widths and vertical padding are the theme's (Borders, Density), never literals:
        // at the default theme they are the 1 px lines and the padding this was drawn with.
        private float LineWidth => _runtime.Theme.Borders.Width;
        private float StrongLine => _runtime.Theme.Borders.Strong;
        private float QuoteRule => MathF.Round(StrongLine * 1.5f);   // a heavy rule: 3 px at the default 2
        private float Dense(float pixels) => _runtime.Theme.Density.Of(pixels);

        public string? ClickedLink;
        public int ToggledTask = -1;

        public Look Body => new(_textSize, FontSlot.Body, false, UiColor.TextPrimary);

        public void Begin(BibuiRuntime runtime, Entry entry, ComponentSize textSize)
        {
            _runtime = runtime;
            _entry = entry;
            _doc = entry.Document!;
            _textSize = textSize;
            _nextId = 0;
            _listDepth = 0;
            _breakSlack = 0f;
            ClickedLink = null;
            ToggledTask = -1;
        }

        public void End()
        {
            _runtime = null!;
            _entry = null!;
            _doc = null!;
        }

        private string NextId() => _entry.IdAt(_nextId++);

        private float Px(Look look) => _runtime.FontInScope(look.Size).Size;

        // ── Blocks ──────────────────────────────────────────────────────────

        public void Blocks(IReadOnlyList<MarkdownBlock> blocks, float room, Look look, bool tight)
        {
            float px = Px(look);
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (i > 0)
                {
                    float gap = tight ? TightGap : MathF.Round(px * 0.85f);
                    if (block.Kind == MarkdownBlockKind.Heading) gap = MathF.Round(px * 1.4f);
                    else if (blocks[i - 1].Kind == MarkdownBlockKind.Heading) gap = MathF.Round(px * 0.6f);
                    _runtime.Gap(gap);
                }
                Block(block, room, look, tight);
            }
        }

        private void Block(MarkdownBlock block, float room, Look look, bool tight)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Paragraph:
                    Inline(_doc.RunsOf(block), room, look, MarkdownAlign.None);
                    break;
                case MarkdownBlockKind.Heading:
                    Heading(block, room, look);
                    break;
                case MarkdownBlockKind.CodeBlock:
                    CodeBlock(block, room, look);
                    break;
                case MarkdownBlockKind.Quote:
                    Quote(block, room, look);
                    break;
                case MarkdownBlockKind.List:
                    List(block, room, look);
                    break;
                case MarkdownBlockKind.Table:
                    Table(block, room, look);
                    break;
                case MarkdownBlockKind.ThematicBreak:
                    Rule(room);
                    break;
            }
        }

        private void Heading(MarkdownBlock block, float room, Look look)
        {
            // h1 three steps above the text, h2 two, h3 one, h4–h6 the text's own size.
            int step = Math.Clamp((int)_textSize + Math.Max(0, 4 - block.Level), 0, (int)ComponentSize.Xxl);
            var heading = new Look((ComponentSize)step, FontSlot.Heading, true,
                block.Level == 6 ? UiColor.TextSecondary : look.Color);
            Inline(_doc.RunsOf(block), room, heading, MarkdownAlign.None);
            if (block.Level <= 2)
            {
                _runtime.Gap(MathF.Round(Px(heading) * 0.3f));
                Rule(room);
            }
        }

        private void Rule(float room)
        {
            Box rule = _runtime.Box();
            rule.SizeX = SemanticSize.Pixels(room);
            rule.SizeY = SemanticSize.Pixels(LineWidth);
            rule.Style.BackgroundColor = UiColor.Border;
        }

        // Mono, unwrapped, on a raised surface; wider than the room, it is cut
        // at its edge and scrolls sideways under the wheel.
        private void CodeBlock(MarkdownBlock block, float room, Look look)
        {
            var mono = look with { Family = FontSlot.Mono, Bold = false };
            Font font = FontOf(mono);
            float lineHeight = _runtime.Fonts.LineHeight(font);
            float advance = MathF.Max(lineHeight, _runtime.Fonts.LineAdvance(font));

            // Scrolls only when there is somewhere to scroll: claiming the wheel
            // otherwise would take it from a scrolling view around the document.
            string id = NextId();
            Box view = _runtime.BoxFromId(id);
            bool wide = view.ContentSize.X > view.Extent.X + 0.5f;
            view.Claims = InteractionKinds.Clip | (wide ? InteractionKinds.ViewScrollX | InteractionKinds.ClampX : InteractionKinds.None);
            _runtime.InteractionsFor(view);
            if (!wide) view.TargetViewOffset = view.ViewOffset = default;

            view.ChildLayoutAxis = LayoutAxis.Y;
            view.SizeX = SemanticSize.Pixels(room);
            view.SizeY = SemanticSize.ChildrenSum();
            view.AllowOverflowX = true;
            view.Style.Padding = EdgeInsets.Symmetric(horizontal: BlockPad, vertical: Dense(BlockPad));
            view.Style.BackgroundColor = UiColor.SurfaceRaised;
            view.Style.BorderColor = UiColor.Border;
            view.Style.BorderWidth = 1f;
            view.Style.CornerRadius = 6f;

            var code = _doc.CodeOf(block).Span;
            using (_runtime.Parent(view))
            {
                int start = 0, index = 0;
                while (true)
                {
                    int newline = code[start..].IndexOf('\n');
                    int end = newline < 0 ? code.Length : start + newline;
                    if (index++ > 0) _runtime.Gap(advance - lineHeight);
                    using (_runtime.FontFace(FontSlot.Mono, FontWeight.Regular))
                        _runtime.Text(code[start..end], mono.Size, look.Color, FontSlot.Mono);
                    if (newline < 0) break;
                    start = end + 1;
                }
            }
        }

        private void Quote(MarkdownBlock block, float room, Look look)
        {
            Box quote = _runtime.Box();
            quote.ChildLayoutAxis = LayoutAxis.Y;
            quote.SizeX = SemanticSize.Pixels(room);
            quote.SizeY = SemanticSize.ChildrenSum();
            quote.Style.Padding = new EdgeInsets(0f, 0f, 0f, QuoteRule + QuotePad);
            quote.Style.BorderLeft = new EdgeBorder(QuoteRule, UiColor.Border);
            using (_runtime.Parent(quote))
                Blocks(block.Children, room - QuoteRule - QuotePad, look with { Color = UiColor.TextSecondary }, tight: false);
        }

        // Items in rows: a marker column — a bullet, a number or a task box —
        // and the item's blocks beside it, so wrapped lines hang under the text.
        private void List(MarkdownBlock list, float room, Look look)
        {
            Font font = FontOf(look);
            float px = font.Size;
            float lineHeight = _runtime.Fonts.LineHeight(font);
            int count = list.Children.Count;

            float numberWidth = 0f;
            Span<char> number = stackalloc char[12];
            if (list.Ordered)
                numberWidth = _runtime.Fonts.Measure(Number(list.Level + count - 1, number), font);
            bool tasks = false;
            for (int k = 0; k < count; k++) tasks |= list.Children[k].Task != MarkdownTask.None;
            float markerWidth = MathF.Ceiling(MathF.Max(px * 1.5f,
                MathF.Max(list.Ordered ? numberWidth + px * 0.5f : 0f, tasks ? TaskBox + px * 0.6f : 0f)));

            Box column = _runtime.Box();
            column.ChildLayoutAxis = LayoutAxis.Y;
            column.SizeX = SemanticSize.Pixels(room);
            column.SizeY = SemanticSize.ChildrenSum();
            using var inColumn = _runtime.Parent(column);
            for (int k = 0; k < count; k++)
            {
                var item = list.Children[k];
                if (k > 0) _runtime.Gap(list.Tight ? TightGap : MathF.Round(px * 0.85f));

                Box row = _runtime.Box();
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.Pixels(room);
                row.SizeY = SemanticSize.ChildrenSum();
                using var inRow = _runtime.Parent(row);

                Box marker = _runtime.Box();
                marker.ChildLayoutAxis = LayoutAxis.X;
                marker.SizeX = SemanticSize.Pixels(markerWidth);
                marker.SizeY = SemanticSize.Pixels(lineHeight);
                using (_runtime.Parent(marker))
                {
                    if (item.Task != MarkdownTask.None) Task(item, lineHeight);
                    else if (list.Ordered)
                    {
                        var text = Number(list.Level + k, number);
                        _runtime.Gap(markerWidth - px * 0.5f - _runtime.Fonts.Measure(text, font));
                        _runtime.Text(text, look.Size, look.Color, look.Family);
                    }
                    else Bullet(lineHeight, look.Color);
                }

                Box content = _runtime.Box();
                content.ChildLayoutAxis = LayoutAxis.Y;
                content.SizeX = SemanticSize.Pixels(MathF.Max(0f, room - markerWidth));
                content.SizeY = SemanticSize.ChildrenSum();
                using (_runtime.Parent(content))
                {
                    _listDepth++;
                    Blocks(item.Children, MathF.Max(0f, room - markerWidth), look, list.Tight);
                    _listDepth--;
                }
            }
        }

        // A filled dot, a ring a level down, a square below that: drawn, so no font needs the glyphs.
        private void Bullet(float lineHeight, UiColor color)
        {
            int depth = _listDepth % 3;
            float size = depth == 2 ? 5f : 6f;
            Box dot = _runtime.Box();
            dot.SizeX = dot.SizeY = SemanticSize.Pixels(size);
            dot.At(4f, MathF.Round((lineHeight - size) / 2f));
            if (depth == 1)
            {
                dot.Style.BorderColor = color;
                dot.Style.BorderWidth = LineWidth * 1.25f;
                dot.Style.CornerRadius = size / 2f;
            }
            else
            {
                dot.Style.BackgroundColor = color;
                dot.Style.CornerRadius = depth == 0 ? size / 2f : 1f;
            }
        }

        // A checkbox's look, centred on the first line. It takes a press and
        // reports it; the source is the caller's to change.
        private void Task(MarkdownBlock item, float lineHeight)
        {
            bool ticked = item.Task == MarkdownTask.Checked;
            Box box = _runtime.BoxFromId(NextId(), item.TaskOffset >= 0 ? InteractionKinds.LeftPress : InteractionKinds.None);
            if (_runtime.InteractionsFor(box).Clicked) ToggledTask = item.TaskOffset;
            box.ChildLayoutAxis = LayoutAxis.X;
            box.SizeX = box.SizeY = SemanticSize.Pixels(TaskBox);
            box.At(0f, MathF.Round((lineHeight - TaskBox) / 2f));
            box.Style.CornerRadius = 3f;
            box.Style.BorderWidth = LineWidth;
            box.Style.BorderColor = ticked ? UiColor.Accent : UiColor.BorderHover;
            box.Style.HoverBorderColor = ticked ? UiColor.AccentHover : UiColor.BorderFocus;
            box.Style.BackgroundColor = ticked ? UiColor.Accent : UiColor.Surface;
            box.Style.Padding = EdgeInsets.All(LineWidth);
            if (!ticked) return;
            using (_runtime.Parent(box))
                _runtime.Icon("check", size: TaskBox - 2f).Color = UiColor.AccentForeground;
        }

        // Columns sized from their content: each at its widest line when they
        // all fit, otherwise each given its widest word and a share of what is
        // left by how much more it would take. Cells wrap at their column.
        private void Table(MarkdownBlock table, float room, Look look)
        {
            int columns = table.Aligns.Count;
            if (columns == 0 || table.Children.Count == 0) return;
            Span<float> min = columns <= 32 ? stackalloc float[columns] : new float[columns];
            Span<float> max = columns <= 32 ? stackalloc float[columns] : new float[columns];
            Span<float> widths = columns <= 32 ? stackalloc float[columns] : new float[columns];
            min.Clear();
            max.Clear();

            for (int r = 0; r < table.Children.Count; r++)
            {
                var cellLook = CellLook(look, r);
                var cells = table.Children[r].Children;
                for (int c = 0; c < columns && c < cells.Count; c++)
                {
                    MeasureInline(_doc.RunsOf(cells[c]), cellLook, out float least, out float most);
                    min[c] = MathF.Max(min[c], least);
                    max[c] = MathF.Max(max[c], most);
                }
            }
            // Cells give up some of their padding when the words do not fit as they are.
            float sumMin = 0f;
            foreach (float m in min) sumMin += m;
            float padX = sumMin + columns * 2f * CellPadX <= room - 2f ? CellPadX : TightCellPadX;
            for (int c = 0; c < columns; c++) { min[c] += 2f * padX; max[c] += 2f * padX; }
            ColumnWidths(min, max, room - 2f, widths);
            // A word a hair too wide for a cramped column runs into its padding rather than break.
            _breakSlack = padX == TightCellPadX ? padX - 2f : 0f;
            float tableWidth = 0f;
            foreach (float w in widths) tableWidth += w;

            Box box = _runtime.Box();
            box.ChildLayoutAxis = LayoutAxis.Y;
            box.SizeX = SemanticSize.Pixels(tableWidth + 2f * LineWidth);
            box.SizeY = SemanticSize.ChildrenSum();
            box.Style.Padding = EdgeInsets.All(LineWidth);   // room for the outline
            box.Style.BorderColor = UiColor.Border;
            box.Style.BorderWidth = LineWidth;
            using var inTable = _runtime.Parent(box);

            for (int r = 0; r < table.Children.Count; r++)
            {
                var cellLook = CellLook(look, r);
                var cells = table.Children[r].Children;
                if (r > 0)
                {
                    Box line = _runtime.Box();
                    line.SizeX = SemanticSize.Pixels(tableWidth);
                    line.SizeY = SemanticSize.Pixels(LineWidth);
                    line.Style.BackgroundColor = UiColor.Border;
                }

                float rowHeight = 0f;
                for (int c = 0; c < columns && c < cells.Count; c++)
                    rowHeight = MathF.Max(rowHeight, InlineHeight(_doc.RunsOf(cells[c]), widths[c] - 2f * padX, cellLook));

                Box row = _runtime.Box();
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.Pixels(tableWidth);
                row.SizeY = SemanticSize.Pixels(rowHeight + 2f * Dense(CellPadY));
                if (r == 0) row.Style.BackgroundColor = UiColor.SurfaceRaised;
                using var inRow = _runtime.Parent(row);
                for (int c = 0; c < columns; c++)
                {
                    Box cell = _runtime.Box();
                    cell.ChildLayoutAxis = LayoutAxis.Y;
                    cell.SizeX = SemanticSize.Pixels(widths[c]);
                    cell.SizeY = SemanticSize.ParentPct(1f);
                    cell.Style.Padding = EdgeInsets.Symmetric(padX, Dense(CellPadY));
                    if (c > 0) cell.Style.BorderLeft = new EdgeBorder(LineWidth, UiColor.Border);
                    if (c >= cells.Count) continue;
                    using (_runtime.Parent(cell))
                        Inline(_doc.RunsOf(cells[c]), widths[c] - 2f * padX, cellLook, table.Aligns[c]);
                }
            }
            _breakSlack = 0f;
        }

        private static Look CellLook(Look look, int row) => row == 0 ? look with { Bold = true } : look;

        // Every column at its widest line when that fits. Else short columns are
        // set on one line, and the others get their widest word and a share of the
        // rest by how much more each would take. When not even
        // the words fit, columns whose widest word fits a fair share of what is
        // left get it, narrowest first, and the rest share the remainder by how
        // much text each holds — so only words that could never fit are broken.
        private static void ColumnWidths(ReadOnlySpan<float> min, ReadOnlySpan<float> max, float room, Span<float> widths)
        {
            int n = min.Length;
            float sumMin = 0f, sumMax = 0f;
            for (int c = 0; c < n; c++) { sumMin += min[c]; sumMax += max[c]; }
            if (sumMax <= room)
            {
                for (int c = 0; c < n; c++) widths[c] = max[c];
                return;
            }
            widths.Fill(-1f);
            if (sumMin <= room)
            {
                // Short columns first: one whose whole text fits a fair share of
                // what is left, leaving the others their words, is set on one line.
                float left = room, restMin = sumMin, restMax = sumMax;
                for (int open = n; open > 0; open--)
                {
                    int shortest = -1;
                    for (int c = 0; c < n; c++)
                        if (widths[c] < 0f && (shortest < 0 || max[c] < max[shortest])) shortest = c;
                    if (max[shortest] > left / open || max[shortest] + restMin - min[shortest] > left) break;
                    widths[shortest] = max[shortest];
                    left -= max[shortest];
                    restMin -= min[shortest];
                    restMax -= max[shortest];
                }
                for (int c = 0; c < n; c++)
                    if (widths[c] < 0f)
                        widths[c] = min[c] + (max[c] - min[c]) * (left - restMin) / MathF.Max(1f, restMax - restMin);
                return;
            }
            float remaining = room, unmet = sumMax;
            for (int left = n; left > 0; left--)
            {
                int narrowest = -1;
                for (int c = 0; c < n; c++)
                    if (widths[c] < 0f && (narrowest < 0 || min[c] < min[narrowest])) narrowest = c;
                if (min[narrowest] > remaining / left) break;
                widths[narrowest] = min[narrowest];
                remaining -= min[narrowest];
                unmet -= max[narrowest];
            }
            for (int c = 0; c < n; c++)
                if (widths[c] < 0f) widths[c] = remaining * max[c] / MathF.Max(1f, unmet);
        }

        // ── Inline text ─────────────────────────────────────────────────────

        private Font FontOf(Look look) => FontOf(look.Size, look.Family, look.Bold);

        private Font FontOf(FontSize size, FontSlot family, bool bold)
        {
            using (_runtime.FontFace(family, bold ? FontWeight.Bold : FontWeight.Regular))
                return _runtime.FontInScope(size, family);
        }

        private void PrepareFonts(Look look)
        {
            _fonts[0] = FontOf(look.Size, look.Family, look.Bold);
            _fonts[1] = FontOf(look.Size, look.Family, true);
            _fonts[2] = FontOf(look.Size, FontSlot.Mono, look.Bold);
            _fonts[3] = FontOf(look.Size, FontSlot.Mono, true);
        }

        private static int FontIndex(MarkdownStyle style, Look look)
            => ((style & MarkdownStyle.Code) != 0 ? 2 : 0) + (look.Bold || (style & MarkdownStyle.Strong) != 0 ? 1 : 0);

        private Font RunFont(in MarkdownRun run, Look look) => _fonts[FontIndex(run.Style, look)];

        // The block's runs as words and spaces, each measured in its run's font.
        private void Fragments(ReadOnlySpan<MarkdownRun> runs, Look look)
        {
            _frags.Clear();
            var text = _doc.Text.Span;
            for (int r = 0; r < runs.Length; r++)
            {
                var run = runs[r];
                if ((run.Style & MarkdownStyle.LineBreak) != 0)
                {
                    _frags.Add(new Frag { Run = r, Start = run.Start, Break = true });
                    continue;
                }
                Font font = RunFont(run, look);
                int i = run.Start, end = run.Start + run.Length;
                while (i < end)
                {
                    bool space = text[i] == ' ';
                    int j = i;
                    while (j < end && (text[j] == ' ') == space) j++;
                    _frags.Add(new Frag
                    {
                        Run = r, Start = i, Len = j - i, Space = space,
                        Width = _runtime.Fonts.Measure(text[i..j], font),
                    });
                    i = j;
                }
            }
        }

        // A code span's padding, counted where a piece of one starts.
        private static float PadBefore(ReadOnlySpan<MarkdownRun> runs, List<Frag> frags, int i, bool lineStart)
        {
            if ((runs[frags[i].Run].Style & MarkdownStyle.Code) == 0) return 0f;
            return lineStart || i == 0 || frags[i - 1].Run != frags[i].Run ? 2f * CodePadX : 0f;
        }

        // The widest unbreakable word and the whole text on one line, for sizing table columns.
        private void MeasureInline(ReadOnlySpan<MarkdownRun> runs, Look look, out float least, out float most)
        {
            PrepareFonts(look);
            Fragments(runs, look);
            least = most = 0f;
            float line = 0f, word = 0f;
            for (int i = 0; i < _frags.Count; i++)
            {
                var f = _frags[i];
                if (f.Break) { most = MathF.Max(most, line); line = 0f; word = 0f; continue; }
                float w = f.Width + PadBefore(runs, _frags, i, false);
                line += w;
                if (f.Space) word = 0f;
                else least = MathF.Max(least, word += w);
            }
            most = MathF.Max(most, line);
            // A word wider than a column breaks inside it; past a point it is cheaper to break it.
            least = MathF.Min(least, 160f);
        }

        private float InlineHeight(ReadOnlySpan<MarkdownRun> runs, float room, Look look)
        {
            BreakLines(runs, room, look);
            return LinesHeight(look);
        }

        private float LinesHeight(Look look)
        {
            var (lineHeight, advance) = LineMetrics();
            int n = Math.Max(1, _lines.Count);
            return lineHeight + (n - 1) * advance;
        }

        private (float LineHeight, float Advance) LineMetrics()
        {
            float lineHeight = MathF.Max(_runtime.Fonts.LineHeight(_fonts[0]), _runtime.Fonts.LineHeight(_fonts[2]));
            float advance = MathF.Max(lineHeight, _runtime.Fonts.LineAdvance(_fonts[0]));
            return (lineHeight, advance);
        }

        // Greedy line breaking across runs: words go on the line while they fit;
        // a word wider than the whole line is broken between characters.
        private void BreakLines(ReadOnlySpan<MarkdownRun> runs, float room, Look look)
        {
            PrepareFonts(look);
            Fragments(runs, look);
            _placed.Clear();
            _lines.Clear();
            int lineStart = 0;
            float x = 0f;
            bool content = false;

            int i = 0, n = _frags.Count;
            while (i < n)
            {
                if (_frags[i].Break)
                {
                    EndLine(ref lineStart, ref x, ref content);
                    content = false;
                    i++;
                    continue;
                }
                int spaces = i;
                float spaceWidth = 0f;
                while (i < n && _frags[i].Space) spaceWidth += _frags[i++].Width;
                int group = i;
                float groupWidth = 0f;
                while (i < n && !_frags[i].Space && !_frags[i].Break)
                {
                    groupWidth += _frags[i].Width + PadBefore(runs, _frags, i, i == group);
                    i++;
                }
                if (group == i) continue;   // spaces before a break or the end

                if (content && Fits(x + spaceWidth + groupWidth, room))
                {
                    for (int k = spaces; k < group; k++) _placed.Add(_frags[k]);
                    for (int k = group; k < i; k++) _placed.Add(_frags[k]);
                    x += spaceWidth + groupWidth;
                    continue;
                }
                if (content) EndLine(ref lineStart, ref x, ref content);
                if (Fits(groupWidth, room + _breakSlack))
                {
                    for (int k = group; k < i; k++) _placed.Add(_frags[k]);
                    x = groupWidth;
                    content = true;
                    continue;
                }
                PlaceBroken(runs, look, group, i, room + _breakSlack, ref lineStart, ref x, ref content);
            }
            if (content || _lines.Count == 0) EndLine(ref lineStart, ref x, ref content);
        }

        private void EndLine(ref int lineStart, ref float x, ref bool content)
        {
            _lines.Add(new Line { Start = lineStart, End = _placed.Count, Width = x });
            lineStart = _placed.Count;
            x = 0f;
            content = false;
        }

        // A word too wide for a line, cut between characters to fill each line.
        private void PlaceBroken(ReadOnlySpan<MarkdownRun> runs, Look look, int from, int to, float room,
            ref int lineStart, ref float x, ref bool content)
        {
            var text = _doc.Text.Span;
            for (int k = from; k < to; k++)
            {
                var f = _frags[k];
                Font font = RunFont(runs[f.Run], look);
                bool code = (runs[f.Run].Style & MarkdownStyle.Code) != 0;
                while (true)
                {
                    float pad = code && (x == 0f || k == from) ? 2f * CodePadX : 0f;
                    if (Fits(x + pad + f.Width, room))
                    {
                        _placed.Add(f);
                        x += pad + f.Width;
                        content = true;
                        break;
                    }
                    var chars = text.Slice(f.Start, f.Len);
                    float avail = room - x - pad;
                    // Cut between clusters, so a word too wide for the line never splits 🧑‍🚀:
                    // as many as fit in avail, measured as the walk sets them (physical px).
                    var style = TextLayer.StyleFromFont(_runtime.Fonts, font, font.Scale);
                    int cut = 0;
                    float cutWidth = 0f;
                    while (cut < chars.Length)
                    {
                        int end = TextLayer.ScanCluster(_runtime.Fonts, style, chars, cut, +1, out float width);
                        if (!Fits((cutWidth + width) / font.Scale, avail)) break;
                        (cut, cutWidth) = (end, cutWidth + width);
                    }
                    if (cut == 0)
                    {
                        if (content) { EndLine(ref lineStart, ref x, ref content); continue; }
                        cut = TextLayer.ScanCluster(_runtime.Fonts, style, chars, 0, +1, out cutWidth);   // one cluster, whatever its width
                    }
                    if (cut >= f.Len) { _placed.Add(f); x += pad + f.Width; content = true; break; }
                    float head = cutWidth / font.Scale;
                    _placed.Add(f with { Len = cut, Width = head });
                    x += pad + head;
                    content = true;
                    EndLine(ref lineStart, ref x, ref content);
                    f = f with { Start = f.Start + cut, Len = f.Len - cut, Width = _runtime.Fonts.Measure(chars[cut..], font) };
                }
            }
        }

        // A paragraph's lines, each a row of texts: a text per stretch of one run.
        private void Inline(ReadOnlySpan<MarkdownRun> runs, float room, Look look, MarkdownAlign align)
        {
            BreakLines(runs, room, look);
            var (lineHeight, advance) = LineMetrics();
            for (int l = 0; l < _lines.Count; l++)
            {
                var line = _lines[l];
                if (l > 0) _runtime.Gap(advance - lineHeight);
                Box row = _runtime.Box();
                row.ChildLayoutAxis = LayoutAxis.X;
                row.SizeX = SemanticSize.Pixels(MathF.Max(room, 0f));
                row.SizeY = SemanticSize.Pixels(lineHeight);
                using var inRow = _runtime.Parent(row);

                float slack = MathF.Max(0f, room - line.Width);
                if (align == MarkdownAlign.Center && slack > 0f) _runtime.Gap(MathF.Floor(slack / 2f));
                else if (align == MarkdownAlign.Right && slack > 0f) _runtime.Gap(MathF.Floor(slack));

                int p = line.Start;
                while (p < line.End)
                {
                    var first = _placed[p];
                    int q = p + 1, end = first.Start + first.Len;
                    while (q < line.End && _placed[q].Run == first.Run && _placed[q].Start == end)
                    {
                        end += _placed[q].Len;
                        q++;
                    }
                    Piece(runs[first.Run], first.Start, end - first.Start, look, lineHeight);
                    p = q;
                }
            }
        }

        // One stretch of a run on a line: a text, in a box when it needs one —
        // a code span's background, a link's press and underline, a strike.
        private void Piece(in MarkdownRun run, int start, int length, Look look, float lineHeight)
        {
            var style = run.Style;
            Font font = RunFont(run, look);
            bool code = (style & MarkdownStyle.Code) != 0;
            bool link = run.Link >= 0;
            bool strike = (style & MarkdownStyle.Strike) != 0;
            bool bold = look.Bold || (style & MarkdownStyle.Strong) != 0;
            FontSlot family = code ? FontSlot.Mono : look.Family;
            UiColor color = link ? UiColor.Accent : look.Color;
            var chars = _doc.Text.Span.Slice(start, length);
            float textHeight = _runtime.Fonts.LineHeight(font);
            float top = MathF.Round((lineHeight - textHeight) / 2f);

            if (!code && !link && !strike && top <= 0f)
            {
                using (_runtime.FontFace(family, bold ? FontWeight.Bold : FontWeight.Regular))
                    _runtime.Text(chars, look.Size, color, family);
                return;
            }

            Box box;
            bool hovering = false;
            if (link)
            {
                box = _runtime.BoxFromId(NextId(), InteractionKinds.LeftPress);
                var interaction = _runtime.InteractionsFor(box);
                if (interaction.Clicked) ClickedLink = _doc.Links[run.Link];
                hovering = interaction.Hovering;
            }
            else box = _runtime.Box();
            box.ChildLayoutAxis = LayoutAxis.X;
            box.SizeX = SemanticSize.ChildrenSum();
            box.SizeY = SemanticSize.Pixels(lineHeight);
            float padX = code ? CodePadX : 0f;
            box.Style.Padding = new EdgeInsets(top, padX, 0f, padX);
            if (code)
            {
                box.Style.BackgroundColor = UiColor.SurfaceRaised;
                box.Style.CornerRadius = CodeRadius;
            }

            using (_runtime.Parent(box))
            {
                using (_runtime.FontFace(family, bold ? FontWeight.Bold : FontWeight.Regular))
                    _runtime.Text(chars, look.Size, color, family);
                float width = _runtime.Fonts.Measure(chars, font);
                if (link) Overlay(padX, top + MathF.Round(textHeight * UnderlineAt), width, hovering ? StrongLine : LineWidth, color);
                if (strike) Overlay(padX, top + MathF.Round(textHeight * StrikeAt), width, LineWidth, color);
            }
        }

        private void Overlay(float x, float y, float width, float thickness, UiColor color)
        {
            Box line = _runtime.Box();
            line.SizeX = SemanticSize.Pixels(width);
            line.SizeY = SemanticSize.Pixels(thickness);
            line.At(x, y);
            line.Style.BackgroundColor = color;
        }
    }

    // ── Numbers ─────────────────────────────────────────────────────────────

    // "n." formatted into the caller's buffer, which 12 chars always holds:
    // nothing is kept, since the Text the number goes to copies it.
    internal static ReadOnlySpan<char> Number(int n, Span<char> into)
    {
        n.TryFormat(into, out int length);
        into[length] = '.';
        return into[..(length + 1)];
    }
}

/// <summary>The Markdown component, called on the runtime that builds it.</summary>
public static class MarkdownExtensions
{
    /// <summary>
    /// Builds the viewer named <paramref name="id"/> under the parent in scope,
    /// showing <paramref name="source"/>. It is <paramref name="width"/> wide, or
    /// as wide as its parent when that is null — in which case the parent must
    /// have a width of its own — and as tall as the document. Text is set in
    /// <paramref name="textSize"/>, headings in the steps above it.
    /// </summary>
    public static MarkdownWidget Markdown(this BibuiRuntime runtime, string id, BibuiStr source, float? width = null,
        ComponentSize textSize = ComponentSize.Md)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(source);
        var cache = Caches.GetValue(runtime, static _ => new Cache());
        var entry = cache.Entry(id);
        if (!entry.Parsed || !source.Str.SequenceEqual(entry.Source.Str))
        {
            entry.Source.ReplaceRange((0, (int)entry.Source.Length), source.Str);
            entry.Document = MarkdownDocument.Parse(entry.Source.Str);
            entry.Parsed = true;
        }
        return Build(runtime, id, cache, entry, width, textSize);
    }

    /// <summary>
    /// The same, for a document the caller parsed and keeps: no comparison with
    /// last frame's source at all.
    /// </summary>
    public static MarkdownWidget Markdown(this BibuiRuntime runtime, string id, MarkdownDocument document, float? width = null,
        ComponentSize textSize = ComponentSize.Md)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(document);
        var cache = Caches.GetValue(runtime, static _ => new Cache());
        var entry = cache.Entry(id);
        entry.Parsed = false;
        entry.Document = document;
        return Build(runtime, id, cache, entry, width, textSize);
    }
}
