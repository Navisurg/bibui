// A TestPort with what an app does with it every frame, over bibui's public API, and the app's model
// read by a client (Bibui.Commands' ModelJson).

using System.Buffers;
using System.Text.Json;
using Bibui.Commands;
using System.Text.Json.Serialization.Metadata;
using Bibui.Core;
using Bibui.Core.Draw;
using Bibui.Core.Runtime;
using Bibui.Core.Runtime.Platform;

namespace Bibui.Automation;

/// <summary>
/// A <see cref="TestPort"/> with what an app does with it every frame, done for it: a
/// client's events into the frame's events, screenshots read from the frame and written,
/// the app's commands read with its JSON, the agent's cursor folded from the events and
/// drawn on request. Left to the app is what only it knows: where its commands go and when it
/// has settled. What a client reads of the app is its model, not its runtime.
///
/// A command is pulled with its context: the fields its entry declares, read from beside its arguments
/// against the model's tree (<c>"panel": "#7"</c>); one it declares and the client left out refuses it.
///
/// <code>
/// using var test = TestSession.Open(runtime,
///     new TestPortOptions { Name = "counter", Commands = CmdJson.For(AppJson.Default.Options, AppCmdOps.All) }, screenshots: app.ReadPixels, model: ui);
/// while (app.WaitForFrame())
/// {
///     while (test.TryPull(app.Events, out var cmd, out var ctx)) ui.Push(cmd, ctx);   // begin
///     ...                                                           // the build
///     test.DrawAgent();                                              // optional, last in the build
///     runtime.EndBuild();
///     app.Draw();
///     test.End(settled: !ui.HasPending);                            // end: between Draw and Present
///     app.Present();
/// }
/// </code>
/// </summary>
public sealed class TestSession<TCtx> : IDisposable where TCtx : struct
{
    /// <summary>The port the session drives.</summary>
    public readonly TestPort Port;

    /// <summary>
    /// The agent driving the app, folded from the events it sends; drawn by <see cref="DrawAgent"/>.
    /// <see cref="TryPull"/> sets it connected while a client is: an app that wants it shown
    /// otherwise sets <c>Agent.Connected</c> after pulling.
    /// </summary>
    public AgentCursor Agent;

    private readonly BibuiRuntime _runtime;
    private readonly JsonTypeInfo<Cmd> _commands;
    private readonly Func<RasterPixels, bool>? _screenshots;   // reads the frame drawn (app.ReadPixels); none, and they are refused
    private readonly bool _waitForAnimations;      // answers wait until nothing on screen is still moving
    private bool _agentSettled = true;             // this frame's: false while the cursor is still showing a step
    private string? _shotPath;                     // a screenshot asked for this frame: read in End
    private RectF? _shotRegion;
    private byte[] _shot = [];                     // what it is read into, pinned; kept while the frame's size holds
    private RasterPixels _shotPixels;
    private readonly Model<TCtx>? _model;    // model.get and model.log read it; a command's answer says the generations it made; a command's context is read against its tree
    private readonly ArrayBufferWriter<byte> _answer = new(4096);   // a model.* answer is written here, then handed to the port
    private readonly Utf8JsonWriter _answerJson;
    private long _cmdFrom = -1;                    // the model's generation when the command or events in hand were pulled; -1 with none
    private readonly Schema _schema;               // model.schema answers it, each kind with its row's commands; names the kinds in model.get and the change output (an app with none: the library's alone)
    private readonly IReadOnlyList<OpInfo> _ops;
    private readonly Func<string>? _text;          // model.text answers it: the app drawn as text, through its lenses
    private readonly ArrayBufferWriter<byte> _stripped = new(256);   // a command's request without its context's fields: what its JSON reads

    internal TestSession(BibuiRuntime runtime, TestPort port, JsonTypeInfo<Cmd> commands,
        Func<RasterPixels, bool>? screenshots, AgentCursor? agent, bool waitForAnimations, Model<TCtx>? model,
        Schema? schema, IReadOnlyList<OpInfo> ops, Func<string>? text)
    {
        (_runtime, Port, _commands, _screenshots, _waitForAnimations, _model) = (runtime, port, commands, screenshots, waitForAnimations, model);
        (_schema, _ops, _text) = (schema ?? Schemas.Make(), ops, text);
        _answerJson = new Utf8JsonWriter(_answer);
        Agent = agent ?? AgentCursor.Start("Agent", UiColor.Hex("#3B82F6"));
    }

    public void Dispose() => Port.Dispose();

