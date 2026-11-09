using System.Buffers.Binary;

namespace Bibui.Platform.Portal.DBus;

internal enum DBusMessageType : byte { MethodCall = 1, MethodReturn = 2, Error = 3, Signal = 4 }

/// <summary>
/// One D-Bus message: the header fields this client reads or writes, and the
/// body as written bytes under <see cref="Signature"/>. Encoded little-endian;
/// decoded in either byte order.
/// </summary>
internal sealed class DBusMessage
{
    /// <summary>The spec's ceiling on a whole message.</summary>
    public const int MaxLength = 128 * 1024 * 1024;

    private const byte FieldPath = 1, FieldInterface = 2, FieldMember = 3, FieldErrorName = 4,
        FieldReplySerial = 5, FieldDestination = 6, FieldSender = 7, FieldSignature = 8, FieldUnixFds = 9;

    public DBusMessageType Type { get; init; }
    public byte Flags { get; init; }
    public uint Serial { get; set; }
    public string? Path { get; init; }
    public string? Interface { get; init; }
    public string? Member { get; init; }
    public string? ErrorName { get; init; }
    public uint? ReplySerial { get; init; }
    public string? Destination { get; init; }
    public string? Sender { get; init; }

    /// <summary>The body's signature, empty for no body.</summary>
    public string Signature { get; init; } = "";

    /// <summary>The body, written from an 8-aligned offset (<see cref="DBusWriter"/>).</summary>
    public byte[] Body { get; init; } = [];

    /// <summary>Byte order of <see cref="Body"/>: true when the sender wrote it big-endian.</summary>
    public bool BigEndian { get; init; }

    /// <summary>A method call to <paramref name="destination"/>; the serial is set when it is sent.</summary>
    public static DBusMessage Call(string destination, string path, string @interface, string member,
        string signature = "", DBusWriter? body = null) => new()
    {
        Type = DBusMessageType.MethodCall,
        Destination = destination,
        Path = path,
        Interface = @interface,
        Member = member,
        Signature = signature,
        Body = body?.ToArray() ?? [],
    };

    /// <summary>Reads the body: one value per complete type of <see cref="Signature"/>.</summary>
    public object[] ReadBody() => new DBusReader(Body, 0, Body.Length, BigEndian).ReadAll(Signature);

    /// <summary>The error's first argument, its message, when it carries one.</summary>
    public string ErrorText()
    {
        if (Signature.Length == 0 || Signature[0] != 's') return "";
        try { return (string)ReadBody()[0]; }
        catch (DBusFormatException) { return ""; }
    }

    /// <summary>The message as bytes on the wire.</summary>
    public byte[] Encode()
    {
        var w = new DBusWriter();
        w.Byte((byte)'l');
        w.Byte((byte)Type);
        w.Byte(Flags);
        w.Byte(1);
        w.UInt32((uint)Body.Length);
        w.UInt32(Serial);
        var fields = w.BeginArray(8);
        if (Path is not null) Field(w, FieldPath, "o", Path);
        if (Interface is not null) Field(w, FieldInterface, "s", Interface);
        if (Member is not null) Field(w, FieldMember, "s", Member);
        if (ErrorName is not null) Field(w, FieldErrorName, "s", ErrorName);
        if (ReplySerial is { } reply)
        {
            w.BeginStruct();
            w.Byte(FieldReplySerial);
            w.Variant("u");
            w.UInt32(reply);
        }
        if (Destination is not null) Field(w, FieldDestination, "s", Destination);
        if (Sender is not null) Field(w, FieldSender, "s", Sender);
        if (Signature.Length > 0)
        {
            w.BeginStruct();
            w.Byte(FieldSignature);
            w.Variant("g");
            w.Signature(Signature);
        }
        w.EndArray(fields);
        w.Pad(8);
        var bytes = new byte[w.Length + Body.Length];
        w.Written.CopyTo(bytes);
        Body.CopyTo(bytes, w.Length);
        return bytes;
    }

    private static void Field(DBusWriter w, byte code, string signature, string value)
    {
        w.BeginStruct();
        w.Byte(code);
        w.Variant(signature);
        w.String(value);
    }

    /// <summary>
    /// The whole message's length, from its first 16 bytes (the fixed header
    /// and the length of the header fields).
    /// </summary>
    public static int LengthFromPrefix(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < 16) throw new ArgumentException("needs the first 16 bytes", nameof(prefix));
        bool big = prefix[0] switch
        {
            (byte)'l' => false,
            (byte)'B' => true,
            _ => throw new DBusFormatException($"unknown byte order 0x{prefix[0]:x2}"),
        };
        if (prefix[3] != 1) throw new DBusFormatException($"protocol version {prefix[3]}");
        uint body = big ? BinaryPrimitives.ReadUInt32BigEndian(prefix[4..]) : BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]);
        uint fields = big ? BinaryPrimitives.ReadUInt32BigEndian(prefix[12..]) : BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]);
        long header = (16L + fields + 7) & ~7L;
        long total = header + body;
        if (total > MaxLength) throw new DBusFormatException($"message of {total} bytes");
        return (int)total;
    }

    /// <summary>A whole message, as <see cref="LengthFromPrefix"/> measured it.</summary>
    public static DBusMessage Decode(byte[] data)
    {
        if (data.Length < 16) throw new DBusFormatException("message shorter than its fixed header");
        int total = LengthFromPrefix(data);
        if (data.Length != total) throw new DBusFormatException($"message is {data.Length} bytes, its header says {total}");
        bool big = data[0] == (byte)'B';
        var type = data[1];
        if (type is < 1 or > 4) throw new DBusFormatException($"message type {type}");

        var r = new DBusReader(data, 0, data.Length, big);
        r.ReadAll("yyyyuu");
        uint serial = big ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var fields = (List<object>)r.ReadAll("a(yv)")[0];
        string? path = null, iface = null, member = null, error = null, destination = null, sender = null, signature = null;
        uint? replySerial = null;
        foreach (object[] f in fields.Cast<object[]>())
        {
            var code = (byte)f[0];
            var v = (DBusVariant)f[1];
            switch (code)
            {
                case FieldPath: path = Expect<string>(v, "o"); break;
                case FieldInterface: iface = Expect<string>(v, "s"); break;
                case FieldMember: member = Expect<string>(v, "s"); break;
                case FieldErrorName: error = Expect<string>(v, "s"); break;
                case FieldReplySerial: replySerial = Expect<uint>(v, "u"); break;
                case FieldDestination: destination = Expect<string>(v, "s"); break;
                case FieldSender: sender = Expect<string>(v, "s"); break;
                case FieldSignature: signature = Expect<string>(v, "g"); break;
                case FieldUnixFds: break; // none are negotiated, so none can arrive
                default: break; // unknown fields are ignored, as the spec asks
            }
        }
        r.Align(8);
        int bodyStart = r.Position;
        return new DBusMessage
        {
            Type = (DBusMessageType)type,
            Flags = data[2],
            Serial = serial,
            Path = path,
            Interface = iface,
            Member = member,
            ErrorName = error,
            ReplySerial = replySerial,
            Destination = destination,
            Sender = sender,
            Signature = signature ?? "",
            Body = data.AsSpan(bodyStart).ToArray(),
            BigEndian = big,
        };
    }

    private static T Expect<T>(DBusVariant v, string signature)
    {
        if (v.Signature != signature) throw new DBusFormatException($"header field of type '{v.Signature}', expected '{signature}'");
        return (T)v.Value;
    }
}
