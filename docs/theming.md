# Theming

A bibui theme is one value, `ThemeTokens`: the colours, sizes, line widths,
density and motion that every component draws with. This guide covers what a
theme holds, how to change part of one, how to write one as a file, and how to
generate one from a brand colour. It ends with how components read the theme
and how to check that text stays readable. It assumes the model in
[Concepts](concepts.md).

The samples assume these usings:

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;
using Bibui.Core.Theming;
```

## Using a theme

A runtime is given its theme with `SetTheme`, as a value or by name:

```csharp
runtime.SetTheme(ThemeLoader.BibuiLight());                        // a value
runtime.SetTheme(ThemeRegistry.Load("default-dark"));              // a registered name
```

`runtime.SetTheme(theme)` works before the first frame and at any time after,
from any thread. The next frame is painted with the new one, with nothing
rebuilt by hand. `runtime.Theme` is
the theme in force.

Names are resolved in one table, `ThemeRegistry`, so a name that works in one
place works in all of them. bibui registers its own themes (`default-dark` and
`default-light` among them; `ThemeRegistry.Names` lists them all). You add yours
once at start-up:

```csharp
ThemeRegistry.Register("my-dark", () => MyThemes.Dark());
```

A name can be registered only once. Registering a name that is already taken,
including a built-in one, throws `InvalidOperationException` instead of
replacing it. Otherwise a name would mean whichever theme was registered last.

## What a theme holds

`ThemeTokens` is a record of token groups:

| Group | Type | What it sets | Read |
|---|---|---|---|
| `Colors` | `ColorTokens` | ramps and colour roles | at paint time, through `UiColor` tokens |
| `Spacing` | `SpacingTokens` | `Xs` 4, `Sm` 8, `Md` 12, `Lg` 16, `Xl` 24 | by your build code |
| `Typography` | `TypographyTokens` | text sizes `SizeSm` … `Size2Xl`, weights, font families | when text is measured and drawn |
| `Radii` | `RadiiTokens` | `Sm` 4, `Md` 6, `Lg` 8, `Full` 9999 | by components as they build |
| `Elevation` | `ElevationTokens` | shadows for levels 1 to 4; flat by default, `ElevationTokens.Soft` for depth | by components as they build |
| `Borders` | `BorderTokens` | `Width` 1 for outlines and rules, `Strong` 2 for a chosen outline | by components as they build |
| `FocusRing` | `FocusRingTokens` | width, offset and colour of the keyboard focus ring | by components as they build |
| `Density` | `DensityTokens` | `Compact`, `Default` or `Comfortable` vertical rhythm | by components as they build |
| `Motion` | `MotionTokens` | speeds for hover, following (scrolling, the caret), toggles and `runtime.Animate`, and the easing; the runtime turns them into `runtime.Rates` once a frame | by the runtime and components |

Every default reproduces what the built-in components draw. A theme sets only
what it changes.

## Colours: ramps and roles

`ColorTokens` holds colours in two layers.

**Ramps** are the primitive palettes. A `ColorRamp` is one hue in eleven steps
from light to dark: 50, 100, 200 … 900, 950. A theme has two, in
`ColorTokens.Ramps`:

- `Neutral`: greys for backgrounds, surfaces, outlines and text. The default is
  `ColorRamp.DefaultNeutral`, a zinc-like grey.
- `Accent`: the brand hue for filled controls, focus and selection. The default
  is `ColorRamp.DefaultAccent`, a violet.

**Roles** are what components paint with:

| Role | Used for | Unset, dark | Unset, light |
|---|---|---|---|
| `Background` | the page behind everything | neutral-950 | neutral-50 |
| `Surface` | a panel on the page | neutral-950 | neutral-50 |
| `SurfaceRaised` | a card, a field, a menu | neutral-900 | neutral-100 |
| `SurfaceRaisedHover`, `SurfaceRaisedActive` | the same under the pointer, pressed | neutral-800, -700 | neutral-200, -300 |
| `Border` | an outline or rule | neutral-800 | neutral-200 |
| `BorderHover` | an outline under the pointer | neutral-600 | neutral-300 |
| `BorderFocus` | an outline with the keyboard | accent-400 | accent-500 |
| `TextPrimary` | reading text | neutral-50 | neutral-950 |
| `TextSecondary` | descriptions, placeholders | neutral-400 | neutral-600 |
| `TextDisabled` | text on a control that takes nothing | neutral-500 | neutral-400 |
| `Accent` | the filled control, the selection | accent-500 | accent-600 |
| `AccentForeground` | text and icons on `Accent` | neutral-50 | neutral-50 |
| `AccentHover`, `AccentActive` | `Accent` under the pointer, pressed | accent-400, -600 | accent-700, -800 |
| `Destructive`, `DestructiveForeground` | a dangerous action | fixed red, white | fixed red, white |
| `Success`, `Warning`, `Info` and their `…Foreground`, `…Bg` | status | fixed colours | fixed colours |

A role is set to one of two things:

- a colour: `"#RRGGBB"` or `"#RRGGBBAA"`;
- a ramp step: `"neutral-900"`, `"accent-500"`.

Reading a role always gives a colour. A role nobody sets takes the step in the
table for the theme's **`Mode`** (`ThemeMode.Dark` or `ThemeMode.Light`), so a
theme that sets only its ramps and its mode already has every role. Status
colours are not taken from a ramp. They default to fixed colours that suit the
mode.

Setting a role to anything else throws `ArgumentException` when the theme is
built, not later when it is painted:

```csharp
var colors = new ColorTokens { Accent = "teal" };
// ArgumentException: Accent = 'teal' is neither a colour (#RRGGBB, #RRGGBBAA) nor a ramp step
// (neutral-900, accent-500; steps are 50, 100, 200 … 900, 950).
```

`ColorTokens.Extra` holds any further colours your theme names, keyed by your
own token names. Read them with `UiColor.Token(c => c.Extra["chart-1"])`.

## Changing one role or one ramp

Themes are immutable records, so you change one with `with`. Replace one layer
and leave the other alone.

Change one role, and nothing else:

```csharp
ThemeTokens baseTheme = ThemeLoader.BibuiDark();
ThemeTokens sky = baseTheme with { Colors = baseTheme.Colors with { Accent = "#0EA5E9" } };
```

Replace a ramp, and every role that names a step of it follows. `Accent`,
`AccentHover`, `AccentActive` and `BorderFocus` all move to the new hue at
once:

```csharp
var teal = new ColorRamp(
    "#F0FDFA", "#CCFBF1", "#99F6E4", "#5EEAD4", "#2DD4BF", "#14B8A6",
    "#0D9488", "#0F766E", "#115E59", "#134E4A", "#042F2E");

