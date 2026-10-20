using System.Globalization;
using System.Text;

namespace Bibui.Core.Runtime;

/// <summary>
/// Parses Markdown into a <see cref="MarkdownDocument"/>, in CommonMark's two
/// phases: blocks line by line, then each block's inlines once every link
/// reference definition is known. Written from the CommonMark specification
/// (https://spec.commonmark.org) and its appendix, "A parsing strategy"; no
/// code is taken from a CommonMark implementation.
///
/// <para>Blocks follow CommonMark's open-container algorithm: each line first
/// continues the quotes and list items still open, then may open new ones, then
/// ends in a leaf — a paragraph (with lazy continuation), an ATX or setext
/// heading, a fenced or indented code block, a thematic break, or a GFM table.
/// Raw HTML is not recognised as a block; it stays text.</para>
///
/// <para>Inlines follow CommonMark's delimiter-stack algorithm for emphasis
/// (with GFM <c>~~</c>) and its bracket stack for links, so nesting, intraword
/// underscores and the rule of three come out as the spec says.</para>
///
/// <para>Simplified: tabs are expanded only in a line's leading whitespace, HTML
/// blocks and entities beyond a common handful are left as text, and link titles
/// are parsed and dropped.</para>
/// </summary>
internal sealed class MarkdownParser
{
    private char[] _src = [];
    private readonly List<int> _lineStart = [], _lineEnd = [], _origStart = [], _lineDelta = [];

    private readonly CharBuf _raw = new();     // inline sources: paragraphs, headings, cells
    private readonly CharBuf _out = new();     // the document's text
    private readonly List<MarkdownRun> _runs = [];
    private readonly List<string> _links = [];
    private readonly Dictionary<string, string> _refs = new(StringComparer.Ordinal);

    private readonly List<MarkdownBlock> _open = [];
    private MarkdownBlock? _leaf;
    private readonly List<(int Start, int End, int Line)> _leafLines = [];
    private MarkdownAlign[] _tableAligns = [];
    private int _line;

    public MarkdownDocument Run(ReadOnlySpan<char> text)
    {
        Prepare(text);
        var root = new MarkdownBlock(MarkdownBlockKind.Document);
        _open.Add(root);
        for (_line = 0; _line < _lineStart.Count; _line++) Line(_lineStart[_line], _lineEnd[_line]);
        CloseFrom(1);
        CloseLeaf();

        var inlines = new InlineParser(this);
        Inlines(root, inlines);
        return new MarkdownDocument(root, _out.ToArray(), [.. _runs], [.. _links]);
    }

    // ── Lines ───────────────────────────────────────────────────────────────

    // Copies the source into _src a line at a time, \r\n and \r made \n, tabs in
    // leading whitespace expanded to the next multiple of four.
    private void Prepare(ReadOnlySpan<char> text)
    {
        var buf = new CharBuf(text.Length + 16);
        int i = 0;
        while (i <= text.Length)
        {
            int start = i;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
            int end = i;
            if (start == text.Length && start > 0 && end == start) break;   // no line after a final newline

            int lineStart = buf.Length, col = 0, j = start;
            for (; j < end && (text[j] == ' ' || text[j] == '\t'); j++)
            {
                int width = text[j] == '\t' ? 4 - col % 4 : 1;
                for (int k = 0; k < width; k++) buf.Add(' ');
                col += width;
            }
            int delta = col - (j - start);
            buf.Add(text[j..end]);
            _lineStart.Add(lineStart);
            _lineEnd.Add(buf.Length);
            _origStart.Add(start);
            _lineDelta.Add(delta);
            buf.Add('\n');

            if (i >= text.Length) break;
            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            if (i == text.Length) break;
        }
        _src = buf.ToArray();
    }

    // The source offset of a position in _src on line `line`, past its leading whitespace.
    private int SourceOffset(int pos, int line) => pos - _lineStart[line] + _origStart[line] - _lineDelta[line];

