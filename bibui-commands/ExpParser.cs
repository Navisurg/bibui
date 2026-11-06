using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bibui.Commands;

// The text form of Expr, read (docs/proposals/expression-grammar.md): a production per case of the
// union, so what Exprs.Text prints parses back as the same expression, and what parses prints back as
// its text. One pass over the text: Parse holds one token of lookahead, Advance scans the next from
// the text when it is taken, and every choice is made by that one token (LL(1)). Precedence is a
// number per operator in one table, not a function per level (precedence climbing), so the parser is
// one function: what an expression starts with, then each operator that follows it while that
// operator binds more tightly than the one the call was entered for. Adding an operator is a row in
// the table, not a new level. Nothing throws: a text that is not an expression gives null and
// where it stopped, and what it wanted there.

/// <summary>
/// Where parsing stopped, and what it wanted there: <see cref="At"/>, the offset in the text of the
/// token that broke it (the text's length at its end); <see cref="Expected"/>, in words (")", "an
/// expression"). <see cref="None"/>: it did not stop.
/// </summary>
public readonly record struct ParseError(int At, string Expected)
{
    public static readonly ParseError None = new(-1, "");
    public bool Failed => At >= 0;
}

/// <summary>The kinds of token the text of an expression is read as.</summary>
public enum ExprToken : byte
{
    End, Bad,
    Name, It, True, False, Int, Real, Text, Address,
    Dot, LBracket, RBracket, LParen, RParen, Comma, Not,
    Operator,   // a binary operator: which one is its row's Op
}

/// <summary>
/// How tightly a form of expression holds together, read and printed by the same levels: an operator
/// binds what is to its left while its level is at least what its caller takes, and the printer
/// parenthesises an operand looser than its place. ! is Unary: its operand takes steps (Postfix) but
/// not a comparison, so !a == b is (!a) == b.
/// </summary>
public static class ExprLevel
{
    public const int None = 0, Or = 1, And = 2, Compare = 3, Unary = 4, Postfix = 5;
}

/// <summary>
/// One symbol the text of an expression is written with: its text, the token it is read as, the
/// operator it is (a binary one's), how tightly it binds what is to its left (<see cref="ExprLevel"/>;
/// None: it ends an expression), and, for half an operator, what it wanted to be.
/// </summary>
public readonly record struct ExprSymbol(string Text, ExprToken Kind, BinaryOp Op = default, int Level = ExprLevel.None, string? Wanted = null);

/// <summary>
/// Every symbol, read by the scanner (the longest that matches), the parser (its level and operator)
/// and the printer (an operator's text and level). The binary operators come first, in
/// <see cref="BinaryOp"/>'s order, so an operator's row is <c>Table[(int)op]</c>.
/// </summary>
public static class ExprSymbols
{
    public static readonly ExprSymbol[] Table =
    [
        new("==", ExprToken.Operator, BinaryOp.Eq, ExprLevel.Compare),
        new("!=", ExprToken.Operator, BinaryOp.NotEq, ExprLevel.Compare),
        new("<", ExprToken.Operator, BinaryOp.Less, ExprLevel.Compare),
        new("<=", ExprToken.Operator, BinaryOp.LessEq, ExprLevel.Compare),
        new(">", ExprToken.Operator, BinaryOp.Greater, ExprLevel.Compare),
        new(">=", ExprToken.Operator, BinaryOp.GreaterEq, ExprLevel.Compare),
        new("&&", ExprToken.Operator, BinaryOp.And, ExprLevel.And),
        new("||", ExprToken.Operator, BinaryOp.Or, ExprLevel.Or),

        new(".", ExprToken.Dot, Level: ExprLevel.Postfix),
        new("[", ExprToken.LBracket, Level: ExprLevel.Postfix),
        new("(", ExprToken.LParen, Level: ExprLevel.Postfix),
        new("]", ExprToken.RBracket),
        new(")", ExprToken.RParen),
        new(",", ExprToken.Comma),
        new("!", ExprToken.Not),

        new("=", ExprToken.Bad, Wanted: "\"==\""),
        new("&", ExprToken.Bad, Wanted: "\"&&\""),
        new("|", ExprToken.Bad, Wanted: "\"||\""),
    ];
}

