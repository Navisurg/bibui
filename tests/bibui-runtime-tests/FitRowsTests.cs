using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;
using Bibui.Core.TextEdit;
using Bibui.Testing;
using Bibui.Text;

/// <summary>
/// A multi-line field given fitRows: a row tall for each line it draws, from
/// the least rows to the most, then scrolling with the caret kept in sight.
/// </summary>
static class FitRowsTests
{
    public static void Register(Action<string, Action> run)
    {
        run("fitRows: as tall as its lines from the least rows to the most, in the frame a line is typed", () =>
        {
            using var lab = new Lab("", width: 300f, fit: (2, 4));
            Equal(lab.RowsTall(2), lab.View.Extent.Y, "empty, the least: two rows");
            lab.Page.TypeChar('a');
            lab.Page.Press(Key.Enter);
            Equal(lab.RowsTall(2), lab.View.Extent.Y, "two lines, two rows");
            lab.Page.Press(Key.Enter);
            Equal(lab.RowsTall(3), lab.View.Extent.Y, "a third line: three rows in the frame it is typed");
            lab.Page.Press(Key.Enter);
            Equal(lab.RowsTall(4), lab.View.Extent.Y, "four");
            Equal(0f, lab.View.ViewOffset.Y, "and, every line in sight, nothing scrolled on the way");
            lab.Page.Press(Key.Enter);
            lab.Page.Press(Key.Enter);
            Equal(lab.RowsTall(4), lab.View.Extent.Y, "six lines, the most: four rows");
            lab.Page.Press(Key.Backspace);
            lab.Page.Press(Key.Backspace);
            lab.Page.Press(Key.Backspace);
            Equal(lab.RowsTall(3), lab.View.Extent.Y, "back to three lines, three rows in the frame");
        });

        run("fitRows: the rows are the lines it draws, wrapped ones and the placeholder's counted", () =>
        {
            const string words = "a sentence long enough that a field three hundred pixels wide has to wrap it onto more than one line";
            using var lab = new Lab(words, width: 300f, fit: (1, 10));
            int lines = lab.Lines(words);
            IsTrue(lines > 1, $"the sentence wraps at 300 px: {lines} lines");
            Equal(lab.RowsTall(lines), lab.View.Extent.Y, "a row for each line it wraps into");

            using var empty = new Lab("", width: 300f, fit: (1, 10), placeholder: words);
            Equal(empty.RowsTall(empty.Lines(words)), empty.View.Extent.Y, "empty, a row for each of the placeholder's lines");
        });

        run("fitRows: past the most rows it scrolls, the caret's line kept in sight", () =>
        {
            using var lab = new Lab("", width: 300f, fit: (1, 3));
            for (int i = 0; i < 6; i++) { lab.Page.TypeChar('x'); lab.Page.Press(Key.Enter); }
            lab.Page.TypeChar('y');
            Equal(lab.RowsTall(3), lab.View.Extent.Y, "seven lines in three rows");
            float top = 6 * lab.Advance, bottom = top + lab.LineHeight;   // the caret's line, the seventh
            float scrolled = lab.View.TargetViewOffset.Y;
            IsTrue(scrolled > 0f && top >= scrolled && bottom - scrolled <= lab.View.Extent.Y + 0.01f,
                $"the caret's line {top}..{bottom} in view at {scrolled}, {lab.View.Extent.Y} tall");

            for (int i = 0; i < 6; i++) lab.Page.Press(Key.Up);
            Equal(0f, lab.View.TargetViewOffset.Y, "up on the first line, the view at the top");
        });

        run("fitRows: a styled field counts the lines its bold text wraps into", () =>
        {
            const string words = "wide wide wide wide wide wide";
            var probe = BibuiRuntime.Create();
            var font = probe.FontInScope();
            float width = TextLayer.WidthFromChars(probe.Fonts, words, TextLayer.StyleFromFont(probe.Fonts, font, font.Scale), font.Scale) + 2f;
            using var plain = new Lab(words, width, fit: (1, 5), styles: []);
            Equal(plain.RowsTall(1), plain.View.Extent.Y, "regular, one line, one row");
            using var bold = new Lab(words, width, fit: (1, 5), styles: [new TextStyleRange(0, words.Length, Weight: FontWeight.Bold)]);
            Equal(bold.RowsTall(2), bold.View.Extent.Y, "bold, two lines, two rows");
        });
    }

    /// <summary>
    /// One focused field, multi-line (styled when given styles), fitting its
    /// rows, in a column of the given width; laid out twice, so it wraps at
    /// its width. A Rows(1) is pushed, which fitRows overrides.
    /// </summary>
    private sealed class Lab : IDisposable
    {
        public readonly BibuiRuntime Runtime = BibuiRuntime.Create();
        public readonly BibuiPage Page;
        public Box View = null!;
        private readonly float _width;

        public Lab(string initial, float width, (int Min, int Max) fit, string placeholder = "", TextStyleRange[]? styles = null)
        {
            _width = width;
            var text = BibuiStr.FromStrLit(initial);
            var state = new StyledTextState();
            state.Edit.Cursor = state.Edit.Mark = initial.Length;
            Page = BibuiPage.From(Runtime, () =>
            {
                using (Runtime.Parent(Runtime.Column()))
                using (Runtime.Parent(Runtime.Column().Sized(Size.Px(width))))
                using (Runtime.SizeY(TextField.Rows(Runtime, 1)))
                {
                    Runtime.Focus("field");
                    View = (Box)(styles is not null
                        ? Runtime.StyledTextField("field", text, state, styles, placeholder: placeholder, fitRows: fit)
                        : Runtime.MultilineTextField("field", text, state.Edit, placeholder: placeholder, fitRows: fit)).Node;
                }
            }, window: new WindowFrame(500, 400, 1f, 60f));
            Page.Frame();
            Page.Frame();
        }

        public float LineHeight => Runtime.Fonts.LineHeight(Runtime.FontInScope());
        public float Advance => Runtime.Fonts.LineAdvance(Runtime.FontInScope());

        public float RowsTall(int rows) => TextField.Rows(Runtime, rows).Value;

        // The lines a plain text breaks into at the field's width.
        public int Lines(string words)
        {
            var font = Runtime.FontInScope();
            var style = TextLayer.StyleFromFont(Runtime.Fonts, font, font.Scale);
            return TextLayer.LinesFromText(Runtime.Fonts, words, style, font.Scale, _width, TextWrap.Words).Length;
        }

        public void Dispose() => Page.Dispose();
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{message}: expected {expected}, got {actual}");
    }

    private static void IsTrue(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
