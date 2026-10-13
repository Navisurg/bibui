using System.Runtime.InteropServices;

namespace Bibui.Platform.Wayland;

/// <summary>
/// The libc calls a wl_shm buffer and a wakeable event loop need. Blittable,
/// no SetLastError (runtime marshalling is disabled in this assembly): errno is
/// read with <see cref="Marshal.GetLastSystemError"/> straight after a failure.
/// </summary>
internal static unsafe class Libc
{
    private const string C = "libc";

    [DllImport(C, ExactSpelling = true)] public static extern int memfd_create(byte* name, uint flags);
    [DllImport(C, ExactSpelling = true)] public static extern int ftruncate(int fd, long length);
    [DllImport(C, ExactSpelling = true)] public static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);
    [DllImport(C, ExactSpelling = true)] public static extern int munmap(nint addr, nuint length);
    [DllImport(C, ExactSpelling = true)] public static extern int close(int fd);
    [DllImport(C, ExactSpelling = true)] public static extern int poll(PollFd* fds, nuint nfds, int timeout);
    [DllImport(C, ExactSpelling = true)] public static extern int eventfd(uint initval, int flags);
    [DllImport(C, ExactSpelling = true)] public static extern nint read(int fd, void* buf, nuint count);
    [DllImport(C, ExactSpelling = true)] public static extern nint write(int fd, void* buf, nuint count);

    public const uint MFD_CLOEXEC = 1;
    public const int PROT_READ = 1, PROT_WRITE = 2, MAP_SHARED = 1;
    public static readonly nint MAP_FAILED = -1;
    public const short POLLIN = 1, POLLOUT = 4;
    public const int EFD_CLOEXEC = 0x80000, EFD_NONBLOCK = 0x800;
    public const int EINTR = 4, EAGAIN = 11;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd { public int Fd; public short Events; public short Revents; }

    public static int Errno => Marshal.GetLastSystemError();
}
