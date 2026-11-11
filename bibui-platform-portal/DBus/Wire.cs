using System.Buffers.Binary;
using System.Text;

namespace Bibui.Platform.Portal.DBus;

/// <summary>A message the bus or a peer sent that does not follow the wire format.</summary>
internal sealed class DBusFormatException(string message) : Exception(message);

/// <summary>A value of type <c>v</c>: its signature and the value read under it.</summary>
internal sealed record DBusVariant(string Signature, object Value);

/// <summary>
/// The D-Bus wire format, written: little-endian, every value aligned to its
/// type's boundary measured from the start of what is being written. A message
/// body starts on an 8-byte boundary of its message, so a body written here
/// from offset 0 keeps its alignment when the header is put in front of it.
/// </summary>
internal sealed class DBusWriter
{
    private byte[] _buf = new byte[256];

    /// <summary>Bytes written so far.</summary>
    public int Length { get; private set; }

    /// <summary>What has been written.</summary>
    public ReadOnlySpan<byte> Written => _buf.AsSpan(0, Length);

    public byte[] ToArray() => Written.ToArray();

    private Span<byte> Take(int n)
    {
        if (Length + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + n));
        var span = _buf.AsSpan(Length, n);
        Length += n;
        return span;
    }

    /// <summary>Zero bytes up to the next multiple of <paramref name="align"/>.</summary>
    public void Pad(int align)
    {
        int n = (align - Length % align) % align;
        Take(n).Clear();
    }

    public void Byte(byte v) => Take(1)[0] = v;

    public void Bool(bool v) => UInt32(v ? 1u : 0u);

    public void UInt32(uint v)
    {
        Pad(4);
        BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
    }

    public void Int32(int v) => UInt32(unchecked((uint)v));

    /// <summary>A string (<c>s</c>) or an object path (<c>o</c>): length, UTF-8, a NUL.</summary>
    public void String(string s)
    {
        var bytes = Utf8(s);
        UInt32((uint)bytes.Length);
        bytes.CopyTo(Take(bytes.Length));
        Byte(0);
    }

    /// <summary>A signature (<c>g</c>): one byte of length, the ASCII, a NUL.</summary>
    public void Signature(string s)
    {
        if (s.Length > 255) throw new ArgumentException("a D-Bus signature is at most 255 characters", nameof(s));
        Byte((byte)s.Length);
        Encoding.ASCII.GetBytes(s, Take(s.Length));
        Byte(0);
    }

    /// <summary>
    /// Opens an array whose elements align to <paramref name="elementAlign"/>:
    /// its length is filled in by <see cref="EndArray"/>. The padding to the
    /// first element is written even when the array stays empty, and is not
    /// counted in its length.
    /// </summary>
    public (int LengthAt, int Start) BeginArray(int elementAlign)
    {
        UInt32(0);
        int at = Length - 4;
        Pad(elementAlign);
        return (at, Length);
    }

    public void EndArray((int LengthAt, int Start) array) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.AsSpan(array.LengthAt, 4), (uint)(Length - array.Start));

    /// <summary>A struct or a dict entry starts on an 8-byte boundary.</summary>
    public void BeginStruct() => Pad(8);

    /// <summary>A variant's signature; the value under it is written next.</summary>
    public void Variant(string signature) => Signature(signature);

    /// <summary>An array of bytes (<c>ay</c>).</summary>
    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        var a = BeginArray(1);
        bytes.CopyTo(Take(bytes.Length));
        EndArray(a);
    }

    /// <summary>An array of strings (<c>as</c>).</summary>
    public void Strings(IReadOnlyList<string> strings)
    {
        var a = BeginArray(4);
        foreach (var s in strings) String(s);
        EndArray(a);
    }

    private static byte[] Utf8(string s)
    {
        if (s.Contains('\0')) throw new ArgumentException("a D-Bus string cannot hold a NUL character", nameof(s));
        return Encoding.UTF8.GetBytes(s);
    }
}

/// <summary>
/// The D-Bus wire format, read, in either byte order and against any
/// signature: basic types come back boxed, an array as a
/// <see cref="List{T}"/> of object, a dict as a
/// <see cref="Dictionary{TKey, TValue}"/>, a struct as an object array and a
/// variant as a <see cref="DBusVariant"/>. Everything is bounds-checked; a
/// malformed value throws <see cref="DBusFormatException"/>.
/// </summary>
internal sealed class DBusReader(byte[] data, int start, int end, bool bigEndian)
{
    /// <summary>Containers nested deeper than this are refused, as the spec allows.</summary>
    private const int MaxDepth = 64;

    private readonly int _start = start;
    private int _pos = start;

    /// <summary>Offset from the start alignment is measured from.</summary>
    public int Position => _pos - _start;

    public bool AtEnd => _pos >= end;

    /// <summary>Reads one value of each complete type in <paramref name="signature"/>, in order.</summary>
    public object[] ReadAll(string signature)
    {
        var values = new List<object>();
        int i = 0;
        while (i < signature.Length) values.Add(Read(signature, ref i, 0));
        return [.. values];
    }

