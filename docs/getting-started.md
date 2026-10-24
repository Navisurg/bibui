# Getting started

This guide takes you from an empty folder to a small bibui app: a window, a
component of your own, an interaction, a theme, a headless test, and a Native
AOT binary.

You need the .NET 10 SDK on Linux x64. The first release supports Linux on x64
only.

## 1. Create a project and add the packages

```bash
dotnet new console -n Stepper
cd Stepper
dotnet add package Bibui.App.SilkSkiaGl
```

`Bibui.App.SilkSkiaGl` brings the three parts an app needs: `Bibui.Core`, the
library; `Bibui.Platform.Silk`, a platform that opens the window (GLFW) and puts
frames on screen with OpenGL; and `Bibui.Raster.Skia`, the renderer that draws
them, with Skia on the GPU. This route runs on the JIT; for a single native
binary, section 7 moves the same app to Wayland with the managed rasterizer by
changing the package.

### Building against a clone instead

To work against the source rather than the packages, clone the repository and
reference the same package's project. It brings the rest with it:

```bash
git clone https://github.com/Navisurg/bibui ../bibui
```

```xml
<ItemGroup>
  <ProjectReference Include="../bibui/bibui-apps/silk-skia-gl/bibui-app-silk-skia-gl.csproj" />
</ItemGroup>
```

## 2. The first window

Replace `Program.cs`:

```csharp
using Bibui.Core;
using Bibui.App;
using Bibui.Core.Runtime;

var runtime = BibuiRuntime.Create();
var hello = "Hello, bibui";

using var app = BibuiApp.Open(runtime, new("Stepper", 800, 600));
while (app.BeginFrame())
{
    runtime.BeginBuild(app.Events);
    var page = runtime.BeginParent(runtime.Column().Sized(Size.Fill, Size.Fill));
    page.Style.Padding = EdgeInsets.All(24f);
    runtime.Text(hello);
    runtime.EndParent(page);
    runtime.EndBuild();
    app.EndFrame();
}
```

`dotnet run` opens an 800 × 600 window.

- The app owns the loop. `app.BeginFrame()` waits until there is a frame to
  draw (input, or a frame the runtime asked for) and returns false once the
  window closes. `app.EndFrame()` draws the frame into the window, puts it on
  screen, and paces the loop.
- Between `BeginBuild` and `EndBuild` you build one frame. The runtime owns the
  frame's root: what you build with no parent begun lands under it.
  `EndBuild` lays the tree out and records it into `runtime.DrawList`.
- `BibuiApp.Open` opens the window and makes the renderer the package names;
  bibui never looks one up at run time. `app.Window` and `app.Renderer` are
  those two, for when you need them (textures are made on the renderer). To
  write the loop from the window and renderer yourself, see the README's
  "Writing the loop yourself".
- `runtime.Column()` stacks its items vertically and is as large as they are.
  `.Sized(Size.Fill, Size.Fill)` makes it fill its parent, the window, on both
  axes. What is built between `BeginParent(page)` and `EndParent(page)` is
  linked under it.
- Every node has a size on each axis: in pixels (`Size.Px(n)`), a share of its
  parent (`Size.Fill`, `Size.Pct(f)`), or the size of its content
  (`Size.Fit`). Text is sized by its words.
- Text is a plain `string`, or any `ReadOnlySpan<char>`.
- Every frame starts cleared to the theme's background colour, so the root
  needs no background of its own. Colours are theme tokens such as
  `UiColor.Accent`, resolved when the frame is painted, so a theme change
  reaches them without touching your code.

## 3. A component, with the state owned by the app

A component is a plain static function. It takes the value to draw, builds
boxes, and returns its node and what happened. It keeps no state of its own and
does not change yours: the app decides what a press means.

Add `Stepper.cs`, a number with a minus and a plus button:

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

/// <summary>What a stepper built this frame: its node, and the value after any press.</summary>
public readonly record struct StepperWidget(Box Node, int Value);

public static class Stepper
{
    const string Minus = "-";
    const string Plus = "+";

