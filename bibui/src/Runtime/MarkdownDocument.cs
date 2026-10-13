using System.Text;

namespace Bibui.Runtime;

/// <summary>What a <see cref="MarkdownBlock"/> is.</summary>
public enum MarkdownBlockKind
{
    Document, Paragraph, Heading, CodeBlock, Quote, List, ListItem, Table, TableRow, TableCell, ThematicBreak,
}

/// <summary>How a <see cref="MarkdownRun"/> is set. Flags: a run can be bold, struck and a link at once.</summary>
[Flags]
public enum MarkdownStyle : byte
{
    None = 0,
    Strong = 1 << 0,
    Emphasis = 1 << 1,
    Code = 1 << 2,
    Strike = 1 << 3,
    Link = 1 << 4,
    /// <summary>An image's alt text, its <see cref="MarkdownRun.Link"/> the image's source.</summary>
    Image = 1 << 5,
    /// <summary>A hard line break: a run with no text that ends the line.</summary>
    LineBreak = 1 << 6,
}

/// <summary>A table column's alignment, from its delimiter row (<c>:---</c>, <c>:---:</c>, <c>---:</c>).</summary>
public enum MarkdownAlign : byte { None, Left, Center, Right }

/// <summary>Whether a list item is a GFM task item, and whether it is ticked.</summary>
public enum MarkdownTask : byte { None, Unchecked, Checked }

/// <summary>
/// A piece of inline text in one style: <see cref="Length"/> bytes of
/// <see cref="MarkdownDocument.Text"/> from <see cref="Start"/>. <see cref="Link"/>
/// indexes <see cref="MarkdownDocument.Links"/>, or is -1.
/// </summary>
public readonly record struct MarkdownRun(int Start, int Length, MarkdownStyle Style, int Link);

/// <summary>
/// One block of a parsed document. Containers (<see cref="MarkdownBlockKind.Quote"/>,
/// lists, items, tables and rows) hold <see cref="Children"/>; paragraphs,
/// headings and table cells hold inline runs; a code block holds text.
/// </summary>
public sealed class MarkdownBlock
{
    internal readonly List<MarkdownBlock> ChildList = [];

    internal MarkdownBlock(MarkdownBlockKind kind) => Kind = kind;

    public MarkdownBlockKind Kind { get; internal set; }

    /// <summary>The blocks inside a container, in order.</summary>
    public IReadOnlyList<MarkdownBlock> Children => ChildList;

    /// <summary>A heading's level, 1–6; an ordered list's first number.</summary>
    public int Level { get; internal set; }

    /// <summary>A list that is numbered.</summary>
    public bool Ordered { get; internal set; }

    /// <summary>A list whose items are not separated by blank lines: its paragraphs sit close.</summary>
    public bool Tight { get; internal set; } = true;

    /// <summary>A list item's task box, if it has one.</summary>
    public MarkdownTask Task { get; internal set; }

    /// <summary>
    /// The byte offset in the source of the character between a task item's
    /// brackets — the space or the <c>x</c> — or -1. What <see cref="Markdown.ToggleTask"/> rewrites.
    /// </summary>
    public int TaskOffset { get; internal set; } = -1;

    /// <summary>Where this block's runs start in <see cref="MarkdownDocument.Runs"/>.</summary>
    public int RunStart { get; internal set; }

    /// <summary>How many runs this block holds.</summary>
    public int RunCount { get; internal set; }

    /// <summary>A code block's text, in <see cref="MarkdownDocument.Text"/>: lines separated by '\n'.</summary>
    public int TextStart { get; internal set; }

    /// <inheritdoc cref="TextStart"/>
    public int TextLength { get; internal set; }

    /// <summary>A fenced code block's info string (its language), or null.</summary>
    public string? Info { get; internal set; }

    /// <summary>A table's column alignments, one per column.</summary>
    public IReadOnlyList<MarkdownAlign> Aligns { get; internal set; } = [];

    // ── Parse state ─────────────────────────────────────────────────────────
    internal int RawStart, RawLength, RawSource = -1;
    internal int ContentIndent;          // list item
    internal bool HasContent;            // list item: a non-blank line has been seen
    internal byte Marker;                // list: '-', '+', '*', '.', ')'
    internal bool PendingBlank;          // list: a blank line since the last content
    internal byte FenceChar;
    internal int FenceLength, FenceIndent;
    internal bool Fenced;
}

/// <summary>
/// A parsed Markdown document: CommonMark's blocks and inlines, with GFM tables,
/// task items, strikethrough and bare-URL autolinks. Parsed once from UTF-8 and
/// immutable after; every piece of text is a slice of <see cref="Text"/>, so a
/// text node can hold it across frames without a copy.
/// </summary>
public sealed class MarkdownDocument
{
    private readonly byte[] _text;
    private readonly MarkdownRun[] _runs;

    internal MarkdownDocument(MarkdownBlock root, byte[] text, MarkdownRun[] runs, string[] links)
    {
        Root = root;
        _text = text;
        _runs = runs;
        Links = links;
    }

    /// <summary>The document: a block of kind <see cref="MarkdownBlockKind.Document"/>.</summary>
    public MarkdownBlock Root { get; }

    /// <summary>Every run's text and every code block's, in one buffer.</summary>
    public ReadOnlyMemory<byte> Text => _text;

    /// <summary>Every block's inline runs, in one array.</summary>
    public ReadOnlySpan<MarkdownRun> Runs => _runs;

    /// <summary>Link destinations, indexed by <see cref="MarkdownRun.Link"/>.</summary>
    public IReadOnlyList<string> Links { get; }

    /// <summary>The runs of a paragraph, heading or table cell.</summary>
    public ReadOnlySpan<MarkdownRun> RunsOf(MarkdownBlock block) => _runs.AsSpan(block.RunStart, block.RunCount);

    /// <summary>A run's text.</summary>
    public ReadOnlyMemory<byte> TextOf(in MarkdownRun run) => _text.AsMemory(run.Start, run.Length);

    /// <summary>A code block's text.</summary>
    public ReadOnlyMemory<byte> CodeOf(MarkdownBlock block) => _text.AsMemory(block.TextStart, block.TextLength);

    /// <summary>Parses UTF-8 Markdown.</summary>
    public static MarkdownDocument Parse(ReadOnlySpan<byte> utf8) => new MarkdownParser().Run(utf8);

    /// <summary>Parses Markdown from a string.</summary>
    public static MarkdownDocument Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return Parse(Encoding.UTF8.GetBytes(markdown));
    }

    /// <summary>The plain text of a block's runs, for tests and diagnostics.</summary>
    public string PlainText(MarkdownBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var sb = new StringBuilder();
        foreach (var run in RunsOf(block))
            sb.Append((run.Style & MarkdownStyle.LineBreak) != 0 ? "\n" : Encoding.UTF8.GetString(_text, run.Start, run.Length));
        return sb.ToString();
    }
}
