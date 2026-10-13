# Bibui.Testing

Headless testing for bibui apps. A `BibuiPage` holds a runtime with no window
and renders it with Skia; a test finds nodes by id, reads their laid-out
bounds and text, clicks, types and presses keys, and takes screenshots.
`WorkflowClient` drives a running app the same way over its test server.

```csharp
var runtime = new BibuiRuntime(new SkiaRasterBackend());
runtime.Mount(() => MyScreen.Build(runtime));

using var page = BibuiPage.From(runtime);
page.Click("save");
var bounds = page.GetBounds("save");
```

`BibuiPage.Launch` does the same from a build function alone.

`WorkflowClient` talks to a `BibuiTestServer` the app starts on purpose, for
example with `BibuiTestServer.StartIfEnabled(runtime)`, which starts one only
when `BIBUI_TEST_SERVER=1`. The client sends the server's token with every
request, reading it from the file the server writes for the user alone, and
screenshot and recording paths are relative to the server's output directory
(`BIBUI_TEST_SERVER_OUT`):

```csharp
using var client = new WorkflowClient();   // 127.0.0.1:44334, token from its file
client.Connect();
client.Click("save");
client.Screenshot("after-save.png");       // written in the server's output directory
```

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
