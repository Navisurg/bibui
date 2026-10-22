using System.Globalization;
using Bibui.Core.FreeType;
using FtFont = Bibui.Core.FreeType.Font;

namespace Bibui.Core;

/// <summary>
/// Which face draws which text: resolves (family, weight) to a <see cref="FontFace"/>
/// and splits a string into runs of one face each. The policy is bibui's: a
/// family registered with <see cref="FontRegistry"/>, else the embedded Inter
/// (Regular below 600, SemiBold from 600 up). Faces come from the one
/// <see cref="TextEngine"/> its <see cref="Fonts"/> owns, never from a raster
/// backend, so the backend cannot move layout.
///
/// <para>Installed fonts are not looked up. A character the family lacks is set
/// in the first fallback registered with <see cref="FontRegistry.RegisterFallback"/>
/// that has it, a grapheme cluster at a time (<see cref="Choose"/>), and with none
/// that has it, drawn as the family's missing glyph. Text no longer depends on the
/// machine it runs on.</para>
/// </summary>
internal sealed class FontProvider
{
    private readonly TextEngine _engine;

    // Per-(family, weight) resolved face. "" family key = the built-in Inter.
    private readonly Dictionary<(string Family, int Weight), FontFace> _cache = new();

    // One FontFace per engine font, so runs compare faces by reference.
    private readonly Dictionary<FtFont, FontFace> _faces = new(ReferenceEqualityComparer.Instance);

    // Typeface runs, addressed by a content hash of (text, family, weight) — see RunsKey.
    // Touch-evicted, because its keys come from arbitrary UI text: text that stops
    // being drawn falls out on its own. Made by Fonts, which is what ticks it —
    // this class has no EndFrame of its own to be called out of step.
    private readonly ResourceCache<RunsKey, TextRun> _runs;

    // The fallbacks as faces, in registration order, as of _version.
    private FontFace[] _fallbacks = [];
    private int _version = -1;

    internal FontProvider(TextEngine engine, ResourceCache<RunsKey, TextRun> runs)
    {
        _engine = engine;
        _runs = runs;
    }

    public FontFace Regular => Resolve(null, 400);

    /// <summary>
    /// Takes in what <see cref="FontRegistry"/> holds now, when it changed since:
    /// families and fallbacks resolve again. True when it did, for <see cref="Fonts"/>
    /// to drop what it measured under the old fonts.
    /// </summary>
    internal bool Sync()
    {
        int version = FontRegistry.Version;
        if (version == _version) return false;
        _version = version;
        _cache.Clear();
        _runs.Entries.Clear();
        var bytes = FontRegistry.Fallbacks();
        var faces = new List<FontFace>(bytes.Length);
        foreach (var b in bytes)
            if (_engine.Load(b) is { } font) faces.Add(Face(font));
        _fallbacks = [.. faces];
        return true;
    }

    public FontFace Resolve(string? family, int weight)
    {
        var key = (family ?? "", weight);
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var face = Face(Load(family, weight));
        _cache[key] = face;
        return face;
    }

    private FtFont Load(string? family, int weight)
    {
        // A registered family is the caller's deliberate ask. Identical bytes open
        // as one font however often they are handed over.
        if (family is not null && FontRegistry.TryResolve(family, weight) is { } bytes && _engine.Load(bytes) is { } registered)
            return registered;
        return _engine.Resolve(null, weight);
    }

    private FontFace Face(FtFont font)
    {
        if (!_faces.TryGetValue(font, out var face)) _faces[font] = face = new FontFace(font);
        return face;
    }

    internal TextRun? SplitRuns(ReadOnlySpan<char> text, string? family, int weight)
    {
        if (text.IsEmpty) return null;
        var key = RunsKey.For(text, family, weight);
        if (_runs.TryGet(key, out var runs)) return runs;
        var face = Resolve(family, weight);
        return _runs.Add(key, _fallbacks.Length == 0 || !NeedsFallback(text, face)
            ? new TextRun(face, 0, text.Length, shaped: false)
            : Split(text, face));
    }

