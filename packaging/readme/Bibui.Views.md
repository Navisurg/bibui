# Bibui.Views

A Bibui.Commands model's values drawn as bibui widgets, through their lenses. A value is drawn
through the first lens in its lens chain that the app has a drawer for, else by its shape
(fields, a table, rows, a pick), else as text. A node's row of commands, as its kind's `[Row]`
lists them, is drawn as buttons; the one pressed is the caller's to push, in the context it drew
the row in.

```csharp
using Bibui.Commands;
using Bibui.Views;

var view = UIView.Make(runtime, evalCtx, types, drawers, model.Push, views);   // drawers: lens name → UiDrawer, the app's

// each frame
UIView.BeginFrame(view, frame);
using (model.ContextScope(model.GetCtx() with { Panel = panel, View = panel }))   // what is drawn is about this panel, and keeps its view state
{
    if (UIView.Commands(view, panelNode, model.Offers) is { } pressed) model.Push(pressed.Cmd!);
    UIView.Draw(view, shown, $"{panel}/shown", Size.Fill);
}
```

- **A drawer** (`UiDrawer`) has two forms: its line, the value on one row, and its block, its
  full form under it. An app adds its own lenses' drawers to the table it gives `Make`; the
  library's (`PlainDrawer`, `FieldsDrawer`, `TableDrawer`, `RowsDrawer`, `PickDrawer`) cover
  the core lenses.
- **The context** is the app's (cmdgen's `Context`, on its model's stack): which nodes what is
  drawn is about, and the node whose view state a lens keeps. The app's code sets it in scopes
  (`model.ContextScope`) as it draws, and its lenses read it (`model.GetCtx()`). The library's
  drawers never read or set it.
- **A node's row** (`UIView.Commands`) draws the commands its kind's `[Row]` lists that the model
  offers where it is drawn (`model.Offers`), and says which was pressed. A row's command takes no
  arguments (`OpInfo.Cmd`): the caller pushes it, in the context that holds the node.
- **The UI never writes the model directly.** A pick sets its field with `node.set`, and a
  row's button pushes its command, so every change a person makes is a logged command an
  agent could also send. What only the view keeps (a board's zoom, a tree's pan) is view
  state (`ViewStates`), kept by the node it is drawn for.
- **Ids** are each value's place: a node by its handle, a field by its node's handle and key,
  so a test client can click what it read from the model.
