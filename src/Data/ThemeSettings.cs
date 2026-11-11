using SpankBank.Data.Repositories;

namespace SpankBank.Data;

/// <summary>
/// The look of this instance: the theme and the embellishments, stored in the database (<c>Ui.Theme</c>, <c>Ui.ThemeExtra</c>)
/// and chosen on Settings &gt; Theme. Nothing about a theme depends on the site. The values are kept in memory: only this
/// web process writes them.
/// </summary>
public sealed class ThemeSettings
{
    public const string DefaultTheme = "hexagon";
    public const string DefaultExtra = "hexagon";

    /// <summary>Value, label and the browser's theme colour of each theme (styles/themes/_theme_*.scss).</summary>
    public static readonly IReadOnlyList<(string Value, string Label, string Color)> Themes =
    [
        ("hexagon", "Hexagon", "#00549e"),
        ("pony", "Pony", "#250d51"),
        ("bloodlust", "Bloodlust", "#000000"),
        ("serpent", "Serpent", "#44a544"),
        ("hotdog", "Hotdog", "#ffff00"),
        ("forest", "Forest", "#002e12"),
    ];

    /// <summary>Value and label of each embellishment (styles/themes/_extra_*.scss; hexagon is the stock one).</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Extras =
    [
        ("hexagon", "Hexagon"),
        ("none", "None"),
        ("autumn", "Autumn"),
        ("winter", "Winter"),
        ("spring", "Spring"),
        ("fennec", "Fennec"),
        ("aurora", "Aurora"),
        ("space", "Space"),
        ("stars", "Stars"),
        ("scales", "Scales"),
    ];

    private readonly SettingsRepository _settings;
    private sealed record Choice(string Theme, string Extra);

    private volatile Choice? _current;

    public ThemeSettings(SettingsRepository settings) => _settings = settings;

    public string Theme => Current.Theme;
    public string Extra => Current.Extra;
    public string ThemeColor => Themes.First(t => t.Value == Theme).Color;

    /// <summary>False for a value that is not one of the choices.</summary>
    public static bool IsTheme(string? value) => Themes.Any(t => t.Value == value);
    public static bool IsExtra(string? value) => Extras.Any(e => e.Value == value);

    public void Save(string theme, string extra)
    {
        if (!IsTheme(theme) || !IsExtra(extra)) throw new ArgumentException("Unknown theme or embellishment.");
        _settings.Set(WorkerSettingsKeys.UiTheme, theme);
        _settings.Set(WorkerSettingsKeys.UiThemeExtra, extra);
        _current = new Choice(theme, extra);
    }

    private Choice Current
    {
        get
        {
            if (_current is { } known) return known;
            var theme = _settings.Get(WorkerSettingsKeys.UiTheme);
            var extra = _settings.Get(WorkerSettingsKeys.UiThemeExtra);
            var loaded = new Choice(IsTheme(theme) ? theme! : DefaultTheme, IsExtra(extra) ? extra! : DefaultExtra);
            _current = loaded;
            return loaded;
        }
    }
}
