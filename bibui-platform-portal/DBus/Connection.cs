using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Bibui.Platform.Portal.DBus;

/// <summary>Where the session bus is, from the environment the way libdbus finds it.</summary>
internal static class SessionBus
{
    /// <summary>
    /// The session bus's address: <c>DBUS_SESSION_BUS_ADDRESS</c>, else
    /// <c>unix:path=$XDG_RUNTIME_DIR/bus</c> when that socket exists, else null.
    /// </summary>
    public static string? Address()
    {
        var env = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
        if (!string.IsNullOrEmpty(env)) return env;
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(runtime) && File.Exists(Path.Combine(runtime, "bus")))
            return "unix:path=" + Path.Combine(runtime, "bus");
        return null;
    }

    /// <summary>
    /// The Unix sockets an address names, in order. An address is a list of
    /// <c>transport:key=value,…</c> separated by <c>;</c>, with values
    /// percent-escaped; only <c>unix:path=</c> and <c>unix:abstract=</c> are
    /// sockets this client can open, and the rest are skipped.
    /// </summary>
    public static List<UnixDomainSocketEndPoint> Endpoints(string address)
    {
        var endpoints = new List<UnixDomainSocketEndPoint>();
        foreach (var entry in address.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            if (colon < 0 || entry[..colon] != "unix") continue;
            foreach (var pair in entry[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;
                var key = pair[..eq];
                var value = Unescape(pair[(eq + 1)..]);
                if (value is null) continue;
                if (key == "path") endpoints.Add(new UnixDomainSocketEndPoint(value));
                else if (key == "abstract") endpoints.Add(new UnixDomainSocketEndPoint("\0" + value));
            }
        }
        return endpoints;
    }

    private static string? Unescape(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '%') { bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString())); continue; }
            if (i + 2 >= value.Length || !byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, null, out var b)) return null;
            bytes.Add(b);
            i += 2;
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }
}

/// <summary>
/// A connection to a D-Bus bus over a Unix socket: SASL EXTERNAL, <c>Hello</c>,
/// then method calls awaited by serial and signals handed to
/// <see cref="Signal"/>, all read by one background loop. Nothing blocks a
/// thread; no Unix file descriptors are negotiated.
/// </summary>
internal sealed class DBusConnection : IAsyncDisposable
{
    public const string Bus = "org.freedesktop.DBus";

    [DllImport("libc", ExactSpelling = true)] private static extern uint getuid();

    private readonly Socket _socket;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<DBusMessage>> _pending = new();
    private readonly TaskCompletionSource<Exception> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private uint _serial;
    private Task _reading = Task.CompletedTask;

    /// <summary>The name the bus gave this connection (<c>:1.42</c>).</summary>
    public string UniqueName { get; private set; } = "";

    /// <summary>
    /// Called on the read loop for every signal that reaches this connection:
    /// those its match rules let through, and any sent to it by name. Must not
    /// block.
    /// </summary>
    public Action<DBusMessage>? Signal { get; set; }

    /// <summary>Completes, with why, when the connection is lost or disposed.</summary>
    public Task<Exception> Closed => _closed.Task;

    private DBusConnection(Socket socket) => _socket = socket;

