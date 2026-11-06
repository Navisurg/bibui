using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Text;

namespace ComponentGallery;

/// <summary>
/// Finds what a page lays out where it cannot be seen whole: a node that runs
/// past the box it is in (and so over its neighbours, or under the box's clip),
/// a text narrower than what it says, and anything off the surface. Read over
/// the laid-out tree of the frame just rendered, so it checks the layout an
/// application would show at that size.
/// </summary>
internal static class LayoutCheck
{
    /// <summary>A layout fault: what, and the path of nodes to it.</summary>
    internal readonly record struct Fault(string Kind, string Path, string Detail)
    {
        public override string ToString() => $"{Kind}: {Path} — {Detail}";
    }

    private const float Tolerance = 1f;

    /// <summary>Every fault under <paramref name="root"/>, on a surface <paramref name="width"/> by <paramref name="height"/>.</summary>
    internal static List<Fault> Check(BibuiRuntime runtime, UiNode root, float width, float height)
    {
        var faults = new List<Fault>();
        Walk(runtime, root, Describe(root), faults, width, height, (false, false));
        return faults;
    }

    // scrolled: whether a box above scrolls along x, y — what it holds may lie past
    // the surface on that axis, and is reached by scrolling.
    private static void Walk(BibuiRuntime runtime, UiNode node, string path, List<Fault> faults, float width, float height,
        (bool X, bool Y) scrolled)
    {
        if (node is Text text && text.SizeX.Kind != SizeKind.Text && text.Content.Length > 0)
        {
            var font = runtime.FontInScope(text.Size, text.Family);
            float wants = TextLayer.WidthFromChars(runtime.Fonts, text.Content, TextLayer.StyleFromFont(runtime.Fonts, font, font.Scale), font.Scale);
            if (wants > node.Extent.X + Tolerance)
                faults.Add(new("cut", node.Id is null ? path + " > " + Describe(node) : path, $"says {wants:0} px of text in {node.Extent.X:0} px"));
        }

        for (var child = node.FirstChild; child is not null; child = child.Next == child ? null : child.Next)
        {
            // A closed or collapsed node shows nothing, and nothing under it.
            if (child.Extent.X <= 0f || child.Extent.Y <= 0f) continue;
            // The path names the boxes with an id; anonymous ones in between are left out.
            string childPath = path + " > " + Describe(child);
            string walkPath = child.Id is null ? path : childPath;

            for (int a = 0; a < 2; a++)
            {
                bool floats = a == 0 ? child.FloatingX : child.FloatingY;
                bool scrolls = a == 0 ? node.AllowOverflowX : node.AllowOverflowY;
                float start = a == 0 ? child.Rect.X : child.Rect.Y;
                float end = start + (a == 0 ? child.Extent.X : child.Extent.Y);
                string axis = a == 0 ? "x" : "y";

                if (floats)
                {
                    // Placed on its own: it has to be on the surface, or in a view that scrolls to it.
                    float limit = a == 0 ? width : height;
                    bool reachable = (a == 0 ? scrolled.X : scrolled.Y) || (a == 0 ? node.AllowOverflowX : node.AllowOverflowY);
                    if (!reachable && (start < -Tolerance || end > limit + Tolerance))
                        faults.Add(new("off-surface", childPath, $"{axis} {start:0}..{end:0} on a surface of {limit:0}"));
                    continue;
                }
                if (scrolls) continue;

                float pStart = a == 0 ? node.Rect.X : node.Rect.Y;
                float pEnd = pStart + (a == 0 ? node.Extent.X : node.Extent.Y);
                if (start < pStart - Tolerance || end > pEnd + Tolerance)
                    faults.Add(new("spills", childPath, $"{axis} {start:0}..{end:0} out of its parent's {pStart:0}..{pEnd:0}"));
            }

            Walk(runtime, child, walkPath, faults, width, height,
                (scrolled.X || child.AllowOverflowX, scrolled.Y || child.AllowOverflowY));
        }
    }

    private static string Describe(UiNode node)
    {
        if (node.Id is { } id) return id;
        if (node is Text t)
        {
            string s = new string(t.Content);
            return $"\"{(s.Length > 24 ? s[..24] + "…" : s)}\"";
        }
        return node.GetType().Name;
    }
}