    /// <summary>Reads the single complete type at <paramref name="i"/> of <paramref name="sig"/> and steps past it.</summary>
    public object Read(string sig, ref int i, int depth)
    {
        if (depth > MaxDepth) throw new DBusFormatException("values nested too deeply");
        if (i >= sig.Length) throw new DBusFormatException($"signature '{sig}' ends early");
        char c = sig[i++];
        switch (c)
        {
            case 'y': return Take(1)[0];
            case 'b':
                uint b = U32();
                if (b > 1) throw new DBusFormatException($"boolean {b}");
                return b == 1;
            case 'n': Align(2); return (short)(bigEndian ? BinaryPrimitives.ReadInt16BigEndian(Take(2)) : BinaryPrimitives.ReadInt16LittleEndian(Take(2)));
            case 'q': Align(2); return bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(Take(2)) : BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            case 'i': return unchecked((int)U32());
            case 'u': case 'h': return U32();
            case 'x': Align(8); return bigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
            case 't': Align(8); return bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(Take(8)) : BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
            case 'd':
                Align(8);
                long bits = bigEndian ? BinaryPrimitives.ReadInt64BigEndian(Take(8)) : BinaryPrimitives.ReadInt64LittleEndian(Take(8));
                return BitConverter.Int64BitsToDouble(bits);
            case 's': case 'o': return String();
            case 'g': return Signature();
            case 'v':
            {
                string inner = Signature();
                int j = 0;
                var value = Read(inner, ref j, depth + 1);
                if (j != inner.Length) throw new DBusFormatException($"variant signature '{inner}' is not one complete type");
                return new DBusVariant(inner, value);
            }
            case 'a':
            {
                uint length = U32();
                if (length > 64 * 1024 * 1024) throw new DBusFormatException($"array of {length} bytes");
                if (i >= sig.Length) throw new DBusFormatException($"signature '{sig}' ends in an array");
                int element = i;
                Align(AlignmentOf(sig[element]));
                int stop = _pos + (int)length;
                if (stop > end) throw new DBusFormatException("array runs past the message");
                bool dict = sig[element] == '{';
                var list = dict ? null : new List<object>();
                var map = dict ? new Dictionary<object, object>() : null;
                int after = element;
                if (_pos == stop) after = element + TypeLength(sig, element);
                while (_pos < stop)
                {
                    after = element;
                    if (dict)
                    {
                        Align(8);
                        after++; // '{'
                        var key = Read(sig, ref after, depth + 1);
                        var value = Read(sig, ref after, depth + 1);
                        if (after >= sig.Length || sig[after] != '}') throw new DBusFormatException($"signature '{sig}' has a dict entry of more than two types");
                        after++;
                        map![key] = value;
                    }
                    else list!.Add(Read(sig, ref after, depth + 1));
                }
                if (_pos != stop) throw new DBusFormatException("array elements run past its length");
                i = after;
                return dict ? map! : list!;
            }
            case '(':
            {
                Align(8);
                var fields = new List<object>();
                while (i < sig.Length && sig[i] != ')') fields.Add(Read(sig, ref i, depth + 1));
                if (i >= sig.Length) throw new DBusFormatException($"signature '{sig}' leaves a struct open");
                i++;
                return fields.ToArray();
            }
            default:
                throw new DBusFormatException($"unknown type '{c}' in signature '{sig}'");
        }
    }

    /// <summary>The alignment of a value whose type starts with <paramref name="c"/>.</summary>
    public static int AlignmentOf(char c) => c switch
    {
        'y' or 'g' or 'v' => 1,
        'n' or 'q' => 2,
        'b' or 'i' or 'u' or 'h' or 's' or 'o' or 'a' => 4,
        'x' or 't' or 'd' or '(' or '{' => 8,
        _ => throw new DBusFormatException($"unknown type '{c}'"),
    };

    /// <summary>How many characters the single complete type at <paramref name="i"/> takes.</summary>
    public static int TypeLength(string sig, int i)
    {
        int start = i, open = 0;
        while (i < sig.Length && sig[i] == 'a') i++;
        do
        {
            if (i >= sig.Length) throw new DBusFormatException($"signature '{sig}' ends early");
            char c = sig[i++];
            if (c == '(' || c == '{') open++;
            else if (c == ')' || c == '}') open--;
        } while (open > 0);
        return i - start;
    }

    public uint U32()
    {
        Align(4);
        var s = Take(4);
        return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    }

    public void Align(int align)
    {
        int n = (align - Position % align) % align;
        foreach (var b in Take(n))
            if (b != 0) throw new DBusFormatException("padding is not zero");
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > end) throw new DBusFormatException("value runs past the message");
        var span = data.AsSpan(_pos, n);
        _pos += n;
        return span;
    }

    private string String()
    {
        uint length = U32();
        if (length > int.MaxValue - 1) throw new DBusFormatException("string too long");
        var bytes = Take((int)length);
        if (Take(1)[0] != 0) throw new DBusFormatException("string is not NUL-terminated");
        return Encoding.UTF8.GetString(bytes);
    }

    private string Signature()
    {
        int length = Take(1)[0];
        var bytes = Take(length);
        if (Take(1)[0] != 0) throw new DBusFormatException("signature is not NUL-terminated");
        return Encoding.ASCII.GetString(bytes);
    }
}
