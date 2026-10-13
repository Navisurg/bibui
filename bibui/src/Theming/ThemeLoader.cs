namespace Bibui.Theming;

public static class ThemeLoader
{
    /// <summary>The theme registered as <paramref name="name"/>; see <see cref="ThemeRegistry"/>.</summary>
    public static ThemeTokens Load(string name) => ThemeRegistry.Load(name);

    // bibui's own themes, as their files (Assets/Themes/*.json) give them.

    /// <summary>bibui's own dark theme, the default: warm graphite with a petrol-teal accent.</summary>
    public static ThemeTokens BibuiDark() => ThemeRegistry.Load("bibui-dark");

    /// <summary>bibui's own light theme: warm paper with a petrol-teal accent.</summary>
    public static ThemeTokens BibuiLight() => ThemeRegistry.Load("bibui-light");

    public static ThemeTokens NeutralDark() => ThemeRegistry.Load("neutral-dark");

    public static ThemeTokens DefaultDark() => ThemeRegistry.Load("default-dark");

    public static ThemeTokens DefaultLight() => ThemeRegistry.Load("default-light");
}
