# Bibui.Testing

Headless testing for bibui apps. A `BibuiPage` holds a runtime with no window
and draws it with Skia on the CPU, unless `BibuiPage.From(runtime, build, draw)`
is given another `DrawToPixels`; a test finds nodes by id, reads their laid-out
bounds and text, clicks, types and presses keys, and takes screenshots. To
drive a running app instead, over a socket, see `Bibui.Automation`'s `TestPort`.

```csharp
var runtime = BibuiRuntime.Create();
using var page = BibuiPage.From(runtime, () => MyScreen.Build(runtime));
page.Click("save");
var bounds = page.GetBounds("save");
```

The build is what the app's loop runs between `BeginBuild` and `EndBuild`;
each action builds the frame it causes, and `page.Frame()` builds one nothing
caused. `BibuiPage.Launch(runtime => MyScreen.Build(runtime))` does the same on
a runtime of its own.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