    /// <summary>
    /// The top of the frame: the client's request, if one is published. Events go into
    /// <paramref name="events"/> and fold into <see cref="Agent"/>, a screenshot waits for <see cref="End"/>;
    /// true with one of the app's own commands, read with its JSON, and the context it is sent in (the
    /// fields its entry declares, read beside its arguments), to push where its commands go. One it cannot
    /// read, or that lacks a field it declares, is refused with the reason.
    /// </summary>
    public bool TryPull(List<WindowEvent> events, out Cmd cmd, out TCtx ctx)
    {
        Agent.Connected = Port.HasClient;
        ctx = default;
        while (Port.TryPullCmd(out var request))
            switch (request.Kind)
            {
                case TestCmdKind.Events:
                    events.AddRange(request.Events);
                    _cmdFrom = _model?.Gen ?? -1;   // a click or a key may run a command: its answer says what changed
                    Agent = AgentCursor.FromWindowEvents(Agent, request.Events);
                    break;

                case TestCmdKind.Screenshot:
                    // Read from this frame once it is drawn, in End.
                    if (_screenshots is null) Port.RefuseCmd("This app takes no screenshots.");
                    else (_shotPath, _shotRegion) = (request.Path, request.Region);
                    break;

                case TestCmdKind.App:
                    if (_model is not null && AnswerModel(_model, request.Json)) break;
                    try
                    {
                        if (ReadContext(request.Json, out var json, out ctx) is { } why) { Port.RefuseCmd(why); break; }
                        cmd = JsonSerializer.Deserialize(json, _commands)!;
                        _cmdFrom = _model?.Gen ?? -1;
                        return true;
                    }
                    catch (JsonException e) { Port.RefuseCmd(Unread(request.Json, e)); }
                    catch (NotSupportedException e) { Port.RefuseCmd($"A command this app cannot read from JSON: {e.Message}"); }   // an argument of a type JSON cannot make
                    break;
            }
        cmd = default!;
        return false;
    }

    // A command's context, from beside its arguments: the fields its entry declares (OpInfo.Context), read
    // against the model's tree, and the request without them, for its JSON, which takes only arguments.
    // Why not, or null: a field that does not read, or one it declares that the client left out.
    private string? ReadContext(ReadOnlySpan<byte> request, out ReadOnlySpan<byte> json, out TCtx ctx)
    {
        json = request;
        ctx = default;
        var reader = new Utf8JsonReader(request);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("$cmd", out var c) || c.ValueKind != JsonValueKind.String) return null;
        OpInfo? op = null;
        foreach (var o in _ops) if (o.Wire == c.GetString()) { op = o; break; }
        if (op is null || op.Context.Length == 0) return null;
        if (_model?.Context is { } context)
        {
            if (context.Read(op, root, _model.Tree, ref ctx) is { } why) return $"{op.Wire}: {why}";
            if (context.Missing(ctx, op) is { } missing) return $"{op.Wire} reads {missing.Name} from the context: send it beside its arguments (\"{missing.Name}\": \"#4\").";
        }
        _stripped.ResetWrittenCount();
        using (var w = new Utf8JsonWriter(_stripped))
        {
            w.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                bool declared = false;
                foreach (var f in op.Context) declared |= string.Equals(f.Name, property.Name, StringComparison.OrdinalIgnoreCase);
                if (!declared) property.WriteTo(w);
            }
            w.WriteEndObject();
        }
        json = _stripped.WrittenSpan;
        return null;
    }

    // Why a request did not read as a command: one this app has whose arguments do not read (an
    // expression that does not parse: the parser's caret under where it stopped), or none it has.
    private string Unread(ReadOnlySpan<byte> request, JsonException e)
    {
        string? name = null;
        try
        {
            var reader = new Utf8JsonReader(request);
            using var doc = JsonDocument.ParseValue(ref reader);
            if (doc.RootElement.TryGetProperty("$cmd", out var c) && c.ValueKind == JsonValueKind.String) name = c.GetString();
        }
        catch (JsonException) { }
        foreach (var op in _ops)
            if (op.Wire == name) return $"{name}: its arguments do not read: {e.Message}";
        return $"Not a command of this app: {e.Message}";
    }

    // model.get and model.log, answered here rather than handed to the app as commands: they read
    // the model and change nothing. False for any other request.
    private bool AnswerModel(Model<TCtx> model, ReadOnlySpan<byte> request)
    {
        var reader = new Utf8JsonReader(request);
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        string? verb = root.TryGetProperty("$cmd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (verb is not ("model.get" or "model.log" or "model.schema" or "model.text")) return false;

        _answer.ResetWrittenCount();
        _answerJson.Reset(_answer);
        string? error = null;
        if (verb == "model.get")
        {
            string path = root.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
            int depth = root.TryGetProperty("depth", out var d) && d.TryGetInt32(out var n) ? n : 16;
            error = ModelJson.Get(_answerJson, model, _schema, path, depth);
        }
        else if (verb == "model.log")
        {
            long since = root.TryGetProperty("since", out var g) && g.TryGetInt64(out var n) ? n : 0;
            ModelJson.Log(_answerJson, model, _commands, _ops, since);
        }
        else if (verb == "model.text")
        {
            if (_text is null) error = "This app draws no text.";
            else
            {
                _answerJson.WriteStartObject();
                _answerJson.WriteString("text", _text());
                _answerJson.WriteEndObject();
            }
        }
        else ModelJson.Schema(_answerJson, _schema);
        _answerJson.Flush();
        if (error is not null) Port.RefuseCmd(error);
        else Port.ResultCmd(_answer.WrittenSpan);
        return true;
    }

    /// <summary>
    /// The agent's cursor, under the parent in scope: call it last in the build, at the
    /// root, so it is drawn over everything. With <paramref name="pace"/>, the answer waits
    /// until the cursor has shown each step, so a person watching can follow; without, it
    /// is drawn but holds nothing. <paramref name="size"/> scales the pointer.
    /// </summary>
    public void DrawAgent(float size = 1f, bool pace = true)
    {
        bool shown = AgentCursorUi.Build(_runtime, Agent, size);
        if (pace) _agentSettled = shown;
    }

    /// <summary>
    /// The end of the frame, between <c>app.Draw</c> and <c>app.Present</c>: a screenshot
    /// asked for is read from the frame drawn and written. The request is answered once
    /// <paramref name="settled"/> (nothing the app has pending) and the agent's cursor has
    /// shown it, and, if the session waits for them, the animations have ended.
    /// </summary>
    public void End(bool settled)
    {
        if (_shotPath is { } path)
        {
            var (w, h) = (_runtime.Window.Width, _runtime.Window.Height);
            if (_shotPixels.Width != w || _shotPixels.Height != h)
            {
                _shot = GC.AllocateArray<byte>(Math.Max(1, w * h * 4), pinned: true);
                _shotPixels = new RasterPixels(System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(_shot, 0), w, h, w * 4);
            }
            try
            {
                if (_screenshots!(_shotPixels)) File.WriteAllBytes(path, FramePixels.Png(_shot, w, h, _runtime.ScaleFactor, _shotRegion));
                else Port.RefuseCmd("The window changed size as the screenshot was taken: ask again.");
            }
            catch (IOException e) { Port.RefuseCmd($"The screenshot was not written: {e.Message}"); }
            catch (UnauthorizedAccessException e) { Port.RefuseCmd($"The screenshot was not written: {e.Message}"); }
            _shotPath = null;
        }

        bool still = !_waitForAnimations || !_runtime.IsAnimating;
        bool answering = settled && _agentSettled && still;
        if (answering && _cmdFrom >= 0 && _model is not null)
        {
            // What the command (or the input) did: the generations after from, up to gen, and what
            // changed across them; none when from == gen.
            _answer.ResetWrittenCount();
            _answerJson.Reset(_answer);
            // The answer: what changed, and what the commands answered (an eval's value), joined in
            // the order they ran; then the outputs are this answer's, and go.
            _answerJson.WriteStartObject();
            ModelJson.Diff(_answerJson, _model, _schema, _cmdFrom);
            if (_model.Outputs.Count > 0) _answerJson.WriteString("output", string.Concat(_model.Outputs));
            _answerJson.WriteEndObject();
            _answerJson.Flush();
            Port.ResultCmd(_answer.WrittenSpan);
            _model.Outputs.Clear();
            _cmdFrom = -1;
        }
        Port.AnswerCmds(answering);
        _agentSettled = true;
    }
}

