using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SpankBank.Web.Pages;

/// <summary>The page behind every error status (404 mostly): the site's layout and menu, a short text, the status code kept.</summary>
public class ErrorModel : PageModel
{
    public int Code { get; private set; } = 404;
    public string RequestedPath { get; private set; } = "";

    public string Title => Code == 404 ? "Page not found" : $"Error {Code}";

    public void OnGet(int? code)
    {
        Code = code is >= 400 and <= 599 ? code.Value : 404;
        var original = HttpContext.Features.Get<IStatusCodeReExecuteFeature>();
        RequestedPath = original is null ? Request.Path.Value ?? "" : original.OriginalPathBase + original.OriginalPath;
        Response.StatusCode = Code;
    }
}
