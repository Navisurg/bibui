# Bibui.Testing

Headless testing for bibui apps. A `BibuiPage` holds a runtime with no window
and draws it with Skia on the CPU, unless `BibuiPage.From(runtime, build, draw)`
is given another `DrawToPixels`; a test finds nodes by id, reads their laid-out
bounds and text, clicks, types and presses keys, and takes screenshots.
`WorkflowClient` drives a running app the same way over its test server.

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

`WorkflowClient` talks to a `BibuiTestServer` the app starts on purpose, for
example with `BibuiTestServer.StartIfEnabled(runtime, draw: cpu.Draw)` (`cpu`
a renderer that draws into memory), which starts one only when
`BIBUI_TEST_SERVER=1`. The app's loop steps it once a frame; while a command
runs, the server is the keyboard and mouse:

```csharp
runtime.BeginBuild(server?.Step(window.Events) ?? window.Events);
```

Its screenshots are drawn with the function you pass, on the
loop. The client sends the server's token with every
request, reading it from the file the server writes for the user alone, and
screenshot paths are relative to the server's output directory
(`BIBUI_TEST_SERVER_OUT`):

```csharp
using var client = new WorkflowClient();   // 127.0.0.1:44334, token from its file
client.Connect();
client.Click("save");
client.Screenshot("after-save.png");       // written in the server's output directory
```

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