/// <summary>Opens a <see cref="TestSession{TCtx}"/>: its context's type is the model's.</summary>
public static class TestSession
{
    /// <summary>
    /// Opens a port with <paramref name="options"/>, whose <c>Commands</c> is the app's commands'
    /// JSON (read here, and listed by <c>test.ops</c>): its source-generated JSON, so nothing is
    /// reflected.
    /// <paramref name="screenshots"/> reads the frame the window drew (<c>app.ReadPixels</c>), so a
    /// screenshot is what the window shows, textures too; without it they are refused. <paramref name="agent"/> names and colours the agent's cursor. With
    /// <paramref name="waitForAnimations"/>, an answer also waits until nothing on screen
    /// is moving (<c>runtime.IsAnimating</c>), so a screenshot taken next is of the
    /// layout settled, not of it mid-glide: for scripts that compare screenshots. With
    /// <paramref name="model"/>, a client reads it with <c>model.get</c> and <c>model.log</c>, and
    /// each of the app's commands, and each frame of input, answers the generations it made and
    /// what changed in them (<c>{"from":40,"gen":42,"diff":[...]}</c>).
    /// With <paramref name="schema"/>, <c>model.schema</c> answers what each kind of node is. With
    /// <paramref name="text"/>, <c>model.text</c> answers the app drawn as text (its TextView).
    /// </summary>
    public static TestSession<TCtx> Open<TCtx>(BibuiRuntime runtime, TestPortOptions options,
        Func<RasterPixels, bool>? screenshots = null, AgentCursor? agent = null, bool waitForAnimations = false,
        Model<TCtx>? model = null, Schema? schema = null, Func<string>? text = null) where TCtx : struct
    {
        ArgumentNullException.ThrowIfNull(options);
        var commands = options.Commands as JsonTypeInfo<Cmd>
            ?? throw new ArgumentException("options.Commands is the JSON of Cmd: CmdJson.For, the app's commands and the library's.", nameof(options));
        return new(runtime, TestPort.Open(runtime, options), commands, screenshots, agent, waitForAnimations, model,
            schema, [.. options.Ops ?? [], .. BuiltinCmd.Ops], text);
    }
}
