using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bibui.Platform.Wayland;

/// <summary>
/// The two ends of a clipboard transfer, on plain fds: reading what another
/// client sends through a pipe we created (paste), and writing our text into
/// the fd a reader handed the compositor (copy). Neither blocks without a
/// bound: a reader that never reads, or a source that never writes, costs a
/// timeout, never the loop.
///
/// <para>Its P/Invokes live here rather than in <see cref="Libc"/>, so the
/// clipboard's edits stay in its own files.</para>
/// </summary>
internal static unsafe class ClipboardPipe
{
    private const string C = "libc";

    [DllImport(C, ExactSpelling = true)] private static extern int pipe2(int* fds, int flags);
    // fcntl is variadic in C; with int arguments only, the fixed-argument call
    // is what it receives on every Linux ABI .NET runs on.
    [DllImport(C, ExactSpelling = true)] private static extern int fcntl(int fd, int cmd, int arg);

    private const int O_CLOEXEC = 0x80000, O_NONBLOCK = 0x800;
    private const int F_GETFL = 3, F_SETFL = 4;
    private const short POLLERR = 8, POLLHUP = 0x10;
    public const int EPIPE = 32;

    /// <summary>The most a paste reads before giving up on it: 64 MiB.</summary>
    public const int DefaultMaxBytes = 64 << 20;

    /// <summary>
    /// A close-on-exec pipe, both ends blocking. The write end goes to another
    /// client, which may well write to it naively; ours is only ever read after
    /// poll says so.
    /// </summary>
    public static bool TryCreate(out int readFd, out int writeFd)
    {
        int* fds = stackalloc int[2];
        if (pipe2(fds, O_CLOEXEC) != 0)
        {
            readFd = writeFd = -1;
            return false;
        }
        readFd = fds[0];
        writeFd = fds[1];
        return true;
    }

    public static bool SetNonBlocking(int fd)
    {
        int flags = fcntl(fd, F_GETFL, 0);
        return flags >= 0 && fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0;
    }

    public static void Close(int fd)
    {
        if (fd >= 0) Libc.close(fd);
    }

    /// <summary>
    /// Reads <paramref name="fd"/> to end of file. Null when it takes longer
    /// than <paramref name="timeout"/> in all, grows past
    /// <paramref name="maxBytes"/>, or fails. Waits in slices of at most
    /// <paramref name="sliceMs"/>, calling <paramref name="whileWaiting"/>
    /// between them (the host dispatches its display there). Does not close
    /// <paramref name="fd"/>.
    /// </summary>
    public static byte[]? ReadAll(int fd, TimeSpan timeout, int maxBytes = DefaultMaxBytes,
                                  Action? whileWaiting = null, int sliceMs = 10)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var buffer = new byte[16 * 1024];
        using var into = new MemoryStream();
        while (true)
        {
            long left = (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
            if (left <= 0) return null;

            var pfd = new Libc.PollFd { Fd = fd, Events = Libc.POLLIN };
            int n = Libc.poll(&pfd, 1, (int)Math.Min(left, sliceMs));
            if (n < 0)
            {
                if (Libc.Errno == Libc.EINTR) continue;
                return null;
            }
            if (n == 0 || (pfd.Revents & (Libc.POLLIN | POLLHUP | POLLERR)) == 0)
            {
                whileWaiting?.Invoke();
                continue;
            }

            // Readable: a pipe's read returns what is there without blocking.
            nint got;
            fixed (byte* p = buffer) got = Libc.read(fd, p, (nuint)buffer.Length);
            if (got < 0)
            {
                int errno = Libc.Errno;
                if (errno is Libc.EINTR or Libc.EAGAIN) continue;
                return null;
            }
            if (got == 0) return into.ToArray(); // end of file: the source closed its end
            if (into.Length + got > maxBytes) return null;
            into.Write(buffer, 0, (int)got);
        }
    }

    /// <summary>
    /// Writes all of <paramref name="data"/> to <paramref name="fd"/>, which is
    /// made non-blocking. Gives up (false) when the reader has gone (EPIPE) or
    /// has taken nothing for <paramref name="stall"/>. Does not close
    /// <paramref name="fd"/>.
    /// </summary>
    public static bool WriteAll(int fd, ReadOnlySpan<byte> data, TimeSpan stall)
    {
        if (!SetNonBlocking(fd)) return false;
        int stallMs = (int)Math.Clamp(stall.TotalMilliseconds, 0, int.MaxValue);
        int off = 0;
        while (off < data.Length)
        {
            nint wrote;
            fixed (byte* p = data) wrote = Libc.write(fd, p + off, (nuint)(data.Length - off));
            if (wrote > 0)
            {
                off += (int)wrote;
                continue;
            }
            int errno = Libc.Errno;
            if (wrote < 0 && errno == Libc.EINTR) continue;
            if (wrote < 0 && errno != Libc.EAGAIN) return false; // EPIPE: the reader closed its end

            // Full: wait until the reader takes some, or until it has stalled too long.
            var pfd = new Libc.PollFd { Fd = fd, Events = Libc.POLLOUT };
            int n = Libc.poll(&pfd, 1, stallMs);
            if (n < 0 && Libc.Errno == Libc.EINTR) continue;
            if (n <= 0) return false;
            if ((pfd.Revents & (POLLERR | POLLHUP)) != 0 && (pfd.Revents & Libc.POLLOUT) == 0) return false;
        }
        return true;
    }
}
