# Bibui.Testing

Reading a laid-out bibui tree from a test. A test runs the app's loop itself, a
frame at a time and with no window — `BeginBuild` with the events a mouse and
keyboard would send, the app's build, `EndBuild` — and reads the tree between
frames, from the root the app built into:

```csharp
var at = Nodes.Centre(Nodes.FindById(app.Root, "save")!);
runtime.BeginBuild(window, [
    WindowEvent.Mouse(WindowEventKind.MouseDown, at.X, at.Y, MouseButton.Left),
    WindowEvent.Mouse(WindowEventKind.MouseUp, at.X, at.Y, MouseButton.Left),
]);
app.Build(runtime);
runtime.EndBuild();
```

`Nodes.FindById(root, id)` is the node named `id` under `root`, or null;
`Nodes.Centre(node)` is where a person would click it. Nothing here builds a
frame or keeps a node: the next `BeginBuild` reuses the tree. To draw a frame,
hand `FramePixels` a renderer's `Draw`. To drive a running app instead, over a
socket, see `Bibui.Automation`'s `TestPort`.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