    /// <summary>
    /// Opens the first socket of <paramref name="address"/> that answers,
    /// authenticates as this process's user and says <c>Hello</c>. Throws on
    /// a bus that cannot be reached or refuses.
    /// </summary>
    public static async Task<DBusConnection> ConnectAsync(string address, CancellationToken ct)
    {
        var endpoints = SessionBus.Endpoints(address);
        if (endpoints.Count == 0) throw new IOException($"no Unix socket in the bus address '{address}'");
        Exception? last = null;
        foreach (var endpoint in endpoints)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                var connection = new DBusConnection(socket);
                try
                {
                    await connection.AuthenticateAsync(ct).ConfigureAwait(false);
                    connection._reading = Task.Run(connection.ReadLoop, CancellationToken.None);
                    var hello = await connection.CallAsync(DBusMessage.Call(Bus, "/org/freedesktop/DBus", Bus, "Hello"), ct).ConfigureAwait(false);
                    if (hello.Type == DBusMessageType.Error) throw new IOException($"the bus refused Hello: {hello.ErrorName} {hello.ErrorText()}");
                    connection.UniqueName = (string)hello.ReadBody()[0];
                    return connection;
                }
                catch
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch (Exception e) when (e is SocketException or IOException or DBusFormatException)
            {
                socket.Dispose();
                last = e;
            }
        }
        throw last!;
    }

    private async Task AuthenticateAsync(CancellationToken ct)
    {
        // A NUL byte first (where credentials would be passed), then EXTERNAL
        // with the uid in hex-encoded ASCII: the bus checks it against the
        // socket's peer credentials.
        var uid = Convert.ToHexString(Encoding.ASCII.GetBytes(getuid().ToString(CultureInfo.InvariantCulture)));
        await SendRawAsync(Encoding.ASCII.GetBytes($"\0AUTH EXTERNAL {uid}\r\n"), ct).ConfigureAwait(false);
        var line = await ReadLineAsync(ct).ConfigureAwait(false);
        if (!line.StartsWith("OK ", StringComparison.Ordinal)) throw new IOException($"the bus refused authentication: {line}");
        await SendRawAsync("BEGIN\r\n"u8.ToArray(), ct).ConfigureAwait(false);
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        var one = new byte[1];
        while (line.Count < 1024)
        {
            if (await _socket.ReceiveAsync(one, SocketFlags.None, ct).ConfigureAwait(false) == 0)
                throw new IOException("the bus closed the connection during authentication");
            if (one[0] == '\n' && line.Count > 0 && line[^1] == '\r') return Encoding.ASCII.GetString([.. line])[..^1];
            line.Add(one[0]);
        }
        throw new IOException("the bus sent an authentication line over 1024 bytes");
    }

    private async Task SendRawAsync(byte[] bytes, CancellationToken ct)
    {
        int sent = 0;
        while (sent < bytes.Length)
            sent += await _socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, ct).ConfigureAwait(false);
    }

    /// <summary>Sends <paramref name="message"/> with the next serial, which it returns.</summary>
    public async Task<uint> SendAsync(DBusMessage message, CancellationToken ct)
    {
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            message.Serial = ++_serial;
            await SendRawAsync(message.Encode(), ct).ConfigureAwait(false);
            return message.Serial;
        }
        finally { _sending.Release(); }
    }

    /// <summary>
    /// Sends a method call and waits for its reply: a method return or an
    /// error, both returned. Throws when the connection is lost first, or when
    /// <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken ct)
    {
        var reply = new TaskCompletionSource<DBusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _sending.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            call.Serial = ++_serial;
            _pending[call.Serial] = reply;
            if (_closed.Task.IsCompleted) throw new IOException("the bus connection is closed", _closed.Task.Result);
            await SendRawAsync(call.Encode(), ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(call.Serial, out _);
            throw;
        }
        finally { _sending.Release(); }
        try { return await reply.Task.WaitAsync(ct).ConfigureAwait(false); }
        finally { _pending.TryRemove(call.Serial, out _); }
    }

    private async Task ReadLoop()
    {
        var prefix = new byte[16];
        try
        {
            while (true)
            {
                await ReceiveExactAsync(prefix, _stop.Token).ConfigureAwait(false);
                var data = new byte[DBusMessage.LengthFromPrefix(prefix)];
                prefix.CopyTo(data, 0);
                await ReceiveExactAsync(data.AsMemory(16), _stop.Token).ConfigureAwait(false);
                var message = DBusMessage.Decode(data);
                switch (message.Type)
                {
                    case DBusMessageType.MethodReturn:
                    case DBusMessageType.Error:
                        if (message.ReplySerial is { } serial && _pending.TryRemove(serial, out var waiting))
                            waiting.TrySetResult(message);
                        break;
                    case DBusMessageType.Signal:
                        Signal?.Invoke(message);
                        break;
                    default:
                        break; // nobody calls methods on this client; the bus times such a call out
                }
            }
        }
        catch (Exception e)
        {
            Close(e);
        }
    }

    private async Task ReceiveExactAsync(Memory<byte> into, CancellationToken ct)
    {
        while (into.Length > 0)
        {
            int n = await _socket.ReceiveAsync(into, SocketFlags.None, ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("the bus closed the connection");
            into = into[n..];
        }
    }

    private void Close(Exception why)
    {
        if (!_closed.TrySetResult(why)) return;
        foreach (var serial in _pending.Keys)
            if (_pending.TryRemove(serial, out var waiting))
                waiting.TrySetException(new IOException("the bus connection closed: " + why.Message, why));
    }

    public async ValueTask DisposeAsync()
    {
        Close(new ObjectDisposedException(nameof(DBusConnection)));
        _stop.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch (SocketException) { }
        _socket.Dispose();
        try { await _reading.ConfigureAwait(false); } catch { }
        _stop.Dispose();
        _sending.Dispose();
    }
}
