using Bibui.Core.FreeType;
using FtFont = Bibui.Core.FreeType.Font;

namespace Bibui.Core;

/// <summary>
/// Which face draws which text: resolves (family, weight) to a <see cref="FontFace"/>.
/// The policy is bibui's: a
/// family registered with <see cref="FontRegistry"/>, else the embedded Inter
/// (Regular below 600, SemiBold from 600 up). Faces come from the one
/// <see cref="TextEngine"/> its <see cref="Fonts"/> owns, never from a raster
/// backend, so the backend cannot move layout.
///
/// <para>Installed fonts are not looked up, and there is no fallback per
/// character yet: a named family that is not registered gets Inter, and a
/// character Inter lacks is drawn as its missing glyph. Text no longer depends
/// on the machine it runs on.</para>
/// </summary>
internal sealed class FontProvider
{
    private readonly TextEngine _engine;

    // Per-(family, weight) resolved face. "" family key = the built-in Inter.
    private readonly Dictionary<(string Family, int Weight), FontFace> _cache = new();

    // One FontFace per engine font, so runs compare faces by reference.
    private readonly Dictionary<FtFont, FontFace> _faces = new(ReferenceEqualityComparer.Instance);

    internal FontProvider(TextEngine engine) => _engine = engine;

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
}
