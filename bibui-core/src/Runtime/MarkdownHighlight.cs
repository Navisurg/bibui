using System.Numerics;

namespace Bibui.Core.Runtime;

/// <summary>
/// Markdown source as <see cref="StyledTextFieldExtensions.StyledTextField"/>
/// ranges: markers dimmed, code in the accent colour, strong text bold,
/// emphasis italic and the two together bold italic. Found left to right, a
/// line at a time, so sorted and none overlapping: at a line's start its quote
/// and list markers, a task box, a heading's <c>#</c>s or a fence; in the rest,
/// code spans, links and emphasis. A grammar of its own, as small as it can be,
/// since the parser keeps source offsets for blocks but not for inline runs.
///
/// <para>Where it can differ from how the viewer renders: emphasis is found
/// within a line, not across a paragraph's lines; strikethrough, autolinks,
/// HTML, setext underlines and thematic breaks are left plain; spans nested
/// deeper than it follows leave the rest plain.</para>
/// </summary>
public static class MarkdownHighlight
{
    private const int MaxDelimiter = 3;     // *a*, **a**, ***a***
    private const int MaxHeading = 6;
    private const int MinFence = 3;
    private const int MinBreak = 3;          // ---, ***, ___
    private const int MaxListNumber = 9;     // digits in an ordered list's number
    private const int TaskBox = 3;           // [ ], [x]
    private const int MaxNesting = 16;       // spans inside spans, past which the rest is plain

    /// <summary>
    /// Writes <paramref name="text"/>'s Markdown syntax into <paramref name="ranges"/> as
    /// StyledTextField ranges, sorted and not overlapping: markers dimmed, code in the
    /// accent colour, strong bold, emphasis italic. The array grows to the most ranges
    /// found and never shrinks. Answers how many.
    /// </summary>
    public static int RangesFromSource(ReadOnlySpan<char> text, ref TextStyleRange[] ranges)
    {
        var scan = new Scan(text, ref ranges);
        for (int start = 0; start < text.Length; )
        {
            int length = text[start..].IndexOf('\n');
            int end = length < 0 ? text.Length : start + length;
            scan.Line(start, end > start && text[end - 1] == '\r' ? end - 1 : end);
            start = end + 1;
        }
        return scan.Count;
    }

    [Flags]
    private enum Look : byte { None = 0, Strong = 1, Emphasis = 2 }

