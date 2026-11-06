using System.ComponentModel;
using Bibui.Commands;

// The board's lenses, a record each. A record declares what its lens draws (its first parameter), then
// the lens's arguments, which live in its call. cmdgen writes, in a part of each one's
// file (BoardLens.cs, TreeLens.cs), its function (BoardLens.Func), a typed call (BoardLens.Call(of)), and
// a reader per argument (TreeLens.Parent(shown)). Its drawing is its file's, by hand.
[LensSet]
public static class BoardLenses
{
    [Description("Parts, each drawn through its own lens where it sits on a board that pans.")]
    public sealed record Board(
        [Description("What it lays out: parts.")] Expr Of);

    [Description("Parts drawn as a tree on a canvas that pans and zooms: each a stroke from the end of its parent, as long and as wide as it says, coloured by what by says of it (it the part).")]
    public sealed record Tree(
        [Description("What it draws: a set of parts (vessels(it.mode).paths).")] Expr Of,
        [Description("The index of the part it branches from; a part's parent comes before it, and anything else (-1) is a root.")] [PerPart] Expr Parent,
        [Description("How long it is drawn.")] [PerPart] Expr Length,
        [Description("How wide it is drawn.")] [PerPart] Expr Width,
        [Description("What colours it: a number, shaded blue to red; a condition, lit or faint; none, the plain colour.")] [PerPart] Expr? By);
}
