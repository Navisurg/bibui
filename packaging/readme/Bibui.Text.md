# Bibui.Text

bibui's text layer: fonts and faces on FreeType and HarfBuzz, measuring, a
text's runs and glyphs, line breaking, and the built-in Inter. It knows
nothing of nodes, themes or windows. `Bibui.Core` lays text out and paints it
with this package, which it depends on, so an app gets it with any bibui
package.

An app measures and breaks text with `TextLayer`, in the font a text built
in scope would get (`runtime.FontInScope()`), and gets the widths and lines
layout gives the same text:

```csharp
using Bibui.Text;

Font font = runtime.FontInScope();
FontStyle style = TextLayer.StyleFromFont(runtime.Fonts, font, font.Scale);
float width = TextLayer.WidthFromChars(runtime.Fonts, "Hello", style, font.Scale);
foreach (TextLine line in TextLayer.LinesFromText(runtime.Fonts, paragraph, style, font.Scale, 320f, TextWrap.Words))
    runtime.Text(paragraph.AsSpan(line.Start, line.End - line.Start));
```

Fonts of the app's own are registered once, before the first frame, with
`FontRegistry.Register(family, weight, bytes)`, and a theme names them.

Text is UTF-16 throughout. bibui looks up no installed fonts for text, so it
measures and draws the same on every machine, but one: the colour emoji font
a platform sets up for grapheme clusters its own faces cannot set
(`Fonts.UseClusterFont`; on Linux, fontconfig's).
