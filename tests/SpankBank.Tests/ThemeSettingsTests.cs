using SpankBank.Data;
using SpankBank.Data.Repositories;

namespace SpankBank.Tests;

public class ThemeSettingsTests : IDisposable
{
    private readonly TempDatabases _db = new();
    private readonly SettingsRepository _settings;

    public ThemeSettingsTests() => _settings = new SettingsRepository(_db.Paths.AppDatabasePath);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Hexagon_with_hexagon_embellishments_is_the_default_for_every_site()
    {
        var theme = new ThemeSettings(_settings);

        Assert.Equal(("hexagon", "hexagon"), (theme.Theme, theme.Extra));
        Assert.Equal("#00549e", theme.ThemeColor);
    }

    [Fact]
    public void The_choice_is_written_to_the_database_and_read_back_by_a_new_instance()
    {
        new ThemeSettings(_settings).Save("bloodlust", "none");

        Assert.Equal("bloodlust", _settings.Get(WorkerSettingsKeys.UiTheme));
        Assert.Equal("none", _settings.Get(WorkerSettingsKeys.UiThemeExtra));
        var again = new ThemeSettings(_settings);
        Assert.Equal(("bloodlust", "none", "#000000"), (again.Theme, again.Extra, again.ThemeColor));
    }

    [Fact]
    public void An_unknown_stored_value_falls_back_to_the_default()
    {
        _settings.Set(WorkerSettingsKeys.UiTheme, "plaid");
        _settings.Set(WorkerSettingsKeys.UiThemeExtra, "<script>");

        var theme = new ThemeSettings(_settings);

        Assert.Equal(("hexagon", "hexagon"), (theme.Theme, theme.Extra));
    }

    [Fact]
    public void Only_listed_values_can_be_saved()
    {
        var theme = new ThemeSettings(_settings);

        Assert.Throws<ArgumentException>(() => theme.Save("plaid", "none"));
        Assert.Throws<ArgumentException>(() => theme.Save("hexagon", "confetti"));
        Assert.Null(_settings.Get(WorkerSettingsKeys.UiTheme));
    }

    [Fact]
    public void Every_theme_and_embellishment_has_a_stylesheet_rule()
    {
        // The choices must match styles/themes (the extras' stock "hexagon" is the body default, so it has no own rule).
        var styles = Path.Combine(AppContext.BaseDirectory, "../../../../../src/Web/Frontend/src/styles/themes");
        if (!Directory.Exists(styles)) return; // not run from a checkout
        foreach (var (value, _, _) in ThemeSettings.Themes)
            Assert.Contains($"data-th-main=\"{value}\"", File.ReadAllText(Path.Combine(styles, $"_theme_{value}.scss")));
        foreach (var (value, _) in ThemeSettings.Extras.Where(e => e.Value != "hexagon"))
            Assert.Contains($"data-th-extra=\"{value}\"", File.ReadAllText(Path.Combine(styles, $"_extra_{value}.scss")));
    }
}