/// <summary>
/// A parse in progress: the text, the one token of lookahead (<see cref="Kind"/>, <see cref="From"/>,
/// <see cref="To"/>), where the scanner reads the next (<see cref="At"/>: the lookahead's end), the end
/// of the token taken before it (<see cref="Last"/>: where the expression just read ends, for its
/// span), where it stopped, and the spans asked for.
/// </summary>
public ref struct ExpParserState
{
    public ReadOnlySpan<char> Text;
    public ExprToken Kind;
    public int Symbol;    // the lookahead's row of ExprSymbols.Table; -1 for a token no symbol writes (a name, a number)
    public int From, To, At, Last;
    public ParseError Error;
    public Dictionary<Expr, (int From, int To)>? Spans;
}

public static class ExpParser
{
    /// <summary>
    /// The expression <paramref name="text"/> writes, which an app wrote itself (a [ViewRule]'s, cmdgen's): an
    /// expression that does not parse is the app's mistake, found at startup, so it throws with where and why.
    /// </summary>
    public static Expr Required(string text) =>
        Parse(text, out var error) ?? throw new InvalidOperationException($"Not an expression: {text}: {error}");

    /// <summary>
    /// The expression <paramref name="text"/> writes, or null and <paramref name="error"/>: where it
    /// stopped and what it wanted. With <paramref name="spans"/> (made with
    /// <see cref="ReferenceEqualityComparer"/>: equal sub-expressions are different nodes), each node's
    /// place in the text, from its first character to past its last, for whoever reports on it.
    /// </summary>
    public static Expr? Parse(ReadOnlySpan<char> text, out ParseError error, Dictionary<Expr, (int From, int To)>? spans = null)
    {
        var p = new ExpParserState { Text = text, Error = ParseError.None, Spans = spans };
        Advance(ref p);
        var e = Expression(ref p, ExprLevel.Or);
        if (e is not null && p.Kind != ExprToken.End) e = Fail(ref p, "the end, or an operator");   // something after a whole expression
        error = p.Error;
        return e;
    }

    /// <summary>
    /// An error for a person or an agent, on two lines: the text, then a caret under where it stopped,
    /// and what it wanted there.
    /// <code>
    /// heat(plane(fit, "tmax")
    ///                        ^ at 23: expected ")"
    /// </code>
    /// </summary>
    public static string Describe(ReadOnlySpan<char> text, in ParseError error)
    {
        var line = text.ToString().Replace('\n', ' ').Replace('\t', ' ');   // one line, so the caret lines up
        return $"{line}\n{new string(' ', Math.Clamp(error.At, 0, line.Length))}^ at {error.At}: expected {error.Expected}";
    }

    // How tightly the lookahead binds what is to its left (ExprLevel); None ends the expression.
    static int Level(in ExpParserState p) => p.Symbol >= 0 ? ExprSymbols.Table[p.Symbol].Level : ExprLevel.None;

    //- the parser

