using Bibui;

namespace Showcase;

// Icons an observatory console wants that bibui's set does not have. Lucide's
// (ISC licence; activity comes from Feather, MIT — see NOTICE), each written
// as one path: several elements joined with an absolute M, a circle as two arcs.
internal static class AppIcons
{
    /// <summary>Registers them; once, at start-up, before the first frame.</summary>
    public static void Register()
    {
        Icons.Register("sun",
            "M16 12a4 4 0 11-8 0 4 4 0 018 0z" +
            "M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41");
        Icons.Register("moon","M12 3a6 6 0 009 9 9 9 0 11-9-9z");
        Icons.Register("activity", "M22 12h-4l-3 9L9 3l-3 9H2");
        Icons.Register("thermometer", "M14 4v10.54a4 4 0 11-4 0V4a2 2 0 014 0z");
        Icons.Register("telescope",
            "M10.065 12.493l-6.18 1.318a.934.934 0 01-1.108-.702l-.537-2.15a1.07 1.07 0 01.691-1.265l13.504-4.44" +
            "M13.56 11.747l4.332-.924" +
            "M16 21l-3.105-6.21" +
            "M16.485 5.94a2 2 0 011.455-2.425l1.09-.272a1 1 0 011.212.727l1.515 6.06a1 1 0 01-.727 1.213l-1.09.272a2 2 0 01-2.425-1.455z" +
            "M6.158 8.633l1.114 4.456" +
            "M8 21l3.105-6.21" +
            "M14 13a2 2 0 11-4 0 2 2 0 014 0z");
    }
}
