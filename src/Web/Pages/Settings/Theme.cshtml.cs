using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SpankBank.Data;

namespace SpankBank.Web.Pages.SettingsPages;

/// <summary>Settings &gt; Theme: the theme and the embellishments, stored in the database.</summary>
public class ThemeModel : PageModel
{
    private readonly ThemeSettings _theme;

    public ThemeModel(ThemeSettings theme) => _theme = theme;

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public string Theme => _theme.Theme;
    public string Extra => _theme.Extra;

    public IActionResult OnPost(string? theme, string? extra)
    {
        if (!ThemeSettings.IsTheme(theme) || !ThemeSettings.IsExtra(extra))
        {
            Error = "Pick one of the listed themes and embellishments.";
            return RedirectToPage();
        }
        _theme.Save(theme!, extra!);
        Message = "Saved.";
        return RedirectToPage();
    }
}
