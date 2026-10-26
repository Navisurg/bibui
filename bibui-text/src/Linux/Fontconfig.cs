using System.Runtime.InteropServices;

namespace Bibui.Text;

/// <summary>
/// The system's fonts on Linux, as fontconfig (libfontconfig.so.1) names them:
/// how a Linux platform sets up the text layer. Nothing in bibui's text calls it
/// on its own; a platform does, once, at start-up (the Linux <c>Bibui.App</c>
/// packages do in <c>BibuiApp.Open</c>). The library is loaded by hand rather
/// than imported, so a machine without it, or a platform that is not Linux,
/// simply has no answer instead of failing to start.
/// </summary>
public static unsafe class Fontconfig
{
    /// <summary>
    /// Sets <paramref name="fonts"/>' cluster face to the system's colour emoji
    /// font, when fontconfig finds one and it can be read; else leaves it (the
    /// built-in Inter). The font is read once a process, however many
    /// <see cref="Fonts"/> it is handed to.
    /// </summary>
    public static void UseColorEmoji(Fonts fonts)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        if (ColorEmojiFont() is { } font) fonts.UseClusterFont(font);
    }

    /// <summary>
    /// The colour emoji font fontconfig matches (<c>emoji:color=true</c>), as a
    /// file path; "" when there is none: not Linux, no fontconfig, or no colour
    /// font installed. Asked the first time it is read, then kept for the process.
    /// </summary>
    public static string ColorEmojiFile => s_colorEmojiFile ??= MatchColorFile("emoji:color=true\0"u8);

    // Two threads that both find it unset both ask, and get the same answer.
    private static string? s_colorEmojiFile;

    // The colour emoji font's bytes, read once a process: every Fonts opens its
    // face from them (a runtime makes one, and tests make hundreds). Null when
    // there is no file or it could not be read, which is asked only once too.
    private static readonly Lock s_fontLock = new();
    private static FreeType.FontData? s_font;
    private static bool s_fontRead;

    private static FreeType.FontData? ColorEmojiFont()
    {
        lock (s_fontLock)
        {
            if (s_fontRead) return s_font;
            s_fontRead = true;
            string path = ColorEmojiFile;
            if (path.Length == 0) return null;
            // The file is the system's: one that is gone or unreadable leaves the
            // system without a cluster face, not the app.
            try { s_font = FreeType.FontData.FromFile(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
            return s_font;
        }
    }

    // The file of fontconfig's best match for a pattern, when that match is a
    // colour font. Fontconfig always answers with something, so a machine with
    // no colour emoji font gets a text font back: that is no answer here.
    private static string MatchColorFile(ReadOnlySpan<byte> pattern)
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libfontconfig.so.1", out IntPtr lib)) return "";
        if (!NativeLibrary.TryGetExport(lib, "FcInit", out IntPtr fcInit)
            || !NativeLibrary.TryGetExport(lib, "FcNameParse", out IntPtr fcNameParse)
            || !NativeLibrary.TryGetExport(lib, "FcConfigSubstitute", out IntPtr fcConfigSubstitute)
            || !NativeLibrary.TryGetExport(lib, "FcDefaultSubstitute", out IntPtr fcDefaultSubstitute)
            || !NativeLibrary.TryGetExport(lib, "FcFontMatch", out IntPtr fcFontMatch)
            || !NativeLibrary.TryGetExport(lib, "FcPatternGetBool", out IntPtr fcPatternGetBool)
            || !NativeLibrary.TryGetExport(lib, "FcPatternGetString", out IntPtr fcPatternGetString)
            || !NativeLibrary.TryGetExport(lib, "FcPatternDestroy", out IntPtr fcPatternDestroy))
            return "";

        // FcBool is an int; FcResultMatch is 0; FcMatchPattern is 0. A null
        // config is the current one, which FcInit loads once for the process.
        if (((delegate* unmanaged<int>)fcInit)() == 0) return "";
        IntPtr pat;
        fixed (byte* p = pattern) pat = ((delegate* unmanaged<byte*, IntPtr>)fcNameParse)(p);
        if (pat == 0) return "";
        ((delegate* unmanaged<IntPtr, IntPtr, int, int>)fcConfigSubstitute)(0, pat, 0);
        ((delegate* unmanaged<IntPtr, void>)fcDefaultSubstitute)(pat);
        int result;
        IntPtr match = ((delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)fcFontMatch)(0, pat, &result);
        ((delegate* unmanaged<IntPtr, void>)fcPatternDestroy)(pat);
        if (match == 0) return "";

        string file = "";
        int color = 0;
        byte* path = null;
        fixed (byte* colorKey = "color\0"u8)
        fixed (byte* fileKey = "file\0"u8)
        {
            if (((delegate* unmanaged<IntPtr, byte*, int, int*, int>)fcPatternGetBool)(match, colorKey, 0, &color) == 0 && color != 0
                && ((delegate* unmanaged<IntPtr, byte*, int, byte**, int>)fcPatternGetString)(match, fileKey, 0, &path) == 0)
                file = Marshal.PtrToStringUTF8((IntPtr)path) ?? "";
        }
        ((delegate* unmanaged<IntPtr, void>)fcPatternDestroy)(match);
        return file;
    }
}
