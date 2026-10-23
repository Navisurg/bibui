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
using var cpu = new SkiaCpuRenderer();   // what screenshots are drawn with: any renderer into memory

while (app.BeginFrame())
{
    while (port.TryPullCmd(out var cmd))
        switch (cmd.Kind)
        {
            case TestCmdKind.Events: app.Events.AddRange(cmd.Events); break;
            case TestCmdKind.Screenshot: File.WriteAllBytes(cmd.Path!, FramePixels.Png(runtime, cpu.Draw)); break;
            case TestCmdKind.App: /* the app's own command: cmd.Json */ break;
        }

    runtime.BeginBuild(app.Events);
    Build();
    runtime.EndBuild();
    port.AnswerCmds();
    app.EndFrame();
}
```

- **The socket** is `$XDG_RUNTIME_DIR/bibui/<name>-<pid>.sock`, in a folder only
  the user can enter, readable only by the user. `port.SocketPath` says where.
- **Events** are what a window would report; adding them to the frame's events
  is all it takes.
- **Screenshots are the app's**: at the top of the frame the last frame's draw
  list is still the runtime's, so the frame on screen is drawn, with any renderer
  that draws into memory (`SkiaCpuRenderer`, `ManagedRenderer`). An app that takes
  none refuses them: `port.RefuseCmd("…")`.
- **The app's own commands** come as bytes (`cmd.Json`), for the app's own
  deserializer; one it cannot read is refused with `port.RefuseCmd`. With
  `TestPortOptions.Commands` (its source-generated `JsonTypeInfo`), `test.schema`
  describes them.
- **Answers wait for the app to settle**: `port.AnswerCmds(settled, state, stateType)`
  answers events and app commands only once `settled`, when nothing they set off
  is still pending, and `test.state` with the app's state.

The port has one thread of its own, which only reads the socket and hands each
line to the loop. Everything else happens on the loop: only it writes to the
socket, the tree and the state are read between frames, and nothing is shared.

## From a client

One JSON line per request, one answer line back:

| Request | Answer |
|---|---|
| `{"$cmd":"test.tree"}` | the UI tree the last frame laid out: each node's kind, id, rect, text, and claims |
| `{"$cmd":"test.state"}` | the app's state |
| `{"$cmd":"test.schema"}` | a JSON schema of every request: the `test.*` ones and the app's |
| `{"$cmd":"test.events","events":[…]}` | `{"ok":true}`, once the events and what they set off have run |
| `{"$cmd":"test.screenshot","path":"/tmp/frame.png"}` | `{"ok":true}`, the PNG written by the app |
| an app command, e.g. `{"$cmd":"counter.set","Value":10}` | `{"ok":true}`, once it has run |

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
echo '{"$cmd":"test.state"}' | socat - UNIX-CONNECT:$S
```

Several lines may be sent without waiting; they are answered in order.

A box's `claims` are its `InteractionKinds`, one name per bit: `LeftPress` is
clickable, `Disabled` is disabled, `ViewScrollY` scrolls its view, and `Clip`
cuts its children to it, so a node is in view when its centre is inside every
ancestor claiming `Clip`.

`bibui-port` (`tools/bibui-port`) is a native command-line client: it finds the
socket, builds the JSON, and clicks a node by id, scrolled into view first.

```sh
bibui-port tree --depth 2          # an outline: kind, id, rect, claims, text
bibui-port find "Add one"          # the nodes holding a text, and the id to click
bibui-port click add
bibui-port cmd counter.set Value=10
bibui-port state
```

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