    private void Line(int p, int e)
    {
        // 1. Continue the open containers.
        int matched = 1;
        for (int i = 1; i < _open.Count; i++)
        {
            var c = _open[i];
            if (c.Kind == MarkdownBlockKind.Quote)
            {
                int ind = Indent(p, e);
                if (ind > 3 || p + ind >= e || _src[p + ind] != '>') break;
                p += ind + 1;
                if (p < e && _src[p] == ' ') p++;
            }
            else if (c.Kind == MarkdownBlockKind.ListItem)
            {
                if (IsBlank(p, e)) { if (!c.HasContent) break; }
                else if (Indent(p, e) >= c.ContentIndent) p += c.ContentIndent;
                else break;
            }
            matched++;
        }
        bool blank = IsBlank(p, e);
        bool allMatched = matched == _open.Count;

        // 2. A code block takes the line as it is.
        if (allMatched && _leaf is { Kind: MarkdownBlockKind.CodeBlock, Fenced: true } fence)
        {
            int ind = Indent(p, e), q = p + ind, run = 0;
            while (q + run < e && _src[q + run] == fence.FenceChar) run++;
            if (ind <= 3 && run >= fence.FenceLength && IsBlank(q + run, e)) { CloseLeaf(); return; }
            int strip = Math.Min(ind, fence.FenceIndent);
            _leafLines.Add((p + strip, e, _line));
            return;
        }
        if (allMatched && _leaf is { Kind: MarkdownBlockKind.CodeBlock, Fenced: false })
        {
            if (blank) { _leafLines.Add((Math.Min(e, p + 4), e, _line)); return; }
            if (Indent(p, e) >= 4) { _leafLines.Add((p + 4, e, _line)); return; }
            CloseLeaf();
        }

        // 3. Lazy continuation: a paragraph goes on whatever containers the line left out.
        if (!allMatched && _leaf is { Kind: MarkdownBlockKind.Paragraph } && !blank && !StartsBlock(p, e))
        {
            _leafLines.Add((p, e, _line));
            return;
        }
        if (!allMatched) CloseFrom(matched);

        // 4. New containers.
        while (!blank)
        {
            int ind = Indent(p, e);
            if (ind >= 4) break;
            int q = p + ind;
            if (_src[q] == '>')
            {
                CloseLeaf();
                CloseTrailingList();
                var quote = new MarkdownBlock(MarkdownBlockKind.Quote);
                Tip.ChildList.Add(quote);
                _open.Add(quote);
                p = q + 1;
                if (p < e && _src[p] == ' ') p++;
                blank = IsBlank(p, e);
                continue;
            }
            if (IsThematicBreak(q, e)) break;
            if (!ListMarker(q, e, out char marker, out bool ordered, out int number, out int markerEnd)) break;

            int after = markerEnd, spaces = 0;
            while (after < e && _src[after] == ' ') { after++; spaces++; }
            bool emptyItem = after >= e;
            if (_leaf is { Kind: MarkdownBlockKind.Paragraph } && (emptyItem || ordered && number != 1)) break;

            int contentStart = emptyItem || spaces > 4 ? markerEnd + 1 : after;
            if (emptyItem) contentStart = Math.Min(contentStart, e);
            CloseLeaf();
            var list = Tip.Kind == MarkdownBlockKind.List ? Tip : null;
            if (list is not null && (list.Marker != marker || list.Ordered != ordered))
            {
                CloseFrom(_open.Count - 1);
                list = null;
            }
            if (list is null)
            {
                list = new MarkdownBlock(MarkdownBlockKind.List) { Marker = marker, Ordered = ordered, Level = number };
                Tip.ChildList.Add(list);
                _open.Add(list);
            }
            var item = new MarkdownBlock(MarkdownBlockKind.ListItem) { ContentIndent = contentStart - p };
            list.ChildList.Add(item);
            _open.Add(item);
            p = spaces > 4 && !emptyItem ? markerEnd + 1 : contentStart;
            blank = IsBlank(p, e);
            item.HasContent = !blank;
        }

        // A list whose item the line did not continue, and which no new item joined, ends.
        if (!blank)
        {
            CloseTrailingList();
            MarkLooseLists();
        }

        // 5. The leaf.
        if (blank)
        {
            if (_leaf is { Kind: MarkdownBlockKind.Paragraph or MarkdownBlockKind.Table }) CloseLeaf();
            for (int i = _open.Count - 1; i > 0; i--)
                if (_open[i].Kind == MarkdownBlockKind.List) { _open[i].PendingBlank = true; break; }
            return;
        }
        if (Tip.Kind == MarkdownBlockKind.ListItem) Tip.HasContent = true;

        int indent = Indent(p, e), at = p + indent;
        if (indent >= 4)
        {
            if (_leaf is { Kind: MarkdownBlockKind.Paragraph }) { _leafLines.Add((p, e, _line)); return; }
            CloseLeaf();
            OpenLeaf(new MarkdownBlock(MarkdownBlockKind.CodeBlock));
            _leafLines.Add((p + 4, e, _line));
            return;
        }
        if (_leaf is { Kind: MarkdownBlockKind.Paragraph } paragraph && SetextLevel(at, e) is var level and > 0)
        {
            paragraph.Kind = MarkdownBlockKind.Heading;
            paragraph.Level = level;
            CloseLeaf();
            return;
        }
        if (AtxHeading(at, e, out int hLevel, out int hStart, out int hEnd))
        {
            CloseLeaf();
            var heading = new MarkdownBlock(MarkdownBlockKind.Heading) { Level = hLevel };
            Tip.ChildList.Add(heading);
            SetRaw(heading, _src.AsSpan(hStart, hEnd - hStart), hStart < hEnd ? SourceOffset(hStart, _line) : -1);
            return;
        }
        if (Fence(at, e, out char fenceChar, out int fenceLength, out string? info))
        {
            CloseLeaf();
            OpenLeaf(new MarkdownBlock(MarkdownBlockKind.CodeBlock)
            {
                Fenced = true, FenceChar = fenceChar, FenceLength = fenceLength, FenceIndent = indent, Info = info,
            });
            return;
        }
        if (IsThematicBreak(at, e))
        {
            CloseLeaf();
            Tip.ChildList.Add(new MarkdownBlock(MarkdownBlockKind.ThematicBreak));
            return;
        }
        if (_leaf is { Kind: MarkdownBlockKind.Table }) { _leafLines.Add((at, e, _line)); return; }
        if (_leaf is { Kind: MarkdownBlockKind.Paragraph } && DelimiterRow(at, e) is { } aligns)
        {
            var header = _leafLines[^1];
            if (SplitCells(header.Start, header.End).Count == aligns.Length)
            {
                _leafLines.RemoveAt(_leafLines.Count - 1);
                if (_leafLines.Count > 0) CloseLeaf();
                else { Tip.ChildList.Remove(_leaf); _leaf = null; }
                OpenLeaf(new MarkdownBlock(MarkdownBlockKind.Table) { Aligns = aligns });
                _tableAligns = aligns;
                _leafLines.Add(header);
                return;
            }
        }
        if (_leaf is { Kind: MarkdownBlockKind.Paragraph }) { _leafLines.Add((at, e, _line)); return; }
        CloseLeaf();
        OpenLeaf(new MarkdownBlock(MarkdownBlockKind.Paragraph));
        _leafLines.Add((at, e, _line));
    }

    private MarkdownBlock Tip => _open[^1];

    private void OpenLeaf(MarkdownBlock leaf)
    {
        Tip.ChildList.Add(leaf);
        _leaf = leaf;
        _leafLines.Clear();
    }

    // A blank line inside a list followed by more of it makes the list loose.
    private void MarkLooseLists()
    {
        foreach (var block in _open)
            if (block.PendingBlank)
            {
                block.Tight = false;
                block.PendingBlank = false;
            }
    }

    private void CloseTrailingList()
    {
        if (Tip.Kind == MarkdownBlockKind.List) CloseFrom(_open.Count - 1);
    }

    // Closes the leaf and every open container from index `from` on. A list that
    // closes with a blank line pending hands it to the list around it: the blank
    // now separates that list's items, or two blocks of one of them.
    private void CloseFrom(int from)
    {
        CloseLeaf();
        for (int i = _open.Count - 1; i >= from; i--)
        {
            var block = _open[i];
            if (block.Kind == MarkdownBlockKind.List && block.PendingBlank)
                for (int j = from - 1; j > 0; j--)
                    if (_open[j].Kind == MarkdownBlockKind.List) { _open[j].PendingBlank = true; break; }
            _open.RemoveAt(i);
        }
    }

