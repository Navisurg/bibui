using System.Runtime.InteropServices;

namespace Bibui.FreeType.Native;

/// <summary>
/// The slice of libfreetype.so.6 the engine needs. Struct fields are read by
/// offset rather than mirrored as C# structs, because only a handful are touched.
/// The offsets are LP64 (x86-64 and arm64 Linux) and were checked with offsetof
/// against the freetype2 2.14.3 headers; FT_FaceRec, FT_GlyphSlotRec and
/// FT_Bitmap are public structs whose layout FreeType 2.x keeps stable.
/// </summary>
internal static unsafe partial class FT
{
    private const string Lib = "libfreetype.so.6";

    public const int LOAD_NO_HINTING = 1 << 1;
    public const int LOAD_NO_BITMAP = 1 << 3;
    public const int LOAD_FORCE_AUTOHINT = 1 << 5;
    public const int LOAD_IGNORE_GLOBAL_ADVANCE_WIDTH = 1 << 9;
    public const int LOAD_TARGET_NORMAL = 0;
    public const int LOAD_TARGET_LIGHT = 1 << 16;

    public const int RENDER_MODE_NORMAL = 0;

    // FT_FaceRec
    public const int Face_NumGlyphs = 32;
    public const int Face_UnitsPerEm = 136;
    public const int Face_Ascender = 138;
    public const int Face_Descender = 140;
    public const int Face_Height = 142;
    public const int Face_Glyph = 152;

    // FT_GlyphSlotRec
    public const int Slot_LinearHoriAdvance = 112;
    public const int Slot_Bitmap = 152;
    public const int Slot_BitmapLeft = 192;
    public const int Slot_BitmapTop = 196;
    public const int Slot_Outline = 200;

    // FT_Bitmap
    public const int Bitmap_Rows = 0;
    public const int Bitmap_Width = 4;
    public const int Bitmap_Pitch = 8;
    public const int Bitmap_Buffer = 16;

    [LibraryImport(Lib)] public static partial int FT_Init_FreeType(out IntPtr library);
    [LibraryImport(Lib)] public static partial int FT_Done_FreeType(IntPtr library);
    [LibraryImport(Lib)] public static partial int FT_New_Memory_Face(IntPtr library, byte* data, nint size, nint faceIndex, out IntPtr face);
    [LibraryImport(Lib)] public static partial int FT_Done_Face(IntPtr face);
    [LibraryImport(Lib)] public static partial int FT_New_Size(IntPtr face, out IntPtr size);
    [LibraryImport(Lib)] public static partial int FT_Activate_Size(IntPtr size);
    [LibraryImport(Lib)] public static partial int FT_Set_Char_Size(IntPtr face, nint charWidth, nint charHeight, uint hres, uint vres);
    [LibraryImport(Lib)] public static partial uint FT_Get_Char_Index(IntPtr face, nuint charcode);
    [LibraryImport(Lib)] public static partial int FT_Load_Glyph(IntPtr face, uint glyphIndex, int loadFlags);
    [LibraryImport(Lib)] public static partial int FT_Get_Advance(IntPtr face, uint glyphIndex, int loadFlags, out nint advance);
    [LibraryImport(Lib)] public static partial int FT_Render_Glyph(IntPtr slot, int renderMode);
    [LibraryImport(Lib)] public static partial void FT_Outline_Translate(IntPtr outline, nint xOffset, nint yOffset);
    [LibraryImport(Lib)] public static partial void FT_Library_Version(IntPtr library, out int major, out int minor, out int patch);

    public static T Read<T>(IntPtr p, int offset) where T : unmanaged => *(T*)((byte*)p + offset);

    public static void Check(int err, string what)
    {
        if (err != 0) throw new InvalidOperationException($"{what} failed: FreeType error {err}");
    }
}
