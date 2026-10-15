# Publishing with Native AOT

A bibui app on the Wayland host and the managed rasterizer publishes as one
native executable: no .NET runtime to install, no JIT at start-up, and no Skia,
GLFW or OpenGL inside it. This page covers how to set that up, the rules your
own code has to keep for the publish to stay clean, and what to expect of the
result.

## Which route publishes

| Route | Packages | Native AOT |
|---|---|---|
| Wayland + managed raster | `Bibui`, `Bibui.Platform.Wayland`, `Bibui.Raster.Managed.Backend` | **Yes.** Tested in CI with warnings as errors |
| Silk + Skia | `Bibui`, `Bibui.Platform.Silk` | **No, JIT only for now** |

The Silk + Skia route is JIT-only for now. bibui's own Silk host and Skia
backend are AOT compatible, but Silk.NET itself is not trim-safe: it finds its
platform by reflection and resolves native library paths through
`Assembly.Location`, so an AOT publish reports Silk.NET's own warnings (IL2072,
IL3000, IL3002). Those cannot be suppressed for one assembly, and bibui does not
ask you to accept them. Run the Silk + Skia route on the JIT, and use the
Wayland + managed route when you need a native binary. This may change if
Silk.NET becomes trim-safe or GPU rendering comes to the Wayland host.

Your build code does not depend on the route. Only `Program.cs`, which picks
the host and the backend, differs.

## Set up the project

You need the .NET 10 SDK and, for the native link step, `clang` and the zlib
development headers (`zlib1g-dev` on Debian and Ubuntu). The app needs these
libraries at run time: `libwayland-client`, `libxkbcommon`, FreeType and
HarfBuzz (`libfreetype6` and `libharfbuzz0b` on Debian and Ubuntu).

```bash
dotnet new console -n Counter && cd Counter
dotnet add package Bibui
dotnet add package Bibui.Platform.Wayland
dotnet add package Bibui.Raster.Managed.Backend
```

In the `.csproj`, turn on AOT and make every trimming warning an error, so a
problem fails the build instead of a start-up:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

`PublishAot` also turns on the trimming and AOT analyzers for an ordinary
`dotnet build`, so you see the warnings while you work, not only when you
publish. `InvariantGlobalization` is optional. It leaves out the ICU culture
data, which a UI that formats its numbers with the invariant culture does not
need.

## Name the backend

Pass the raster backend to the runtime yourself:

```csharp
using Bibui;
using Bibui.Platform.Wayland;
using Bibui.Raster.Managed.Backend;
using Bibui.Runtime;
using static Bibui.Arrange;

var runtime = BibuiRuntime.Create().WithManagedRasterizer();
int count = 0;

runtime.Mount(() =>
{
    var add = runtime.Button("add", "Add one");
    if (add.Clicked) count++;
    var text = runtime.Text($"Clicked {count} times");
    return Column(text, Space(12f), add).FillX().FillY().Padded(EdgeInsets.All(24f));
});

if (!WaylandPlatformHost.IsSupported)
{
    Console.Error.WriteLine("This app needs a Wayland session.");
    return 1;
}
BibuiWindow.Create(runtime).WithWayland(new WaylandHostOptions { AppId = "dev.example.counter" })
    .Run("Counter");
return 0;
```

There is no default backend: `BibuiRuntime.Create()` is followed by the
backend you choose (`.WithManagedRasterizer()`, `.WithSkia()` or
`.WithRaster(backend)`), so the trimmer sees the type you name and keeps it.

The host is chosen the same way, after `BibuiWindow.Create(runtime)`. bibui looks nothing up at run time: no
platform discovery, no plug-in loading, no reflection over your types.

## Publish

```bash
dotnet publish -c Release -r linux-x64 -o out
./out/Counter
```

`-p:PublishAot=true` on the command line does the same, if you would rather not
set it in the project. The result is a single executable. `WaylandPlatformHost.IsSupported`
is false outside a Wayland session (no `WAYLAND_DISPLAY`, or no
libwayland-client), so the app can say so instead of failing to connect.

## What to expect

