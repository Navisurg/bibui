using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Bibui.Core.Runtime;

namespace Bibui.Testing;

/// <summary>
/// TCP client for <c>BibuiTestServer</c>. Sends line-delimited JSON commands and
/// returns typed results. All methods block until the server responds.
///
/// Call <see cref="Connect"/> before any other method. Retries automatically
/// while the server is starting up.
///
/// <para>Every request carries the server's token. Give it, or leave it null to
/// read it from the server's token file (<c>BibuiTestServer.TokenFilePath(port)</c>),
/// which the server writes when it starts. Paths given to <see cref="Screenshot"/>
/// are relative to the server's output directory.</para>
/// </summary>
public sealed class WorkflowClient : IDisposable
{
    private readonly string  _host;
    private readonly int     _port;
    private readonly string? _token;
    private string?          _tokenMember;
    private TcpClient?       _tcp;
    private StreamReader?    _reader;
    private StreamWriter?    _writer;
    private readonly object  _sendLock = new();

    public WorkflowClient(string host = "127.0.0.1", int port = BibuiTestServer.DefaultPort, string? token = null)
    {
        _host  = host;
        _port  = port;
        _token = token;
    }

    /// <summary>
    /// Connects to the server, retrying until it is up or retries are exhausted.
    /// With no token given, the token file is read here, so a server still
    /// starting has time to write it.
    /// </summary>
    public void Connect(int retries = 20, int retryDelayMs = 200)
    {
        Exception? last = null;
        for (int i = 0; i < retries; i++)
        {
            try
            {
                var token = _token ?? File.ReadAllText(BibuiTestServer.TokenFilePath(_port)).Trim();
                _tokenMember = "{\"token\":" + JsonSerializer.Serialize(token, WorkflowJson.Default.String) + ",";
                _tcp   = new TcpClient(_host, _port);
                var ns = _tcp.GetStream();
                _reader = new StreamReader(ns, Encoding.UTF8);
                _writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(retryDelayMs);
            }
        }
        throw new Exception($"Could not connect to test server at {_host}:{_port} after {retries} attempts", last);
    }

    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _tcp?.Dispose();
    }

    // ── Transport ──────────────────────────────────────────────────────────

    private void SendOk<TRequest>(TRequest request, JsonTypeInfo<TRequest> requestType)
        => Send(request, requestType);

    private TResult SendResult<TRequest, TResult>(TRequest request, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResult> resultType)
        => Send(request, requestType)["result"].Deserialize(resultType)!;

    private JsonNode Send<TRequest>(TRequest request, JsonTypeInfo<TRequest> requestType)
    {
        if (_writer is null || _reader is null)
            throw new InvalidOperationException("Not connected. Call Connect() first.");

        // Every request is a JSON object; the token goes in as its first member.
        var json = _tokenMember + JsonSerializer.Serialize(request, requestType)[1..];
        JsonNode resp;
        lock (_sendLock)
        {
            _writer.WriteLine(json);
            var line = _reader.ReadLine() ?? throw new InvalidOperationException("Connection closed unexpectedly.");
            resp = JsonNode.Parse(line) ?? throw new InvalidOperationException("Server returned invalid JSON.");
        }
        if (resp["ok"]?.GetValue<bool>() != true)
            throw new Exception($"Test server error: {resp["error"]?.GetValue<string>() ?? "unknown"}");
        return resp;
    }

    // ── Commands ───────────────────────────────────────────────────────────

    private static WorkflowJson J => WorkflowJson.Default;

    public void Ping()
        => SendOk(new CmdRequest("ping"), J.CmdRequest);

    public void Click(string id)
        => SendOk(new IdRequest("click", id), J.IdRequest);

    public void ClickAt(float x, float y)
        => SendOk(new PointRequest("click_at", x, y), J.PointRequest);

    public void Hover(float x, float y)
        => SendOk(new PointRequest("hover", x, y), J.PointRequest);

    public void Scroll(float x, float y, float dy)
        => SendOk(new ScrollRequest("scroll", x, y, dy), J.ScrollRequest);

    public void Screenshot(string path)
        => SendOk(new PathRequest("screenshot", path), J.PathRequest);

    public void Fill(string id, string value)
        => SendOk(new FillRequest("fill", id, value), J.FillRequest);

    public bool IsVisible(string id)
        => SendResult(new IdRequest("is_visible", id), J.IdRequest, J.Boolean);

    public bool IsDisabled(string id)
        => SendResult(new IdRequest("is_disabled", id), J.IdRequest, J.Boolean);

    public string? GetText(string id)
        => SendResult(new IdRequest("get_text", id), J.IdRequest, J.String);

    // ── Polling ────────────────────────────────────────────────────────────

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout
    /// elapses. Throws <see cref="TimeoutException"/> on failure.
    /// </summary>
    public void WaitUntil(
        Func<WorkflowClient, bool> condition,
        string? label        = null,
        TimeSpan? timeout    = null,
        TimeSpan? pollInterval = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(100);

        while (DateTime.UtcNow < deadline)
        {
            if (condition(this)) return;
            Thread.Sleep(interval);
        }

        var t = timeout ?? TimeSpan.FromSeconds(10);
        throw new TimeoutException(label is null
            ? $"WaitUntil condition not met within {t.TotalSeconds:F1}s."
            : $"WaitUntil '{label}' not met within {t.TotalSeconds:F1}s.");
    }
}

// The commands sent to BibuiTestServer, serialised through a source-generated
// context so the client carries no reflection JSON. Property order and
// names are the wire format: {"cmd":"scroll","x":…,"y":…,"dy":…}.
internal sealed record CmdRequest(string Cmd);
internal sealed record IdRequest(string Cmd, string Id);
internal sealed record PointRequest(string Cmd, float X, float Y);
internal sealed record ScrollRequest(string Cmd, float X, float Y, float Dy);
internal sealed record PathRequest(string Cmd, string Path);
internal sealed record FillRequest(string Cmd, string Id, string Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CmdRequest))]
[JsonSerializable(typeof(IdRequest))]
[JsonSerializable(typeof(PointRequest))]
[JsonSerializable(typeof(ScrollRequest))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(FillRequest))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
internal sealed partial class WorkflowJson : JsonSerializerContext;
