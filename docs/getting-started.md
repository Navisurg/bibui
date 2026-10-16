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
dotnet add package Bibui
dotnet add package Bibui.Platform.Silk
```

`Bibui` is the library. `Bibui.Platform.Silk` is a host: it opens the window
(GLFW), renders with Skia on OpenGL, and falls back to the CPU when there is no
GPU. It runs on the JIT; for a single native binary, section 7 moves the same
app to the other host, Wayland with the managed rasterizer.

### Building against a clone instead

To work against the source rather than the packages, clone the repository and
reference the host's project. It brings `Bibui` with it:

```bash
git clone https://github.com/Navisurg/bibui ../bibui
```

```xml
<ItemGroup>
  <ProjectReference Include="../bibui/bibui-platform-silk/bibui-platform-silk.csproj" />
</ItemGroup>
```

## 2. The first window

Replace `Program.cs`:

```csharp
using Bibui;
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;
using static Bibui.Arrange;

var runtime = BibuiRuntime.Create();
using var skia = new SkiaRenderer();
var hello = "Hello, bibui";

runtime.Mount(() =>
    Column(runtime.Text(hello)).FillX().FillY().Padded(EdgeInsets.All(24f)));

BibuiWindow.Create(runtime).WithSilk(skia).Run("Stepper");
```

`dotnet run` opens an 800 × 600 window.

- `new SkiaRenderer()` is the renderer that draws each frame, and
  `WithSilk(skia)` hands it to the window. bibui never looks one up at run
  time; you pass it.
- `Mount` takes a build function. bibui calls it whenever a frame is needed,
  lays out the tree it returns, and paints it.
- `Column(...)` stacks its items vertically and is as large as they are.
  `FillX()` and `FillY()` make it fill its parent, the window.
- Every node has a size on each axis: in pixels (`.Px(...)`), a share of its
  parent (`.FillX()`), or the size of its content. Text is sized by its words.
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
using Bibui;
using Bibui.Runtime;
using static Bibui.Arrange;

/// <summary>What a stepper built this frame: its node, and the value after any press.</summary>
public readonly record struct StepperWidget(Box Node, int Value);

public static class Stepper
{
    const string Minus = "-";
    const string Plus = "+";

    public static StepperWidget Build(BibuiRuntime runtime, string id, int value)
    {
        var down = runtime.Button(id + "-down", Minus);
        var up = runtime.Button(id + "-up", Plus);
        if (down.Clicked) value--;
        if (up.Clicked) value++;

        Span<char> digits = stackalloc char[11];
        value.TryFormat(digits, out int length);
        var text = runtime.TextFromId(id + "-value", digits[..length], ComponentSize.Lg);
        Box row = Row(CentredY(down), Space(12f), CentredY(text), Space(12f), CentredY(up));
        return new(row.Px(height: 40f), value);
    }
}
```

Two things to notice:

- Button labels are plain strings; a `const` costs nothing per frame. The
  value is formatted into a stack buffer rather than a new string each frame:
  the text copies it, so the buffer can go when the call returns.
- The row is given a height (`Px(height: 40f)`) because its items are centred
  in it. Centring fills the parent's height, so that height cannot come from
  the same items. A fill inside a parent sized by its children is the usual
  cause of a `LayoutException`.

The app holds its state in a record and mounts with `MountStateful`. The build
function gets the current state and a `setState` handle. Add `App.cs`:

```csharp
using Bibui;
using Bibui.Runtime;
using static Bibui.Arrange;

/// <summary>The app's state. bibui never holds it; the build reads it and asks for a new one.</summary>
public record AppState(int Count);

public static class App
{
    const string ResetLabel = "Reset";

    public static void Mount(BibuiRuntime runtime, string theme = "default-dark") =>
        runtime.MountStateful(new AppState(0), (state, setState) =>
        {
            // Ask first: the stepper's buttons, then the reset box.
            var stepper = Stepper.Build(runtime, "count", state.Count);
            if (stepper.Value != state.Count) setState.Set(state with { Count = stepper.Value });

            Box reset = runtime.BoxFromId("reset", InteractionKinds.LeftPress);
            Interaction press = runtime.InteractionsFor(reset);
            if (press.Clicked) setState.Set(state with { Count = 0 });

            // Then describe.
            reset.Px(120f, 36f);
            reset.Style.CornerRadius = 8f;
            reset.Style.Padding = EdgeInsets.All(8f);
            reset.Style.BackgroundColor = press.Hovering ? UiColor.Accent : UiColor.SurfaceRaised;
            using (runtime.PushParent(reset))
                runtime.Text(ResetLabel, textColor: press.Hovering ? TextColor.AccentForeground : TextColor.Primary);

            return Column(stepper.Node, Space(16f), reset)
                .FillX().FillY()
                .Padded(EdgeInsets.All(24f));
        },
        surface: new SurfaceOptions { Width = 480, Height = 320 },
        theme: theme);
}
```

