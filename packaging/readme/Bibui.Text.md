# Bibui.Text

bibui's text layer: fonts and faces on FreeType and HarfBuzz, measuring, a
text's runs and glyphs, line breaking, and the built-in Inter. It knows
nothing of nodes, themes or windows. `Bibui.Core` lays text out and paints it
with this package, which it depends on, so an app gets it with any bibui
package.

An app uses it through the runtime: `runtime.Fonts` measures and wraps in a
`Font`, and `runtime.FontInScope()` gives the font a text built there would
get.

```csharp
using Bibui.Text;

Font font = runtime.FontInScope();
float width = runtime.Fonts.Measure("Hello", font);
for (TextLine? line = runtime.Fonts.Wrap(paragraph, font, 320f); line is not null; line = line.Next)
    runtime.Text(paragraph.AsSpan(line.Start, line.End - line.Start));
```

Fonts of the app's own are registered once, before the first frame, with
`FontRegistry.Register(family, weight, bytes)`, and a theme names them.

Text is UTF-16 throughout. bibui looks up no installed fonts for text, so it
measures and draws the same on every machine.
