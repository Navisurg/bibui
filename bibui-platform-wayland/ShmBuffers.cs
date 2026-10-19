using Bibui.Platform.Wayland.Protocol;
using Bibui.Draw;

namespace Bibui.Platform.Wayland;

/// <summary>
/// One wl_shm buffer: shared memory the compositor reads, drawn into where it
/// lies, so a frame is drawn where it is presented from — no copy. BGRA8888
/// premultiplied is wl_shm's ARGB8888 on a little-endian machine.
/// </summary>
internal sealed class ShmBuffer(WlBuffer wl, nint map, nuint size, int width, int height)
{
    public readonly WlBuffer Wl = wl;
    public readonly nint Map = map;
    public readonly nuint Size = size;
    public readonly int Width = width, Height = height;

    /// <summary>The mapped memory, as a renderer draws into it.</summary>
    public RasterPixels Pixels => new(Map, Width, Height, Width * 4);
}

/// <summary>
/// Allocates <see cref="ShmBuffer"/>s: a memfd per buffer, mapped, shared with
/// the compositor as a one-buffer wl_shm_pool. One pool per buffer rather than
/// one shared pool, so a buffer retired by a resize frees its memory on its own
/// release without moving any other.
/// </summary>
internal sealed unsafe class ShmAllocator(WlShm shm, WlBuffer.Listener* listener, void* data)
    : IBufferAllocator<ShmBuffer>
{
    public ShmBuffer Allocate(int width, int height)
    {
        int stride = width * 4;
        nuint size = (nuint)stride * (nuint)height;

        int fd;
        fixed (byte* name = "bibui-wl-shm\0"u8) fd = Libc.memfd_create(name, Libc.MFD_CLOEXEC);
        if (fd < 0) throw new InvalidOperationException($"memfd_create failed: errno {Libc.Errno}");
        nint map = 0;
        try
        {
            if (Libc.ftruncate(fd, (long)size) != 0) throw new InvalidOperationException($"ftruncate failed: errno {Libc.Errno}");
            map = Libc.mmap(0, size, Libc.PROT_READ | Libc.PROT_WRITE, Libc.MAP_SHARED, fd, 0);
            if (map == Libc.MAP_FAILED) { map = 0; throw new InvalidOperationException($"mmap failed: errno {Libc.Errno}"); }

            // libwayland dups the fd while marshalling create_pool, so ours closes below.
            var pool = shm.CreatePool(fd, (int)size);
            var wl = pool.CreateBuffer(0, width, height, stride, WlShm.Format.Argb8888);
            pool.Destroy(); // the buffer keeps the memory; the pool object is done
            wl.AddListener(listener, data);

            return new ShmBuffer(wl, map, size, width, height);
        }
        catch
        {
            if (map != 0) Libc.munmap(map, size);
            throw;
        }
        finally
        {
            Libc.close(fd);
        }
    }

    public void Destroy(ShmBuffer buffer)
    {
        buffer.Wl.Destroy();
        Libc.munmap(buffer.Map, buffer.Size);
    }
}