[`samples/showcase`](../samples/showcase), an observatory console that uses
most of bibui (charts, Markdown, dialogs, theme files, a seeded theme), is the
reference:

| | Showcase, Wayland + managed, Native AOT |
|---|---|
| Executable | about **5.2 MB**, one file |
| First rendered frame (1440 × 900), from spawn | about **29 ms** |

The counter above and the [getting-started](getting-started.md) app publish
to about 5 MB as well. Most of that is bibui and the managed rasterizer, so it
grows slowly with your own code; `System.Text.Json`, for example, adds about
half a megabyte.

The showcase builds both routes from one project. A build property leaves Silk
and Skia out of the AOT binary, so only the managed route ships:

```xml
<ItemGroup>
  <PackageReference Include="Bibui" Version="0.9.0" />
  <PackageReference Include="Bibui.Platform.Wayland" Version="0.9.0" />
  <PackageReference Include="Bibui.Raster.Managed.Backend" Version="0.9.0" />
</ItemGroup>
<ItemGroup Condition="'$(AppRoute)' != 'wayland'">
  <PackageReference Include="Bibui.Platform.Silk" Version="0.9.0" />
</ItemGroup>
<PropertyGroup Condition="'$(AppRoute)' == 'wayland'">
  <DefineConstants>$(DefineConstants);WAYLAND_ONLY</DefineConstants>
</PropertyGroup>
```

```bash
dotnet run                                                          # JIT, Silk + Skia available
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -p:AppRoute=wayland -o out
```

Code that names `SilkPlatformHost` or `SkiaRasterBackend` goes inside
`#if !WAYLAND_ONLY`.

## Trimming rules for your code

bibui keeps these rules in all its libraries, and an AOT app has to keep them in
its own code too. Each one shows up as an IL2xxx or IL3xxx warning when it is
broken; with `TreatWarningsAsErrors` that warning fails the build.

- **No reflection-based serialization.** `JsonSerializer.Serialize(value)` and
  `Deserialize<T>(json)` without a type-info argument read your types by
  reflection, which AOT cannot keep. Use a source-generated context:

  ```csharp
  using System.Text.Json;
  using System.Text.Json.Serialization;

  var settings = JsonSerializer.Deserialize(json, AppJson.Default.Settings);

  [JsonSerializable(typeof(Settings))]
  partial class AppJson : JsonSerializerContext;
  ```

  Or read the JSON as a DOM (`JsonDocument`, `JsonNode`), as bibui's own
  theme loader does. Theme files (`ThemeRegistry.RegisterFile`) are therefore
  safe to load in an AOT app.
- **No discovery by name.** `Type.GetType("...")`, `Activator.CreateInstance`
  on a computed type, `Assembly.Load` and scanning assemblies for types that
  implement an interface all depend on code the trimmer could not see being
  used. Name the types in code: a `switch` that constructs each one, or a list
  of factories.
- **No runtime code generation.** No `System.Reflection.Emit`, no
  `Expression.Compile()`, no `dynamic`. AOT has no JIT to run what they
  produce.
- **No `Assembly.Location`.** It is empty in a single-file binary. Find files
  that ship beside the executable with `AppContext.BaseDirectory`, and copy them
  there with `CopyToPublishDirectory`:

  ```xml
  <None Include="themes/*.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  ```

- **Check every dependency.** A package you add can break these rules on your
  behalf. Publish with AOT once after adding one; a library that ships
  `IsAotCompatible` has already been checked by its authors.
- **Do not suppress the warnings.** An `[UnconditionalSuppressMessage]` or a
  `NoWarn` for IL2xxx/IL3xxx hides a failure that shows up later, at run time,
  as a missing member. Fix the code instead.

## Checking a publish in CI

Headless machines have no Wayland session, so check the binary without a
window. Give the app a flag that mounts, renders one frame and exits:

```csharp
if (args.Contains("--first-frame"))
{
    _ = runtime.Render();   // builds, lays out and paints one frame; throws if any of it fails
    return 0;
}
```

Then publish with warnings as errors and run it:

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=true -o out
./out/Counter --first-frame
```

bibui's own CI does the same for its Wayland sample.