    public static StepperWidget Build(BibuiRuntime runtime, string id, int value)
    {
        var row = runtime.BeginParent(runtime.Row().Sized(y: Size.Px(40f)));
        runtime.BeginCenterAcross();
        if (runtime.Button(id + "-down", Minus).Clicked) value--;
        runtime.EndCenterAcross();
        runtime.Gap(12f);

        Span<char> digits = stackalloc char[11];
        value.TryFormat(digits, out int length);
        runtime.BeginCenterAcross();
        runtime.TextFromId(id + "-value", digits[..length], ComponentSize.Lg);
        runtime.EndCenterAcross();
        runtime.Gap(12f);
        runtime.BeginCenterAcross();
        if (runtime.Button(id + "-up", Plus).Clicked) value++;
        runtime.EndCenterAcross();
        runtime.EndParent(row);
        return new(row, value);
    }
}
```

Three things to notice:

- Button labels are plain strings; a `const` costs nothing per frame. The
  value is formatted into a stack buffer rather than a new string each frame:
  the text copies it, so the buffer can go when the call returns.
- Each button is built where it goes, and asked as it is built. The value
  between them shows a press on `-` in the same frame; a press on `+` is
  answered after the value was built, so the value shows it on the next frame,
  which every pointer event asks for.
- The row is given a height (`.Sized(y: Size.Px(40f))`) because its items are
  centred across it. `BeginCenterAcross` makes a cell as tall as the row, so
  that height cannot come from the same items. A fill inside a parent sized by
  its children is the usual cause of a `LayoutException`.

The app's state is the app's own fields; bibui never holds it. Add `App.cs`:

```csharp
using Bibui.Core;
using Bibui.Core.Runtime;

public sealed class App
{
    const string ResetLabel = "Reset";

    /// <summary>The app's state: a field the build reads and a press writes.</summary>
    public int Count;

    /// <summary>One frame, built between the runtime's BeginBuild and EndBuild.</summary>
    public void Build(BibuiRuntime runtime)
    {
        var page = runtime.BeginParent(runtime.Column().Sized(Size.Fill, Size.Fill));
        page.Style.Padding = EdgeInsets.All(24f);

        Count = Stepper.Build(runtime, "count", Count).Value;
        runtime.Gap(16f);

        // A button of the app's own: a box that claims left presses, asked, then described.
        Box reset = runtime.BoxFromId("reset", InteractionKinds.LeftPress);
        Interaction press = runtime.InteractionsFor(reset);
        if (press.Clicked) Count = 0;
        reset.Sized(Size.Px(120f), Size.Px(36f));
        reset.Style.CornerRadius = 8f;
        reset.Style.Padding = EdgeInsets.All(8f);
        reset.Style.BackgroundColor = press.Hovering ? UiColor.Accent : UiColor.SurfaceRaised;
        runtime.BeginParent(reset);
        runtime.Text(ResetLabel, textColor: press.Hovering ? TextColor.AccentForeground : TextColor.Primary);
        runtime.EndParent(reset);

        runtime.EndParent(page);
    }
}
```

And `Program.cs` becomes:

```csharp
using Bibui.App;
using Bibui.Core.Runtime;

var runtime = BibuiRuntime.Create();
var stepper = new App();