And `Program.cs` becomes:

```csharp
using Bibui.Raster.Skia;
using Bibui.Runtime;
using Bibui.Runtime.Platform;

var runtime = BibuiRuntime.Create();
using var skia = new SkiaRenderer();
App.Mount(runtime);
BibuiWindow.Create(runtime).WithSilk(skia).Run("Stepper");
```

`setState.Set` stores the new state and asks for a frame, and the next build
sees it. Keeping `App.Mount` apart from `Program.cs` lets a test mount the same
app without a window (section 6).

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

The last argument of `Mount` and `MountStateful` is a theme name:

```csharp
App.Mount(runtime, theme: "default-light");
```

`ThemeRegistry.Names` lists the themes available, `default-dark` and
`default-light` among them. To switch while
the app runs, call `runtime.SetTheme(ThemeLoader.DefaultLight())` (namespace
`Bibui.Theming`). Theme files, your own themes and the token list are in the
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
using Bibui.Runtime;
using Bibui.Testing;
using Xunit;

public class StepperTests
{
    [Fact]
    public void Plus_adds_one_and_reset_returns_to_zero()
    {
        var runtime = BibuiRuntime.Create();
        App.Mount(runtime);
        using var page = BibuiPage.From(runtime);

        page.Click("count-up");
        page.Click("count-up");
        Assert.Equal("2", page.GetText("count-value"));

        page.Click("reset");
        Assert.Equal("0", page.GetText("count-value"));
    }

    [Fact]
    public void The_first_frame_renders()
    {
        var runtime = BibuiRuntime.Create();
        App.Mount(runtime, theme: "default-light");
        using var page = BibuiPage.From(runtime);

        byte[] png = page.Screenshot();
        Assert.Equal(0x89, png[0]);   // a PNG
    }
}
```

```bash
dotnet run
```

`page.Click(id)` presses the middle of the box with that id. `GetText`,
`GetBounds`, `FindById`, `Fill`, `Press` and `Screenshot` cover reading and
typing. The page draws its screenshots with Skia on the CPU, so the test needs
no renderer of its own. Ids come from `BoxFromId`, `TextFromId` and the `id`
you pass to a component.

## 7. Publishing with Native AOT

The Wayland route has no native dependencies beyond libwayland-client and
publishes as one self-contained binary. Swap the packages:

```bash
dotnet remove package Bibui.Platform.Silk
dotnet add package Bibui.Platform.Wayland
dotnet add package Bibui.Raster.Managed.Backend
```

`Program.cs` names the managed rasterizer and the Wayland host. `App.cs` and
`Stepper.cs` do not change:

```csharp
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;

var runtime = BibuiRuntime.Create();
var managed = new ManagedRenderer();
App.Mount(runtime);

var host = new WaylandPlatformHost(managed.Draw, new WaylandHostOptions { AppId = "dev.example.stepper" });
BibuiWindow.Create(runtime).WithHost(host).Run("Stepper");
```

Publish:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -o out
./out/Stepper
```

Native AOT needs `clang` and the zlib development headers (`zlib1g-dev` on
Debian and Ubuntu). The result is a single executable of about 5 MB that runs
under a Wayland compositor. `WaylandPlatformHost.IsSupported` tells you whether
the current session can open a window.

The Silk + Skia route is JIT-only for now: Silk.NET itself is not trim-safe,
so Native AOT is supported on the Wayland + managed route only. [Publishing
with Native AOT](aot.md) covers the trimming rules your own code has to keep,
and what to expect of size and start-up time.

## Next

- [Concepts](concepts.md): how layout, asking and painting fit together.
- [Theming](theming.md): theme files, tokens and your own themes.
- [Components](components/index.md): the components bibui ships and what each returns.
- [Native AOT](aot.md): publishing one native binary.
- [`samples/component-gallery`](../samples/component-gallery): every component
  running in one app.
- [`samples/showcase`](../samples/showcase): an observatory console that uses
  most of bibui, on either route.