    private void CloseLeaf()
    {
        var leaf = _leaf;
        if (leaf is null) return;
        _leaf = null;
        switch (leaf.Kind)
        {
            case MarkdownBlockKind.Paragraph:
            case MarkdownBlockKind.Heading:
                CloseParagraph(leaf);
                break;
            case MarkdownBlockKind.CodeBlock:
                CloseCode(leaf);
                break;
            case MarkdownBlockKind.Table:
                CloseTable(leaf);
                break;
        }
        _leafLines.Clear();
    }

    // A paragraph's lines joined by '\n', each without its leading whitespace,
    // the last without its trailing. Link reference definitions at its start
    // are taken out; a paragraph that was nothing else is dropped.
    private void CloseParagraph(MarkdownBlock paragraph)
    {
        var joined = new CharBuf();
        int source = -1;
        for (int i = 0; i < _leafLines.Count; i++)
        {
            var (start, end, line) = _leafLines[i];
            while (start < end && _src[start] == ' ') start++;
            if (i == 0 && start < end) source = SourceOffset(start, line);
            if (i > 0) joined.Add('\n');
            joined.Add(_src.AsSpan(start, end - start));
        }
        var text = joined.Span;
        int trimmed = text.Length;
        while (trimmed > 0 && text[trimmed - 1] is ' ' or '\n') trimmed--;
        text = text[..trimmed];

        if (paragraph.Kind == MarkdownBlockKind.Paragraph)
        {
            int consumed = 0;
            while (consumed < text.Length && text[consumed] == '[' && RefDefinition(text[consumed..]) is var used and > 0)
            {
                consumed += used;
                source = -1;
            }
            text = text[consumed..];
            if (text.IsEmpty)
            {
                RemoveFromParent(paragraph);
                return;
            }
        }
        SetRaw(paragraph, text, source);
    }

    private void RemoveFromParent(MarkdownBlock block)
    {
        for (int i = _open.Count - 1; i >= 0; i--)
            if (_open[i].ChildList.Remove(block)) return;
    }

    private void SetRaw(MarkdownBlock block, ReadOnlySpan<char> text, int source)
    {
        block.RawStart = _raw.Length;
        block.RawLength = text.Length;
        block.RawSource = source;
        _raw.Add(text);
    }