using var app = BibuiApp.Open(runtime, new("Stepper", 480, 320));
while (app.BeginFrame())
{
    runtime.BeginBuild(app.Events);
    stepper.Build(runtime);
    runtime.EndBuild();
    app.EndFrame();
}
```

A press writes `Count`, and the next frame the runtime builds reads it. Keeping
`App.Build` apart from `Program.cs` lets a test build the same app without a
window (section 6). [`samples/counter`](../samples/counter/Program.cs) is the
same loop with the build inline.

## 4. Using an interaction

`runtime.Button` is built from the same pieces you can use yourself, as the reset
box above shows:

1. **Acquire** a box with an id and say what it takes:
   `runtime.BoxFromId("reset", InteractionKinds.LeftPress)`. The id keeps the
   box from frame to frame.
2. **Ask** what happened to it: `runtime.InteractionsFor(reset)` returns an
   `Interaction` with `Clicked`, `Hovering`, `Pressed`, `Dragging`, the pointer
   position and more.
3. **Describe** it: size, style, children. Describing can come after asking,
   so what the ask returned can decide how the box looks.

There are no callbacks. A box that claims nothing (`InteractionKinds.None`) is
never answered, and `InteractionKinds.Disabled` takes the press without
reporting it, so it does not fall through to what is underneath.

The order in which boxes ask is their z-order: the first box to ask for a point
gets it. Ask for controls before the container around them, and for an overlay
before the content beneath it. The [concepts guide](concepts.md) covers this in
full.

## 5. Theming in one line

Set the theme on the runtime, by name, before the loop:

```csharp
runtime.SetTheme(ThemeRegistry.Load("default-light"));   // namespace Bibui.Core.Theming
```

`ThemeRegistry.Names` lists the themes available, `default-dark` and
`default-light` among them. The same call switches while the app runs, for
example `runtime.SetTheme(ThemeLoader.DefaultLight())` from a click; the next
frame is painted in it. Theme files, your own themes and the token list are in the
[theming guide](theming.md).

## 6. A headless test with BibuiPage

`Bibui.Testing` drives a runtime without a window. Clicks go through the same
input path a real pointer takes, so a control covered by something else is not
clicked, just as it would not be for a person.

Create a test project next to the app. Any test framework works; this uses
xUnit v3, whose test projects are programs you run:

```bash
dotnet new console -n Stepper.Tests
cd Stepper.Tests
rm Program.cs                      # xUnit v3 supplies the entry point
dotnet add package xunit.v3
dotnet add package Bibui.Testing
dotnet add reference ../Stepper/Stepper.csproj
```

```csharp
using Bibui.Testing;
using Bibui.Core.Theming;
using Xunit;

public class StepperTests
{
    [Fact]
    public void Plus_adds_one_and_reset_returns_to_zero()
    {
        using var page = BibuiPage.Launch(new App().Build);

        page.Click("count-up");
        page.Click("count-up");
        Assert.Equal("2", page.GetText("count-value"));

        page.Click("reset");
        Assert.Equal("0", page.GetText("count-value"));
    }

    [Fact]
    public void The_first_frame_renders()
    {
        using var page = BibuiPage.Launch(new App().Build, theme: ThemeLoader.DefaultLight());

        byte[] png = page.Screenshot();
        Assert.Equal(0x89, png[0]);   // a PNG
    }
}
```

```bash
dotnet run
```

`BibuiPage.Launch` makes a runtime with no window and builds a frame with the
build you hand it, as the app's loop does. `page.Click(id)` presses the middle
of the box with that id and builds the frame the press causes. `GetText`,
`GetBounds`, `FindById`, `Fill`, `Press` and `Screenshot` cover reading and
typing. The page draws its screenshots with Skia on the CPU, so the test needs
no renderer of its own. Ids come from `BoxFromId`, `TextFromId` and the `id`
you pass to a component.

## 7. Publishing with Native AOT

The Wayland route has no native dependencies beyond libwayland-client and
publishes as one self-contained binary. Swap the package:

```bash
dotnet remove package Bibui.App.SilkSkiaGl
dotnet add package Bibui.App.WaylandManaged
```

Nothing in `Program.cs`, `App.cs` or `Stepper.cs` has to change: `BibuiApp` is
the same type in every `Bibui.App` package. A Wayland compositor groups and
themes windows by an application id, which you can give:

```csharp
using var app = BibuiApp.Open(runtime, new("Stepper", 480, 320),
    new BibuiAppOptions { AppId = "dev.example.stepper" });
```

The window draws on the CPU: the managed rasterizer draws each frame into the
window's `wl_shm` buffer.

Publish:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o out
./out/Stepper
```

Native AOT needs `clang` and the zlib development headers (`zlib1g-dev` on
Debian and Ubuntu). The result is a single executable of about 5 MB that runs
under a Wayland compositor. `WaylandWindow.IsSupported` tells you whether
the current session can open a window.

Native AOT is supported on the Wayland routes only; the Silk route runs on the
JIT. [Publishing with Native AOT](aot.md) says why, and covers the trimming
rules your own code has to keep and what to expect of size and start-up time.

## Next

- [Concepts](concepts.md): how layout, asking and painting fit together.
- [Theming](theming.md): theme files, tokens and your own themes.
- [Components](components/index.md): the components bibui ships and what each returns.
- [Native AOT](aot.md): publishing one native binary.
- [`samples/component-gallery`](../samples/component-gallery): every component
  running in one app.
- [`samples/showcase`](../samples/showcase): an observatory console that uses
  most of bibui, on either route.
