# Bibui.Views

A Bibui.Commands model's values drawn as bibui widgets, through their lenses. A value is drawn
through the first lens in its lens chain that the app has a drawer for, else by its shape
(fields, a table, rows, a pick), else as text. A node's row of commands, as its kind's `[Row]`
lists them, is drawn as buttons, each built from the context the node is drawn in.

```csharp
using Bibui.Commands;
using Bibui.Views;

var view = UIView.Make(runtime, evalCtx, types, drawers, model, views);   // drawers: lens name → UiDrawer, the app's

// each frame
UIView.BeginFrame(view, frame);
using (view.Context.Push(NodeKinds.Panel, panel).View(panel))   // what is drawn is about this panel, and keeps its view state
    UIView.Draw(view, shown, $"{panel}/shown", Size.Fill);
```

- **A drawer** (`UiDrawer`) has two forms: its line, the value on one row, and its block, its
  full form under it. An app adds its own lenses' drawers to the table it gives `Make`; the
  library's (`PlainDrawer`, `FieldsDrawer`, `TableDrawer`, `RowsDrawer`, `PickDrawer`) cover
  the core lenses.
- **The context** (`view.Context`, a `ContextStack`) says what is being drawn: the nodes it is
  about, by kind, and the node whose view state a lens keeps. Scopes are pushed as drawing
  descends and end with their `using`; nothing is restored by hand.
- **The UI never writes the model directly.** A pick sets its field with `node.set`, and a
  row's button pushes its command, so every change a person makes is a logged command an
  agent could also send. What only the view keeps (a board's zoom, a tree's pan) is view
  state (`ViewStates`), kept by the node it is drawn for.
- **Ids** are each value's place: a node by its handle, a field by its node's handle and key,
  so a test client can click what it read from the model.