    private void CloseCode(MarkdownBlock code)
    {
        int count = _leafLines.Count;
        if (!code.Fenced)
            while (count > 0 && IsBlank(_leafLines[count - 1].Start, _leafLines[count - 1].End)) count--;
        code.TextStart = _out.Length;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) _out.Add('\n');
            var (start, end, _) = _leafLines[i];
            _out.Add(_src.AsSpan(start, end - start));
        }
        code.TextLength = _out.Length - code.TextStart;
    }

    private void CloseTable(MarkdownBlock table)
    {
        int columns = _tableAligns.Length;
        foreach (var (start, end, _) in _leafLines)
        {
            var row = new MarkdownBlock(MarkdownBlockKind.TableRow);
            table.ChildList.Add(row);
            var cells = SplitCells(start, end);
            for (int c = 0; c < columns; c++)
            {
                var cell = new MarkdownBlock(MarkdownBlockKind.TableCell);
                row.ChildList.Add(cell);
                if (c >= cells.Count) { SetRaw(cell, default, -1); continue; }
                // \| is a pipe in a cell, inside a code span too.
                var raw = new CharBuf();
                var (cs, ce) = cells[c];
                for (int i = cs; i < ce; i++)
                {
                    if (_src[i] == '\\' && i + 1 < ce && _src[i + 1] == '|') continue;
                    raw.Add(_src[i]);
                }
                SetRaw(cell, raw.Span, -1);
            }
        }
    }

    // A row's cells, trimmed, split at unescaped pipes; a leading and a trailing pipe are optional.
    private List<(int Start, int End)> SplitCells(int start, int end)
    {
        var cells = new List<(int, int)>();
        while (start < end && _src[start] == ' ') start++;
        while (end > start && _src[end - 1] == ' ') end--;
        if (start < end && _src[start] == '|') start++;
        if (end > start && _src[end - 1] == '|' && (end - 2 < start || _src[end - 2] != '\\')) end--;
        int cellStart = start;
        for (int i = start; i <= end; i++)
        {
            if (i < end && _src[i] == '\\') { i++; continue; }
            if (i < end && _src[i] != '|') continue;
            int s = cellStart, e = Math.Min(i, end);
            while (s < e && _src[s] == ' ') s++;
            while (e > s && _src[e - 1] == ' ') e--;
            cells.Add((s, e));
            cellStart = i + 1;
        }
        return cells;
    }

    private MarkdownAlign[]? DelimiterRow(int start, int end)
    {
        bool anyPipe = false;
        for (int i = start; i < end; i++)
        {
            char b = _src[i];
            if (b == '|') anyPipe = true;
            else if (b is not ('-' or ':' or ' ')) return null;
        }
        var cells = SplitCells(start, end);
        if (cells.Count == 0 || !anyPipe && cells.Count < 2) return null;
        if (!anyPipe) return null;
        var aligns = new MarkdownAlign[cells.Count];
        for (int c = 0; c < cells.Count; c++)
        {
            var (s, e) = cells[c];
            if (e <= s) return null;
            bool left = _src[s] == ':', right = _src[e - 1] == ':';
            int dashes = 0;
            for (int i = s + (left ? 1 : 0); i < e - (right ? 1 : 0); i++)
            {
                if (_src[i] != '-') return null;
                dashes++;
            }
            if (dashes == 0) return null;
            aligns[c] = left && right ? MarkdownAlign.Center : right ? MarkdownAlign.Right : left ? MarkdownAlign.Left : MarkdownAlign.None;
        }
        return aligns;
    }

    // ── Line tests ──────────────────────────────────────────────────────────

    private int Indent(int p, int e)
    {
        int n = 0;
        while (p + n < e && _src[p + n] == ' ') n++;
        return n;
    }

    private bool IsBlank(int p, int e)
    {
        for (int i = p; i < e; i++)
            if (_src[i] != ' ') return false;
        return true;
    }

    // Whether a line whose containers did not all continue starts a block, so it
    // cannot be a paragraph's lazy continuation.
    private bool StartsBlock(int p, int e)
    {
        int ind = Indent(p, e);
        if (ind >= 4) return false;
        int q = p + ind;
        if (_src[q] == '>') return true;
        if (IsThematicBreak(q, e) || AtxHeading(q, e, out _, out _, out _) || Fence(q, e, out _, out _, out _)) return true;
        // Not a paragraph's own line, so a list item may start it whatever its number.
        return ListMarker(q, e, out _, out _, out _, out _);
    }

    private bool IsThematicBreak(int p, int e)
    {
        if (p >= e) return false;
        char c = _src[p];
        if (c is not ('*' or '-' or '_')) return false;
        int count = 0;
        for (int i = p; i < e; i++)
        {
            if (_src[i] == c) count++;
            else if (_src[i] != ' ') return false;
        }
        return count >= 3;
    }

    private bool ListMarker(int p, int e, out char marker, out bool ordered, out int number, out int markerEnd)
    {
        marker = '\0'; ordered = false; number = 0; markerEnd = p;
        if (p >= e) return false;
        char c = _src[p];
        if (c is '-' or '+' or '*')
        {
            if (p + 1 < e && _src[p + 1] != ' ') return false;
            marker = c;
            markerEnd = p + 1;
            return true;
        }
        int i = p;
        while (i < e && i - p < 9 && _src[i] is >= '0' and <= '9') number = number * 10 + (_src[i++] - '0');
        if (i == p || i >= e || _src[i] is not ('.' or ')')) return false;
        if (i + 1 < e && _src[i + 1] != ' ') return false;
        marker = _src[i];
        ordered = true;
        markerEnd = i + 1;
        return true;
    }

    private bool AtxHeading(int p, int e, out int level, out int start, out int end)
    {
        level = 0; start = end = p;
        while (p + level < e && _src[p + level] == '#') level++;
        if (level is 0 or > 6) return false;
        int q = p + level;
        if (q < e && _src[q] != ' ') return false;
        while (q < e && _src[q] == ' ') q++;
        int z = e;
        while (z > q && _src[z - 1] == ' ') z--;
        // A closing run of #s, after a space or alone.
        int hashes = z;
        while (hashes > q && _src[hashes - 1] == '#') hashes--;
        if (hashes < z && (hashes == q || _src[hashes - 1] == ' '))
        {
            z = hashes;
            while (z > q && _src[z - 1] == ' ') z--;
        }
        start = q;
        end = z;
        return true;
    }

    private bool Fence(int p, int e, out char fenceChar, out int length, out string? info)
    {
        fenceChar = '\0'; length = 0; info = null;
        if (p >= e || _src[p] is not ('`' or '~')) return false;
        char c = _src[p];
        while (p + length < e && _src[p + length] == c) length++;
        if (length < 3) return false;
        int s = p + length, z = e;
        while (s < z && _src[s] == ' ') s++;
        while (z > s && _src[z - 1] == ' ') z--;
        if (c == '`' && _src.AsSpan(s, z - s).Contains('`')) return false;
        fenceChar = c;
        info = z > s ? new string(_src, s, z - s) : null;
        return true;
    }

    private int SetextLevel(int p, int e)
    {
        if (p >= e || _src[p] is not ('=' or '-')) return 0;
        char c = _src[p];
        int i = p;
        while (i < e && _src[i] == c) i++;
        return IsBlank(i, e) ? c == '=' ? 1 : 2 : 0;
    }

    // `[label]: destination "title"` at the start of a paragraph: how many chars
    // it takes, through its line end, or 0 when it is not one.
    private int RefDefinition(ReadOnlySpan<char> text)
    {
        int i = 1;
        while (i < text.Length && text[i] != ']')
        {
            if (text[i] == '[') return 0;
            if (text[i] == '\\') i++;
            i++;
        }
        if (i >= text.Length || i > 1000 || i + 1 >= text.Length || text[i + 1] != ':') return 0;
        string label = NormalizeLabel(text[1..i]);
        if (label.Length == 0) return 0;
        i += 2;
        i = SkipSpaceAndOneNewline(text, i);
        if (!InlineParser.Destination(text, ref i, out string url)) return 0;
        int afterDest = i;
        int j = SkipSpaceAndOneNewline(text, i);
        bool titled = j > afterDest && InlineParser.Title(text, ref j);
        if (titled && LineEndsAt(text, j)) i = j;
        else if (!LineEndsAt(text, afterDest)) return 0;
        else i = afterDest;
        while (i < text.Length && text[i] == ' ') i++;
        if (i < text.Length) i++;   // the newline
        _refs.TryAdd(label, url);
        return i;
    }

    private static bool LineEndsAt(ReadOnlySpan<char> text, int i)
    {
        while (i < text.Length && text[i] == ' ') i++;
        return i >= text.Length || text[i] == '\n';
    }

    private static int SkipSpaceAndOneNewline(ReadOnlySpan<char> text, int i)
    {
        while (i < text.Length && text[i] == ' ') i++;
        if (i < text.Length && text[i] == '\n') i++;
        while (i < text.Length && text[i] == ' ') i++;
        return i;
    }

    internal static string NormalizeLabel(ReadOnlySpan<char> label)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (char ch in label.Trim())
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    // ── Inlines ─────────────────────────────────────────────────────────────

    // Every block with inline source, depth first. A list item whose first
    // paragraph opens with [ ] or [x] is a task item.
    private void Inlines(MarkdownBlock block, InlineParser parser)
    {
        if (block.Kind == MarkdownBlockKind.ListItem && block.ChildList.Count > 0
            && block.ChildList[0] is { Kind: MarkdownBlockKind.Paragraph } first)
        {
            var raw = _raw.Span.Slice(first.RawStart, first.RawLength);
            if (raw.Length >= 3 && raw[0] == '[' && raw[2] == ']' && raw[1] is ' ' or 'x' or 'X'
                && (raw.Length == 3 || raw[3] is ' ' or '\n'))
            {
                block.Task = raw[1] == ' ' ? MarkdownTask.Unchecked : MarkdownTask.Checked;
                block.TaskOffset = first.RawSource >= 0 ? first.RawSource + 1 : -1;
                int skip = Math.Min(raw.Length, 4);
                first.RawStart += skip;
                first.RawLength -= skip;
            }
        }
        if (block.RawLength > 0 || block.Kind is MarkdownBlockKind.Paragraph or MarkdownBlockKind.Heading or MarkdownBlockKind.TableCell)
        {
            block.RunStart = _runs.Count;
            parser.Parse(_raw.Span.Slice(block.RawStart, block.RawLength));
            block.RunCount = _runs.Count - block.RunStart;
        }
        foreach (var child in block.ChildList) Inlines(child, parser);
    }

    private sealed class InlineParser(MarkdownParser owner)
    {
        private enum Kind : byte { Text, Code, Delim, Bracket, LinkStart, LinkEnd, Break }

        private struct Tok
        {
            public Kind Kind;
            public int Start, Len;           // in _out
            public char Ch;
            public int Orig, Count, Closed, Opened;
            public bool CanOpen, CanClose;
            public int StrongOpen, EmOpen, StrikeOpen, StrongClose, EmClose, StrikeClose;
            public int Prev, Next;           // the delimiter list
            public bool Active, Image;       // a bracket
            public int RawPos, DelimBottom;  // a bracket: just past it, and the delimiter top when it opened
            public int Link;
        }

        private readonly List<Tok> _toks = [];
        private readonly List<int> _brackets = [];
        private int _delimTop = -1;
        private CharBuf Out => owner._out;

        public void Parse(ReadOnlySpan<char> raw)
        {
            _toks.Clear();
            _brackets.Clear();
            _delimTop = -1;
            Scan(raw);
            ProcessEmphasis(-1);
            Flatten();
        }

        private void Scan(ReadOnlySpan<char> raw)
        {
            int i = 0;
            while (i < raw.Length)
            {
                char b = raw[i];
                switch (b)
                {
                    case '\n':
                        Newline(raw, ref i);
                        break;
                    case '\\':
                        if (i + 1 < raw.Length && raw[i + 1] == '\n') { TrimTrailingSpaces(); Add(Kind.Break); i += 2; SkipSpaces(raw, ref i); }
                        else if (i + 1 < raw.Length && IsAsciiPunct(raw[i + 1])) { AppendText(raw.Slice(i + 1, 1)); i += 2; }
                        else { AppendText(raw.Slice(i, 1)); i++; }
                        break;
                    case '`':
                        CodeSpan(raw, ref i);
                        break;
                    case '*' or '_' or '~':
                        Delimiter(raw, ref i);
                        break;
                    case '[':
                        OpenBracket(i + 1, image: false, "[");
                        i++;
                        break;
                    case '!' when i + 1 < raw.Length && raw[i + 1] == '[':
                        OpenBracket(i + 2, image: true, "![");
                        i += 2;
                        break;
                    case ']':
                        CloseBracket(raw, ref i);
                        break;
                    case '<':
                        if (!Autolink(raw, ref i)) { AppendText("<"); i++; }
                        break;
                    case '&':
                        if (!Entity(raw, ref i)) { AppendText("&"); i++; }
                        break;
                    default:
                        if ((b is 'h' or 'w' or 'H' or 'W') && BareUrl(raw, ref i)) break;
                        int start = i;
                        while (i < raw.Length && !IsSpecial(raw[i])) i++;
                        if (i == start) i++;
                        AppendText(raw[start..i]);
                        break;
                }
            }
        }

        private static bool IsSpecial(char b) => b is '\n' or '\\' or '`' or '*' or '_'
            or '~' or '[' or '!' or ']' or '<' or '&'
            or 'h' or 'w' or 'H' or 'W';

        // Two spaces before a line end break the line; otherwise it is a space.
        private void Newline(ReadOnlySpan<char> raw, ref int i)
        {
            int spaces = 0;
            if (_toks.Count > 0 && _toks[^1] is { Kind: Kind.Text } last && last.Start + last.Len == Out.Length)
                for (int k = Out.Length - 1; k >= last.Start && Out[k] == ' '; k--) spaces++;
            TrimTrailingSpaces();
            if (spaces >= 2) Add(Kind.Break);
            else AppendText(" ");
            i++;
            SkipSpaces(raw, ref i);
        }

        private static void SkipSpaces(ReadOnlySpan<char> raw, ref int i)
        {
            while (i < raw.Length && raw[i] == ' ') i++;
        }

        private void TrimTrailingSpaces()
        {
            if (_toks.Count == 0 || _toks[^1] is not { Kind: Kind.Text } last || last.Start + last.Len != Out.Length) return;
            while (last.Len > 0 && Out[last.Start + last.Len - 1] == ' ') { last.Len--; Out.Length--; }
            _toks[^1] = last;
        }

        private void CodeSpan(ReadOnlySpan<char> raw, ref int i)
        {
            int n = 0;
            while (i + n < raw.Length && raw[i + n] == '`') n++;
            int j = i + n;
            while (j < raw.Length)
            {
                if (raw[j] != '`') { j++; continue; }
                int m = 0;
                while (j + m < raw.Length && raw[j + m] == '`') m++;
                if (m == n) break;
                j += m;
            }
            if (j >= raw.Length) { AppendText(raw.Slice(i, n)); i += n; return; }

            var content = raw[(i + n)..j];
            int start = Out.Length;
            foreach (char c in content) Out.Add(c == '\n' ? ' ' : c);
            int len = Out.Length - start;
            bool allSpace = true;
            for (int k = start; k < start + len; k++) if (Out[k] != ' ') { allSpace = false; break; }
            if (len >= 2 && !allSpace && Out[start] == ' ' && Out[start + len - 1] == ' ')
            {
                start++;
                len -= 2;
            }
            _toks.Add(new Tok { Kind = Kind.Code, Start = start, Len = len, Prev = -1, Next = -1 });
            i = j + n;
        }

        private void Delimiter(ReadOnlySpan<char> raw, ref int i)
        {
            char c = raw[i];
            int n = 0;
            while (i + n < raw.Length && raw[i + n] == c) n++;
            int before = i > 0 ? PrevRune(raw, i) : ' ';
            int after = i + n < raw.Length ? NextRune(raw, i + n) : ' ';
            bool beforeWs = IsWhitespace(before), afterWs = IsWhitespace(after);
            bool beforeP = IsPunct(before), afterP = IsPunct(after);
            bool left = !afterWs && (!afterP || beforeWs || beforeP);
            bool right = !beforeWs && (!beforeP || afterWs || afterP);
            bool canOpen, canClose;
            if (c == '_') { canOpen = left && (!right || beforeP); canClose = right && (!left || afterP); }
            else { canOpen = left; canClose = right; }
            if (c == '~' && n > 2) { canOpen = canClose = false; }

            int start = Out.Length;
            Out.Add(raw.Slice(i, n));
            i += n;
            int index = _toks.Count;
            _toks.Add(new Tok
            {
                Kind = Kind.Delim, Start = start, Len = n, Ch = c, Orig = n, Count = n,
                CanOpen = canOpen, CanClose = canClose, Prev = _delimTop, Next = -1,
            });
            if (!canOpen && !canClose) return;   // literal; never on the stack
            if (_delimTop >= 0) SetNext(_delimTop, index);
            _delimTop = index;
        }

        private void OpenBracket(int rawPos, bool image, ReadOnlySpan<char> literal)
        {
            int start = Out.Length;
            Out.Add(literal);
            _brackets.Add(_toks.Count);
            _toks.Add(new Tok
            {
                Kind = Kind.Bracket, Start = start, Len = literal.Length, Active = true, Image = image,
                RawPos = rawPos, DelimBottom = _delimTop, Prev = -1, Next = -1, Link = -1,
            });
        }

        private void CloseBracket(ReadOnlySpan<char> raw, ref int i)
        {
            if (_brackets.Count == 0) { AppendText("]"); i++; return; }
            int openerIndex = _brackets[^1];
            var opener = _toks[openerIndex];
            if (!opener.Active)
            {
                _brackets.RemoveAt(_brackets.Count - 1);
                AppendText("]");
                i++;
                return;
            }

            string? url = null;
            int end = i + 1;
            if (end < raw.Length && raw[end] == '(')
            {
                int j = end + 1;
                SkipWhitespace(raw, ref j);
                if (j < raw.Length && raw[j] == ')') { url = ""; end = j + 1; }
                else if (Destination(raw, ref j, out string dest))
                {
                    int k = j;
                    SkipWhitespace(raw, ref k);
                    if (k > j && Title(raw, ref k)) SkipWhitespace(raw, ref k);
                    else k = j;
                    SkipWhitespace(raw, ref k);
                    if (k < raw.Length && raw[k] == ')') { url = dest; end = k + 1; }
                }
            }
            if (url is null)
            {
                // Reference: [text][label], [text][], or [text].
                string? label = null;
                int after = i + 1;
                if (after < raw.Length && raw[after] == '[')
                {
                    int close = raw[after..].IndexOf(']');
                    if (close > 0)
                    {
                        label = close == 1 ? NormalizeLabel(raw[opener.RawPos..i]) : NormalizeLabel(raw.Slice(after + 1, close - 1));
                        end = after + close + 1;
                    }
                }
                if (label is null) { label = NormalizeLabel(raw[opener.RawPos..i]); end = i + 1; }
                if (label.Length > 0 && owner._refs.TryGetValue(label, out var found)) url = found;
            }

            _brackets.RemoveAt(_brackets.Count - 1);
            if (url is null) { AppendText("]"); i++; return; }

            ProcessEmphasis(opener.DelimBottom);
            opener.Kind = Kind.LinkStart;
            opener.Len = 0;
            opener.Link = owner._links.Count;
            owner._links.Add(url);
            _toks[openerIndex] = opener;
            _toks.Add(new Tok { Kind = Kind.LinkEnd, Start = Out.Length, Prev = -1, Next = -1 });
            if (!opener.Image)
                foreach (int b in _brackets)
                    if (!_toks[b].Image) { var t = _toks[b]; t.Active = false; _toks[b] = t; }
            i = end;
        }

        // <scheme:...> or <user@host>.
        private bool Autolink(ReadOnlySpan<char> raw, ref int i)
        {
            int close = raw[(i + 1)..].IndexOf('>');
            if (close <= 0) return false;
            var inner = raw.Slice(i + 1, close);
            if (inner.IndexOfAny(' ', '<', '\n') >= 0) return false;
            int colon = inner.IndexOf(':');
            string url;
            if (colon >= 2 && colon <= 32 && IsAsciiLetter(inner[0]) && IsScheme(inner[..colon]))
                url = new string(inner);
            else if (inner.IndexOf('@') > 0 && inner.IndexOf('@') < inner.Length - 1 && inner.IndexOf('\\') < 0)
                url = string.Concat("mailto:", inner);
            else return false;
            AddLink(url, inner);
            i += close + 2;
            return true;
        }

        private static bool IsScheme(ReadOnlySpan<char> s)
        {
            foreach (char b in s)
                if (!(IsAsciiLetter(b) || b is >= '0' and <= '9' || b is '+' or '.' or '-')) return false;
            return true;
        }

        // GFM's extended autolinks: http://, https:// and www. at the start of a word.
        private bool BareUrl(ReadOnlySpan<char> raw, ref int i)
        {
            if (i > 0 && raw[i - 1] is not (' ' or '\n' or '*' or '_' or '~' or '(')) return false;
            var rest = raw[i..];
            int prefix = StartsWithIgnoreCase(rest, "https://") ? 8 : StartsWithIgnoreCase(rest, "http://") ? 7
                : StartsWithIgnoreCase(rest, "www.") ? 4 : 0;
            if (prefix == 0) return false;
            int end = prefix;
            while (end < rest.Length && rest[end] is not (' ' or '\n' or '<')) end++;
            while (end > prefix)
            {
                char last = rest[end - 1];
                if (last is '?' or '!' or '.' or ',' or ':' or '*' or '_'
                    or '~' or '\'' or '"') { end--; continue; }
                if (last == ')' && Count(rest[..end], ')') > Count(rest[..end], '(')) { end--; continue; }
                break;
            }
            if (end <= prefix) return false;
            var text = rest[..end];
            string url = prefix == 4 ? string.Concat("http://", text) : new string(text);
            AddLink(url, text);
            i += end;
            return true;
        }

        private static int Count(ReadOnlySpan<char> s, char b)
        {
            int n = 0;
            foreach (char c in s) if (c == b) n++;
            return n;
        }

        private static bool StartsWithIgnoreCase(ReadOnlySpan<char> s, ReadOnlySpan<char> prefix)
        {
            if (s.Length < prefix.Length) return false;
            for (int k = 0; k < prefix.Length; k++)
                if ((s[k] | 0x20) != (prefix[k] | 0x20) && s[k] != prefix[k]) return false;
            return true;
        }

        private void AddLink(string url, ReadOnlySpan<char> text)
        {
            int link = owner._links.Count;
            owner._links.Add(url);
            _toks.Add(new Tok { Kind = Kind.LinkStart, Start = Out.Length, Link = link, Prev = -1, Next = -1 });
            AppendText(text);
            _toks.Add(new Tok { Kind = Kind.LinkEnd, Start = Out.Length, Prev = -1, Next = -1 });
        }

        private static readonly (string Name, string Value)[] Entities =
        [
            ("amp", "&"), ("lt", "<"), ("gt", ">"), ("quot", "\""), ("apos", "'"), ("nbsp", " "),
            ("copy", "©"), ("reg", "®"), ("trade", "™"), ("hellip", "…"), ("mdash", "—"), ("ndash", "–"),
            ("larr", "←"), ("rarr", "→"), ("times", "×"), ("middot", "·"), ("deg", "°"),
        ];

        private bool Entity(ReadOnlySpan<char> raw, ref int i)
        {
            int semi = raw[(i + 1)..].IndexOf(';');
            if (semi <= 0 || semi > 32) return false;
            var name = raw.Slice(i + 1, semi);
            Span<char> chars = stackalloc char[2];
            if (name[0] == '#')
            {
                bool hex = name.Length > 1 && name[1] is 'x' or 'X';
                var digits = name[(hex ? 2 : 1)..];
                if (digits.IsEmpty || digits.Length > (hex ? 6 : 7)) return false;
                int value = 0;
                foreach (char d in digits)
                {
                    int v = d is >= '0' and <= '9' ? d - '0'
                        : hex && (d | 0x20) is >= 'a' and <= 'f' ? (d | 0x20) - 'a' + 10 : -1;
                    if (v < 0) return false;
                    value = value * (hex ? 16 : 10) + v;
                }
                var rune = Rune.IsValid(value) && value != 0 ? new Rune(value) : Rune.ReplacementChar;
                AppendText(chars[..rune.EncodeToUtf16(chars)]);
            }
            else
            {
                string? found = null;
                foreach (var (n, v) in Entities)
                    if (name.SequenceEqual(n)) { found = v; break; }
                if (found is null) return false;
                AppendText(found);
            }
            i += semi + 2;
            return true;
        }

        private void AppendText(ReadOnlySpan<char> text)
        {
            if (text.IsEmpty) return;
            if (_toks.Count > 0 && _toks[^1] is { Kind: Kind.Text } last && last.Start + last.Len == Out.Length)
            {
                Out.Add(text);
                last.Len += text.Length;
                _toks[^1] = last;
                return;
            }
            int start = Out.Length;
            Out.Add(text);
            _toks.Add(new Tok { Kind = Kind.Text, Start = start, Len = text.Length, Prev = -1, Next = -1 });
        }

        private void Add(Kind kind) => _toks.Add(new Tok { Kind = kind, Start = Out.Length, Prev = -1, Next = -1 });

        private void SetNext(int index, int next) { var t = _toks[index]; t.Next = next; _toks[index] = t; }
        private void SetPrev(int index, int prev) { var t = _toks[index]; t.Prev = prev; _toks[index] = t; }

        private void Remove(int index)
        {
            var t = _toks[index];
            if (t.Prev >= 0) SetNext(t.Prev, t.Next);
            if (t.Next >= 0) SetPrev(t.Next, t.Prev);
            if (_delimTop == index) _delimTop = t.Prev;
            t.Prev = t.Next = -1;
            _toks[index] = t;
        }

        // The spec appendix's "process emphasis" (with its openers_bottom), over
        // the delimiters above `bottom`.
        private void ProcessEmphasis(int bottom)
        {
            // The first delimiter above bottom.
            int current = _delimTop;
            if (current < 0 || current == bottom) return;
            while (_toks[current].Prev != bottom && _toks[current].Prev >= 0) current = _toks[current].Prev;

            Span<int> openersBottom = stackalloc int[3 * 3 * 2];
            openersBottom.Fill(bottom);

            while (current >= 0)
            {
                var closer = _toks[current];
                if (!closer.CanClose) { current = closer.Next; continue; }
                int slot = (closer.Ch == '*' ? 0 : closer.Ch == '_' ? 1 : 2) * 6 + closer.Orig % 3 * 2 + (closer.CanOpen ? 1 : 0);
                int opener = closer.Prev;
                bool found = false;
                while (opener >= 0 && opener != bottom && opener != openersBottom[slot])
                {
                    var o = _toks[opener];
                    if (o.CanOpen && o.Ch == closer.Ch)
                    {
                        if (closer.Ch == '~') found = o.Count == closer.Count;
                        else
                        {
                            bool odd = (o.CanClose || closer.CanOpen) && (o.Orig + closer.Orig) % 3 == 0
                                       && !(o.Orig % 3 == 0 && closer.Orig % 3 == 0);
                            found = !odd;
                        }
                        if (found) break;
                    }
                    opener = o.Prev;
                }

                if (!found)
                {
                    openersBottom[slot] = closer.Prev;
                    int next = closer.Next;
                    if (!closer.CanOpen) Remove(current);
                    current = next;
                    continue;
                }

                var op = _toks[opener];
                int use = closer.Ch == '~' ? closer.Count : op.Count >= 2 && closer.Count >= 2 ? 2 : 1;
                op.Count -= use; op.Opened += use;
                closer.Count -= use; closer.Closed += use;
                if (closer.Ch == '~') { op.StrikeOpen++; closer.StrikeClose++; }
                else if (use == 2) { op.StrongOpen++; closer.StrongClose++; }
                else { op.EmOpen++; closer.EmClose++; }
                _toks[opener] = op;
                _toks[current] = closer;

                // Delimiters between them are literal now.
                for (int between = op.Next; between >= 0 && between != current;)
                {
                    int next = _toks[between].Next;
                    Remove(between);
                    between = next;
                }
                if (op.Count == 0) Remove(opener);
                if (_toks[current].Count == 0)
                {
                    int next = _toks[current].Next;
                    Remove(current);
                    current = next;
                }
            }

            // What is left above bottom is literal.
            while (_delimTop >= 0 && _delimTop != bottom) Remove(_delimTop);
        }

        // The tokens as runs, styled by the emphasis open around each.
        private void Flatten()
        {
            int strong = 0, em = 0, strike = 0, link = -1;
            bool image = false;
            int first = owner._runs.Count;   // runs before it are another block's
            foreach (var t in _toks)
            {
                switch (t.Kind)
                {
                    case Kind.Text:
                    case Kind.Bracket:
                        Emit(t.Start, t.Len, Style(MarkdownStyle.None));
                        break;
                    case Kind.Code:
                        Emit(t.Start, t.Len, Style(MarkdownStyle.Code));
                        break;
                    case Kind.Break:
                        owner._runs.Add(new MarkdownRun(t.Start, 0, MarkdownStyle.LineBreak, -1));
                        break;
                    case Kind.LinkStart:
                        link = t.Link;
                        image = t.Image;
                        break;
                    case Kind.LinkEnd:
                        link = -1;
                        image = false;
                        break;
                    case Kind.Delim:
                        strong -= t.StrongClose; em -= t.EmClose; strike -= t.StrikeClose;
                        Emit(t.Start + t.Closed, t.Orig - t.Closed - t.Opened, Style(MarkdownStyle.None));
                        strong += t.StrongOpen; em += t.EmOpen; strike += t.StrikeOpen;
                        break;
                }
            }

            MarkdownStyle Style(MarkdownStyle style)
            {
                if (strong > 0) style |= MarkdownStyle.Strong;
                if (em > 0) style |= MarkdownStyle.Emphasis;
                if (strike > 0) style |= MarkdownStyle.Strike;
                if (link >= 0) style |= image ? MarkdownStyle.Link | MarkdownStyle.Image : MarkdownStyle.Link;
                return style;
            }

            void Emit(int start, int length, MarkdownStyle style)
            {
                if (length <= 0) return;
                int linkIndex = (style & MarkdownStyle.Link) != 0 ? link : -1;
                var runs = owner._runs;
                if (runs.Count > first && runs[^1] is var last && last.Style == style && last.Link == linkIndex
                    && last.Start + last.Length == start && (style & MarkdownStyle.LineBreak) == 0)
                {
                    runs[^1] = last with { Length = last.Length + length };
                    return;
                }
                runs.Add(new MarkdownRun(start, length, style, linkIndex));
            }
        }

        // ── Shared with link reference definitions ───────────────────────────

        private static void SkipWhitespace(ReadOnlySpan<char> raw, ref int i)
        {
            while (i < raw.Length && raw[i] is ' ' or '\n') i++;
        }

        // A link destination: <...>, or a run without spaces whose parentheses balance.
        internal static bool Destination(ReadOnlySpan<char> raw, ref int i, out string url)
        {
            url = "";
            var sb = new CharBuf();
            if (i < raw.Length && raw[i] == '<')
            {
                int j = i + 1;
                while (j < raw.Length && raw[j] is not ('>' or '\n' or '<'))
                {
                    if (raw[j] == '\\' && j + 1 < raw.Length && IsAsciiPunct(raw[j + 1])) j++;
                    sb.Add(raw[j++]);
                }
                if (j >= raw.Length || raw[j] != '>') return false;
                i = j + 1;
                url = new string(sb.Span);
                return true;
            }
            int depth = 0, k = i;
            while (k < raw.Length)
            {
                char b = raw[k];
                if (b <= ' ') break;
                if (b == '\\' && k + 1 < raw.Length && IsAsciiPunct(raw[k + 1])) { sb.Add(raw[k + 1]); k += 2; continue; }
                if (b == '(') depth++;
                else if (b == ')') { if (depth == 0) break; depth--; }
                sb.Add(b);
                k++;
            }
            if (k == i || depth != 0) return false;
            i = k;
            url = new string(sb.Span);
            return true;
        }

        // A link title in "", '' or (): parsed past, and dropped.
        internal static bool Title(ReadOnlySpan<char> raw, ref int i)
        {
            if (i >= raw.Length) return false;
            char open = raw[i];
            char close = open switch { '"' => '"', '\'' => '\'', '(' => ')', _ => '\0' };
            if (close == '\0') return false;
            for (int j = i + 1; j < raw.Length; j++)
            {
                if (raw[j] == '\\') { j++; continue; }
                if (raw[j] == close) { i = j + 1; return true; }
            }
            return false;
        }

        private static int PrevRune(ReadOnlySpan<char> raw, int i)
            => Rune.DecodeLastFromUtf16(raw[..i], out var rune, out _) == System.Buffers.OperationStatus.Done ? rune.Value : ' ';

        private static int NextRune(ReadOnlySpan<char> raw, int i)
            => Rune.DecodeFromUtf16(raw[i..], out var rune, out _) == System.Buffers.OperationStatus.Done ? rune.Value : ' ';

        private static bool IsWhitespace(int c) => c is ' ' or '\n' or '\t' or '\r' or '\f' || c > 127 && Rune.IsWhiteSpace(new Rune(c));

        private static bool IsPunct(int c)
        {
            if (c < 128) return IsAsciiPunct((char)c);
            var category = Rune.GetUnicodeCategory(new Rune(c));
            return category is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.OtherSymbol;
        }

        private static bool IsAsciiLetter(char b) => (b | 0x20) is >= 'a' and <= 'z';
    }

    private static bool IsAsciiPunct(char c) => c is >= '!' and <= '/' or >= ':' and <= '@' or >= '[' and <= '`' or >= '{' and <= '~';

    // A growable char buffer: List<char> without the per-char call.
    private sealed class CharBuf(int capacity = 256)
    {
        private char[] _chars = new char[Math.Max(16, capacity)];

        public int Length { get; set; }

        public char this[int index] => _chars[index];

        public ReadOnlySpan<char> Span => _chars.AsSpan(0, Length);

        public void Add(char b)
        {
            if (Length == _chars.Length) Array.Resize(ref _chars, _chars.Length * 2);
            _chars[Length++] = b;
        }

        public void Add(ReadOnlySpan<char> chars)
        {
            if (Length + chars.Length > _chars.Length) Array.Resize(ref _chars, Math.Max(_chars.Length * 2, Length + chars.Length));
            chars.CopyTo(_chars.AsSpan(Length));
            Length += chars.Length;
        }

        public char[] ToArray() => _chars.AsSpan(0, Length).ToArray();
    }
}