    private ref struct Scan
    {
        // Where a search for a closer found none, per closer, so a line of
        // openers without closers is searched once for each: code spans, then
        // runs of '*', then of '_', each 1 to MaxDelimiter long.
        private const int Closers = 3 * MaxDelimiter;

        private readonly ReadOnlySpan<char> _text;
        private readonly ref TextStyleRange[] _ranges;
        public int Count;

        // The fence a code block is open under: its char (0 for none), length,
        // and the quote markers before it.
        private char _fence;
        private int _fenceLength, _fenceQuotes;

        public Scan(ReadOnlySpan<char> text, ref TextStyleRange[] ranges)
        {
            _text = text;
            _ranges = ref ranges;
        }

        public void Line(int start, int end)
        {
            int at = start, quotes = 0;
            while (true)
            {
                at = Spaces(at, end);
                if (at == end) return;
                if (_text[at] == '>' && (_fence == 0 || quotes < _fenceQuotes))
                {
                    Dim(at++, 1);
                    quotes++;
                    continue;
                }
                if (_fence != 0)
                {
                    FenceLine(at, end);
                    return;
                }
                if (Break(at, end)) return;
                int marker = ListMarker(at, end);
                if (marker == 0) break;
                Dim(at, marker);
                at = Spaces(at + marker, end);
                if (Task(at, end))
                {
                    Dim(at, TaskBox);
                    at += TaskBox;
                }
            }

            int hashes = Run(at, end, '#');
            if (hashes > 0 && hashes <= MaxHeading && (at + hashes == end || IsSpace(_text[at + hashes])))
            {
                Dim(at, hashes);
                Inline(at + hashes, end, Look.None, 0);
                return;
            }
            char c = _text[at];
            int fence = c is '`' or '~' ? Run(at, end, c) : 0;
            if (fence >= MinFence && (c == '~' || _text[(at + fence)..end].IndexOf('`') < 0))
            {
                Dim(at, fence);
                (_fence, _fenceLength, _fenceQuotes) = (c, fence, quotes);
                return;
            }
            Inline(at, end, Look.None, 0);
        }

        // A line of an open code block: its text, or the fence that closes it.
        private void FenceLine(int at, int end)
        {
            int run = Run(at, end, _fence);
            if (run >= _fenceLength && Spaces(at + run, end) == end)
            {
                Dim(at, run);
                _fence = '\0';
            }
            else Add(at, end - at, UiColor.Accent, null);
        }

        // Code spans, links and emphasis in [from, to), set in `look` and
        // whatever they add to it, inside `nesting` others.
        private void Inline(int from, int to, Look look, int nesting)
        {
            if (nesting == MaxNesting)
            {
                Plain(from, to, look);
                return;
            }
            Span<int> none = stackalloc int[Closers];
            none.Fill(int.MaxValue);
            int plain = from;
            for (int at = from; at < to; )
            {
                char c = _text[at];
                if (c == '\\')
                {
                    at += 2;
                    continue;
                }
                if (c == '`')
                {
                    int ticks = Run(at, to, c);
                    int close = CodeCloser(at + ticks, to, ticks, none);
                    if (close >= 0)
                    {
                        Plain(plain, at, look);
                        Dim(at, ticks);
                        Add(at + ticks, close - at - ticks, UiColor.Accent, Weight(look));
                        Dim(close, ticks);
                        plain = close + ticks;
                    }
                    at += close >= 0 ? close + ticks - at : ticks;
                    continue;
                }
                if (c == '[' || c == '!' && at + 1 < to && _text[at + 1] == '[')
                {
                    int open = c == '!' ? 2 : 1;
                    if (Link(at + open, to, none, out int textEnd, out int linkEnd))
                    {
                        Plain(plain, at, look);
                        Dim(at, open);
                        Inline(at + open, textEnd, look, nesting + 1);
                        Dim(textEnd, linkEnd - textEnd);
                        at = plain = linkEnd;
                    }
                    else at += open;
                    continue;
                }
                if (c is '*' or '_')
                {
                    int n = Run(at, to, c);
                    int close = n <= MaxDelimiter && Opens(at, n, to) ? DelimiterCloser(at + n, to, c, n, none) : -1;
                    if (close >= 0)
                    {
                        Plain(plain, at, look);
                        Dim(at, n);
                        Inline(at + n, close, look | (n == 1 ? Look.Emphasis : n == 2 ? Look.Strong : Look.Strong | Look.Emphasis), nesting + 1);
                        Dim(close, n);
                        at = plain = close + n;
                    }
                    else at += n;
                    continue;
                }
                at++;
            }
            Plain(plain, to, look);
        }

        // The run of `ticks` backticks, no more and no fewer, that closes a code span opened before `from`, or -1.
        private readonly int CodeCloser(int from, int to, int ticks, Span<int> none)
        {
            int slot = ticks <= MaxDelimiter ? ticks - 1 : -1;
            if (slot >= 0 && from >= none[slot]) return -1;
            for (int at = from; at < to; )
            {
                if (_text[at] != '`')
                {
                    at++;
                    continue;
                }
                int run = Run(at, to, '`');
                if (run == ticks) return at;
                at += run;
            }
            if (slot >= 0) none[slot] = from;
            return -1;
        }

        // The run of `n` `c`s that closes emphasis opened before `from`, or -1:
        // code spans and escaped chars passed over.
        private readonly int DelimiterCloser(int from, int to, char c, int n, Span<int> none)
        {
            int slot = (c == '*' ? 1 : 2) * MaxDelimiter + n - 1;
            if (from >= none[slot]) return -1;
            for (int at = from; at < to; )
            {
                char d = _text[at];
                if (d == '\\') at += 2;
                else if (d == '`')
                {
                    int ticks = Run(at, to, d);
                    int close = CodeCloser(at + ticks, to, ticks, none);
                    at = close >= 0 ? close + ticks : at + ticks;
                }
                else if (d == c)
                {
                    int run = Run(at, to, c);
                    if (run == n && Closes(at, n, to)) return at;
                    at += run;
                }
                else at++;
            }
            none[slot] = from;
            return -1;
        }

        // A link's text from `from` to its `]`, and its `](destination)` to `linkEnd`.
        private readonly bool Link(int from, int to, Span<int> none, out int textEnd, out int linkEnd)
        {
            textEnd = linkEnd = -1;
            int depth = 0;
            for (int at = from; at < to; at++)
            {
                char c = _text[at];
                if (c == '\\') at++;
                else if (c == '`')
                {
                    int ticks = Run(at, to, c);
                    int close = CodeCloser(at + ticks, to, ticks, none);
                    at = (close >= 0 ? close + ticks : at + ticks) - 1;
                }
                else if (c == '[') depth++;
                else if (c == ']' && depth-- == 0)
                {
                    textEnd = at;
                    break;
                }
            }
            if (textEnd < 0 || textEnd + 1 >= to || _text[textEnd + 1] != '(') return false;
            depth = 0;
            for (int at = textEnd + 2; at < to; at++)
            {
                char c = _text[at];
                if (c == '\\') at++;
                else if (c == '(') depth++;
                else if (c == ')' && depth-- == 0)
                {
                    linkEnd = at + 1;
                    return true;
                }
            }
            return false;
        }

        // A run that can open emphasis: text after it, and for '_' none before it.
        private readonly bool Opens(int at, int n, int to) =>
            at + n < to && !char.IsWhiteSpace(_text[at + n])
            && (_text[at] != '_' || at == 0 || !char.IsLetterOrDigit(_text[at - 1]));

        // A run that can close it: text before it, and for '_' none after it.
        private readonly bool Closes(int at, int n, int to) =>
            at > 0 && !char.IsWhiteSpace(_text[at - 1])
            && (_text[at] != '_' || at + n == to || !char.IsLetterOrDigit(_text[at + n]));

        // A thematic break: MinBreak or more of one of - * _, and spaces, to the line's end.
        private readonly bool Break(int at, int end)
        {
            char c = _text[at];
            if (c is not ('-' or '*' or '_')) return false;
            int marks = 0;
            for (int i = at; i < end; i++)
            {
                if (_text[i] == c) marks++;
                else if (!IsSpace(_text[i])) return false;
            }
            return marks >= MinBreak;
        }

        // A list item's marker, `-`, `+`, `*`, `1.` or `1)`, followed by a space or the line's end: its length, or 0.
        private readonly int ListMarker(int at, int end)
        {
            int length = _text[at] is '-' or '+' or '*' ? 1 : 0;
            if (length == 0)
            {
                int digits = 0;
                while (at + digits < end && digits <= MaxListNumber && char.IsAsciiDigit(_text[at + digits])) digits++;
                if (digits == 0 || digits > MaxListNumber || at + digits == end || _text[at + digits] is not ('.' or ')')) return 0;
                length = digits + 1;
            }
            return at + length == end || IsSpace(_text[at + length]) ? length : 0;
        }

        private readonly bool Task(int at, int end) =>
            at + TaskBox <= end && _text[at] == '[' && _text[at + 1] is ' ' or 'x' or 'X' && _text[at + 2] == ']'
            && (at + TaskBox == end || IsSpace(_text[at + TaskBox]));

        private readonly int Run(int at, int end, char c)
        {
            int i = at;
            while (i < end && _text[i] == c) i++;
            return i - at;
        }

        private readonly int Spaces(int at, int end)
        {
            while (at < end && IsSpace(_text[at])) at++;
            return at;
        }

        private static bool IsSpace(char c) => c is ' ' or '\t';

        private static FontWeight? Weight(Look look) => look switch
        {
            Look.Strong => FontWeight.Bold,
            Look.Emphasis => FontWeight.Italic,
            Look.Strong | Look.Emphasis => FontWeight.BoldItalic,
            _ => null,
        };

        private void Plain(int from, int to, Look look)
        {
            if (Weight(look) is { } weight) Add(from, to - from, null, weight);
        }

        private void Dim(int at, int length) => Add(at, length, UiColor.TextSecondary, null);

        private void Add(int start, int length, UiColor? color, FontWeight? weight)
        {
            if (length <= 0) return;
            if (Count == _ranges.Length) Array.Resize(ref _ranges, (int)BitOperations.RoundUpToPowerOf2((uint)Count + 1));
            _ranges[Count++] = new TextStyleRange(start, length, color, weight);
        }
    }
}
