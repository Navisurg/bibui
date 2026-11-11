using System.Net.Sockets;
using System.Text;
using Bibui.Platform.Portal.DBus;

namespace Bibui.PortalTests;

/// <summary>
/// A bus of one client on a socket in a temporary folder: it authenticates
/// the client, answers <c>Hello</c> and <c>AddMatch</c> as the bus does, and
/// hands every other call to <see cref="Portal"/>, which plays the portal.
/// Everything the client sends is kept in <see cref="Received"/>.
/// </summary>
internal sealed class FakeBus : IAsyncDisposable
{
    public const string ClientName = ":1.7", PortalName = ":1.2";

    private readonly string _dir;
    private readonly Socket _listener;
    private readonly Task _serving;
    private Socket? _client;
    private uint _serial;
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>The bus's address, for <c>FileChooser.ChooseAsync(options, address, ct)</c>.</summary>
    public string Address { get; }

    /// <summary>The client's AUTH line, as sent.</summary>
    public string AuthLine { get; private set; } = "";

    /// <summary>What to answer AUTH with; anything but OK refuses the client.</summary>
    public string AuthReply { get; set; } = "OK 0123456789abcdef0123456789abcdef";

    /// <summary>Every message the client sent, in order.</summary>
    public List<DBusMessage> Received { get; } = [];

    /// <summary>The match rules the client added.</summary>
    public List<string> Rules { get; } = [];

    /// <summary>Plays the portal: called for each call not to the bus itself.</summary>
    public Func<DBusMessage, Task>? Portal { get; set; }

    /// <summary>Completes with each call the portal is handed.</summary>
    public TaskCompletionSource<DBusMessage> FirstPortalCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeBus()
    {
        _dir = Directory.CreateTempSubdirectory("bibui-portal-bus-").FullName;
        var path = Path.Combine(_dir, "bus");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(1);
        Address = "unix:path=" + path + ",guid=0123456789abcdef0123456789abcdef";
        _serving = Task.Run(Serve);
    }

    private async Task Serve()
    {
        try
        {
            _client = await _listener.AcceptAsync();
            AuthLine = await ReadLine();
            await SendRaw(Encoding.ASCII.GetBytes(AuthReply + "\r\n"));
            if (!AuthReply.StartsWith("OK")) { _client.Close(); return; }
            if (await ReadLine() != "BEGIN") throw new Exception("no BEGIN");
            var prefix = new byte[16];
            while (true)
            {
                await ReceiveExact(prefix);
                var data = new byte[DBusMessage.LengthFromPrefix(prefix)];
                prefix.CopyTo(data, 0);
                await ReceiveExact(data.AsMemory(16));
                var m = DBusMessage.Decode(data);
                lock (Received) Received.Add(m);
                if (m.Destination == DBusConnection.Bus)
                {
                    if (m.Member == "Hello") await Reply(m, "s", w => w.String(ClientName), DBusConnection.Bus);
                    else if (m.Member == "AddMatch")
                    {
                        lock (Rules) Rules.Add((string)m.ReadBody()[0]);
                        await Reply(m, "", null, DBusConnection.Bus);
                    }
                    else await Error(m, "org.freedesktop.DBus.Error.UnknownMethod", "not on this bus");
                }
                else
                {
                    FirstPortalCall.TrySetResult(m);
                    if (Portal is { } portal) await portal(m);
                    else await Error(m, "org.freedesktop.DBus.Error.ServiceUnknown", $"The name {m.Destination} was not provided by any .service files");
                }
            }
        }
        catch (Exception) when (_client is null || !_client.Connected) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    /// <summary>The bytes of a method return to <paramref name="call"/>.</summary>
    public byte[] ReplyBytes(DBusMessage call, string signature, Action<DBusWriter>? body, string sender = PortalName)
    {
        var w = new DBusWriter();
        body?.Invoke(w);
        return Stamp(new DBusMessage
        {
            Type = DBusMessageType.MethodReturn,
            ReplySerial = call.Serial,
            Destination = ClientName,
            Sender = sender,
            Signature = signature,
            Body = w.ToArray(),
        });
    }

    public Task Reply(DBusMessage call, string signature, Action<DBusWriter>? body, string sender = PortalName) =>
        SendRaw(ReplyBytes(call, signature, body, sender));

    public Task Error(DBusMessage call, string name, string text)
    {
        var w = new DBusWriter();
        w.String(text);
        return SendRaw(Stamp(new DBusMessage
        {
            Type = DBusMessageType.Error,
            ReplySerial = call.Serial,
            ErrorName = name,
            Destination = ClientName,
            Sender = PortalName,
            Signature = "s",
            Body = w.ToArray(),
        }));
    }

    /// <summary>The bytes of a <c>Response</c> signal on <paramref name="path"/>.</summary>
    public byte[] ResponseBytes(string path, uint code, string sender, params string[] uris)
    {
        var m = ChooserTests.Response(code, uris);
        return Stamp(new DBusMessage
        {
            Type = DBusMessageType.Signal,
            Path = path,
            Interface = m.Interface,
            Member = m.Member,
            Sender = sender,
            Signature = m.Signature,
            Body = m.Body,
        });
    }

    private byte[] Stamp(DBusMessage m)
    {
        m.Serial = Interlocked.Increment(ref _serial);
        return m.Encode();
    }

    public async Task SendRaw(byte[] bytes)
    {
        await _sending.WaitAsync();
        try
        {
            int sent = 0;
            while (sent < bytes.Length) sent += await _client!.SendAsync(bytes.AsMemory(sent), SocketFlags.None);
        }
        finally { _sending.Release(); }
    }

    /// <summary>Drops the client, as a bus that went away would.</summary>
    public void Kill()
    {
        try { _client?.Shutdown(SocketShutdown.Both); } catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        _client?.Close();
    }

    private async Task<string> ReadLine()
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            if (await _client!.ReceiveAsync(one, SocketFlags.None) == 0) throw new IOException("client gone");
            if (one[0] == '\n' && line.Count > 0 && line[^1] == '\r') return Encoding.ASCII.GetString([.. line])[..^1];
            line.Add(one[0]);
        }
    }

    private async Task ReceiveExact(Memory<byte> into)
    {
        while (into.Length > 0)
        {
            int n = await _client!.ReceiveAsync(into, SocketFlags.None);
            if (n == 0) throw new IOException("client gone");
            into = into[n..];
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        _listener.Close();
        try { await _serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
        Directory.Delete(_dir, recursive: true);
    }
}
