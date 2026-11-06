# Bibui.Automation

A test port for a running bibui app: a test, a script or an agent drives the app
over a Unix socket only the user can reach. It reads the UI tree and the app's
state, sends input events and the app's own commands, and asks for screenshots.
An app that does not reference this package ships none of it.

## In the app

Open a `TestPort`, and call two things a frame: `TryPullCmd` at the top, and
`AnswerCmds` at the end.

```csharp
using Bibui.Automation;

using var port = TestPort.Open(runtime, new TestPortOptions { Name = "counter" });
string? shot = null; RectF? region = null;   // a screenshot asked for, taken from this frame

app.Frame = Frame;
while (app.WaitForFrame()) Frame();

void Frame()
{
    while (port.TryPullCmd(out var cmd))
        switch (cmd.Kind)
        {
            case TestCmdKind.Events: app.Events.AddRange(cmd.Events); break;
            case TestCmdKind.Screenshot: (shot, region) = (cmd.Path, cmd.Region); break;
            case TestCmdKind.App: /* the app's own command: cmd.Json */ break;
        }

    runtime.BeginBuild(app.Window.Frame, app.Events);
    Build();
    runtime.EndBuild();
    app.Draw();
    if (shot is not null)   // the frame drawn, read before it is shown
    {
        var (w, h) = (runtime.Window.Width, runtime.Window.Height);
        var bgra = GC.AllocateArray<byte>(w * h * 4, pinned: true);
        var into = new RasterPixels(Marshal.UnsafeAddrOfPinnedArrayElement(bgra, 0), w, h, w * 4);
        if (app.ReadPixels(into)) File.WriteAllBytes(shot, FramePixels.Png(bgra, w, h, runtime.ScaleFactor, region));
        else port.RefuseCmd("The window changed size: ask again.");
        shot = null;
    }
    app.Present();
    port.AnswerCmds();
}
```

- **The socket** is `$XDG_RUNTIME_DIR/bibui/<name>-<pid>.sock`, in a folder only
  the user can enter, readable only by the user. `port.SocketPath` says where.
- **Events** are what a window would report; adding them to the frame's events
  is all it takes.
- **Screenshots are the app's**: the frame the window shows, read between
  `app.Draw()` and `app.Present()` (`app.ReadPixels`), so it holds what the
  window's renderer drew, textures too. An app that takes none refuses them:
  `port.RefuseCmd("…")`.
- **The app's own commands** come as bytes (`cmd.Json`), for the app's own
  deserializer; one it cannot read is refused with `port.RefuseCmd`. With
  `TestPortOptions.Commands` (its source-generated `JsonTypeInfo`), `test.ops`
  lists them: an entry per command with its `kind`, `wire` name and `input` (its
  arguments, as the app's JSON reads them). With `TestPortOptions.Ops` (their
  table, from Bibui.Commands) each entry also says what the command is: its
  `label`, `description`, its arguments' descriptions, and whether it only
  reads (`readOnly`), the shape a CLI or an MCP tool needs.
  `bibui-port ops` prints it, and `bibui-port cmd` checks a command against it
  before sending it.
- **Answers wait for the app to settle**: `port.AnswerCmds(settled)` answers
  events and app commands only once `settled`, when nothing they set off is
  still pending. What a command or input did, the app says in its answer:
  `port.ResultCmd(json)` before `AnswerCmds` makes it `{"ok":true,"result":…}`.
  There is no request for the app's whole state: a client reads what the app
  chooses to answer, not its runtime.
- **An app with a Bibui.Commands model** can open a `TestSession` instead of a
  bare port. It does the per-frame work for the app (events into the frame,
  screenshots, the agent cursor) and answers four requests from the model
  itself: `model.get` (a node and what is under it, at the current generation
  or a logged one), `model.log` (the commands that made the generations),
  `model.schema` (each kind of node, its fields and its row of commands) and
  `model.text` (the app drawn as text through its lenses). A command's answer
  says what it changed in the model.

The port has one thread of its own, which only reads the socket and hands each
line to the loop. Everything else happens on the loop: only it writes to the
socket, the tree is read between frames, and nothing is shared.

## From a client

One JSON line per request, one answer line back:

| Request | Answer |
|---|---|
| `{"$cmd":"test.tree"}` | the UI tree the last frame laid out: each node's kind, id, rect, text, and claims |
| `{"$cmd":"test.ops"}` | what the app can be asked to do, an entry per operation: `kind`, `wire`, `label`, `description`, `readOnly`, `input` |
| `{"$cmd":"test.events","events":[…]}` | `{"ok":true}`, once the events and what they set off have run; with the app's `result`, if it gives one |
| `{"$cmd":"test.screenshot","path":"/tmp/frame.png"}` | `{"ok":true}`, the PNG written by the app; with `"id"`, only that node's rect (`cmd.Region`) |
| an app command, e.g. `{"$cmd":"counter.set","Value":10}` | `{"ok":true}`, once it has run; with the app's `result`, if it gives one |
| `{"$cmd":"model.get","path":"panels/7","depth":2}` | with a `TestSession` and a model: the node at the path and what is under it |
| `{"$cmd":"model.log","since":12}` | with a model: the commands that made each generation after `since` |
| `{"$cmd":"model.schema"}` | with a schema: each kind of node, its fields and its row |
| `{"$cmd":"model.text"}` | with a text view: the app drawn as text, through its lenses |

