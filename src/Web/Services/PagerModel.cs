namespace SpankBank.Web.Services;

/// <summary>What the shared paginator partial needs: the current and last page, and how to link to a page (0 = fast travel).</summary>
public sealed record PagerModel(int Current, int Last, Func<int, string> Url);