    // Whether any character might be set in a fallback: one the face lacks, or a
    // request for emoji presentation. Plain text answers no in one pass over its
    // code points, through the face's cached cmap.
    private static bool NeedsFallback(ReadOnlySpan<char> text, FontFace face)
    {
        for (int i = 0; i < text.Length; i++)
        {
            int cp = FontStrike.NextCodepoint(text, ref i);
            if (cp == 0xFE0F || (!IsIgnorable(cp) && !face.HasGlyph(cp))) return true;
        }
        return false;
    }

    // The text a cluster at a time, each in the face Choose gives it; neighbours in
    // one face are one run.
    private TextRun Split(ReadOnlySpan<char> text, FontFace primary)
    {
        TextRun? first = null, last = null;
        int runStart = 0;
        FontFace? runFace = null;
        for (int i = 0; i < text.Length;)
        {
            int length = StringInfo.GetNextTextElementLength(text[i..]);
            if (length <= 0) length = 1;
            var face = Choose(text.Slice(i, length), primary);
            if (runFace is not null && face != runFace)
            {
                Append(new TextRun(runFace, runStart, i, shaped: runFace != primary));
                runStart = i;
            }
            runFace = face;
            i += length;
        }
        Append(new TextRun(runFace!, runStart, text.Length, shaped: runFace != primary));
        return first!;

        void Append(TextRun run) => last = first is null ? first = run : last!.Append(run);
    }

    /// <summary>
    /// The face one grapheme cluster is set in. A cluster that asks for emoji —
    /// U+FE0F, a keycap, a skin tone, a ZWJ sequence, a regional indicator — goes to
    /// the first colour fallback that has all of it; otherwise the text's own face
    /// when it has all of it (U+FE0E asks for exactly that); else the first fallback
    /// that does. With none that has all of it, the first that has its first code point,
    /// else the text's own face, which draws its missing glyph.
    /// </summary>
    internal FontFace Choose(ReadOnlySpan<char> cluster, FontFace primary)
    {
        bool emoji = false, text = false;
        for (int i = 0; i < cluster.Length; i++)
        {
            int cp = FontStrike.NextCodepoint(cluster, ref i);
            if (cp == 0xFE0E) text = true;
            else if (cp is 0xFE0F or 0x20E3 or 0x200D || cp is >= 0x1F3FB and <= 0x1F3FF || cp is >= 0x1F1E6 and <= 0x1F1FF)
                emoji = true;
        }
        emoji &= !text;

        if (emoji)
            foreach (var f in _fallbacks)
                if (f.IsColor && Covers(f, cluster)) return f;
        if (Covers(primary, cluster)) return primary;
        foreach (var f in _fallbacks)
            if ((!text || !f.IsColor) && Covers(f, cluster)) return f;

        int j = 0;
        int lead = FontStrike.NextCodepoint(cluster, ref j);
        if (primary.HasGlyph(lead)) return primary;
        foreach (var f in _fallbacks)
            if (f.HasGlyph(lead)) return f;
        return primary;
    }

    // Whether the face has every code point of the cluster that is meant to be seen.
    private static bool Covers(FontFace face, ReadOnlySpan<char> cluster)
    {
        for (int i = 0; i < cluster.Length; i++)
        {
            int cp = FontStrike.NextCodepoint(cluster, ref i);
            if (!IsIgnorable(cp) && !face.HasGlyph(cp)) return false;
        }
        return true;
    }

    // Unicode's Default_Ignorable_Code_Point, the parts text meets: joiners, marks of
    // direction, variation selectors and tags draw nothing of their own, so a face
    // need not have them.
    private static bool IsIgnorable(int cp) => cp is 0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x3164 or 0xFEFF or 0xFFA0
        || cp is >= 0x180B and <= 0x180F
        || cp is >= 0x200B and <= 0x200F
        || cp is >= 0x202A and <= 0x202E
        || cp is >= 0x2060 and <= 0x206F
        || cp is >= 0xFE00 and <= 0xFE0F
        || cp is >= 0x1BCA0 and <= 0x1BCA3
        || cp is >= 0x1D173 and <= 0x1D17A
        || cp is >= 0xE0000 and <= 0xE0FFF;
}