ThemeTokens baseTheme = ThemeLoader.BibuiDark();
ThemeTokens tealTheme = baseTheme with
{
    Colors = baseTheme.Colors with { Ramps = baseTheme.Colors.Ramps with { Accent = teal } },
};
```

A ramp only recolours roles that *name* a step. A role set to a hex colour
keeps that colour. `default-dark` and `default-light` set their surfaces and
text as hex colours, so replacing their neutral ramp changes nothing you can
see. To build a theme from ramps alone, start from `new ThemeTokens()` (every
role unset) and set the mode:

```csharp
ThemeTokens slateLight = new()
{
    Name = "slate-light",
    Colors = new ColorTokens
    {
        Mode = ThemeMode.Light,
        Ramps = new ColorRamps { Accent = teal },
    },
};
```

`ColorRamp.With(step, hex)` gives a copy of a ramp with one step changed.

Groups other than `Colors` are classes with `init` properties. Replace the
whole group:

```csharp
ThemeTokens compact = baseTheme with
{
    Density = new DensityTokens { Mode = Density.Compact },
    Radii = new RadiiTokens { Sm = 2f, Md = 3f, Lg = 4f },
    Elevation = ElevationTokens.Soft,
};
```

## Theme files

A theme can also be a JSON file that lists only what it changes from the theme
it **extends**:

```json
{
  "name": "ocean",
  "extends": "default-dark",
  "colors": {
    "ramps": { "accent": { "500": "#0EA5E9", "600": "#0284C7" } },
    "accent": "accent-600",
    "surfaceRaised": "#10202E"
  },
  "radii": { "md": 8 },
  "density": { "mode": "comfortable" }
}
```

Register it by path. `RegisterFile` returns the name it was registered under:

```csharp
string name = ThemeRegistry.RegisterFile("themes/ocean.json");   // "ocean"
runtime.SetTheme(ThemeRegistry.Load(name));
```

To read a file without registering it, use `ThemeFile.Load(path)`, or
`ThemeFile.Parse(utf8, sourceName)` for bytes from elsewhere (an embedded
resource, say). Then call `ThemeRegistry.Register(file)`, or `file.Resolve()`
to get the `ThemeTokens`.

The format:

- **Keys** are the `ThemeTokens` properties in camelCase: `colors`, `spacing`,
  `typography`, `radii`, `elevation`, `borders`, `focusRing`, `density`,
  `motion`. Inside `colors` are `mode`, `ramps`, `extra` and the roles
  (`background`, `surfaceRaised`, `accentForeground`, …).
- **`name`** is the theme's name. Without it, the file name without its
  extension is used.
- **`extends`** names any registered theme: a built-in one, one from another
  file, or one registered in code. Without `extends`, the file starts from
  `new ThemeTokens()`.
- **Merging is one key at a time.** A group the file mentions keeps every field
  the file does not mention. So do `ramps`, `extra` and each elevation level.
- **A role** is a colour or a ramp step, as in code.
- **A ramp** is either all eleven colours as an array, or an object of only the
  steps it changes (`{ "600": "#0D9488" }`).
- **An elevation level** is an object of shadow fields (`offsetX`, `offsetY`,
  `blur`, `spread`, `color`), or `null` for flat. Its `color`, like
  `focusRing.color`, is a colour or a role name (`"borderFocus"`).
- **Enums** are their names in lower case: `"light"`, `"compact"`, `"none"`.
- **Comments and trailing commas** are allowed.

### Errors

A file is checked as a whole when it is read. Errors are not deferred to the
first paint. Each error is a `FormatException` that names the file, the key and
the value:

```text
ocean.json: unknown key 'colors.acent'. colors takes: mode, ramps, extra, background, surface, …
ocean.json: colors.accent = "teal" is neither a colour (#RRGGBB, #RRGGBBAA) nor a ramp step (neutral-900, accent-500; steps are 50, 100, 200 … 900, 950).
ocean.json: colors.ramps.accent has 1 colours; a ramp has 11, steps 50 to 950.
ocean.json: density.mode = "tight" is not one of: compact, default, comfortable.
```

The parent is looked up when the theme is *resolved* (when it is loaded or
set), so a file may be registered before the theme it extends. A missing
parent, or themes that extend each other in a ring, throw
`InvalidOperationException` at that point:

```text
ocean.json: extends 'ocean-base', which is not a registered theme. Registered: …
Themes extend each other in a ring: 'ring-a' (ring-a.json) extends 'ring-b' (ring-b.json) extends 'ring-a' (ring-a.json).
```

Theme files are read with `System.Text.Json`'s document model. No reflection
is involved, so they work in Native AOT apps.

## Generating a theme from a seed

`Theme.FromSeed` makes a whole theme (both ramps, every colour role, radii and
density) from a brand colour:

```csharp
ThemeTokens brand = Theme.FromSeed("#0EA5E9");
ThemeTokens brandLight = Theme.FromSeed("#0EA5E9", mode: ThemeMode.Light);
ThemeTokens warm = Theme.FromSeed(
    accent: "#E11D48",
    neutral: "#78716C",         // tints the greys
    radius: 10f,                // Md; Sm is two thirds of it, Lg four thirds
    density: Density.Compact,
    mode: ThemeMode.Dark,
    name: "rose");
```

- **The accent ramp** is built in the OKLCH colour space. The seed sits in it
  exactly, at the step nearest its lightness. The other steps run evenly to
  near-white and near-black in the same hue. `Theme.AccentRamp(seed)` gives the
  ramp on its own.
- **The `Accent` role** is the seed's own step when text in that colour reads on
  the page. Otherwise it is the nearest step that does: lighter on a dark page,
  darker on a light one. A pale yellow stays yellow on a dark theme and turns
  ochre on a light one.
- **`AccentForeground`** is whichever end of the neutral ramp contrasts more
  with the accent, or pure white or black where that end falls short.
  `AccentHover` and `AccentActive` step away from the foreground, so the label
  on them only gains contrast.
- **The neutral seed** gives the greys its hue and a little of its chroma (at
  most 0.04), never its lightness. `Theme.NeutralRamp(seed)` gives that ramp.
  The default neutral seed is `Theme.DefaultNeutralSeed`, a cool grey.
- **Every role is set to a ramp step** where it is drawn from one, so replacing
  a ramp afterwards with `with` still recolours it.
- **Each text colour reaches 4.5:1** (`Theme.MinTextContrast`) against the
  backgrounds it is drawn on, for any seed. Disabled text is exempt.

The result is an ordinary `ThemeTokens`. Change it with `with`, or register it
under a name:

```csharp
ThemeRegistry.Register("brand", () => Theme.FromSeed("#0EA5E9", name: "brand"));
```

## Colours are resolved at paint time

Components never hold a colour. They hold a **token**, a `UiColor` that names a
role and is looked up against the theme when the frame is painted:

```csharp
box.Style.BackgroundColor = UiColor.SurfaceRaised;
box.Style.BorderColor = UiColor.Border;
box.Style.HoverBackgroundColor = UiColor.SurfaceRaisedHover;
runtime.Text(caption, ComponentSize.Sm, TextColor.Secondary);
```

This is why a component takes no theme parameter, and why `SetTheme` recolours
a running app without rebuilding anything by hand. The tokens are:

- **Role tokens**: `UiColor.Background`, `Surface`, `SurfaceRaised`,
  `SurfaceRaisedHover`, `SurfaceRaisedActive`, `Border`, `BorderHover`,
  `BorderFocus`, `TextPrimary`, `TextSecondary`, `TextDisabled`, `Accent`,
  `AccentForeground`, `AccentHover`, `AccentActive`, `Destructive`,
  `DestructiveForeground`, `Success`, `SuccessForeground`, `SuccessBg`.
- **Any role or extra colour**: `UiColor.Token(c => c.WarningBg)`,
  `UiColor.Token(c => c.Extra["chart-1"])`.
- **A translucent role**: `UiColor.Accent.WithAlpha(0.15f)` keeps following the
  theme's accent. A copied hex value would not.
- **Text roles**: `TextColor.Primary`, `Secondary`, `Disabled`, `Accent`,
  `AccentForeground`. Each converts to the matching `UiColor`.

A literal (`"#18181B"`, `UiColor.Rgb(24, 24, 27)`) is not themed. Use one only
for colours that really are fixed, such as a colour swatch the user picked.

`DrawFlags` name the theme's standard treatments, not colours.
`DrawFlags.Border | DrawFlags.Background | DrawFlags.HotEffects | DrawFlags.ActiveEffects`
gives a box the theme's raised fill and outline, the hover outline and the
pressed accent. If the theme changes what those treatments look like, every box
that asked for them follows.

## Reading the theme while building

Some tokens cannot wait for paint time: a line width, a fixed height or an
easing speed is a number the solver or the runtime needs while the frame is
built. Components read those from `runtime.Theme`, which is the theme the frame
will be painted with:

```csharp
public static BibuiWidget Build(BibuiRuntime runtime, string id, ReadOnlySpan<char> label, bool on)
{
    ThemeTokens theme = runtime.Theme;
    Box row = runtime.BoxFromId(id, InteractionKinds.LeftPress);
    Interaction interaction = runtime.InteractionsFor(row);

    row.ChildLayoutAxis = LayoutAxis.X;
    row.SizeX = SemanticSize.ChildrenSum();
    row.SizeY = SemanticSize.Pixels(theme.Density.Of(32f));                       // a fixed height, at the theme's density
    row.Style.Padding = EdgeInsets.Symmetric(horizontal: theme.Spacing.Md, vertical: theme.Density.Of(6f));
    row.Style.CornerRadius = theme.Radii.Md;
    row.Style.BorderWidth = on ? theme.Borders.Strong : theme.Borders.Width;       // the chosen one stands out
    row.Style.BorderColor = on ? UiColor.Accent : UiColor.Border;
    row.Style.Shadow = theme.Elevation.Level(1);

    // Eased at the theme's toggle rate, so a reduced-motion theme snaps.
    float t = runtime.Animate(id, "on", on ? 1f : 0f, runtime.Rates.Toggle);
    row.Style.BackgroundColor = UiColor.Accent.WithAlpha(0.2f * t);

    using (runtime.PushParent(row))
        runtime.Text(label);
    return new BibuiWidget(row, interaction);
}
```

The conventions the built-in components follow:

- **`Density.Of(px)`** for fixed heights and vertical padding only. Density is
  vertical rhythm: it leaves text size, horizontal padding, gaps, radii and line
  widths alone. At `Density.Default` the scale is exactly 1.
- **`Borders.Width`** for outlines, rules and dividers. **`Borders.Strong`** for
  the outline that marks the chosen one, such as a picked radio button.
- **`Elevation.Level(ElevationTokens.Overlay)`** for dropdowns, menus and
  popovers, `ElevationTokens.Tooltip` for tooltips, and `ElevationTokens.Modal`
  for dialogs. Pick the level by role, not by look.
- **`runtime.Rates`**, the theme's motion speeds as this frame's rates:
  **`Rates.Toggle`** for a control crossing between two states,
  **`Rates.Hover`** for hover (the runtime already uses it for `HotT`),
  **`Rates.Follow`** for a position chasing input (view scrolling and the caret
  use it), and **`Rates.Default`** for anything else, which is what
  `runtime.Animate` uses when you pass no rate. Do not hard-code speeds; for
  one of your own, `runtime.Rate(speed)`. Every rate is 1 under reduced motion
  or `Motion.Easing = MotionEasing.None`, so nothing else has to check.

## Contrast

Text has to be readable on whatever it is drawn on. Aim for a contrast ratio of
at least **4.5:1** for body text and **3:1** for large text (about 18 px and
up, or 14 px bold) and for the outlines of controls. These are the WCAG AA
levels.

When you set roles yourself, check these pairs:

| Foreground | On |
|---|---|
| `TextPrimary`, `TextSecondary` | `Background`, `Surface`, `SurfaceRaised`, and `SurfaceRaisedHover` where rows highlight |
| `AccentForeground` | `Accent`, `AccentHover`, `AccentActive` |
| `Accent` used as text or an icon | `Background`, `Surface`, `SurfaceRaised` |
| `DestructiveForeground`, `SuccessForeground`, `WarningForeground`, `InfoForeground` | their own fills |
| `Success`, `Warning`, `Info` used as text | their `…Bg` tints |
| `Border`, `BorderFocus` (3:1) | the surfaces they outline |

`TextDisabled` is meant to be quieter and is exempt. Do not use it for anything
the user has to read to continue.

Things that commonly go wrong:

- **Changing the accent without its foreground.** A lighter brand colour often
  needs dark text on it. When you replace the accent ramp, check
  `AccentForeground` too.
- **Light mode status fills.** Saturated mid-tone greens and yellows are too
  pale behind near-white labels. Take them a step darker in light themes.
- **Translucent fills.** `WithAlpha` blends with whatever is behind it, so
  check the blended colour, not the token.

`Theme.Contrast(foreground, background)` returns the contrast ratio
(1 to 21) for two hex colours, and `Theme.FromSeed` meets 4.5:1 for every
text role by construction. A test over your own theme is a few lines:

```csharp
ThemeTokens t = ThemeRegistry.Load("my-dark");          // or any theme you built
foreach (string surface in new[] { t.Colors.Background, t.Colors.Surface, t.Colors.SurfaceRaised })
{
    if (Theme.Contrast(t.Colors.TextPrimary, surface) < Theme.MinTextContrast) throw new Exception("TextPrimary");
    if (Theme.Contrast(t.Colors.TextSecondary, surface) < Theme.MinTextContrast) throw new Exception("TextSecondary");
}
if (Theme.Contrast(t.Colors.AccentForeground, t.Colors.Accent) < Theme.MinTextContrast) throw new Exception("AccentForeground");
```

## Seeing your theme

The quickest check is to draw a few components headless with your theme and
look at the PNG (using `Bibui.Raster.Skia`):

```csharp
var runtime = BibuiRuntime.Create();
runtime.SetTheme(ThemeRegistry.Load(ThemeRegistry.RegisterFile("themes/ocean.json")));
runtime.Surface = new SurfaceOptions { Width = 360, Height = 80, ScaleFactor = 1f };
const string save = "Save", sync = "Sync";

runtime.BeginBuild();
using (runtime.PushParent(runtime.RowPx(360f, 80f).Padded(EdgeInsets.All(24f))))
{
    runtime.Button("save", save);
    runtime.Gap(12f);
    runtime.Switch("sync", on: true, label: sync);
}
runtime.EndBuild();

using var skia = new SkiaCpuRenderer();
File.WriteAllBytes("ocean.png", FramePixels.Png(runtime, skia.Draw));
```

The component gallery sample shows every component on one page and is the
fuller check. See [Components](components/) for what each component reads from
the theme.