    // The expression at the lookahead, taking every operator that binds at least as tightly as least.
    static Expr? Expression(ref ExpParserState p, int least)
    {
        int from = p.From;
        Expr e;

        //- what it starts with: !, a word, a literal, or a parenthesised expression
        if (p.Kind == ExprToken.Not)
        {
            Advance(ref p);
            if (Expression(ref p, ExprLevel.Unary) is not { } of) return null;
            e = Spanned(ref p, new Expr.Not(of), from);
        }
        else
        {
            var token = p.Text[p.From..p.To];
            switch (p.Kind)
            {
                case ExprToken.It:    e = Exprs.It; break;
                case ExprToken.True:  e = new Expr.Lit(Value.Of(true)); break;
                case ExprToken.False: e = new Expr.Lit(Value.Of(false)); break;
                case ExprToken.Name:  e = new Expr.Name(token.ToString()); break;
                case ExprToken.Int:
                    if (!long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long n))
                        return Fail(ref p, "an int that fits a long");
                    e = new Expr.Lit(Value.Of(n));
                    break;
                case ExprToken.Real:
                    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
                        return Fail(ref p, "a real that fits a double");
                    e = new Expr.Lit(Value.Of(x));
                    break;
                case ExprToken.Text:  e = new Expr.Lit(Value.Of(Unescape(token[1..^1]))); break;
                case ExprToken.Address:
                {
                    if (!long.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out long id))
                        return Fail(ref p, "an address whose id fits a long");
                    e = new Expr.Lit(Value.Of(new NodeId(id)));   // a node, by its id: what the model finds it as (EvalTypes.Node)
                    break;
                }
                case ExprToken.LParen:
                    Advance(ref p);
                    if (Expression(ref p, ExprLevel.Or) is not { } inner) return null;
                    if (p.Kind != ExprToken.RParen) return Fail(ref p, "\")\"");
                    e = inner;   // parentheses leave no node: its span takes them in
                    break;
                case ExprToken.Bad: return Fail(ref p, BadWanted(p.Text[p.From..p.To]));
                default:      return Fail(ref p, "an expression");
            }
            Advance(ref p);
            if (e is not Expr.It) Spanned(ref p, e, from);   // `it` is one shared node: no place of its own
        }

        //- what follows it, while it binds tightly enough: a step, an index, a call, an operator
        for (int level; (level = Level(p)) >= least && level > 0;)
        {
            var symbol = ExprSymbols.Table[p.Symbol];
            Advance(ref p);
            switch (symbol.Kind)
            {
                case ExprToken.Dot:
                    if (p.Kind is not (ExprToken.Name or ExprToken.It or ExprToken.True or ExprToken.False)) return Fail(ref p, "a key after \".\"");
                    e = new Expr.Member(e, p.Text[p.From..p.To].ToString());
                    Advance(ref p);
                    break;
                case ExprToken.LBracket:
                    if (Expression(ref p, ExprLevel.Or) is not { } at) return null;
                    if (p.Kind != ExprToken.RBracket) return Fail(ref p, "\"]\"");
                    Advance(ref p);
                    e = new Expr.Index(e, at);
                    break;
                case ExprToken.LParen:
                {
                    var args = new List<Expr>();
                    if (p.Kind != ExprToken.RParen)
                        for (;;)
                        {
                            if (Expression(ref p, ExprLevel.Or) is not { } arg) return null;
                            args.Add(arg);
                            if (p.Kind != ExprToken.Comma) break;
                            Advance(ref p);
                        }
                    if (p.Kind != ExprToken.RParen) return Fail(ref p, "\",\" or \")\"");
                    Advance(ref p);
                    e = new Expr.Call(e, [.. args]);
                    break;
                }
                default:
                {
                    // A binary operator: its right side binds one level tighter, so a chain leans left;
                    // a comparison takes none bare after it (a < b < c says nothing a reader can trust).
                    if (Expression(ref p, level + 1) is not { } right) return null;
                    e = new Expr.Binary(symbol.Op, e, right);
                    if (level == ExprLevel.Compare && Level(p) == ExprLevel.Compare) return Fail(ref p, "no second comparison: add parentheses");
                    break;
                }
            }
            Spanned(ref p, e, from);
        }
        return e;
    }

    static Expr Spanned(ref ExpParserState p, Expr e, int from)
    {
        if (p.Spans is { } spans) spans[e] = (from, p.Last);
        return e;
    }

    static Expr? Fail(ref ExpParserState p, string expected)
    {
        // The first place it stopped, not a caller's; at a token the scanner could not read, what that
        // token wanted to be (a lone "=" wanted "=="), whatever the parser was after.
        if (p.Error.Failed) return null;
        if (p.Kind == ExprToken.Bad)
            expected = p.Symbol >= 0 ? ExprSymbols.Table[p.Symbol].Wanted! : BadWanted(p.Text[p.From..p.To]);
        p.Error = new ParseError(p.From, expected);
        return null;
    }

    // What a token the scanner could not read, and no symbol writes, wanted to be.
    static string BadWanted(ReadOnlySpan<char> bad) => bad switch
    {
        ['"', ..] => "a closing '\"'",
        ['$', ..] => "an address: $ and digits",
        _ => "an expression",
    };

    //- the scanner

    // Takes the lookahead and reads the next token from the text: a symbol from the table, else by its
    // first character a text, an address, a number or a word, looking at most three ahead (an
    // exponent's e, its sign, its digit).
    static void Advance(ref ExpParserState p)
    {
        var t = p.Text;
        p.Last = p.To;
        int i = p.At;
        while (i < t.Length && t[i] is ' ' or '\t' or '\n' or '\r') i++;
        p.From = i;
        p.Symbol = -1;
        if (i == t.Length)
        {
            (p.Kind, p.To, p.At) = (ExprToken.End, i, i);
            return;
        }
        // A symbol: the longest the table writes here, so <= is not < then =, nor != a ! then =.
        var table = ExprSymbols.Table;
        for (int row = 0; row < table.Length; row++)
            if (t[i..].StartsWith(table[row].Text) && (p.Symbol < 0 || table[row].Text.Length > table[p.Symbol].Text.Length))
                p.Symbol = row;
        if (p.Symbol >= 0)
        {
            int past = i + table[p.Symbol].Text.Length;
            (p.Kind, p.To, p.At) = (table[p.Symbol].Kind, past, past);
            return;
        }

        char c = t[i], d = i + 1 < t.Length ? t[i + 1] : '\0';
        ExprToken k;
        int end = i + 1;
        switch (c)
        {
            case '"':
                while (end < t.Length && t[end] != '"') end += t[end] == '\\' ? 2 : 1;
                (k, end) = end < t.Length ? (ExprToken.Text, end + 1) : (ExprToken.Bad, t.Length);
                break;
            case '$':
            {
                // $digits, nothing between: a node's id
                end = Digits(t, i + 1);
                k = end > i + 1 ? ExprToken.Address : ExprToken.Bad;
                break;
            }
            default:
                if (char.IsAsciiDigit(c) || (c == '-' && char.IsAsciiDigit(d)))
                {
                    // An int, or a real when a point and a digit or an exponent follow: 3.x is 3, then .x
                    end = Digits(t, c == '-' ? i + 1 : i);
                    k = ExprToken.Int;
                    if (end + 1 < t.Length && t[end] == '.' && char.IsAsciiDigit(t[end + 1]))
                        (end, k) = (Digits(t, end + 1), ExprToken.Real);
                    if (end < t.Length && t[end] is 'e' or 'E')
                    {
                        int j = end + 1;
                        if (j < t.Length && t[j] is '+' or '-') j++;
                        if (j < t.Length && char.IsAsciiDigit(t[j])) (end, k) = (Digits(t, j), ExprToken.Real);
                    }
                }
                else if (char.IsAsciiLetter(c) || c == '_')
                {
                    while (end < t.Length && (char.IsAsciiLetterOrDigit(t[end]) || t[end] == '_')) end++;
                    k = t[i..end] switch { "it" => ExprToken.It, "true" => ExprToken.True, "false" => ExprToken.False, _ => ExprToken.Name };
                }
                else k = ExprToken.Bad;
                break;
        }
        (p.Kind, p.To, p.At) = (k, end, end);
    }

    static int Digits(ReadOnlySpan<char> t, int i)
    {
        while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
        return i;
    }

    // A text literal's characters, its escapes read: \" \\ \n \t; any other escaped character as itself.
    static string Unescape(ReadOnlySpan<char> s)
    {
        if (s.IndexOf('\\') < 0) return s.ToString();
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 == s.Length) { sb.Append(s[i]); continue; }
            char e = s[++i];
            sb.Append(e switch { 'n' => '\n', 't' => '\t', _ => e });
        }
        return sb.ToString();
    }
}

/// <summary>
/// An <see cref="Expr"/> in JSON: its text (<see cref="Exprs.Text"/>), read back with
/// <see cref="ExpParser.Parse"/>. On Expr itself, so every command argument, model field and log entry
/// holding one carries text, and an agent sends one as it would type it. A text that does not parse is
/// a JsonException with the parser's description: the process boundary, where the port answers it.
/// </summary>
public sealed class ExprJson : JsonConverter<Expr>
{
    public override Expr Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("An expression is a string: its text, as `bp eval` takes it.");
        var text = reader.GetString() ?? "";
        return ExpParser.Parse(text, out var error) ?? throw new JsonException(ExpParser.Describe(text, error));
    }

    public override void Write(Utf8JsonWriter writer, Expr value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Exprs.Text(value));
}
