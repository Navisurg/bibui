namespace Bibui;

/// <summary>
/// A UTF-8 string bibui can edit in place: every label a component takes, and
/// the buffer a <c>TextField</c> edits. In <c>Bibui</c> rather than
/// <c>Bibui.TextEdit</c> so that writing a label needs no extra <c>using</c>.
/// </summary>
public sealed class BibuiStr
{
    internal readonly Int32 GrowFactor = 1; // bitshift
    internal readonly Int32 ShrinkFactor = 1; // bitshift    
    // Has 1/4 of the memory used
    internal readonly Int32 ShrinkThreshold = 2; // bitshift

    public long Capacity => _memory.Length;    
    public long Length { get; private set; }

    private byte[] _memory;

    public BibuiStr(int capacity)
    {
        // Align to multiple of 2
        int alignedCapacity = ((capacity + 1) / 2) * 2;
        _memory = new byte[alignedCapacity];
        Length = 0;
    }


    public static BibuiStr FromStrLit(ReadOnlySpan<byte> strLit)
    {
        BibuiStr str = new BibuiStr(strLit.Length);
        str.ReplaceRange((0, 0), strLit);
        return str;
    }

    public void ReplaceRange(System.ValueTuple<int, int> range, ReadOnlySpan<byte> replace)
    {
        var oldLen = Length;
        //           Head          replace          tail
        var newLen = range.Item1 + replace.Length + Math.Max(0, oldLen - range.Item2);
        // Allocate more memory if the new string exceeds capacity
        if (newLen > Capacity)
        {
            AlignedResize(newLen);
        }
        
        var allocatedMemory = _memory.AsSpan();
        var tail = Str[range.Item2..];
        tail.CopyTo(allocatedMemory.Slice(range.Item1 + replace.Length));
        replace.CopyTo(allocatedMemory.Slice(range.Item1));
        // Shrink if needed. We do it afterward everything has been copied
        // to the _memory buffer, such that we do not accidentally lose
        // the content of tail.
        if (newLen <= (Capacity >> ShrinkThreshold))
        {
            AlignedResize(newLen);
        }

        Length = newLen;
    }
   
    public Span<byte> Str => _memory.AsSpan(0, (int)Length);    

    /// <summary>
    /// The text as it stands, as memory a node can hold past this call: what a
    /// <see cref="Text"/> is given. A view, not a copy — an edit that grows the
    /// buffer moves it, and a view taken before still reads the text before.
    /// </summary>
    public ReadOnlyMemory<byte> AsMemory() => _memory.AsMemory(0, (int)Length);

    public void Grow() => Resize(Capacity << GrowFactor);
    public void Shrink() => Resize(Capacity >> ShrinkFactor);

    public void AlignedResize(long newSize)
    {
        long alignedCapacity = ((newSize + 1) / 2) * 2;
        Resize(alignedCapacity);
    }

    public void Resize(long newSize)
    {
        Array.Resize<byte>(ref _memory, (int)newSize);
    }       
}

