using System.Runtime.InteropServices;

namespace Bibui.Platform.Wayland;

/// <summary>
/// The few EGL entry points the GPU path needs, from libEGL.so.1 (libglvnd or
/// Mesa), and libwayland-egl's window. Blittable signatures only — the
/// assembly disables runtime marshalling — so nothing needs a stub under
/// Native AOT. EGLBoolean is a 32-bit unsigned int, never a C# bool here.
/// </summary>
internal static unsafe class Egl
{
    public const string Library = "libEGL.so.1";
    public const string WaylandEglLibrary = "libwayland-egl.so.1";

    public const int EGL_NONE = 0x3038;
    public const int EGL_ALPHA_SIZE = 0x3021;
    public const int EGL_BLUE_SIZE = 0x3022;
    public const int EGL_GREEN_SIZE = 0x3023;
    public const int EGL_RED_SIZE = 0x3024;
    public const int EGL_DEPTH_SIZE = 0x3025;
    public const int EGL_STENCIL_SIZE = 0x3026;
    public const int EGL_SURFACE_TYPE = 0x3033;
    public const int EGL_RENDERABLE_TYPE = 0x3040;
    public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    public const int EGL_WINDOW_BIT = 0x0004;
    public const int EGL_OPENGL_ES2_BIT = 0x0004;
    public const int EGL_OPENGL_BIT = 0x0008;
    public const uint EGL_OPENGL_ES_API = 0x30A0;
    public const uint EGL_OPENGL_API = 0x30A2;
    public const uint EGL_PLATFORM_WAYLAND_KHR = 0x31D8;
    public const int EGL_SUCCESS = 0x3000;

    public const uint GL_VENDOR = 0x1F00;
    public const uint GL_RENDERER = 0x1F01;
    public const uint GL_VERSION = 0x1F02;

    [DllImport(Library, ExactSpelling = true)] public static extern nint eglGetPlatformDisplay(uint platform, nint nativeDisplay, nint* attribs);
    [DllImport(Library, ExactSpelling = true)] public static extern nint eglGetDisplay(nint nativeDisplay);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglInitialize(nint display, int* major, int* minor);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglTerminate(nint display);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglBindAPI(uint api);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglChooseConfig(nint display, int* attribs, nint* configs, int size, int* count);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglGetConfigAttrib(nint display, nint config, int attribute, int* value);
    [DllImport(Library, ExactSpelling = true)] public static extern nint eglCreateContext(nint display, nint config, nint share, int* attribs);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglDestroyContext(nint display, nint context);
    [DllImport(Library, ExactSpelling = true)] public static extern nint eglCreateWindowSurface(nint display, nint config, nint window, int* attribs);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglDestroySurface(nint display, nint surface);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglMakeCurrent(nint display, nint draw, nint read, nint context);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglSwapInterval(nint display, int interval);
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglSwapBuffers(nint display, nint surface);
    [DllImport(Library, ExactSpelling = true)] public static extern nint eglGetProcAddress(byte* name);
    [DllImport(Library, ExactSpelling = true)] public static extern int eglGetError();
    [DllImport(Library, ExactSpelling = true)] public static extern uint eglReleaseThread();

    [DllImport(WaylandEglLibrary, ExactSpelling = true)] public static extern nint wl_egl_window_create(nint surface, int width, int height);
    [DllImport(WaylandEglLibrary, ExactSpelling = true)] public static extern void wl_egl_window_destroy(nint window);
    [DllImport(WaylandEglLibrary, ExactSpelling = true)] public static extern void wl_egl_window_resize(nint window, int width, int height, int dx, int dy);

    /// <summary>True when both libEGL and libwayland-egl load on this machine. Says nothing about a working driver.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!NativeLibrary.TryLoad(Library, out var egl)) return false;
            NativeLibrary.Free(egl);
            if (!NativeLibrary.TryLoad(WaylandEglLibrary, out var wlEgl)) return false;
            NativeLibrary.Free(wlEgl);
            return true;
        }
    }

    /// <summary>An entry point by name (EGL 1.5 / EGL_KHR_get_all_proc_addresses: core GL too), or 0.</summary>
    public static nint ProcAddress(string name)
    {
        int n = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> utf8 = n < 256 ? stackalloc byte[n + 1] : new byte[n + 1];
        System.Text.Encoding.UTF8.GetBytes(name, utf8);
        utf8[n] = 0;
        fixed (byte* p = utf8) return eglGetProcAddress(p);
    }

    /// <summary>glGetString of the current context, or null.</summary>
    public static string? GlString(uint name)
    {
        var glGetString = (delegate* unmanaged<uint, byte*>)ProcAddress("glGetString");
        if (glGetString is null) return null;
        byte* s = glGetString(name);
        return s is null ? null : Marshal.PtrToStringUTF8((nint)s);
    }

    public static string Error() => $"EGL error 0x{eglGetError():x4}";
}