An event is `{"kind":…}` with what its kind needs: `mouse_move` (`x`, `y`),
`mouse_down` and `mouse_up` (`x`, `y`, `button`: `left`, `right` or `middle`),
`scroll` (`x`, `y`, `dy`, and `dx`), `key_down` and `key_up` (`key`, a bibui
`Key` name), `key_char` (`char`) and `focus_lost`; any may carry `modifiers`
(`["ctrl","shift"]`). Coordinates are logical pixels. One request is one frame
of events: a click is three requests.

A client needs no library. A click on the node `add`, from a shell:

```sh
S=$XDG_RUNTIME_DIR/bibui/counter-<pid>.sock
read x y < <(echo '{"$cmd":"test.tree"}' | socat - UNIX-CONNECT:$S \
  | jq -r '.. | objects | select(.id? == "add") | "\(.x + .w/2) \(.y + .h/2)"')
for kind in mouse_move mouse_down mouse_up; do
  echo "{\"\$cmd\":\"test.events\",\"events\":[{\"kind\":\"$kind\",\"x\":$x,\"y\":$y,\"button\":\"left\"}]}"
done | socat - UNIX-CONNECT:$S
```

Several lines may be sent without waiting; they are answered in order.

A box's `claims` are its `InteractionKinds`, one name per bit: `LeftPress` is
clickable, `Disabled` is disabled, `ViewScrollY` scrolls its view, and `Clip`
cuts its children to it, so a node is in view when its centre is inside every
ancestor claiming `Clip`.

`bibui-port` (`tools/bibui-port`) is a native command-line client: it finds the
socket, builds the JSON, and acts on a node by id, once it holds still and
scrolled into view first.

```sh
bibui-port tree --depth 2              # an outline: kind, id, rect, claims, text
bibui-port find "Add one"              # the nodes holding a text, and the id to click
bibui-port click add                   # --right, --middle
bibui-port hover help-button           # a move, no press
bibui-port drag slider 320 140         # from a node, or: drag x y x y
bibui-port scroll list -3
bibui-port type "Hello"                # into what has the keyboard
bibui-port key Left --shift --repeat 5
bibui-port ops                         # the app's commands: what each does, and what it takes
bibui-port cmd counter.set value=10    # checked against ops first; text and expressions sent as written
bibui-port get panels/7 --outline      # the model, a line per node, with its row of commands
bibui-port log 12                      # the commands since generation 12
bibui-port schema                      # each kind of node: its fields and its row
bibui-port text                        # the app drawn as text
bibui-port screenshot field.png --id email
```

## Making an app easy to drive

A client finds what to act on in the tree, and the tree holds only what the
runtime knows: boxes and texts, where they were laid out, and their claims. So:

- **Give every box a client acts on an id**, stable from frame to frame, naming
  what it is for (`settings-save`, `nav-inbox`), with its label as a text inside
  it: `find "Save"` then leads to the id.
- **Keyboard focus, a text field's caret and selection, and what kind of control
  a box is are the app's**, not the runtime's: a text field is edited when its
  caller says so. An app whose client should check them answers them from
  its own commands (`ResultCmd`).

## Showing the agent

`AgentCursor` (in `Bibui.Core.Runtime`) draws what an agent driving the app does,
for a person watching: an arrow at its pointer, a tag with its name, a ring on each
click. Nothing is kept: the pointer is folded from the events the agent sends, and
it is shown while `port.HasClient`.

```csharp
var agent = AgentCursor.Start("Agent", UiColor.Hex("#3B82F6"));    // before the loop
...
case TestCmdKind.Events:
    app.Events.AddRange(cmd.Events);
    agent = AgentCursor.FromWindowEvents(agent, cmd.Events);
    break;
...
agent.Connected = port.HasClient;                                  // last in the build
bool agentSettled = AgentCursorUi.Build(runtime, agent, size: 1.5f);
runtime.EndBuild();
port.AnswerCmds(agentSettled);
```

It glides to each point and shows each click, and `Build` says when it has: held
until then, an answer makes the agent's next event wait for the person watching.
`bibui-port move <x> <y>` moves it without a click.

Part of bibui, a code-first native UI library for .NET:
<https://github.com/Navisurg/bibui>. Licensed under Apache-2.0.
